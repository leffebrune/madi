namespace Madi.Core;

public interface ISystemPromptCatalog
{
    Task<string> LoadAsync(CancellationToken cancellationToken = default);
}
