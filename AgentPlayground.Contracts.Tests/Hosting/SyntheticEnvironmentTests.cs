using AgentPlayground.Contracts.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentPlayground.Contracts.Tests.Hosting;

public class SyntheticEnvironmentTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void EnabledDemo_RefusesNonDevelopmentEnvironment(string Name)
    {
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SyntheticDemo:Enabled"] = "true" }).Build();
        var Act = () => SyntheticEnvironment.IsEnabled(Configuration, new TestEnvironment(Name));
        Act.Should().Throw<InvalidOperationException>().WithMessage("*Development*");
    }

    [Fact]
    public void NormalStartup_DoesNotRequireDemoDatabase()
    {
        SyntheticEnvironment.IsEnabled(new ConfigurationBuilder().Build(), new TestEnvironment("Production")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Host=production.example;Database=agentplayground_demo")]
    [InlineData("Host=localhost;Database=agentplayground")]
    [InlineData("Host=localhost,production.example;Database=agentplayground_demo")]
    [InlineData("Host=/var/run/postgresql;Database=agentplayground_demo")]
    public void DemoTarget_RejectsRemoteOrExistingDeveloperDatabase(string ConnectionString)
    {
        var Act = () => SyntheticEnvironment.ValidateTarget(ConnectionString);
        Act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("postgres-demo")]
    public void DemoTarget_AcceptsDedicatedLocalDatabase(string Host)
        => SyntheticEnvironment.ValidateTarget($"Host={Host};Database=agentplayground_demo").Should().Contain("agentplayground_demo");

    private sealed class TestEnvironment(string Name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
