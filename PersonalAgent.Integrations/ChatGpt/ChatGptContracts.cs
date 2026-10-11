namespace PersonalAgent.Integrations.ChatGpt;

public sealed record ChatGptConnection(Guid Id, string Label, bool PlanEnabled, bool Connected, long Version, bool WelcomeRequired = false);
public sealed record ChatGptPairing(string Code, DateTimeOffset ExpiresAt);
public sealed record ChatGptPairingRequest(Guid? ConnectionId = null);
public sealed record ChatGptHandshake(string Code, ChatGptCredentials? Credentials = null);
public sealed record ChatGptAuthorization(string HostId, string State, string Nonce, string Verifier,
    string? ClientId, string? IdTokenHint, string? EmailHint);
public sealed record ChatGptCredentials(string ClientId, string Subject, string? Email, string IdToken,
    string AccessToken, string? RefreshToken, string Scope, DateTimeOffset ExpiresAt);
public sealed record ChatGptDisconnect(bool RemoteRevocationConfirmed);
public sealed class ChatGptException(string Message) : Exception(Message);
