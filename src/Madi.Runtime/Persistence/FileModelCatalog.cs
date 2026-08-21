using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Persistence;

public sealed class FileModelCatalog : IModelCatalog
{
    private readonly string _path;

    public FileModelCatalog(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _path = Path.Combine(Path.GetFullPath(storageRoot), "models.json");
    }

    public async Task<IReadOnlyList<AvailableModel>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            var defaults = CreateDefaults();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await SaveAsync(defaults, cancellationToken);
            return ToDomain(defaults);
        }

        List<ModelProviderDocument> providers;
        var migrated = false;
        await using (var input = File.OpenRead(_path))
        {
            using var json = await JsonDocument.ParseAsync(
                input,
                cancellationToken: cancellationToken);
            if (IsLegacyFormat(json.RootElement))
            {
                var legacyModels = json.RootElement.Deserialize(
                    ModelCatalogJsonContext.Default.ListLegacyModelDocument) ?? [];
                providers = Migrate(legacyModels);
                migrated = true;
            }
            else
            {
                providers = json.RootElement.Deserialize(
                    ModelCatalogJsonContext.Default.ListModelProviderDocument) ?? [];
            }
        }

        var models = ToDomain(providers);
        if (migrated)
        {
            await SaveAsync(providers, cancellationToken);
        }

        return models;
    }

    private async Task SaveAsync(
        List<ModelProviderDocument> providers,
        CancellationToken cancellationToken)
    {
        await using var output = File.Create(_path);
        await JsonSerializer.SerializeAsync(
            output,
            providers,
            ModelCatalogJsonContext.Default.ListModelProviderDocument,
            cancellationToken);
    }

    private static bool IsLegacyFormat(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The model catalog root must be an array.");
        }

        bool? isLegacy = null;
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Each model catalog entry must be an object.");
            }

            var entryIsLegacy = !element.TryGetProperty("models", out _);
            if (isLegacy is not null && isLegacy != entryIsLegacy)
            {
                throw new InvalidDataException(
                    "The model catalog cannot mix legacy models and provider entries.");
            }

            isLegacy = entryIsLegacy;
        }

        return isLegacy ?? false;
    }

    private static List<ModelProviderDocument> Migrate(
        List<LegacyModelDocument> legacyModels)
    {
        foreach (var model in legacyModels)
        {
            if (string.IsNullOrWhiteSpace(model.Provider)
                || string.IsNullOrWhiteSpace(model.ProviderType)
                || string.IsNullOrWhiteSpace(GetLegacyModelName(model)))
            {
                throw new InvalidDataException(
                    "Legacy model name, provider, and provider type are required.");
            }
        }

        return legacyModels
            .GroupBy(model => new
            {
                model.Provider,
                Api = model.ProviderType,
                model.BaseUrl,
            })
            .Select(group => new ModelProviderDocument
            {
                Provider = group.Key.Provider!,
                Api = group.Key.Api!,
                BaseUrl = group.Key.BaseUrl,
                Models = group.Select(model => new ModelEntryDocument
                {
                    Name = GetLegacyModelName(model),
                    SupportsReasoning = model.SupportsReasoning,
                    MaxContextTokens = model.MaxContextTokens,
                    MaxOutputTokens = model.MaxOutputTokens,
                }).ToList(),
            })
            .ToList();
    }

    private static string GetLegacyModelName(LegacyModelDocument model) =>
        string.IsNullOrWhiteSpace(model.ModelName) ? model.Name! : model.ModelName;

    private static IReadOnlyList<AvailableModel> ToDomain(
        IEnumerable<ModelProviderDocument> providers) =>
        providers.SelectMany(provider => provider.ToDomain()).ToList();

    private static List<ModelProviderDocument> CreateDefaults() =>
    [
        new ModelProviderDocument
        {
            Provider = "OpenAI",
            Api = "openai-codex",
            Models =
            [
                new ModelEntryDocument
                {
                    Name = "gpt-5.6-luna",
                    SupportsReasoning = true,
                    MaxContextTokens = 400000,
                    MaxOutputTokens = 128000,
                },
                new ModelEntryDocument
                {
                    Name = "gpt-5.6-terra",
                    SupportsReasoning = true,
                    MaxContextTokens = 400000,
                    MaxOutputTokens = 128000,
                },
                new ModelEntryDocument
                {
                    Name = "gpt-5.6-sol",
                    SupportsReasoning = true,
                    MaxContextTokens = 400000,
                    MaxOutputTokens = 128000,
                },
            ],
        },
    ];
}
