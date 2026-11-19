using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Endpoints;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests;

public sealed class NativeWorkspaceAccessTests
{
    [Theory]
    [InlineData("database", "assigned")]
    [InlineData("jobs", "someone-else")]
    public async Task CoachCannotUseOwnerScreensOrUnassignedProfiles(string Operation, string RequestedProfile)
    {
        var grants = new MobileChatGrant(new EphemeralDataProtectionProvider());
        var proof = RandomNumberGenerator.GetBytes(32);
        var coach = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Coach"),
            new Claim(ClaimTypes.NameIdentifier, "subject"), new Claim(ClaimTypes.Email, "coach@example.invalid")], "Test"));
        var grant = grants.Issue(coach, Convert.ToHexString(SHA256.HashData(proof)));
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {grant}.{Convert.ToHexString(proof)}";
        using var http = new HttpClient(new AssignmentsHandler()) { BaseAddress = new("https://test.invalid") };
        using var client = new PersonalAgentClient(http, new NoBrowserAccount(), Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test" }));
        var result = await NativeWorkspaceEndpoints.ExecuteAsync(Operation, new(RequestedProfile), context, grants, client, default);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private sealed class AssignmentsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Assert.Equal("/api/coach-assignments", Request.RequestUri!.AbsolutePath);
            Assert.Equal("google:subject", Assert.Single(Request.Headers.GetValues("X-Agent-Actor")));
            Assert.Equal("Coach", Assert.Single(Request.Headers.GetValues("X-Agent-Role")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { profiles = new[] { "assigned" } }) });
        }
    }

    private sealed class NoBrowserAccount : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => throw new InvalidOperationException();
    }
}
