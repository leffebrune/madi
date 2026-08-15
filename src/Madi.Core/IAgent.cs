namespace Madi.Core;

/// <summary>
/// Stable entry point for interacting with an agent, independent of UI and authentication.
/// </summary>
public interface IAgent
{
    IAsyncEnumerable<AgentEvent> RespondAsync(
        Work? work,
        Session session,
        string prompt,
        CancellationToken cancellationToken = default);
}
