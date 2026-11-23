using System.Text.Json;

namespace PersonalAgent.Worker.Sandboxes;

internal sealed record RailwayBridgeDiagnostic(string Stage, string Reason, int? Code)
{
    private const string Prefix = "railway-bridge-diagnostic:";
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "decode-request", "dispatch", "coding", "create-sandbox", "connect-sandbox", "connect-scratchpad",
        "destroy-sandbox", "start-scratchpad", "read-input", "upload-input", "publish-input", "execute-cell",
        "pause-scratchpad", "pack-artifacts", "download-artifacts", "store-artifacts", "resume-scratchpad",
        "upload-program", "upload-gateway-settings", "check-docker", "build-image", "read-image-id", "execute-program"
    };
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "authentication", "rate-limited", "graphql-error", "exec-interrupted", "connection-failure", "sandbox-not-found",
        "sandbox-start-failed", "sandbox-file-error", "unexpected-error", "file-not-found", "access-denied",
        "disk-full", "file-exists", "read-only-filesystem", "connection-reset", "connection-refused", "timeout",
        "dns-failure", "connection-closed", "cancelled", "docker-unavailable", "command-failed",
        "container-not-found", "image-not-found",
        "invalid-artifact-length", "incomplete-artifact", "output-limit", "invalid-sandbox-network",
        "resource-limits-unavailable", "invalid-image-id"
    };

    internal static RailwayBridgeDiagnostic? Parse(string Line)
    {
        if (Line.Length > 256 || !Line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            using var Message = JsonDocument.Parse(Line.AsMemory(Prefix.Length));
            var Root = Message.RootElement;
            if (Root.ValueKind != JsonValueKind.Object ||
                !Root.TryGetProperty("stage", out var StageValue) || StageValue.ValueKind != JsonValueKind.String ||
                !Root.TryGetProperty("reason", out var ReasonValue) || ReasonValue.ValueKind != JsonValueKind.String)
                return null;

            var Stage = StageValue.GetString()!;
            var Reason = ReasonValue.GetString()!;
            if (!Stages.Contains(Stage) || !Reasons.Contains(Reason)) return null;

            int? Code = null;
            if (Root.TryGetProperty("code", out var CodeValue) && CodeValue.ValueKind != JsonValueKind.Null)
            {
                if (CodeValue.ValueKind != JsonValueKind.Number || !CodeValue.TryGetInt32(out var Number) ||
                    Number is < -1 or > 4999)
                    return null;
                Code = Number;
            }
            return new(Stage, Reason, Code);
        }
        catch (JsonException) { return null; }
    }
}
