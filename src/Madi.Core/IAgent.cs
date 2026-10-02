namespace Madi.Core;

/// <summary>
/// Stable entry point for interacting with an agent, independent of UI and authentication.
/// </summary>
public interface IAgent
{
    /// <summary>The system instruction used for every session handled by this agent.</summary>
    string SystemInstructions { get; set; }

    IAsyncEnumerable<AgentEvent> RespondAsync(
        Work? work,
        Session session,
        string prompt,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentEvent> RespondAsync(
        Work? work, Session session, string prompt, AgentInputQueue? inputs,
        CancellationToken cancellationToken = default) =>
        RespondAsync(work, session, prompt, cancellationToken);
}
