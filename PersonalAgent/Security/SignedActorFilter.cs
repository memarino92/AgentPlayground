using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Security;

internal sealed class SignedActorFilter(IOptions<SecurityOptions> options, bool ownerOnly = false) : IEndpointFilter
{
    public const string ItemKey = "PersonalAgent.SignedActor";
    private readonly SecurityOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var actorId = request.Headers["X-Agent-Actor"].ToString();
        var role = request.Headers["X-Agent-Role"].ToString();
        var email = request.Headers["X-Agent-Email"].ToString();
        var timestampText = request.Headers["X-Agent-Timestamp"].ToString();
        var signature = request.Headers["X-Agent-Signature"].ToString();

        if (string.IsNullOrWhiteSpace(_options.ActorSigningKey)
            || string.IsNullOrWhiteSpace(actorId)
            || !AgentRoles.IsDefined(role)
            || !long.TryParse(timestampText, out var timestamp)
            || string.IsNullOrWhiteSpace(signature))
            return Results.Unauthorized();

        var requestedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (DateTimeOffset.UtcNow - requestedAt > TimeSpan.FromMinutes(5)
            || requestedAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1))
            return Results.Unauthorized();

        var payload = $"{actorId}\n{role}\n{email}\n{timestampText}";
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.ActorSigningKey), Encoding.UTF8.GetBytes(payload)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToUpperInvariant())))
            return Results.Unauthorized();
        if (ownerOnly && role != AgentRoles.Owner) return Results.Forbid();

        context.HttpContext.Items[ItemKey] = new SignedActor(actorId, role, string.IsNullOrWhiteSpace(email) ? null : email);
        return await next(context);
    }

    public static SignedActor Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is SignedActor actor
            ? actor
            : throw new InvalidOperationException("A signed actor is required for this endpoint.");
}
