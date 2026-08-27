using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

/// <summary>Searches file contents with ripgrep.</summary>
public sealed class GrepTool : ITool
{
    private static readonly JsonElement Schema = ToolSupport.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regex or literal text to search for" },
            "path": { "type": "string", "description": "File or directory to search (default: working directory)" },
            "glob": { "type": "string", "description": "Include or exclude files using an rg glob, for example '*.cs' or '!bin/**'" },
            "case_sensitive": { "type": "boolean", "description": "true for case-sensitive, false for case-insensitive; omitted uses smart case" },
            "literal": { "type": "boolean", "description": "Treat pattern as literal text instead of a regex (default: false)" },
            "hidden": { "type": "boolean", "description": "Search hidden files and directories (default: false)" }
          },
          "required": ["pattern"],
          "additionalProperties": false
        }
        """);

    private readonly string _workingDirectory;
    private readonly string? _utilityBinDirectory;
    private readonly string? _executablePath;

    public GrepTool(
        string? workingDirectory = null,
        string? utilityBinDirectory = null,
        string? executablePath = null)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        _utilityBinDirectory = string.IsNullOrWhiteSpace(utilityBinDirectory)
            ? null
            : Path.GetFullPath(utilityBinDirectory);
        _executablePath = executablePath;
    }

    public string Name => "grep";

    public string Description =>
        "Search file contents with ripgrep (rg). Prefer this tool over bash for content searches. " +
        "Respects ignore files and excludes hidden and binary files by default.";

    public JsonElement Parameters => Schema;

    public bool SupportsParallelExecution => true;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ToolSupport.GetRequiredString(arguments, "pattern"),
            GetOptionalString(arguments, "path"),
            GetOptionalString(arguments, "glob"),
            GetOptionalBoolean(arguments, "case_sensitive"),
            GetOptionalBoolean(arguments, "literal") ?? false,
            GetOptionalBoolean(arguments, "hidden") ?? false,
            cancellationToken);

    public async Task<string> ExecuteAsync(
        string pattern,
        string? path = null,
        string? glob = null,
        bool? caseSensitive = null,
        bool literal = false,
        bool hidden = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var searchPath = ToolSupport.ResolvePath(path ?? ".", _workingDirectory);
        if (!File.Exists(searchPath) && !Directory.Exists(searchPath))
        {
            throw new FileNotFoundException($"Search path not found: {searchPath}", searchPath);
        }

        var arguments = new List<string> { "--color", "never", "--line-number", "--with-filename" };
        arguments.Add(caseSensitive switch
        {
            true => "--case-sensitive",
            false => "--ignore-case",
            null => "--smart-case",
        });
        if (literal)
        {
            arguments.Add("--fixed-strings");
        }
        if (hidden)
        {
            arguments.Add("--hidden");
        }
        if (!string.IsNullOrWhiteSpace(glob))
        {
            arguments.Add("--glob");
            arguments.Add(glob);
        }
        arguments.Add("--");
        arguments.Add(pattern);
        arguments.Add(searchPath);

        var executable = ExecutableResolver.Resolve("rg", _executablePath, _utilityBinDirectory);
        var result = await SearchProcessRunner.RunAsync(
            executable, arguments, _workingDirectory, cancellationToken);
        if (result.ExitCode == 1)
        {
            return "(no matches)";
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(SearchProcessRunner.FormatFailure("rg", result));
        }

        return SearchProcessRunner.FormatOutput(result);
    }

    private static string? GetOptionalString(JsonElement arguments, string name)
    {
        ToolSupport.ValidateArguments(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{name}' must be a string.", nameof(arguments));
        }
        return value.GetString();
    }

    private static bool? GetOptionalBoolean(JsonElement arguments, string name)
    {
        ToolSupport.ValidateArguments(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"Tool argument '{name}' must be a boolean.", nameof(arguments));
        }
        return value.GetBoolean();
    }
}
