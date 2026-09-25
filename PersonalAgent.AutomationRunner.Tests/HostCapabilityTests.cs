using FluentAssertions;
using Xunit;

namespace PersonalAgent.AutomationRunner.Tests;

public sealed class HostCapabilityTests
{
    private const string Supported = """{"OSType":"linux","MemoryLimit":true,"SwapLimit":true,"CpuCfsQuota":true,"PidsLimit":true,"SecurityOptions":["name=seccomp,profile=builtin"]}""";

    [Theory]
    [InlineData("MemoryLimit")]
    [InlineData("SwapLimit")]
    [InlineData("CpuCfsQuota")]
    [InlineData("PidsLimit")]
    public void MissingResourceEnforcementFailsClosed(string Feature) =>
        FluentActions.Invoking(() => DockerProgramExecutor.RequireHostCapabilities(Supported.Replace($"\"{Feature}\":true", $"\"{Feature}\":false")))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void UnconfinedSeccompFailsClosed() =>
        FluentActions.Invoking(() => DockerProgramExecutor.RequireHostCapabilities(Supported.Replace("profile=builtin", "profile=unconfined")))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void SupportedHostIsAccepted() => DockerProgramExecutor.RequireHostCapabilities(Supported);
}
