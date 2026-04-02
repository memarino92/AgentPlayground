using FluentAssertions;
using Xunit;

namespace PersonalAgent.Web.Tests;

public class SmokeTests
{
    [Fact]
    public void Placeholder_Passes() => true.Should().BeTrue();
}
