using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace PersonalAgent.Web.Services;

internal sealed class MobileChatGrant(IDataProtectionProvider Protection)
{
    private readonly ITimeLimitedDataProtector _protector = Protection.CreateProtector("PersonalAgent.Mobile.Workspace.v1").ToTimeLimitedDataProtector();

    public static bool IsChallenge(string? Value) =>
        Value is { Length: 64 } && Value.All(Uri.IsHexDigit);

    public string Issue(ClaimsPrincipal User, string Challenge)
    {
        if (User.Identity?.IsAuthenticated != true || !IsChallenge(Challenge)) throw new InvalidOperationException();
        var Owner = User.IsInRole("Owner");
        var Identity = Owner ? User.FindFirstValue("urn:github:login") : User.FindFirstValue(ClaimTypes.NameIdentifier);
        if ((!Owner && !User.IsInRole("Coach")) || string.IsNullOrWhiteSpace(Identity)) throw new UnauthorizedAccessException();
        var Payload = new Grant(Challenge, Identity, Owner, User.FindFirstValue(ClaimTypes.Email));
        return _protector.Protect(JsonSerializer.Serialize(Payload), TimeSpan.FromDays(1));
    }

    public ClaimsPrincipal? Read(string? Authorization)
    {
        if (Authorization is null || Authorization.Length > 4096 || !Authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var Parts = Authorization[7..].Split('.');
        if (Parts.Length != 2 || !IsChallenge(Parts[1])) return null;
        try
        {
            var Payload = JsonSerializer.Deserialize<Grant>(_protector.Unprotect(Parts[0]));
            if (Payload is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Payload.Challenge),
                SHA256.HashData(Convert.FromHexString(Parts[1])))) return null;
            var Identity = new ClaimsIdentity("MobileChat");
            Identity.AddClaim(new(ClaimTypes.Role, Payload.Owner ? "Owner" : "Coach"));
            Identity.AddClaim(new(Payload.Owner ? "urn:github:login" : ClaimTypes.NameIdentifier, Payload.Identity));
            if (Payload.Email is not null) Identity.AddClaim(new(ClaimTypes.Email, Payload.Email));
            return new(Identity);
        }
        catch (CryptographicException) { return null; }
    }

    private sealed record Grant(string Challenge, string Identity, bool Owner, string? Email);
}
