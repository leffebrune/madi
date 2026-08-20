using Madi.App;

namespace Madi.UI;

/// <summary>
/// Temporary console-based UI implementation.
/// </summary>
public sealed class ConsoleUserInterface : IUserInterface
{
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        Console.In.ReadLineAsync(cancellationToken).AsTask();

    public void Write(string message) => Console.Write(message);

    public void WriteLine(string message) => Console.WriteLine(message);
}
