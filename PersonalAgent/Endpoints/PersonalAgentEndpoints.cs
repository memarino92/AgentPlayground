using PersonalAgent.Configuration;
using Microsoft.Extensions.Options;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;
using Microsoft.Extensions.Logging;

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

        return app;
    }
}
