using Madi.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Madi.Runtime.Persistence;

public sealed class FileSubagentCatalog : ISubagentCatalog
{
    private readonly string _directoryPath;
    private readonly string _defaultModel;
    private readonly IDeserializer _deserializer;

    public FileSubagentCatalog(string storageRoot, string? defaultModel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);

        _directoryPath = Path.Combine(Path.GetFullPath(storageRoot), "subagents");
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel)
            ? "OpenAI/gpt-5.6-luna"
            : defaultModel;
        _deserializer = new StaticDeserializerBuilder(new SubagentYamlContext())
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .WithMaximumRecursion(16)
            .Build();
    }

    private static readonly (string FileName, string Content)[] DefaultSubagents =
    [
        ("reviewer.md", """
            ---
            name: reviewer
            description: Reviews code changes only when the user explicitly requests a review. Invoke at most once per user turn and never in consecutive calls.
            model: OpenAI/gpt-5.6-luna:high
            tools:
              - read
              - grep
              - find
            ---

            You are a code review specialist.
            Use grep for content searches and find for locating files.

            Run only when the user explicitly requested a review. The caller must invoke you at most once per user turn and must not make consecutive reviewer calls.

            Inspect the changes and the surrounding code without modifying files. Prioritize concrete defects and regression risks over stylistic preferences.

            For every finding, provide:

            - Severity
            - File and location
            - Evidence and impact
            - A focused recommendation

            If no actionable issue is found, say so explicitly and identify any remaining verification gap.
            """),
        ("scanner.md", """
            ---
            name: scanner
            description: Explores the codebase to find relevant files, existing patterns, dependencies, and likely change points.
            model: OpenAI/gpt-5.6-luna:medium
            tools:
              - read
              - grep
              - find
            ---

            You are a codebase exploration specialist.
            Use grep for content searches and find for locating files.

            Investigate the user's request without modifying files. Find the relevant code, trace how it works, and identify the smallest set of change points.

            Report:

            - Relevant files and their roles
            - Current behavior and data flow
            - Existing conventions that the implementation should follow
            - Risks, ambiguities, and likely verification steps
            """),
    ];

    public async Task<IReadOnlyList<SubagentDefinition>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directoryPath))
        {
            Directory.CreateDirectory(_directoryPath);
            foreach (var (fileName, content) in DefaultSubagents)
            {
                var filePath = Path.Combine(_directoryPath, fileName);
                await File.WriteAllTextAsync(
                    filePath,
                    ConfigureDefaultContent(content),
                    cancellationToken);
            }
        }

        var definitions = new List<SubagentDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_directoryPath, "*.md")
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var defaultTemplate = DefaultSubagents.FirstOrDefault(item => string.Equals(
                item.FileName,
                Path.GetFileName(path),
                StringComparison.Ordinal));
            if (defaultTemplate != default)
            {
                var previousPolicyContent = GetPreviousReviewerPolicyContent(defaultTemplate.Content);
                var previousToolsContent = GetPreviousDefaultToolsContent(previousPolicyContent);
                var legacyContent = GetLegacyDefaultContent(previousToolsContent);
                var knownDefaults = new[]
                {
                    defaultTemplate.Content,
                    previousPolicyContent,
                    previousToolsContent,
                    legacyContent,
                };
                if (knownDefaults.Any(defaultContent =>
                        ContentEquals(content, defaultContent)
                        || ContentEquals(content, ConfigureDefaultContent(defaultContent))))
                {
                    content = ConfigureDefaultContent(defaultTemplate.Content);
                    await File.WriteAllTextAsync(path, content, cancellationToken);
                }
            }
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

    private string ConfigureDefaultContent(string content) => content.Replace(
        "model: OpenAI/gpt-5.6-luna:",
        $"model: {_defaultModel}:",
        StringComparison.Ordinal);

    private static string GetPreviousReviewerPolicyContent(string content) => content
        .ReplaceLineEndings("\n")
        .Replace(
            "description: Reviews code changes only when the user explicitly requests a review. Invoke at most once per user turn and never in consecutive calls.",
            "description: Reviews code changes for correctness, regressions, missing validation, and maintainability risks.",
            StringComparison.Ordinal)
        .Replace(
            "\nRun only when the user explicitly requested a review. The caller must invoke you at most once per user turn and must not make consecutive reviewer calls.\n",
            string.Empty,
            StringComparison.Ordinal);

    private static string GetPreviousDefaultToolsContent(string content) => content
        .Replace("  - find\n", "  - find\n  - bash\n", StringComparison.Ordinal)
        .Replace(
            "\nUse grep for content searches and find for locating files.",
            "\nUse grep for content searches and find for locating files; do not use bash for those tasks.",
            StringComparison.Ordinal);

    private static string GetLegacyDefaultContent(string content) => content
        .Replace("  - grep\n  - find\n", string.Empty, StringComparison.Ordinal)
        .Replace(
            "\nUse grep for content searches and find for locating files; do not use bash for those tasks.",
            string.Empty,
            StringComparison.Ordinal);

    private static bool ContentEquals(string left, string right) => string.Equals(
        left.ReplaceLineEndings("\n"),
        right.ReplaceLineEndings("\n"),
        StringComparison.Ordinal);

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
}

[YamlStaticContext]
[YamlSerializable(typeof(SubagentDocument))]
public partial class SubagentYamlContext : StaticContext
{
}
