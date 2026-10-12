using Madi.Core;

namespace Madi.Runtime.Agents;

internal sealed class FixedProjectInstructionLoader(ProjectInstructions? instructions) : IProjectInstructionLoader
{
    public Task<ProjectInstructions?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(instructions);
    }
}
