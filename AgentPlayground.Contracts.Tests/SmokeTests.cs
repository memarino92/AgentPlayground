using FluentAssertions;
using Xunit;

namespace AgentPlayground.Contracts.Tests;

public class SmokeTests
{
    [Fact]
    public void Placeholder_Passes() => true.Should().BeTrue();
}
