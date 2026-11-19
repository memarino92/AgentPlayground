using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests;

public class MobileChatGrantTests
{
    private readonly MobileChatGrant _grants = new(new EphemeralDataProtectionProvider());

    [Theory]
    [InlineData("Owner", "urn:github:login")]
    [InlineData("Coach", ClaimTypes.NameIdentifier)]
    public void ChatGrantRequiresOriginalProofAndPreservesActor(string Role, string Identifier)
    {
        var Proof = RandomNumberGenerator.GetBytes(32);
        var User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, Role), new Claim(Identifier, "test-user"), new Claim(ClaimTypes.Email, "actor@example.invalid")
        ], "Test"));
        var Ticket = _grants.Issue(User, Convert.ToHexString(SHA256.HashData(Proof)));
        var Actor = _grants.Read($"Bearer {Ticket}.{Convert.ToHexString(Proof)}");
        Assert.Equal("test-user", Actor?.FindFirstValue(Identifier));
        Assert.True(Actor!.IsInRole(Role));
        Assert.Equal("actor@example.invalid", Actor.FindFirstValue(ClaimTypes.Email));
        Assert.Null(_grants.Read($"Bearer {Ticket}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}"));
        Assert.Null(_grants.Read($"Bearer invalid.{Convert.ToHexString(Proof)}"));
        Assert.Null(_grants.Read($"Bearer {Ticket}"));
    }
}
