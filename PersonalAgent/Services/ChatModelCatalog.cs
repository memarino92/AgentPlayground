using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class ChatModelCatalog(IOptions<ChatModelCatalogOptions> options)
{
    private readonly List<AvailableChatModel> _models = BuildModels(options.Value.Models);

    public IReadOnlyList<AvailableChatModel> GetModels() => _models;

    public AvailableChatModel GetDefaultModel() => _models.First(model => model.IsDefault);

    public AvailableChatModel? FindModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return GetDefaultModel();
        return _models.FirstOrDefault(model => string.Equals(model.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static List<AvailableChatModel> BuildModels(IEnumerable<ChatModelOption> configuredModels)
    {
        var models = configuredModels
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new AvailableChatModel(
                model.Id.Trim(),
                string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id.Trim() : model.DisplayName.Trim(),
                model.IsDefault))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (models.Count is 0)
            return [new AvailableChatModel("gpt-4o-mini", "GPT-4o mini", true)];

        if (models.Any(model => model.IsDefault)) return models;

        var firstModel = models[0];
        models[0] = firstModel with { IsDefault = true };
        return models;
    }
}
