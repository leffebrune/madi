using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;

[assembly: InternalsVisibleTo("Madi.Runtime.Tests")]

namespace Madi.Runtime.Persistence;

public sealed partial class FileWorkSessionStore
{
    internal Action<string>? TurnCheckpoint { get; set; }

    public async Task CommitTurnAsync(Session session, int startIndex, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        if (startIndex < 0 || startIndex > session.Conversation.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            await RecoverTurnUnlockedAsync(session.Id);
            var stored = await LoadSessionUnlockedAsync(session.Id, true, cancellationToken)
                ?? throw new InvalidDataException("Save the session before committing a turn.");
            if (stored.Conversation.Count == session.Conversation.Count && startIndex < session.Conversation.Count)
            {
                if (Enumerable.Range(0, session.Conversation.Count).All(i =>
                    SerializeItem(stored, i) == SerializeItem(session, i))) return; // Exact retry, already committed.
            }
            if (stored.Conversation.Count != startIndex)
                throw new InvalidDataException($"Turn boundary mismatch: stored {stored.Conversation.Count}, expected {startIndex}.");
            for (var i = 0; i < startIndex; i++)
                if (SerializeItem(stored, i) != SerializeItem(session, i))
                    throw new InvalidDataException("The saved conversation changed. Reload before retrying.");

            var conversationPath = GetConversationPath(session.Id);
            var metadataPath = GetSessionMetadataPath(session.Id);
            var pendingPath = Path.Combine(Path.GetDirectoryName(metadataPath)!, "turn.pending");
            var backupPath = metadataPath + ".turn-backup";
            // Flush the undo data and marker before the first append. Recovery always rolls back
            // an incomplete transaction, including one interrupted after metadata replacement.
            await WriteDurableAsync(backupPath, await File.ReadAllBytesAsync(metadataPath, cancellationToken));
            var length = new FileInfo(conversationPath).Length;
            await WriteDurableAsync(pendingPath + ".tmp", Utf8WithoutBom.GetBytes(length.ToString(CultureInfo.InvariantCulture)));
            File.Move(pendingPath + ".tmp", pendingPath, true);
            try
            {
                TurnCheckpoint?.Invoke("before-append");
                cancellationToken.ThrowIfCancellationRequested();
                await using (var stream = new FileStream(conversationPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    for (var i = startIndex; i < session.Conversation.Count; i++)
                    {
                        var bytes = Utf8WithoutBom.GetBytes(SerializeItem(session, i) + "\n");
                        await stream.WriteAsync(bytes, cancellationToken);
                        TurnCheckpoint?.Invoke("append-item");
                    }
                    stream.Flush(flushToDisk: true);
                }
                TurnCheckpoint?.Invoke("after-append");
                await WriteJsonAtomicallyAsync(metadataPath, SessionDocument.FromDomain(session),
                    PersistenceJsonContext.Relaxed.SessionDocument, cancellationToken);
                TurnCheckpoint?.Invoke("after-metadata");
                // Once the marker is removed, success is durable and cancellation cannot undo it.
                File.Delete(pendingPath);
            }
            catch
            {
                await RecoverTurnUnlockedAsync(session.Id);
                throw;
            }
            // A leftover backup is harmless; it is never a recovery marker.
        }
        finally { FileLock.Release(); }
    }

    private static string SerializeItem(Session session, int index) => JsonSerializer.Serialize(
        ConversationItemDocument.FromDomain(index, session.Conversation[index]),
        PersistenceJsonContext.Relaxed.ConversationItemDocument);

    private static async Task WriteDurableAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes);
        stream.Flush(flushToDisk: true);
    }

    private async Task RecoverTurnUnlockedAsync(string id)
    {
        var metadata = GetSessionMetadataPath(id);
        var pending = Path.Combine(Path.GetDirectoryName(metadata)!, "turn.pending");
        if (!File.Exists(pending)) return;
        try
        {
            var length = long.Parse(await File.ReadAllTextAsync(pending), CultureInfo.InvariantCulture);
            var backup = metadata + ".turn-backup";
            if (!File.Exists(backup)) throw new InvalidDataException("Missing turn metadata backup.");
            await using (var stream = new FileStream(GetConversationPath(id), FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (length < 0 || stream.Length < length) throw new InvalidDataException("Invalid turn recovery boundary.");
                stream.SetLength(length); stream.Flush(flushToDisk: true);
            }
            await WriteDurableAsync(metadata + ".recover", await File.ReadAllBytesAsync(backup));
            File.Move(metadata + ".recover", metadata, true);
            File.Delete(pending);
        }
        catch (Exception error)
        {
            throw new InvalidDataException($"Session '{id}' requires turn recovery; data was not accepted as complete.", error);
        }
    }
}
