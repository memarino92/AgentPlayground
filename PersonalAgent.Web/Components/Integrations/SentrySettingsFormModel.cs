using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AgentPlayground.Integrations;

namespace PersonalAgent.Web.Components.Integrations;

public sealed class SentrySettingsFormModel : IValidatableObject
{
    public bool Enabled { get; set; }
    [Required, RegularExpression("^[a-zA-Z0-9_.-]{1,64}$", ErrorMessage = "Use 1–64 letters, numbers, dots, underscores, or hyphens.")]
    public string Environment { get; set; } = "production";
    [Range(0d, 1d)]
    public double SampleRate { get; set; } = 1;
    public string DsnAction { get; set; } = "keep";
    public string Dsn { get; set; } = "";
    public bool DsnConfigured { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ValidationContext)
    {
        if (DsnAction == "replace" && !IntegrationRegistry.IsAllowedDsn(Dsn.Trim()))
            yield return new("Enter a hosted Sentry HTTPS DSN with a public key and project ID.", [nameof(Dsn)]);
        if (Enabled && (DsnAction == "clear" || (DsnAction == "keep" && !DsnConfigured)))
            yield return new("A DSN is required while reporting is enabled.", [nameof(DsnAction)]);
        if (!double.IsFinite(SampleRate)) yield return new("Enter a finite sample rate from 0 to 1.", [nameof(SampleRate)]);
    }

    public SaveIntegrationRequest ToRequest(long Revision)
    {
        var values = new Dictionary<string, string>
        {
            ["Enabled"] = Enabled.ToString().ToLowerInvariant(), ["Environment"] = Environment.Trim(),
            ["SampleRate"] = SampleRate.ToString(CultureInfo.InvariantCulture)
        };
        if (DsnAction != "keep") values["Dsn"] = DsnAction == "clear" ? "" : Dsn.Trim();
        return new(Revision, values);
    }
}
