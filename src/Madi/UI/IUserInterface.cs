namespace Madi.UI;

/// <summary>
/// Output boundary used by the application independently of a concrete UI framework.
/// </summary>
public interface IUserInterface
{
    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    void Write(string message);

    void WriteLine(string message);
}
