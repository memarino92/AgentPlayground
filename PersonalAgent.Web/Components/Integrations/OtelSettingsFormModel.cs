using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AgentPlayground.Integrations;

namespace PersonalAgent.Web.Components.Integrations;

public sealed class OtelSettingsFormModel : IValidatableObject
{
    public bool Enabled { get; set; }
    public bool TracesEnabled { get; set; } = true;
    public bool MetricsEnabled { get; set; }
    public bool LogsEnabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Protocol { get; set; } = "grpc";
    public double SampleRate { get; set; } = 1;
    public int TimeoutMilliseconds { get; set; } = 3000;
    public string Headers { get; set; } = "";
    public string HeadersAction { get; set; } = "keep";
    public bool HeadersConfigured { get; set; }

    public SaveIntegrationRequest ToRequest(long Revision)
    {
        var values = new Dictionary<string, string>
        {
            ["Enabled"] = Enabled.ToString().ToLowerInvariant(), ["Endpoint"] = Endpoint, ["Protocol"] = Protocol,
            ["TracesEnabled"] = TracesEnabled.ToString().ToLowerInvariant(), ["MetricsEnabled"] = MetricsEnabled.ToString().ToLowerInvariant(),
            ["LogsEnabled"] = LogsEnabled.ToString().ToLowerInvariant(),
            ["SampleRate"] = SampleRate.ToString(CultureInfo.InvariantCulture),
            ["TimeoutMilliseconds"] = TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)
        };
        if (HeadersAction != "keep") values["Headers"] = HeadersAction == "clear" ? "" : Headers;
        return new(Revision, values);
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext ValidationContext)
    {
        if (HeadersAction is not ("keep" or "replace" or "clear"))
            return [new("Choose keep, replace or clear.", [nameof(HeadersAction)])];
        var values = OtelSettings.Defaults();
        foreach (var pair in ToRequest(0).Values) values[pair.Key] = pair.Value;
        if (HeadersAction == "keep" && HeadersConfigured) values["Headers"] = "authorization=saved";
        try { OtelSettings.Validate(values); return []; }
        catch (IntegrationValidationException exception)
        {
            return exception.Errors.SelectMany(Pair => Pair.Value.Select(Error => new ValidationResult(Error, [Pair.Key]))).ToArray();
        }
    }
}
