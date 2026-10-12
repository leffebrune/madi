namespace Madi.Core;

public abstract record SubagentRunEvent;

public sealed record SubagentToolStarted(
    string CallId,
    string Name,
    string Arguments) : SubagentRunEvent;

public sealed record SubagentToolCompleted(
    string CallId,
    string Name,
    bool IsError,
    TimeSpan ToolDuration) : SubagentRunEvent;

public interface ISubagentRunner
{
    Task<string> RunAsync(
        string name,
        string task,
        CancellationToken cancellationToken = default);

    Task<string> RunAsync(
        string name,
        string task,
        Func<SubagentRunEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default);
    Task<string> RunAsync(
        string name,
        string task,
        ProjectInstructions? projectInstructions,
        Func<SubagentRunEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default);
}
