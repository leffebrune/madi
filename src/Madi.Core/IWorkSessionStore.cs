namespace Madi.Core;

public sealed record CurrentConversation(Work? Work, Session Session);

public interface IWorkSessionStore
{
    Task<IReadOnlyList<Work>> LoadWorksAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Session>> LoadSessionsAsync(
        CancellationToken cancellationToken = default);

    Task<CurrentConversation?> LoadCurrentAsync(
        CancellationToken cancellationToken = default);

    Task<CurrentConversation?> LoadConversationAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SaveWorkAsync(
        Work work,
        CancellationToken cancellationToken = default);

    Task SaveSessionAsync(
        Session session,
        CancellationToken cancellationToken = default);

    Task AppendConversationAsync(
        Session session,
        int startIndex,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SetCurrentAsync(
        Session session,
        CancellationToken cancellationToken = default);
}
