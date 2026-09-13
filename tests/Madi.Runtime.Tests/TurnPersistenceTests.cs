using Madi.Core;
using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class TurnPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "madi-turn-tests-" + Guid.NewGuid());
    private Session Session() => new() { Id = "A", Title = "before", Model = new ModelOptions("test"), WorkingDirectory = _root };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("before-append")]
    [InlineData("append-item")]
    [InlineData("after-append")]
    [InlineData("after-metadata")]
    public async Task A19_FailureRollsBackBothFilesAndRetryDoesNotDuplicate(string stage)
    {
        var store = new FileWorkSessionStore(_root); var session = Session();
        await store.SaveSessionAsync(session);
        session.Title = "after";
        session.Conversation.AddRange([new UserMessage("one"), new AssistantMessage("two")]);
        store.TurnCheckpoint = checkpoint => { if (checkpoint == stage) throw new IOException("injected failure"); };
        await Assert.ThrowsAsync<IOException>(() => store.CommitTurnAsync(session, 0));
        var restored = (await new FileWorkSessionStore(_root).LoadConversationAsync("A"))!.Session;
        Assert.Empty(restored.Conversation); Assert.Equal("before", restored.Title);
        store.TurnCheckpoint = null;
        await store.CommitTurnAsync(session, 0);
        await store.CommitTurnAsync(session, 0);
        var saved = (await store.LoadConversationAsync("A"))!.Session;
        Assert.Equal(2, saved.Conversation.Count); Assert.Equal("after", saved.Title);
    }

    [Fact]
    public async Task A19_CancellationDuringAppendRestoresBoundary()
    {
        var store = new FileWorkSessionStore(_root); var session = Session();
        await store.SaveSessionAsync(session);
        session.Conversation.AddRange([new UserMessage("one"), new AssistantMessage("two")]);
        using var cancellation = new CancellationTokenSource();
        store.TurnCheckpoint = stage => { if (stage == "append-item") cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitTurnAsync(session, 0, cancellation.Token));
        Assert.Empty((await store.LoadConversationAsync("A"))!.Session.Conversation);
    }

    [Fact]
    public async Task A19_RestartRecoversPendingTransactionAndRejectsUnmarkedCorruptTail()
    {
        var store = new FileWorkSessionStore(_root); var session = Session(); await store.SaveSessionAsync(session);
        var folder = Path.Combine(_root, "sessions", "A");
        File.Copy(Path.Combine(folder, "session.json"), Path.Combine(folder, "session.json.turn-backup"));
        await File.WriteAllTextAsync(Path.Combine(folder, "turn.pending"), "0");
        await File.WriteAllTextAsync(Path.Combine(folder, "conversation.jsonl"), "{partial");
        Assert.Empty((await new FileWorkSessionStore(_root).LoadConversationAsync("A"))!.Session.Conversation);
        Assert.False(File.Exists(Path.Combine(folder, "turn.pending")));
        await File.WriteAllTextAsync(Path.Combine(folder, "conversation.jsonl"), "{unmarked-damage");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadConversationAsync("A"));
    }
}
