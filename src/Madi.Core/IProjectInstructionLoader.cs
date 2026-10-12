namespace Madi.Core;

public interface IProjectInstructionLoader
{
    Task<ProjectInstructions?> LoadAsync(CancellationToken cancellationToken = default);
}
