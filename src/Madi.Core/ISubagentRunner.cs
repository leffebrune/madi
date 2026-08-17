namespace Madi.Core;

public interface ISubagentRunner
{
    Task<string> RunAsync(
        string name,
        string task,
        CancellationToken cancellationToken = default);
}
