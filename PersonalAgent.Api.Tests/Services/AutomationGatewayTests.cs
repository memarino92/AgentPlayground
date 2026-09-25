using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Integrations;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed partial class AutomationTests
{
    private static AutomationRuntimeSettings RuntimeSettings(string Mode = "Off") => new()
    {
        Enabled = true, EnvironmentId = Guid.NewGuid().ToString(), Checkpoint = "test-base-v1", ImageId = "sha256:" + new string('a', 64),
        GatewayUrl = "https://api.example.test", ReviewMode = Mode
    };

    [Fact]
    public async Task RuntimeSettings_CreateEncryptedMaskConflictAndReload()
    {
        using var Host = await CreateHostAsync();
        var Store = Host.Services.GetRequiredService<AutomationRuntimeStore>();
        var Before = await Store.ReadAsync(default);
        var Saved = await Store.SaveAsync(new(Before.Revision, RuntimeSettings(), "replace", "secret-railway-token"), default);
        Saved.HasToken.Should().BeTrue();
        JsonSerializer.Serialize(Saved).Should().NotContain("secret-railway-token");
        var Db = Host.Services.GetRequiredService<IntegrationDatabase>();
        await using var Connection = await Db.OpenAsync(default);
        await using var Query = new Npgsql.NpgsqlCommand("SELECT credential FROM app.automation_runtime WHERE id = 1", Connection);
        ((string)(await Query.ExecuteScalarAsync())!).Should().NotContain("secret-railway-token");
        var Stale = () => Store.SaveAsync(new(Before.Revision, new(), "clear"), default);
        await Stale.Should().ThrowAsync<IntegrationConflictException>();
        (await Store.ReadAsync(default)).Token.Should().Be("secret-railway-token");
        await Store.SaveAsync(new(Saved.Revision, new(), "clear"), default);
        (await Store.ReadAsync(default)).View.HasToken.Should().BeFalse();
    }

    [Fact]
    public async Task SandboxLease_IsExclusiveScopesTokensAndRetainsCleanupCredentialAcrossRotation()
    {
        using var Host = await CreateHostAsync();
        var Store = Host.Services.GetRequiredService<AutomationRuntimeStore>();
        var Before = await Store.ReadAsync(default);
        await Store.SaveAsync(new(Before.Revision, RuntimeSettings(), "replace", "old-token"), default);
        var Snapshot = await Store.ReadAsync(default);
        var Leases = Host.Services.GetRequiredService<AutomationSandboxStore>();
        var Run = Guid.NewGuid();
        (await Leases.ClaimAsync(Run, 0, Snapshot, "capability-hash", default)).Should().BeTrue();
        (await Leases.ClaimAsync(Run, 0, Snapshot, "other", default)).Should().BeFalse();
        (await Leases.AuthorizeAsync(Run, 0, "capability-hash", default)).Should().BeFalse();
        await Leases.AttachAsync(Run, 0, "sandbox-id", default);
        (await Leases.AuthorizeAsync(Run, 0, "capability-hash", default)).Should().BeTrue();
        (await Leases.AuthorizeAsync(Run, 1, "capability-hash", default)).Should().BeFalse();
        await Store.SaveAsync(new(Snapshot.Revision, Snapshot.Settings, "replace", "rotated-token"), default);
        await Leases.SaveResultAsync(Run, 0, "result", default);
        (await Leases.AuthorizeAsync(Run, 0, "capability-hash", default)).Should().BeFalse();
        var Pending = (await Leases.CleanupAsync(default)).Single(L => L.RunId == Run);
        Pending.Credential.Should().Be("old-token");
        await Leases.DestroyedAsync(Run, 0, default);
        (await Leases.ResultAsync(Run, 0, default)).Should().Be("result");
        (await Leases.CleanupAsync(default)).Should().NotContain(L => L.RunId == Run);
    }

    [Theory]
    [InlineData("Off", true, false, null, 0, 0)]
    [InlineData("Shadow", true, false, null, 0, 0)]
    [InlineData("Enforce", false, false, null, 0, 0)]
    [InlineData("Enforce", true, true, "approve", 0.99, 0.99)]
    [InlineData("Enforce", false, true, "approve", 0.99, 0.1)]
    [InlineData("Enforce", false, true, "deny", 0.99, 0.99)]
    [InlineData("Enforce", false, false, "approve", 0.99, 0.99)]
    public async Task Gateway_ScopesCapabilitiesRechecksPermissionAndHandlesUnavailableJev(string Mode, bool Allowed, bool Configured, string? Choice, double Probability, double Confidence)
    {
        var Grants = new Dictionary<string, bool> { [AutomationPrograms.PermissionKey] = true, [AgentToolKeys.GetCurrentDateTime] = true };
        using var Host = await CreateHostAsync(Grants: Grants, JevConfigured: Configured, Decision: new(Choice, Probability, Confidence));
        var Store = Host.Services.GetRequiredService<AutomationRuntimeStore>();
        var Before = await Store.ReadAsync(default);
        await Store.SaveAsync(new(Before.Revision, RuntimeSettings(Mode), "replace", "test-token"), default);
        var Source = JsonSerializer.Serialize(new { steps = new[] { new { id = "program", action = "csharp", arguments = new
        { source = "Console.Write(1);", input = "", tools = new[] { AgentToolKeys.GetCurrentDateTime } } } } });
        var (_, Run) = await SaveAndQueueAsync(Host, Source);
        await Host.StartAsync();
        try
        {
            await UntilAsync(() => Task.FromResult(Host.Services.GetRequiredService<ProgramCapture>().Items.Count == 1));
            const string Capability = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var Leases = Host.Services.GetRequiredService<AutomationSandboxStore>();
            await Leases.ClaimAsync(Run, 0, await Store.ReadAsync(default), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Capability))), default);
            await Leases.AttachAsync(Run, 0, "sandbox-id", default);
            await using var Scope = Host.Services.CreateAsyncScope();
            var Gateway = Scope.ServiceProvider.GetRequiredService<AutomationOperationGateway>();
            var Request = new AutomationOperationRequest("clock-1", AgentToolKeys.GetCurrentDateTime, JsonSerializer.SerializeToElement(new { }));
            var CrossRun = () => Gateway.InvokeAsync(Guid.NewGuid(), 0, Capability, Request, default);
            await CrossRun.Should().ThrowAsync<UnauthorizedAccessException>();
            var Call = () => Gateway.InvokeAsync(Run, 0, Capability, Request, default);
            if (Allowed)
            {
                var Output = await Call();
                (await Call()).Should().Be(Output);
                var Changed = () => Gateway.InvokeAsync(Run, 0, Capability, Request with { Inputs = JsonSerializer.SerializeToElement(new { extra = true }) }, default);
                await Changed.Should().ThrowAsync<ArgumentException>();
                Grants[AgentToolKeys.GetCurrentDateTime] = false;
                await Call.Should().ThrowAsync<UnauthorizedAccessException>();
            }
            else await Call.Should().ThrowAsync<UnauthorizedAccessException>();
            (await Gateway.EvidenceAsync(Run, default)).Should().ContainSingle().Which.Outcome.Should().Be(Allowed ? "Allowed" : "Denied");
        }
        finally { await Host.StopAsync(); }
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("[1.0,2.0)")]
    [InlineData("1.0.0;command")]
    public void RuntimeRejectsUnpinnedPackages(string Version)
    {
        var Validate = () => AutomationRuntimeStore.Validate(RuntimeSettings() with { ApprovedPackages = ["Example@" + Version] }, "token");
        Validate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PackagesRequireApprovalOfTransitiveVersionsBeforeAnyRestore()
    {
        using var Host = await CreateHostAsync();
        var Store = Host.Services.GetRequiredService<AutomationRuntimeStore>();
        var Before = await Store.ReadAsync(default);
        await Store.SaveAsync(new(Before.Revision, RuntimeSettings() with { ApprovedPackages = ["Direct@1.0.0"] }, "replace", "token"), default);
        const string Lock = """{"version":1,"dependencies":{"net11.0":{"Direct":{"type":"Direct","resolved":"1.0.0","contentHash":"hash"},"Indirect":{"type":"Transitive","resolved":"2.0.0","contentHash":"hash"}}}}""";
        var Step = new AutomationStep("code", "csharp", JsonSerializer.SerializeToElement(new { source = "Console.Write(1);", input = "", packages = new[] { "Direct@1.0.0" }, packageLock = Lock }));
        await using var Scope = Host.Services.CreateAsyncScope();
        var Gateway = Scope.ServiceProvider.GetRequiredService<AutomationOperationGateway>();
        var Id = Guid.NewGuid();
        var Approve = () => Gateway.ApprovePackagesAsync(Id, 0, Step, default);
        await Approve.Should().ThrowAsync<UnauthorizedAccessException>();
        (await Gateway.EvidenceAsync(Id, default)).Should().ContainSingle().Which.Policy.Should().Contain("package-allowlist-denied");
        var Current = await Store.ReadAsync(default);
        await Store.SaveAsync(new(Current.Revision, Current.Settings with { ApprovedPackages = ["Direct@1.0.0", "Indirect@2.0.0"] }), default);
        await Gateway.ApprovePackagesAsync(Guid.NewGuid(), 0, Step, default);
    }
}
