using Madi.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Madi.Runtime.Persistence;

public sealed class FileSubagentCatalog : ISubagentCatalog
{
    private readonly string _directoryPath;
    private readonly IDeserializer _deserializer;

    public FileSubagentCatalog(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);

        _directoryPath = Path.Combine(Path.GetFullPath(storageRoot), "subagents");
        _deserializer = new StaticDeserializerBuilder(new SubagentYamlContext())
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .WithMaximumRecursion(16)
            .Build();
    }

    public async Task<IReadOnlyList<SubagentDefinition>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directoryPath))
        {
            return [];
        }

        var definitions = new List<SubagentDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_directoryPath, "*.md")
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var definition = Parse(path, content);
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    definition.Name,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Subagent file '{path}' must have the same file name and name value.");
            }

            if (!names.Add(definition.Name))
            {
                throw new InvalidDataException(
                    $"Duplicate subagent name '{definition.Name}' in '{path}'.");
            }

            definitions.Add(definition);
        }

        return definitions;
    }

    private SubagentDefinition Parse(string path, string content)
    {
        try
        {
            var (yaml, instructions) = SplitFrontMatter(content);
            var document = _deserializer.Deserialize<SubagentDocument>(yaml)
                ?? throw new InvalidDataException("YAML front matter is empty.");
            var definition = new SubagentDefinition
            {
                Name = document.Name ?? string.Empty,
                Description = document.Description ?? string.Empty,
                Model = document.Model ?? string.Empty,
                Tools = document.Tools ?? [],
                MaxToolRounds = document.MaxToolRounds,
                Instructions = instructions.Trim(),
            };
            definition.Validate();
            return definition;
        }
        catch (Exception exception) when (exception is YamlException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Invalid subagent definition '{path}': {exception.Message}",
                exception);
        }
    }

    private static (string Yaml, string Instructions) SplitFrontMatter(string content)
    {
        var normalized = content
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], "---", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The first line must be a YAML front matter delimiter ('---').");
        }

        var closingDelimiter = Array.FindIndex(
            lines,
            1,
            static line => string.Equals(line, "---", StringComparison.Ordinal));
        if (closingDelimiter < 0)
        {
            throw new InvalidDataException("The YAML front matter closing delimiter ('---') is missing.");
        }

        return (
            string.Join('\n', lines[1..closingDelimiter]),
            string.Join('\n', lines[(closingDelimiter + 1)..]));
    }
}

internal sealed class SubagentDocument
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Model { get; set; }
    public List<string>? Tools { get; set; }
    public int? MaxToolRounds { get; set; }
}

[YamlStaticContext]
[YamlSerializable(typeof(SubagentDocument))]
public partial class SubagentYamlContext : StaticContext
{
}
