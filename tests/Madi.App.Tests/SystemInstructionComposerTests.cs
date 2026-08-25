using System.Text.Json;
using Madi.Core;

namespace Madi.App.Tests;

public sealed class SystemInstructionComposerTests
{
    [Fact]
    public void ComposeIncludesSystemMarkdownAndExactToolMetadata()
    {
        var tool = new TestTool(
            "subagent",
            "Delegate work.\n\nAvailable subagents:\n- reviewer: Reviews code",
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "enum": ["reviewer"] }
              },
              "required": ["name"]
            }
            """);

        var result = SystemInstructionComposer.Compose("  You are Madi.  ", [tool]);

        var normalized = result.ReplaceLineEndings("\n");
        Assert.StartsWith("You are Madi.\n\n# Available tools", normalized);
        Assert.Contains("## subagent", normalized);
        Assert.Contains("Available subagents:\n- reviewer: Reviews code", normalized);
        Assert.Contains("\"enum\": [\n        \"reviewer\"", normalized);
    }

    [Fact]
    public void ComposeWithoutToolsReturnsTrimmedSystemMarkdown()
    {
        Assert.Equal(
            "You are Madi.",
            SystemInstructionComposer.Compose("\nYou are Madi.\n", []));
    }

    private sealed class TestTool : ITool
    {
        public TestTool(string name, string description, string schema)
        {
            Name = name;
            Description = description;
            using var document = JsonDocument.Parse(schema);
            Parameters = document.RootElement.Clone();
        }

        public string Name { get; }
        public string Description { get; }
        public JsonElement Parameters { get; }

        public Task<string> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }
}
