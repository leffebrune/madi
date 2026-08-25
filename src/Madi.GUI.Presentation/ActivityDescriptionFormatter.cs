using System.Text.Json;

namespace Madi.GUI.Presentation;

public interface IActivityDescriptionFormatter
{
    string FormatTool(string name, string arguments);
    string FormatSubagent(string name, string prompt);
}

/// <summary>
/// Creates the description shown in a tool or subagent activity header.
/// Add or replace entries in <see cref="ToolFormatters"/> to customize individual tools.
/// Returned descriptions may contain line breaks and are never truncated by the formatter.
/// </summary>
public sealed class ActivityDescriptionFormatter : IActivityDescriptionFormatter
{
    public ActivityDescriptionFormatter()
    {
        ToolFormatters = new Dictionary<string, Func<JsonElement, string>>(StringComparer.Ordinal)
        {
            ["bash"] = static arguments => GetString(arguments, "command"),
            ["read"] = static arguments => FormatRead(arguments),
            ["write"] = static arguments => GetString(arguments, "path"),
            ["edit"] = static arguments => FormatEdit(arguments),
        };
    }

    public IDictionary<string, Func<JsonElement, string>> ToolFormatters { get; }

    public string FormatTool(string name, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && ToolFormatters.TryGetValue(name, out var formatter)
                    ? formatter(document.RootElement)
                    : arguments;
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    public string FormatSubagent(string name, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    private static string FormatRead(JsonElement arguments)
    {
        var path = GetString(arguments, "path");
        var range = new List<string>();
        if (TryGetInt32(arguments, "offset", out var offset))
        {
            range.Add($"offset {offset}");
        }
        if (TryGetInt32(arguments, "limit", out var limit))
        {
            range.Add($"limit {limit}");
        }
        return range.Count == 0
            ? path
            : $"{path}{Environment.NewLine}{string.Join(" · ", range)}";
    }

    private static string FormatEdit(JsonElement arguments)
    {
        var path = GetString(arguments, "path");
        if (arguments.TryGetProperty("edits", out var edits)
            && edits.ValueKind == JsonValueKind.Array)
        {
            return $"{path}{Environment.NewLine}{edits.GetArrayLength()}개 변경";
        }
        return path;
    }

    private static string GetString(JsonElement arguments, string propertyName) =>
        arguments.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetInt32(JsonElement arguments, string propertyName, out int value)
    {
        value = default;
        return arguments.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out value);
    }
}
