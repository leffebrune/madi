using System.Text.Json;

namespace Madi.Core;

/// <summary>A single coding tool that can be invoked with structured arguments.</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the arguments accepted by this tool.</summary>
    JsonElement Parameters { get; }

    /// <summary>
    /// Whether multiple calls to this tool from one model response may execute concurrently.
    /// </summary>
    bool SupportsParallelExecution => false;

    Task<string> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>A tool that delegates work to a named subagent.</summary>
public interface ISubagentTool : ITool
{
    string GetSubagentName(JsonElement arguments);

    string GetSubagentPrompt(JsonElement arguments);

    Task<string> ExecuteAsync(
        JsonElement arguments,
        Func<SubagentRunEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default);
    Task<string> ExecuteAsync(
        JsonElement arguments,
        ProjectInstructions? projectInstructions,
        Func<SubagentRunEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default);
}
