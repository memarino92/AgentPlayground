using PersonalAgent.Configuration;
using Microsoft.Extensions.Options;
using PersonalAgent.Models;
using PersonalAgent.Security;
using PersonalAgent.Services;

namespace PersonalAgent.Endpoints;

internal static class PersonalAgentEndpoints
{
    public static WebApplication MapPersonalAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => new { status = "healthy", service = "PersonalAgent API" });

        var security = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var apiKeyOptions = app.Services.GetRequiredService<IOptions<ApiKeyOptions>>();
        var apiGroup = app.MapGroup("/api").RequireRateLimiting(PersonalAgentConstants.ApiRateLimiter);

        if (security.AllowedOrigins.Length > 0) apiGroup.RequireCors(PersonalAgentConstants.ApiCorsPolicy);

        apiGroup.AddEndpointFilter(new InternalApiKeyFilter(apiKeyOptions));

        apiGroup.MapPost("/sessions", async (CreateSessionRequest request, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            var sessionId = await agentService.CreateSessionAsync(request.ProfileId);
            return Results.Ok(new { sessionId, message = "Session created successfully" });
        });

        apiGroup.MapGet("/sessions", async (string profileId, int? pageSize, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            if (beforeActivityAt is null != beforeSessionId is null)
                return Results.BadRequest(new { error = "BeforeActivityAt and BeforeSessionId must be provided together" });

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

            var response = await agentService.SendMessageAsync(sessionId, request.ProfileId, request.Message);
            return response is not null
                ? Results.Ok(new { sessionId, response })
                : Results.NotFound(new { error = "Session not found" });
        });

        apiGroup.MapGet("/sessions/{sessionId}/messages", async (string sessionId, string profileId, AgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Results.BadRequest(new { error = "ProfileId is required" });

            var messages = await agentService.GetSessionMessagesAsync(sessionId, profileId);
            return messages is not null
                ? Results.Ok(new { sessionId, messages })
                : Results.NotFound(new { error = "Session not found" });
        });

        return app;
    }
}
