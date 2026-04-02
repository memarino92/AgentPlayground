using FluentAssertions;
using Xunit;

namespace PersonalAgent.Worker.Tests;

public class SmokeTests
{
    [Fact]
    public void Placeholder_Passes() => true.Should().BeTrue();
}
