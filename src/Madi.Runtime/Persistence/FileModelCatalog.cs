using System.Text.Json;
using System.Text.Json.Serialization;
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
            await using var output = File.Create(_path);
            await JsonSerializer.SerializeAsync(
                output,
                defaults,
                ModelCatalogJsonContext.Default.ListAvailableModel,
                cancellationToken);
            return defaults;
        }

        await using var input = File.OpenRead(_path);
        var models = await JsonSerializer.DeserializeAsync(
            input,
            ModelCatalogJsonContext.Default.ListAvailableModel,
            cancellationToken) ?? [];
        foreach (var model in models)
        {
            model.Validate();
        }

        return models;
    }

    private static List<AvailableModel> CreateDefaults() =>
    [
        new AvailableModel
        {
            Name = "gpt-5.6-luna",
            Provider = "OpenAI",
            ProviderType = "openai-codex",
            SupportsReasoning = true,
            MaxContextTokens = 400000,
            MaxOutputTokens = 128000,
        },
    ];
}
