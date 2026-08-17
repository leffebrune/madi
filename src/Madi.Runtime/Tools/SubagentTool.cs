using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

public sealed class SubagentTool : ISubagentTool
{
    private readonly ISubagentRunner _runner;

    public SubagentTool(
        ISubagentRunner runner,
        IReadOnlyList<SubagentDefinition> definitions)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(definitions);
        Parameters = CreateSchema(definitions);
        Description = CreateDescription(definitions);
    }

    public string Name => "subagent";

    public string Description { get; }

    public JsonElement Parameters { get; }

    public bool SupportsParallelExecution => true;

    public string GetSubagentName(JsonElement arguments) =>
        ToolSupport.GetRequiredString(arguments, "name");

    public Task<string> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(
            GetSubagentName(arguments),
            ToolSupport.GetRequiredString(arguments, "task"),
            cancellationToken);

    private static string CreateDescription(IReadOnlyList<SubagentDefinition> definitions)
    {
        var lines = definitions.Select(
            static definition => $"- {definition.Name}: {definition.Description}");
        return "Delegate an independent task to a specialized subagent. " +
               "Use multiple calls in one response when the tasks can run independently.\n\n" +
               "Available subagents:\n" + string.Join('\n', lines);
    }

    private static JsonElement CreateSchema(IReadOnlyList<SubagentDefinition> definitions)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteStartObject("name");
            writer.WriteString("type", "string");
            writer.WriteString("description", "Subagent identifier");
            writer.WriteStartArray("enum");
            foreach (var definition in definitions)
            {
                writer.WriteStringValue(definition.Name);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("task");
            writer.WriteString("type", "string");
            writer.WriteString("description", "A complete, self-contained task for the subagent");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteStringValue("name");
            writer.WriteStringValue("task");
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }
}
