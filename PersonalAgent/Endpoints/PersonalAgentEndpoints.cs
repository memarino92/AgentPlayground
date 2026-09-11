using PersonalAgent.Configuration;
using Microsoft.Extensions.Options;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;
using Microsoft.Extensions.Logging;
using System.Net.Mime;
using System.Text;

namespace PersonalAgent.Endpoints;

internal static class PersonalAgentEndpoints
{
    public static WebApplication MapPersonalAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => new { status = "healthy", service = "PersonalAgent API" });

        var securityOptions = app.Services.GetRequiredService<IOptions<SecurityOptions>>();
        var security = securityOptions.Value;
        var apiKeyOptions = app.Services.GetRequiredService<IOptions<ApiKeyOptions>>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PersonalAgent.Api");
        var apiGroup = app.MapGroup("/api").RequireRateLimiting(PersonalAgentConstants.ApiRateLimiter);

        if (security.AllowedOrigins.Length > 0) apiGroup.RequireCors(PersonalAgentConstants.ApiCorsPolicy);

        apiGroup.AddEndpointFilter(new InternalApiKeyFilter(apiKeyOptions));
        apiGroup.MapIntegrationSettings(securityOptions, app.Configuration);
        apiGroup.MapCoachEvidence(securityOptions);

        apiGroup.MapGet("/models", async (IChatModelCatalog chatModelCatalog, CancellationToken cancellationToken) =>
        {
            var models = await chatModelCatalog.GetModelsAsync(cancellationToken);
            logger.LogInformation("Returning {ModelCount} chat models", models.Count);
            return TypedResults.Ok(new ChatModelsResponse(models));
        }).WithName("GetChatModels")
            .WithSummary("Get available chat models")
            .WithDescription("Returns API-approved chat models from a cached provider inventory, with fallback during discovery failures.");

        apiGroup.MapPost("/sessions", async (HttpContext httpContext, CreateSessionRequest request, AgentService agentService, IChatModelCatalog chatModelCatalog, ICoachAssignmentStore assignmentStore, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            var models = await chatModelCatalog.GetModelsAsync(cancellationToken);
            if (models.Count is 0) return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No chat models are available");
            var selectedModel = string.IsNullOrWhiteSpace(request.ModelId)
                ? models.First(Model => Model.IsDefault)
                : models.FirstOrDefault(Model => string.Equals(Model.Id, request.ModelId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selectedModel is null)
                return Results.BadRequest(new { error = $"Model '{request.ModelId}' is not available" });

            var access = await ResolveAccessAsync(httpContext, request.ProfileId, assignmentStore);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            logger.LogInformation("Creating session for actor {ActorId}, role {Role}, subject {ProfileId} using model {ModelId}", access.ActorId, access.Role, access.SubjectProfileId, selectedModel.Id);
            var created = await agentService.CreateSessionAsync(access, selectedModel, cancellationToken);
            return Results.Ok(new { sessionId = created.SessionId, modelId = created.ModelId, message = "Session created successfully" });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/sessions", async (HttpContext httpContext, string profileId, int? pageSize, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            if (beforeActivityAt is null != beforeSessionId is null)
                return Results.BadRequest(new { error = "BeforeActivityAt and BeforeSessionId must be provided together" });

            logger.LogInformation(
                "Loading sessions for profile {ProfileId} with page size {PageSize}, beforeActivityAt {BeforeActivityAt}, beforeSessionId {BeforeSessionId}",
                profileId,
                pageSize ?? 20,
                beforeActivityAt,
                beforeSessionId);
            var access = await ResolveAccessAsync(httpContext, profileId, assignmentStore);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var page = await agentService.GetSessionsAsync(access, beforeActivityAt, beforeSessionId, pageSize ?? 20);
            return Results.Ok(page);
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapPost("/sessions/{sessionId}/messages", async (HttpContext httpContext, string sessionId, SendMessageRequest request, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required" });
            if (request.Message.Length > PersonalAgentConstants.MaxMessageLength)
                return Results.BadRequest(new { error = $"Message length exceeds {PersonalAgentConstants.MaxMessageLength} characters" });

            logger.LogInformation(
                "Sending message for session {SessionId} and profile {ProfileId} with length {MessageLength}",
                sessionId,
                request.ProfileId,
                request.Message.Length);
            var access = await ResolveAccessAsync(httpContext, request.ProfileId, assignmentStore);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var response = await agentService.SendMessageAsync(sessionId, access, request.Message, httpContext.RequestAborted);
            return response is not null
                ? Results.Ok(new { sessionId, response })
                : Results.NotFound(new { error = "Session not found" });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/sessions/{sessionId}/messages", async (HttpContext httpContext, string sessionId, string profileId, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            logger.LogInformation("Loading transcript for session {SessionId} and profile {ProfileId}", sessionId, profileId);
            var access = await ResolveAccessAsync(httpContext, profileId, assignmentStore);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var conversation = await agentService.GetSessionMessagesAsync(sessionId, access);
            return conversation is not null
                ? Results.Ok(new { sessionId = conversation.SessionId, modelId = conversation.ModelId, messages = conversation.Messages })
                : Results.NotFound(new { error = "Session not found" });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapPost("/mobile/devices/register", async (RegisterMobileDeviceTokenRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (string.IsNullOrWhiteSpace(request.DeviceId))
                return Results.BadRequest(new { error = "DeviceId is required" });
            if (string.IsNullOrWhiteSpace(request.Platform))
                return Results.BadRequest(new { error = "Platform is required" });
            if (string.IsNullOrWhiteSpace(request.PushToken))
                return Results.BadRequest(new { error = "PushToken is required" });

            await agentService.RegisterMobileDeviceTokenAsync(request);
            logger.LogInformation(
                "Registered mobile device token for profile {ProfileId}, device {DeviceId}, platform {Platform}",
                request.ProfileId,
                request.DeviceId,
                request.Platform);
            return Results.Ok(new { message = "Device token registered" });
        });

        apiGroup.MapPost("/schedule/notifications", async (ScheduleNotificationRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.TenantId))
                return Results.BadRequest(new { error = "TenantId is required" });
            if (string.IsNullOrWhiteSpace(request.UserId))
                return Results.BadRequest(new { error = "UserId is required" });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title is required" });
            if (string.IsNullOrWhiteSpace(request.Body))
                return Results.BadRequest(new { error = "Body is required" });
            if (!HasExactlyOneTimingInput(request.Delay, request.ExecuteAt, request.When))
                return Results.BadRequest(new { error = "Provide exactly one of Delay, ExecuteAt, or When" });

            var result = await agentService.ScheduleNotificationAsync(request);
            return Results.Ok(new { id = result.Id, executeAtUtc = result.ExecuteAtUtc, correlationId = result.CorrelationId, status = result.Status });
        });

        apiGroup.MapPost("/schedule/agent-tasks", async (ScheduleAgentTaskRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.TenantId))
                return Results.BadRequest(new { error = "TenantId is required" });
            if (string.IsNullOrWhiteSpace(request.UserId))
                return Results.BadRequest(new { error = "UserId is required" });
            if (string.IsNullOrWhiteSpace(request.Instruction))
                return Results.BadRequest(new { error = "Instruction is required" });
            if (!HasExactlyOneTimingInput(request.Delay, request.ExecuteAt, request.When))
                return Results.BadRequest(new { error = "Provide exactly one of Delay, ExecuteAt, or When" });

            var result = await agentService.ScheduleAgentTaskAsync(request);
            return Results.Ok(new { id = result.Id, executeAtUtc = result.ExecuteAtUtc, correlationId = result.CorrelationId, status = result.Status });
        });

        apiGroup.MapPost("/approvals", async (RequestAgentApprovalRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (string.IsNullOrWhiteSpace(request.SessionId))
                return Results.BadRequest(new { error = "SessionId is required" });
            if (string.IsNullOrWhiteSpace(request.ToolName))
                return Results.BadRequest(new { error = "ToolName is required" });
            if (string.IsNullOrWhiteSpace(request.ActionSummary))
                return Results.BadRequest(new { error = "ActionSummary is required" });
            if (string.IsNullOrWhiteSpace(request.RequestedBy))
                return Results.BadRequest(new { error = "RequestedBy is required" });

            var approval = await agentService.RequestApprovalAsync(request);
            return Results.Ok(new
            {
                approvalId = approval.ApprovalId,
                status = approval.Status,
                expiresAt = approval.ExpiresAt
            });
        });

        apiGroup.MapPost("/approvals/{approvalId:guid}/decision", async (Guid approvalId, CompleteAgentApprovalRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (string.IsNullOrWhiteSpace(request.DecidedBy))
                return Results.BadRequest(new { error = "DecidedBy is required" });

            var updated = await agentService.CompleteApprovalAsync(approvalId, request);
            if (updated is null)
                return Results.NotFound(new { error = "Pending approval not found" });

            return Results.Ok(new
            {
                approvalId = updated.ApprovalId,
                status = updated.Status,
                decisionAt = updated.DecisionAt,
                decidedBy = updated.DecidedBy
            });
        });

        apiGroup.MapGet("/approvals/{approvalId:guid}", async (Guid approvalId, AgentService agentService) =>
        {
            var approval = await agentService.GetApprovalAsync(approvalId);
            return approval is null
                ? Results.NotFound(new { error = "Approval not found" })
                : Results.Ok(approval);
        });

        apiGroup.MapPost("/coach-checkins/uploads", async (HttpRequest request, AgentService agentService, IOptions<CoachCheckinOptions> options) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart form data" });

            var form = await request.ReadFormAsync();
            var profileId = form["profileId"].ToString();
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (!string.Equals(SignedActorFilter.Get(request.HttpContext).ActorId, profileId, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.BadRequest(new { error = "File is required" });

            if (!file.FileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only .m4a files are supported" });

            var maxSizeBytes = options.Value.MaxUploadMb * 1024L * 1024L;
            if (file.Length > maxSizeBytes)
                return Results.BadRequest(new { error = $"File size exceeds {options.Value.MaxUploadMb}MB limit" });

            await using var stream = file.OpenReadStream();
            await using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var result = await agentService.CreateCoachCheckinUploadAsync(
                profileId,
                file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Application.Octet : file.ContentType,
                memory.ToArray());

            return Results.Ok(new
            {
                uploadId = result.UploadId,
                correlationId = result.CorrelationId,
                status = result.Status.ToString(),
                createdAtUtc = result.CreatedAtUtc,
                isDuplicate = result.IsDuplicate
            });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        apiGroup.MapGet("/coach-checkins/{uploadId:guid}", async (HttpContext httpContext, Guid uploadId, string profileId, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (await ResolveAccessAsync(httpContext, profileId, assignmentStore) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var status = await agentService.GetCoachCheckinStatusAsync(uploadId, profileId);
            return status is null
                ? Results.NotFound(new { error = "Coach check-in upload not found" })
                : Results.Ok(new
                {
                    uploadId = status.UploadId,
                    sessionId = status.SessionId,
                    profileId = status.ProfileId,
                    status = status.Status.ToString(),
                    error = status.Error,
                    createdAtUtc = status.CreatedAtUtc,
                    updatedAtUtc = status.UpdatedAtUtc
                });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/coach-checkins/{uploadId:guid}/summary", async (HttpContext httpContext, Guid uploadId, string profileId, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (await ResolveAccessAsync(httpContext, profileId, assignmentStore) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var summary = await agentService.GetCoachCheckinSummaryAsync(uploadId, profileId);
            return summary is null
                ? Results.NotFound(new { error = "Coach check-in summary not found" })
                : Results.Ok(new
                {
                    uploadId = summary.UploadId,
                    sessionId = summary.SessionId,
                    summaryMarkdown = summary.SummaryMarkdown,
                    summaryJson = summary.SummaryJson,
                    updatedAtUtc = summary.UpdatedAtUtc
                });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/coach-checkins/{uploadId:guid}/transcript", async (HttpContext httpContext, Guid uploadId, string? profileId, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            var actor = SignedActorFilter.Get(httpContext);
            profileId = string.IsNullOrWhiteSpace(profileId) && actor.Role == AgentRoles.Owner ? actor.ActorId : profileId;
            if (string.IsNullOrWhiteSpace(profileId) || await ResolveAccessAsync(httpContext, profileId, assignmentStore) is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var transcript = await agentService.GetCoachCheckinTranscriptAsync(uploadId, profileId);
            return transcript is null
                ? Results.NotFound(new { error = "Coach check-in transcript not found" })
                : Results.Ok(new
                {
                    uploadId = transcript.UploadId,
                    sessionId = transcript.SessionId,
                    profileId = transcript.ProfileId,
                    status = transcript.Status.ToString(),
                    transcriptText = transcript.TranscriptText,
                    updatedAtUtc = transcript.UpdatedAtUtc,
                    utterances = transcript.Utterances.Select(utterance => new
                    {
                        speakerLabel = utterance.SpeakerLabel,
                        speakerRole = utterance.SpeakerRole,
                        startMs = utterance.StartMs,
                        endMs = utterance.EndMs,
                        text = utterance.Text,
                        confidence = utterance.Confidence
                    })
                });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/coach-checkins/{uploadId:guid}/transcript.txt", async (HttpContext httpContext, Guid uploadId, string? profileId, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            var actor = SignedActorFilter.Get(httpContext);
            profileId = string.IsNullOrWhiteSpace(profileId) && actor.Role == AgentRoles.Owner ? actor.ActorId : profileId;
            if (string.IsNullOrWhiteSpace(profileId) || await ResolveAccessAsync(httpContext, profileId, assignmentStore) is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var transcript = await agentService.GetCoachCheckinTranscriptAsync(uploadId, profileId);
            if (transcript is null)
                return Results.NotFound(new { error = "Coach check-in transcript not found" });

            var content = string.Join(Environment.NewLine, transcript.Utterances.Select(utterance => $"[{TimeSpan.FromMilliseconds(utterance.StartMs):hh\\:mm\\:ss}-{TimeSpan.FromMilliseconds(utterance.EndMs):hh\\:mm\\:ss}] {utterance.SpeakerRole}: {utterance.Text}"));

            var fileName = $"coach-checkin-{uploadId}.txt";
            return Results.File(Encoding.UTF8.GetBytes(content), "text/plain; charset=utf-8", fileName);
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapPost("/coach-checkins/{uploadId:guid}/speaker-overrides", async (HttpContext httpContext, Guid uploadId, CoachSpeakerOverrideRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (request.Overrides.Count is 0)
                return Results.BadRequest(new { error = "At least one speaker override is required" });
            if (request.Overrides.Any(ovr => string.IsNullOrWhiteSpace(ovr.Role)))
                return Results.BadRequest(new { error = "Each override role is required" });
            if (!string.Equals(SignedActorFilter.Get(httpContext).ActorId, request.ProfileId, StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            await agentService.ApplyCoachSpeakerOverridesAsync(uploadId, request.ProfileId, request.Overrides);
            return Results.Ok(new { message = "Speaker overrides applied. Processing restarted." });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        apiGroup.MapGet("/coach-checkins/admin", async (int? limit, AgentService agentService) =>
        {
            var items = await agentService.GetCoachCheckinAdminItemsAsync(limit ?? 100);
            return Results.Ok(items.Select(item => new
            {
                uploadId = item.UploadId,
                sessionId = item.SessionId,
                profileId = item.ProfileId,
                originalFileName = item.OriginalFileName,
                status = item.Status.ToString(),
                error = item.Error,
                createdAtUtc = item.CreatedAtUtc,
                updatedAtUtc = item.UpdatedAtUtc,
                hasAudioBlob = item.HasAudioBlob,
                utteranceCount = item.UtteranceCount,
                chunkCount = item.ChunkCount,
                speakerLabels = item.SpeakerLabels.Select(label => new
                {
                    speakerLabel = label.SpeakerLabel,
                    speakerRole = label.SpeakerRole,
                    utteranceCount = label.UtteranceCount,
                    sampleTexts = label.SampleTexts
                })
            }));
        }).AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        apiGroup.MapGet("/coach-checkins", async (HttpContext httpContext, string profileId, int? limit, AgentService agentService, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(profileId)) return Results.BadRequest(new { error = "ProfileId is required" });
            if (await ResolveAccessAsync(httpContext, profileId, assignmentStore) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var items = await agentService.GetCoachCheckinItemsAsync(profileId, limit ?? 100);
            return Results.Ok(items.Select(item => new
            {
                uploadId = item.UploadId,
                sessionId = item.SessionId,
                profileId = item.ProfileId,
                originalFileName = item.OriginalFileName,
                status = item.Status.ToString(),
                error = item.Error,
                createdAtUtc = item.CreatedAtUtc,
                updatedAtUtc = item.UpdatedAtUtc,
                hasAudioBlob = item.HasAudioBlob,
                utteranceCount = item.UtteranceCount,
                chunkCount = item.ChunkCount,
                speakerLabels = item.SpeakerLabels
            }));
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/admin/tool-access", async (ToolAccessService toolAccessService) =>
            Results.Ok(await toolAccessService.GetCatalogAsync()))
            .AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        apiGroup.MapPut("/admin/tool-access", async (HttpContext httpContext, SaveToolAccessRequest request, ToolAccessService toolAccessService) =>
        {
            if (string.IsNullOrWhiteSpace(request.UpdatedBy)) return Results.BadRequest(new { error = "UpdatedBy is required" });
            try
            {
                await toolAccessService.SaveAsync(request with { UpdatedBy = SignedActorFilter.Get(httpContext).ActorId });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        apiGroup.MapGet("/coach-assignments", async (HttpContext httpContext, ICoachAssignmentStore assignmentStore) =>
        {
            var actor = SignedActorFilter.Get(httpContext);
            if (actor.Role != AgentRoles.Coach || string.IsNullOrWhiteSpace(actor.Email)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new { profiles = await assignmentStore.GetAssignedProfilesAsync(actor.ActorId, actor.Email) });
        }).AddEndpointFilter(new SignedActorFilter(securityOptions));

        apiGroup.MapGet("/admin/coach-assignments", async (HttpContext httpContext, string profileId, ICoachAssignmentStore assignmentStore) =>
        {
            if (!string.Equals(SignedActorFilter.Get(httpContext).ActorId, profileId, StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await assignmentStore.GetAssignmentsAsync(profileId));
        }).AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        apiGroup.MapPut("/admin/coach-assignments", async (HttpContext httpContext, SaveCoachProfileAssignmentRequest request, ICoachAssignmentStore assignmentStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.CoachEmail)
                || string.IsNullOrWhiteSpace(request.SubjectProfileId)
                || string.IsNullOrWhiteSpace(request.UpdatedBy))
                return Results.BadRequest(new { error = "CoachEmail, SubjectProfileId, and UpdatedBy are required" });
            var actor = SignedActorFilter.Get(httpContext);
            if (!string.Equals(actor.ActorId, request.SubjectProfileId, StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            await assignmentStore.SaveAssignmentAsync(request with { UpdatedBy = actor.ActorId });
            return Results.NoContent();
        }).AddEndpointFilter(new SignedActorFilter(securityOptions, ownerOnly: true));

        return app;
    }

    private static bool HasExactlyOneTimingInput(string? delay, DateTimeOffset? executeAt, string? when)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(delay)) count++;
        if (executeAt is not null) count++;
        if (!string.IsNullOrWhiteSpace(when)) count++;
        return count is 1;
    }

    internal static async Task<AgentAccessContext?> ResolveAccessAsync(HttpContext context, string subjectProfileId, ICoachAssignmentStore assignmentStore)
    {
        var actor = SignedActorFilter.Get(context);
        if (actor.Role == AgentRoles.Owner)
            return string.Equals(actor.ActorId, subjectProfileId, StringComparison.OrdinalIgnoreCase)
                ? actor.ForSubject(subjectProfileId)
                : null;

        if (actor.Role != AgentRoles.Coach || string.IsNullOrWhiteSpace(actor.Email)) return null;
        var profiles = await assignmentStore.GetAssignedProfilesAsync(actor.ActorId, actor.Email, context.RequestAborted);
        return profiles.Contains(subjectProfileId, StringComparer.OrdinalIgnoreCase)
            ? actor.ForSubject(subjectProfileId)
            : null;
    }
}
