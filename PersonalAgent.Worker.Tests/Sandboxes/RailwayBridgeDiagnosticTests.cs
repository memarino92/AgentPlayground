using PersonalAgent.Worker.Sandboxes;
using Xunit;

namespace PersonalAgent.Worker.Tests.Sandboxes;

public sealed class RailwayBridgeDiagnosticTests
{
    [Fact]
    public void Parses_only_the_allowed_fields_from_a_diagnostic()
    {
        var Result = RailwayBridgeDiagnostic.Parse(
            """railway-bridge-diagnostic:{"stage":"execute-cell","reason":"exec-interrupted","code":1006,"message":"private output"}""");

        Assert.Equal(new RailwayBridgeDiagnostic("execute-cell", "exec-interrupted", 1006), Result);
    }

    [Theory]
    [InlineData("Raw Node output")]
    [InlineData("""railway-bridge-diagnostic:{"stage":"private.xlsx","reason":"file-not-found"}""")]
    [InlineData("""railway-bridge-diagnostic:{"stage":"upload-input","reason":"private-token"}""")]
    [InlineData("""railway-bridge-diagnostic:{"stage":"upload-input","reason":"connection-reset","code":"private-token"}""")]
    [InlineData("""railway-bridge-diagnostic:{"stage":"upload-input","reason":"connection-reset","code":9348263}""")]
    [InlineData("railway-bridge-diagnostic:{")]
    public void Rejects_unrecognized_or_malformed_diagnostics(string Line) =>
        Assert.Null(RailwayBridgeDiagnostic.Parse(Line));
}
