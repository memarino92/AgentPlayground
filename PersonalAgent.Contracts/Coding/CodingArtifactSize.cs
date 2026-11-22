using System.Text.Encodings.Web;
using System.Text.Json;

namespace PersonalAgent.Contracts.Coding;

public static class CodingArtifactSize
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // The sandbox measures the exported source artifact before the Worker appends bounded diagnostics.
    public static bool Fits(CodingArtifact Artifact) =>
        JsonSerializer.SerializeToUtf8Bytes(Artifact with { Checks = [] }, CompactJson).Length <= CodingJobs.MaxArtifactBytes;

    // This JSON travels over the bridge's standard input.
    public static string SerializeBridgeRequest(object Request) => JsonSerializer.Serialize(Request, CompactJson);
}
