using PersonalAgent.Configuration;
using Microsoft.Extensions.Options;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;
using Microsoft.Extensions.Logging;
using System.Net.Mime;

namespace PersonalAgent.Endpoints;

internal static class PersonalAgentEndpoints
{
    public static WebApplication MapPersonalAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => new { status = "healthy", service = "PersonalAgent API" });

        var security = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var apiKeyOptions = app.Services.GetRequiredService<IOptions<ApiKeyOptions>>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PersonalAgent.Api");
        var apiGroup = app.MapGroup("/api").RequireRateLimiting(PersonalAgentConstants.ApiRateLimiter);

        if (security.AllowedOrigins.Length > 0) apiGroup.RequireCors(PersonalAgentConstants.ApiCorsPolicy);

        apiGroup.AddEndpointFilter(new InternalApiKeyFilter(apiKeyOptions));

        apiGroup.MapGet("/models", (ChatModelCatalog chatModelCatalog) =>
        {
            var models = chatModelCatalog.GetModels();
            logger.LogInformation("Returning {ModelCount} chat models", models.Count);
            return Results.Ok(new { models });
        });

        apiGroup.MapPost("/sessions", async (CreateSessionRequest request, AgentService agentService, ChatModelCatalog chatModelCatalog) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            var selectedModel = chatModelCatalog.FindModel(request.ModelId);
            if (selectedModel is null)
                return Results.BadRequest(new { error = $"Model '{request.ModelId}' is not available" });

            logger.LogInformation("Creating session for profile {ProfileId} using model {ModelId}", request.ProfileId, selectedModel.Id);
            var created = await agentService.CreateSessionAsync(request.ProfileId, selectedModel.Id);
            return Results.Ok(new { sessionId = created.SessionId, modelId = created.ModelId, message = "Session created successfully" });
        });

        apiGroup.MapGet("/sessions", async (string profileId, int? pageSize, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, AgentService agentService) =>
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
            var page = await agentService.GetSessionsAsync(profileId, beforeActivityAt, beforeSessionId, pageSize ?? 20);
            return Results.Ok(page);
        });

        apiGroup.MapPost("/sessions/{sessionId}/messages", async (string sessionId, SendMessageRequest request, AgentService agentService) =>
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
            var response = await agentService.SendMessageAsync(sessionId, request.ProfileId, request.Message);
            return response is not null
                ? Results.Ok(new { sessionId, response })
                : Results.NotFound(new { error = "Session not found" });
        });

        apiGroup.MapGet("/sessions/{sessionId}/messages", async (string sessionId, string profileId, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            logger.LogInformation("Loading transcript for session {SessionId} and profile {ProfileId}", sessionId, profileId);
            var conversation = await agentService.GetSessionMessagesAsync(sessionId, profileId);
            return conversation is not null
                ? Results.Ok(new { sessionId = conversation.SessionId, modelId = conversation.ModelId, messages = conversation.Messages })
                : Results.NotFound(new { error = "Session not found" });
        });

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
                createdAtUtc = result.CreatedAtUtc
            });
        });

        apiGroup.MapGet("/coach-checkins/{uploadId:guid}", async (Guid uploadId, string profileId, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

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
        });

        apiGroup.MapGet("/coach-checkins/{uploadId:guid}/summary", async (Guid uploadId, string profileId, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

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
        });

        apiGroup.MapPost("/coach-checkins/{uploadId:guid}/speaker-overrides", async (Guid uploadId, CoachSpeakerOverrideRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });
            if (request.Overrides.Count is 0)
                return Results.BadRequest(new { error = "At least one speaker override is required" });
            if (request.Overrides.Any(ovr => string.IsNullOrWhiteSpace(ovr.Role)))
                return Results.BadRequest(new { error = "Each override role is required" });

            await agentService.ApplyCoachSpeakerOverridesAsync(uploadId, request.ProfileId, request.Overrides);
            return Results.Ok(new { message = "Speaker overrides applied. Processing restarted." });
        });

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
}
