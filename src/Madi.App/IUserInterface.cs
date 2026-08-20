namespace Madi.App;

public interface IUserInterface
{
    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    void Write(string message);

    void WriteLine(string message);
}
