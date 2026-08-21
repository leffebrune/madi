using System.Text.Json;
using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class FileModelCatalogTests
{
    [Fact]
    public async Task MissingModelsJsonCreatesDefaultsWithThreeModels()
    {
        using var directory = new TemporaryDirectory();
        var catalog = new FileModelCatalog(directory.Path);
        var modelsPath = Path.Combine(directory.Path, "models.json");

        Assert.False(File.Exists(modelsPath));

        var models = await catalog.LoadAsync();

        Assert.True(File.Exists(modelsPath));
        Assert.Equal(3, models.Count);

        Assert.Equal("gpt-5.6-luna", models[0].Name);
        Assert.Equal("OpenAI", models[0].Provider);
        Assert.Equal("openai-codex", models[0].Api);
        Assert.Null(models[0].BaseUrl);
        Assert.True(models[0].SupportsReasoning);

        Assert.Equal("gpt-5.6-terra", models[1].Name);
        Assert.Equal("gpt-5.6-sol", models[2].Name);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(modelsPath));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal("OpenAI", document.RootElement[0].GetProperty("provider").GetString());
        Assert.Equal(3, document.RootElement[0].GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task ExistingModelsJsonIsLoadedWithoutOverwriting()
    {
        using var directory = new TemporaryDirectory();
        var modelsPath = Path.Combine(directory.Path, "models.json");
        await File.WriteAllTextAsync(
            modelsPath,
            """
            [
              {
                "provider": "CustomProvider",
                "api": "custom-api",
                "models": [
                  {
                    "name": "custom-model",
                    "supportsReasoning": false,
                    "maxContextTokens": 100000
                  }
                ]
              }
            ]
            """);

        var catalog = new FileModelCatalog(directory.Path);
        var models = await catalog.LoadAsync();

        Assert.Single(models);
        Assert.Equal("custom-model", models[0].Name);
        Assert.Equal("CustomProvider", models[0].Provider);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-model-catalog-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
