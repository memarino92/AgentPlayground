using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Tests.Components;

public sealed class WhatboxSettingsPageTests : TestContext
{
    [Fact]
    public void NewSettingsCanBeSavedAndTestedWhileDisabled()
    {
        using var handler = new WhatboxHandler();
        var auth = new OwnerAuthentication();
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(handler) { BaseAddress = new("http://localhost") }, auth,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "synthetic-key" })));
        var page = RenderComponent<WhatboxSettingsPage>();
        page.WaitForElement("#whatbox-key");
        page.Find("#whatbox-host").Change("test.whatbox.ca");
        page.Find("#whatbox-user").Change("slot");
        page.Find("#whatbox-fingerprint").Change("SHA256:synthetic");
        page.Find("#whatbox-files").Change("~/media files");
        page.Find("#whatbox-socket").Change("/tmp/custom.sock");
        page.Find("#whatbox-key").Change("test-ssh-key");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal(
            new SaveWhatboxSettingsRequest(null, "test.whatbox.ca", "slot", "SHA256:synthetic", false, "test-ssh-key", "~/media files", "/tmp/custom.sock"),
            handler.Saved));
        Assert.DoesNotContain("test-ssh-key", page.Markup);
        page.FindAll("button").Single(Button => Button.TextContent == "Test connection").Click();
        page.WaitForAssertion(() => Assert.Contains("SSH verified", page.Find("[role=status]").TextContent));
    }

    private sealed class OwnerAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("urn:github:login", "owner"), new Claim(ClaimTypes.Role, "Owner")], "test"))));
    }

    private sealed class WhatboxHandler : HttpMessageHandler
    {
        public SaveWhatboxSettingsRequest? Saved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Assert.True(Request.Headers.Contains("X-Agent-Signature"));
            if (Request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/admin/settings/whatbox/test", Request.RequestUri!.AbsolutePath);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new WhatboxConnectionResult(true, "SSH verified")) };
            }
            Assert.Equal("/api/admin/settings/whatbox", Request.RequestUri!.AbsolutePath);
            if (Request.Method == HttpMethod.Put) Saved = await Request.Content!.ReadFromJsonAsync<SaveWhatboxSettingsRequest>(Token);
            var response = Saved is { } saved
                ? new WhatboxSettingsResponse("1", saved.Host, saved.Username, saved.HostKeySha256, saved.Enabled, true,
                    saved.FilesDirectory, saved.RTorrentSocketPath)
                : new WhatboxSettingsResponse(null, "", "", "", false, false, "~/files", "~/.config/rtorrent/socket");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
