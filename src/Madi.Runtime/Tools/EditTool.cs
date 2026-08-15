using System.Text;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

public sealed record TextEdit(string OldText, string NewText);

public sealed record EditResult(string Message, string Diff, string Patch, int? FirstChangedLine);

/// <summary>
/// Applies one or more unique, non-overlapping text replacements to one file.
/// All matches are located in the original content before any replacement is applied.
/// </summary>
public sealed class EditTool : ITool
{
    private static readonly JsonElement Schema = ToolSupport.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the file to edit (relative or absolute)" },
            "edits": {
              "type": "array",
              "description": "Unique, non-overlapping replacements matched against the original file",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "oldText": { "type": "string", "description": "Exact text to replace" },
                  "newText": { "type": "string", "description": "Replacement text" }
                },
                "required": ["oldText", "newText"],
                "additionalProperties": false
              }
            }
          },
          "required": ["path", "edits"],
          "additionalProperties": false
        }
        """);

    private readonly string _workingDirectory;

    public EditTool(string? workingDirectory = null)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
    }

    public string Name => "edit";

    public string Description =>
        "Edit one file using one or more unique, non-overlapping exact text replacements.";

    public JsonElement Parameters => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var path = ToolSupport.GetRequiredString(arguments, "path");
        ToolSupport.ValidateArguments(arguments);
        if (arguments.TryGetProperty("edits", out var editsElement) && editsElement.ValueKind == JsonValueKind.Array)
        {
            var edits = editsElement.EnumerateArray()
                .Select(edit => new TextEdit(
                    ToolSupport.GetRequiredString(edit, "oldText"),
                    ToolSupport.GetRequiredString(edit, "newText")))
                .ToArray();
            return ExecuteAsync(path, edits, cancellationToken);
        }

        // Accept pi's legacy single-replacement shape as well.
        if (arguments.TryGetProperty("oldText", out _) && arguments.TryGetProperty("newText", out _))
        {
            return ExecuteAsync(
                path,
                ToolSupport.GetRequiredString(arguments, "oldText"),
                ToolSupport.GetRequiredString(arguments, "newText"),
                cancellationToken);
        }

        throw new ArgumentException("Tool argument 'edits' must be an array with at least one replacement.");
    }

    public async Task<string> ExecuteAsync(
        string path,
        IReadOnlyList<TextEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteDetailedAsync(path, edits, cancellationToken);
        return result.Message;
    }

    public Task<string> ExecuteAsync(
        string path,
        string oldText,
        string newText,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(path, [new TextEdit(oldText, newText)], cancellationToken);

    public Task<EditResult> ExecuteDetailedAsync(
        string path,
        IReadOnlyList<TextEdit> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("Edits must contain at least one replacement.", nameof(edits));
        }

        var absolutePath = ToolSupport.ResolvePath(path, _workingDirectory);
        return ToolSupport.WithMutationLockAsync(
            absolutePath,
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(absolutePath))
                {
                    throw new FileNotFoundException($"Could not edit file: {path}.", absolutePath);
                }

                var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
                var rawContent = Encoding.UTF8.GetString(bytes);
                var hasBom = rawContent.StartsWith('\uFEFF');
                var content = hasBom ? rawContent[1..] : rawContent;
                var lineEnding = DetectLineEnding(content);
                var normalizedContent = ToolSupport.NormalizeLineEndings(content);
                var normalizedEdits = edits.Select(edit =>
                {
                    ArgumentNullException.ThrowIfNull(edit);
                    ArgumentNullException.ThrowIfNull(edit.OldText);
                    ArgumentNullException.ThrowIfNull(edit.NewText);
                    return new TextEdit(
                        ToolSupport.NormalizeLineEndings(edit.OldText),
                        ToolSupport.NormalizeLineEndings(edit.NewText));
                }).ToArray();

                var matches = FindMatches(normalizedContent, normalizedEdits, path);
                var newContent = normalizedContent;
                foreach (var match in matches.OrderByDescending(static match => match.Index))
                {
                    newContent = string.Concat(
                        newContent.AsSpan(0, match.Index),
                        match.Edit.NewText,
                        newContent.AsSpan(match.Index + match.Edit.OldText.Length));
                }

                if (newContent == normalizedContent)
                {
                    throw new InvalidOperationException(
                        $"No changes made to {path}. The replacements produced identical content.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var finalContent = (hasBom ? "\uFEFF" : string.Empty) +
                                   ToolSupport.RestoreLineEndings(newContent, lineEnding);
                await File.WriteAllTextAsync(
                    absolutePath,
                    finalContent,
                    new UTF8Encoding(false),
                    cancellationToken);

                var firstChangedLine = GetFirstChangedLine(normalizedContent, matches);
                var diff = CreateDisplayDiff(normalizedContent, newContent);
                var patch = CreatePatch(path, normalizedContent, newContent);
                return new EditResult(
                    $"Successfully replaced {edits.Count} block(s) in {path}.",
                    diff,
                    patch,
                    firstChangedLine);
            },
            cancellationToken);
    }

    private static Match[] FindMatches(string content, IReadOnlyList<TextEdit> edits, string path)
    {
        var matches = new Match[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (edit.OldText.Length == 0)
            {
                throw new ArgumentException(
                    edits.Count == 1
                        ? $"oldText must not be empty in {path}."
                        : $"edits[{i}].oldText must not be empty in {path}.");
            }

            var index = content.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    edits.Count == 1
                        ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
                        : $"Could not find edits[{i}] in {path}. The oldText must match exactly including all whitespace and newlines.");
            }

            var second = content.IndexOf(edit.OldText, index + edit.OldText.Length, StringComparison.Ordinal);
            if (second >= 0)
            {
                throw new InvalidOperationException(
                    edits.Count == 1
                        ? $"Found multiple occurrences of the text in {path}. The text must be unique."
                        : $"Found multiple occurrences of edits[{i}] in {path}. Each oldText must be unique.");
            }
            matches[i] = new Match(i, index, edit);
        }

        var ordered = matches.OrderBy(static match => match.Index).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (previous.Index + previous.Edit.OldText.Length > current.Index)
            {
                throw new InvalidOperationException(
                    $"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. " +
                    "Merge them into one edit or target disjoint regions.");
            }
        }
        return matches;
    }

    private static string DetectLineEnding(string content)
    {
        var lineFeed = content.IndexOf('\n');
        return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
    }

    private static int GetFirstChangedLine(string content, IEnumerable<Match> matches)
    {
        var firstIndex = matches.Min(static match => match.Index);
        var line = 1;
        for (var i = 0; i < firstIndex; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private static string CreateDisplayDiff(string oldContent, string newContent)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var oldSuffix = oldLines.Length - 1;
        var newSuffix = newLines.Length - 1;
        while (oldSuffix >= prefix && newSuffix >= prefix && oldLines[oldSuffix] == newLines[newSuffix])
        {
            oldSuffix--;
            newSuffix--;
        }

        var output = new StringBuilder();
        foreach (var line in oldLines.Skip(prefix).Take(oldSuffix - prefix + 1))
        {
            output.Append("- ").AppendLine(line);
        }
        foreach (var line in newLines.Skip(prefix).Take(newSuffix - prefix + 1))
        {
            output.Append("+ ").AppendLine(line);
        }
        return output.ToString().TrimEnd();
    }

    private static string CreatePatch(string path, string oldContent, string newContent)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var patch = new StringBuilder()
            .Append("--- a/").AppendLine(path)
            .Append("+++ b/").AppendLine(path)
            .Append("@@ -1,").Append(oldLines.Length).Append(" +1,").Append(newLines.Length).AppendLine(" @@");
        foreach (var line in oldLines)
        {
            patch.Append('-').AppendLine(line);
        }
        foreach (var line in newLines)
        {
            patch.Append('+').AppendLine(line);
        }
        return patch.ToString();
    }

    private sealed record Match(int EditIndex, int Index, TextEdit Edit);
}
