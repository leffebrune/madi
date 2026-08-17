namespace Madi.Core;

public interface ISubagentCatalog
{
    Task<IReadOnlyList<SubagentDefinition>> LoadAsync(
        CancellationToken cancellationToken = default);
}
