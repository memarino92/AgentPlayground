using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Tests.Services;

public sealed class WhatboxTests
{
    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly WhatboxConfiguration Configured = new("example.whatbox.ca", "slot", Fingerprint, true, "synthetic-key",
        "~/media files", "/tmp/alternate-rtorrent.sock");
    private static readonly AgentAccessContext Owner = new("owner", AgentRoles.Owner, "owner");

    [Fact]
    public async Task SaveRetainsKeyAndReturnsOnlyItsPresence()
    {
        var store = new Mock<IWhatboxSettingsStore>(MockBehavior.Strict);
        var disabled = Configured with { Enabled = false };
        store.Setup(Value => Value.ReadAsync(default)).ReturnsAsync(("1", Configured));
        store.Setup(Value => Value.SaveAsync("1", disabled, default)).ReturnsAsync("2");
        var service = CreateService(store, new(MockBehavior.Strict));

        var request = new SaveWhatboxSettingsRequest("1", Configured.Host, Configured.Username, "SHA256:" + Fingerprint, false, null,
            Configured.FilesDirectory, Configured.RTorrentSocketPath);
        var result = await service.SaveAsync(request, default);
        Assert.Equal(new WhatboxSettingsResponse("2", Configured.Host, Configured.Username, Fingerprint, false, true,
            Configured.FilesDirectory, Configured.RTorrentSocketPath), result);
        await Assert.ThrowsAsync<IntegrationConflictException>(() =>
            service.SaveAsync(request with { Version = "old" }, default));
        await Assert.ThrowsAsync<IntegrationValidationException>(() =>
            service.SaveAsync(request with { Enabled = true, PrivateKey = "invalid-key" }, default));
        await Assert.ThrowsAsync<IntegrationValidationException>(() =>
            service.SaveAsync(request with { Host = "other.example.com" }, default));
        await Assert.ThrowsAsync<IntegrationValidationException>(() =>
            service.SaveAsync(request with { RTorrentSocketPath = "relative/socket" }, default));
    }

    [Fact]
    public async Task DisabledSettingsPermitConnectionTestAndBlockToolCalls()
    {
        var store = new Mock<IWhatboxSettingsStore>(MockBehavior.Strict);
        var disabled = Configured with { Enabled = false };
        store.Setup(Value => Value.ReadAsync(default)).ReturnsAsync(("1", disabled));
        var ssh = new Mock<IWhatboxSshTransport>(MockBehavior.Strict);
        ssh.Setup(Value => Value.ExecuteAsync(disabled, WhatboxOperation.Connection, default)).ReturnsAsync("");
        var service = CreateService(store, ssh);

        Assert.True((await service.TestAsync(default)).Connected);
        Assert.False(await service.IsEnabledAsync(default));
        await Assert.ThrowsAsync<WhatboxOperationException>(() => service.DiskUsageAsync(Owner, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SeedingStatusAsync(
            new("coach", AgentRoles.Coach, "owner"), default));
        ssh.Verify(Value => Value.ExecuteAsync(disabled, WhatboxOperation.Connection, default), Times.Once);
        ssh.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ToolPermissionRequiresAnEnabledIntegrationAndAnOwnerGrant()
    {
        var store = new Mock<IWhatboxSettingsStore>(MockBehavior.Strict);
        store.SetupSequence(Value => Value.ReadAsync(default))
            .ReturnsAsync(("1", Configured))
            .ReturnsAsync(("2", Configured with { Enabled = false }));
        var permissions = new Mock<IToolAccessStore>(MockBehavior.Strict);
        var granted = new Dictionary<string, bool> { [AgentToolKeys.ReadWhatboxDiskUsage] = true };
        permissions.Setup(Value => Value.GetRolePermissionsAsync(AgentRoles.Owner, default)).ReturnsAsync(granted);
        permissions.Setup(Value => Value.GetRolePermissionsAsync(AgentRoles.Coach, default)).ReturnsAsync(granted);
        var tavily = new Mock<ITavilyMcpToolProvider>(MockBehavior.Strict);
        tavily.SetupGet(Value => Value.IsAvailable).Returns(false);
        tavily.Setup(Value => Value.GetTools()).Returns([]);
        var registry = new AgentToolRegistry(tavily.Object);
        var access = new ToolAccessService(permissions.Object, registry, CreateService(store, new(MockBehavior.Strict)));

        Assert.False(await access.IsAllowedAsync(AgentRoles.Owner, AgentToolKeys.ReadWhatboxSeedingStatus));
        Assert.False(await access.IsAllowedAsync(AgentRoles.Coach, AgentToolKeys.ReadWhatboxDiskUsage));
        Assert.True(await access.IsAllowedAsync(AgentRoles.Owner, AgentToolKeys.ReadWhatboxDiskUsage));
        Assert.False(await access.IsAllowedAsync(AgentRoles.Owner, AgentToolKeys.ReadWhatboxDiskUsage));
    }

    [Fact]
    public async Task StatusToolsReturnTypedAggregatesAndRejectInvalidOutput()
    {
        var store = new Mock<IWhatboxSettingsStore>(MockBehavior.Strict);
        store.Setup(Value => Value.ReadAsync(default)).ReturnsAsync(("1", Configured));
        var ssh = new Mock<IWhatboxSshTransport>(MockBehavior.Strict);
        ssh.SetupSequence(Value => Value.ExecuteAsync(Configured, WhatboxOperation.DiskUsage, default))
            .ReturnsAsync("""{"filesAllocatedBytes":4,"filesystemAvailableBytes":8,"filesystemTotalBytes":16}""")
            .ReturnsAsync("{}");
        ssh.Setup(Value => Value.ExecuteAsync(Configured, WhatboxOperation.SeedingStatus, default))
            .ReturnsAsync("""{"total":6,"seeding":3,"completeInactive":2,"incomplete":1,"seedingUploadBytesPerSecond":1024}""");
        var service = CreateService(store, ssh);

        Assert.Equal(new WhatboxDiskUsage(4, 8, 16), await service.DiskUsageAsync(Owner, default));
        Assert.Equal(new WhatboxSeedingStatus(6, 3, 2, 1, 1024), await service.SeedingStatusAsync(Owner, default));
        await Assert.ThrowsAsync<WhatboxOperationException>(() => service.DiskUsageAsync(Owner, default));
    }

    private static WhatboxService CreateService(Mock<IWhatboxSettingsStore> Store, Mock<IWhatboxSshTransport> Transport) =>
        new(Store.Object, Transport.Object, NullLogger<WhatboxService>.Instance);
}
