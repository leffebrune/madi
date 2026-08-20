using System.Text;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Persistence;

public sealed class FileWorkSessionStore : IWorkSessionStore
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly string _rootPath;
    private readonly string _worksPath;
    private readonly string _sessionsPath;
    private readonly string _currentPath;

    public FileWorkSessionStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = Path.GetFullPath(rootPath);
        _worksPath = Path.Combine(_rootPath, "works");
        _sessionsPath = Path.Combine(_rootPath, "sessions");
        _currentPath = Path.Combine(_rootPath, "current.json");
    }

    public async Task<IReadOnlyList<Work>> LoadWorksAsync(
        CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var works = await LoadAllWorksUnlockedAsync(cancellationToken);
            var sessions = await LoadAllSessionsUnlockedAsync(cancellationToken);
            var worksById = works.ToDictionary(static work => work.Id, StringComparer.Ordinal);

            foreach (var session in sessions)
            {
                if (session.WorkId is not null
                    && worksById.TryGetValue(session.WorkId, out var work))
                {
                    work.Sessions.Add(session);
                }
            }

            return works;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<Session>> LoadSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadAllSessionsUnlockedAsync(cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<CurrentConversation?> LoadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_currentPath))
            {
                return null;
            }

            var current = await ReadJsonAsync(
                _currentPath,
                PersistenceJsonContext.Relaxed.CurrentDocument,
                cancellationToken);

            var session = await LoadSessionUnlockedAsync(
                current.SessionId,
                cancellationToken);
            if (session is null)
            {
                return null;
            }

            Work? work = null;
            if (session.WorkId is not null)
            {
                work = await LoadWorkUnlockedAsync(session.WorkId, cancellationToken)
                    ?? throw new InvalidDataException(
                        $"Session '{session.Id}' references missing work '{session.WorkId}'.");
                work.Sessions.Add(session);
            }

            return new CurrentConversation(work, session);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveWorkAsync(
        Work work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ValidateId(work.Id, nameof(work));

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var path = GetWorkPath(work.Id);
            await WriteJsonAtomicallyAsync(
                path,
                WorkDocument.FromDomain(work),
                PersistenceJsonContext.Relaxed.WorkDocument,
                cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveSessionAsync(
        Session session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSession(session);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            if (session.WorkId is not null && !File.Exists(GetWorkPath(session.WorkId)))
            {
                throw new InvalidDataException(
                    $"Session '{session.Id}' references missing work '{session.WorkId}'.");
            }

            var path = GetSessionMetadataPath(session.Id);
            await WriteJsonAtomicallyAsync(
                path,
                SessionDocument.FromDomain(session),
                PersistenceJsonContext.Relaxed.SessionDocument,
                cancellationToken);

            var conversationPath = GetConversationPath(session.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(conversationPath)!);
            if (!File.Exists(conversationPath))
            {
                await File.WriteAllTextAsync(
                    conversationPath,
                    string.Empty,
                    Utf8WithoutBom,
                    cancellationToken);
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AppendConversationAsync(
        Session session,
        int startIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSession(session);

        if (startIndex < 0 || startIndex > session.Conversation.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (startIndex == session.Conversation.Count)
        {
            return;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var path = GetConversationPath(session.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var storedCount = await CountConversationItemsUnlockedAsync(path, cancellationToken);
            if (storedCount != startIndex)
            {
                throw new InvalidDataException(
                    $"Session '{session.Id}' has {storedCount} stored conversation items, " +
                    $"but append was requested from index {startIndex}.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream, Utf8WithoutBom);

            for (var index = startIndex; index < session.Conversation.Count; index++)
            {
                var document = ConversationItemDocument.FromDomain(
                    index,
                    session.Conversation[index]);
                var json = JsonSerializer.Serialize(
                    document,
                    PersistenceJsonContext.Relaxed.ConversationItemDocument);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            }

            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SetCurrentAsync(
        Session session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateId(session.Id, nameof(session));

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            await WriteJsonAtomicallyAsync(
                _currentPath,
                new CurrentDocument(session.Id),
                PersistenceJsonContext.Relaxed.CurrentDocument,
                cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<Work>> LoadAllWorksUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_worksPath))
        {
            return [];
        }

        var works = new List<Work>();
        foreach (var path in Directory.EnumerateFiles(_worksPath, "*.json"))
        {
            var document = await ReadJsonAsync(
                path,
                PersistenceJsonContext.Relaxed.WorkDocument,
                cancellationToken);
            works.Add(document.ToDomain());
        }

        return works;
    }

    private async Task<List<Session>> LoadAllSessionsUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsPath))
        {
            return [];
        }

        var sessions = new List<Session>();
        foreach (var directory in Directory.EnumerateDirectories(_sessionsPath))
        {
            var sessionId = Path.GetFileName(directory);
            var session = await LoadSessionUnlockedAsync(sessionId, cancellationToken);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    private async Task<Work?> LoadWorkUnlockedAsync(
        string workId,
        CancellationToken cancellationToken)
    {
        ValidateId(workId, nameof(workId));
        var path = GetWorkPath(workId);
        if (!File.Exists(path))
        {
            return null;
        }

        var document = await ReadJsonAsync(
            path,
            PersistenceJsonContext.Relaxed.WorkDocument,
            cancellationToken);
        return document.ToDomain();
    }

    private async Task<Session?> LoadSessionUnlockedAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ValidateId(sessionId, nameof(sessionId));
        var metadataPath = GetSessionMetadataPath(sessionId);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var document = await ReadJsonAsync(
            metadataPath,
            PersistenceJsonContext.Relaxed.SessionDocument,
            cancellationToken);
        var session = document.ToDomain();

        var conversationPath = GetConversationPath(sessionId);
        if (!File.Exists(conversationPath))
        {
            return session;
        }

        using var stream = new FileStream(
            conversationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8WithoutBom);

        var line = await ReadNextNonEmptyLineAsync(reader, cancellationToken);
        while (line is not null)
        {
            var nextLine = await ReadNextNonEmptyLineAsync(reader, cancellationToken);

            ConversationItemDocument item;
            try
            {
                item = JsonSerializer.Deserialize(
                    line,
                    PersistenceJsonContext.Relaxed.ConversationItemDocument)
                    ?? throw new InvalidDataException(
                        $"Conversation item in '{conversationPath}' is null.");
            }
            catch (JsonException) when (nextLine is null)
            {
                // A process may have stopped while appending the last record.
                break;
            }

            if (item.Sequence != session.Conversation.Count)
            {
                throw new InvalidDataException(
                    $"Conversation sequence mismatch in session '{session.Id}': " +
                    $"expected {session.Conversation.Count}, found {item.Sequence}.");
            }

            session.Conversation.Add(item.ToDomain());
            line = nextLine;
        }

        return session;
    }

    private static async Task<string?> ReadNextNonEmptyLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private static async Task<int> CountConversationItemsUnlockedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var count = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8WithoutBom);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken)
            ?? throw new InvalidDataException($"JSON document '{path}' is empty.");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Storage path must include a directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    typeInfo,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetWorkPath(string workId) =>
        Path.Combine(_worksPath, $"{workId}.json");

    private string GetSessionMetadataPath(string sessionId) =>
        Path.Combine(_sessionsPath, sessionId, "session.json");

    private string GetConversationPath(string sessionId) =>
        Path.Combine(_sessionsPath, sessionId, "conversation.jsonl");

    private static void ValidateSession(Session session)
    {
        ValidateId(session.Id, nameof(session));
        if (session.WorkId is not null)
        {
            ValidateId(session.WorkId, nameof(session.WorkId));
        }

        if (session.MaxToolRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(session),
                "Maximum tool rounds must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(session.Instructions))
        {
            throw new ArgumentException("Session instructions are required.", nameof(session));
        }
    }

    private static void ValidateId(string id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("A valid storage ID is required.", parameterName);
        }
    }
}
