using System.Text.Json;
using System.Text.Json.Serialization;
using Madi.App;

namespace Madi.GUI;

internal sealed class GuiSettingsStore
{
    private readonly string _path = Path.Combine(
        MadiAppFactory.ResolveStorageRoot(),
        "ui",
        "gui-settings.json");

    public async Task<IReadOnlyList<string>> LoadRecentWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = File.OpenRead(_path);
        var settings = await JsonSerializer.DeserializeAsync(
            stream,
            GuiSettingsJsonContext.Default.GuiSettingsDocument,
            cancellationToken);
        return settings?.RecentWorkspaces
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public async Task AddRecentWorkspaceAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var recent = (await LoadRecentWorkspacesAsync(cancellationToken))
            .Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(path)
            .Take(8)
            .ToArray();
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new GuiSettingsDocument(recent),
                    GuiSettingsJsonContext.Default.GuiSettingsDocument,
                    cancellationToken);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed record GuiSettingsDocument(IReadOnlyList<string> RecentWorkspaces);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GuiSettingsDocument))]
internal sealed partial class GuiSettingsJsonContext : JsonSerializerContext;
