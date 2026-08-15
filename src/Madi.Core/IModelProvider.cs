namespace Madi.Core;

/// <summary>
/// Abstraction for the model backend used by an agent.
/// </summary>
public interface IModelProvider
{
    string Id { get; }

    IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
