namespace PersonalAgent.Api.Services;

internal static class OpenRouterModelIds
{
    public const string Prefix = "openrouter:";

    public static string ToCatalogId(string ProviderModelId) => Prefix + ProviderModelId;

    public static bool TryGetProviderId(string CatalogId, out string ProviderModelId)
    {
        if (CatalogId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && CatalogId.Length > Prefix.Length)
        {
            ProviderModelId = CatalogId[Prefix.Length..];
            return true;
        }

        ProviderModelId = string.Empty;
        return false;
    }
}
