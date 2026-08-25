using Madi.Core;

namespace Madi.Runtime.Persistence;

public sealed class FileSystemPromptCatalog : ISystemPromptCatalog
{
    private readonly string _path;

    public FileSystemPromptCatalog(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _path = Path.Combine(Path.GetFullPath(storageRoot), "SYSTEM.md");
    }

    private const string DefaultSystemPrompt = """
        You are Madi, an expert software engineering assistant.

        Help the user design, write, test, debug, and refactor software.
        Follow best practices, write clean and maintainable code, and explain your reasoning clearly and concisely.
        """;

    public async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path, DefaultSystemPrompt, cancellationToken);
            return DefaultSystemPrompt;
        }

        var content = await File.ReadAllTextAsync(_path, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException($"System prompt file '{_path}' cannot be empty.");
        }

        return content.Trim();
    }
}
