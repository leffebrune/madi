using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

/// <summary>Finds files and directories with fd.</summary>
public sealed class FindTool : ITool
{
    private static readonly JsonElement Schema = ToolSupport.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regex or literal name to find; use an empty string to list all entries" },
            "path": { "type": "string", "description": "Directory to search (default: working directory)" },
            "type": { "type": "string", "enum": ["file", "directory"], "description": "Restrict results to files or directories" },
            "extension": { "type": "string", "description": "Restrict results to a file extension, without the leading dot" },
            "case_sensitive": { "type": "boolean", "description": "true for case-sensitive, false for case-insensitive; omitted uses smart case" },
            "literal": { "type": "boolean", "description": "Treat pattern as literal text instead of a regex (default: false)" },
            "hidden": { "type": "boolean", "description": "Include hidden files and directories (default: false)" }
          },
          "required": ["pattern"],
          "additionalProperties": false
        }
        """);

    private readonly string _workingDirectory;
    private readonly string? _utilityBinDirectory;
    private readonly string? _executablePath;

    public FindTool(
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

    public string Name => "find";

    public string Description =>
        "Find files and directories by name with fd. Prefer this tool over bash for locating files. " +
        "Respects ignore files and excludes hidden entries by default.";

    public JsonElement Parameters => Schema;

    public bool SupportsParallelExecution => true;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ToolSupport.GetRequiredString(arguments, "pattern"),
            GetOptionalString(arguments, "path"),
            GetOptionalString(arguments, "type"),
            GetOptionalString(arguments, "extension"),
            GetOptionalBoolean(arguments, "case_sensitive"),
            GetOptionalBoolean(arguments, "literal") ?? false,
            GetOptionalBoolean(arguments, "hidden") ?? false,
            cancellationToken);

    public async Task<string> ExecuteAsync(
        string pattern,
        string? path = null,
        string? type = null,
        string? extension = null,
        bool? caseSensitive = null,
        bool literal = false,
        bool hidden = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var searchPath = ToolSupport.ResolvePath(path ?? ".", _workingDirectory);
        if (!Directory.Exists(searchPath))
        {
            throw new DirectoryNotFoundException($"Search directory not found: {searchPath}");
        }
        if (type is not null && type is not ("file" or "directory"))
        {
            throw new ArgumentException("Type must be 'file' or 'directory'.", nameof(type));
        }
        if (extension?.StartsWith('.') == true)
        {
            throw new ArgumentException("Extension must not include a leading dot.", nameof(extension));
        }

        var arguments = new List<string> { "--color", "never" };
        if (type is not null)
        {
            arguments.Add("--type");
            arguments.Add(type == "file" ? "f" : "d");
        }
        if (!string.IsNullOrWhiteSpace(extension))
        {
            arguments.Add("--extension");
            arguments.Add(extension);
        }
        if (caseSensitive is true)
        {
            arguments.Add("--case-sensitive");
        }
        else if (caseSensitive is false)
        {
            arguments.Add("--ignore-case");
        }
        if (literal)
        {
            arguments.Add("--fixed-strings");
        }
        if (hidden)
        {
            arguments.Add("--hidden");
        }
        arguments.Add("--");
        arguments.Add(pattern);
        arguments.Add(searchPath);

        var executable = ExecutableResolver.Resolve("fd", _executablePath, _utilityBinDirectory);
        var result = await SearchProcessRunner.RunAsync(
            executable, arguments, _workingDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(SearchProcessRunner.FormatFailure("fd", result));
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
