using System.Text.Json;
using Madi.Core;
using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class FileWorkSessionStoreTests
{
    [Fact]
    public async Task LegacyDocumentsLoadAndUpgradeOnNextSave()
    {
        using var directory = new TemporaryDirectory();
        var worksDirectory = Path.Combine(directory.Path, "works");
        var sessionDirectory = Path.Combine(directory.Path, "sessions", "session-1");
        Directory.CreateDirectory(worksDirectory);
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(worksDirectory, "work-1.json"),
            """{"id":"work-1","title":"기존 Work","context":""}""");
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "session.json"),
            """{"schemaVersion":1,"id":"session-1","title":"기존 대화","workId":"work-1","model":{"model":"first","reasoningEffort":"medium"},"instructions":"help"}""");
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "conversation.jsonl"),
            """
            {"sequence":0,"type":"model_tool_call","providerId":"test","callId":"call-1","name":"read","arguments":"{}","rawJson":"{}"}
            {"sequence":1,"type":"tool_result","callId":"call-1","output":"Error: failed"}
            """);
        var store = new FileWorkSessionStore(directory.Path);

        var loaded = await store.LoadConversationAsync("session-1");

        Assert.NotNull(loaded);
        Assert.Null(loaded.Session.WorkingDirectory);
        Assert.NotEqual(default, loaded.Session.CreatedAtUtc);
        Assert.True(Assert.IsType<ToolResult>(loaded.Session.Conversation[1]).IsError);

        loaded.Work!.WorkingDirectory = directory.Path;
        loaded.Session.WorkingDirectory = directory.Path;
        await store.SaveWorkAsync(loaded.Work);
        await store.SaveSessionAsync(loaded.Session);

        using var workJson = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(worksDirectory, "work-1.json")));
        using var sessionJson = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(sessionDirectory, "session.json")));
        Assert.Equal(2, workJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, sessionJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(sessionJson.RootElement.TryGetProperty("maxToolRounds", out _));
        Assert.False(sessionJson.RootElement.TryGetProperty("instructions", out _));
        Assert.Equal(directory.Path, sessionJson.RootElement.GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public async Task WorkspaceMetadataAndToolErrorRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var store = new FileWorkSessionStore(directory.Path);
        var work = new Work
        {
            Id = "work-1",
            Title = "작업",
            Context = "장기 목표와 제약",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new Session
        {
            Id = "session-1",
            Title = "대화",
            WorkId = work.Id,
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first", Provider: "provider-one"),
        };
        session.Conversation.Add(new ToolResult("call-1", "failed", IsError: true));

        await store.SaveWorkAsync(work);
        await store.SaveSessionAsync(session);
        await store.AppendConversationAsync(session, 0);
        var loaded = await store.LoadConversationAsync(session.Id);

        Assert.Equal(Path.GetFullPath(directory.Path), loaded?.Session.WorkingDirectory);
        Assert.Equal("장기 목표와 제약", loaded?.Work?.Context);
        Assert.Equal(now, loaded?.Session.CreatedAtUtc);
        Assert.Equal("provider-one", loaded?.Session.Model.Provider);
        Assert.True(Assert.IsType<ToolResult>(Assert.Single(loaded!.Session.Conversation)).IsError);
    }

    [Fact]
    public async Task ListLoadsMetadataWithoutLoadingConversationBodies()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileWorkSessionStore(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var work = new Work
        {
            Id = "work-1",
            Title = "작업",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new Session
        {
            Id = "session-1",
            Title = "대화",
            WorkId = work.Id,
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        session.Conversation.Add(new UserMessage("필요할 때만 읽을 본문"));

        await store.SaveWorkAsync(work);
        await store.SaveSessionAsync(session);
        await store.AppendConversationAsync(session, 0);

        var listedWork = Assert.Single(await store.LoadWorksAsync());
        var listedSession = Assert.Single(await store.LoadSessionsAsync());
        var loadedConversation = await store.LoadConversationAsync(session.Id);

        Assert.Empty(listedWork.Sessions);
        Assert.Empty(listedSession.Conversation);
        Assert.Single(loadedConversation!.Session.Conversation);
    }

    [Fact]
    public async Task RewriteConversationTruncatesStoredItemsAndAllowsSubsequentAppend()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileWorkSessionStore(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var session = new Session
        {
            Id = "session-1",
            Title = "대화",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        session.Conversation.AddRange([
            new UserMessage("A"),
            new AssistantMessage("a"),
            new UserMessage("B"),
            new AssistantMessage("b"),
        ]);

        await store.SaveSessionAsync(session);
        await store.AppendConversationAsync(session, 0);
        session.Conversation.RemoveRange(2, 2);
        await store.RewriteConversationAsync(session);
        session.Conversation.Add(new UserMessage("B-prime"));
        await store.AppendConversationAsync(session, 2);

        var loaded = await store.LoadConversationAsync(session.Id);

        Assert.Equal(3, loaded!.Session.Conversation.Count);
        Assert.Equal("A", Assert.IsType<UserMessage>(loaded.Session.Conversation[0]).Text);
        Assert.Equal("a", Assert.IsType<AssistantMessage>(loaded.Session.Conversation[1]).Text);
        Assert.Equal("B-prime", Assert.IsType<UserMessage>(loaded.Session.Conversation[2]).Text);
    }

    [Fact]
    public async Task DeleteSessionRemovesAllFilesAndClearsOnlyMatchingCurrentPointer()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileWorkSessionStore(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var current = new Session
        {
            Id = "current",
            Title = "현재",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        var other = new Session
        {
            Id = "other",
            Title = "다른 대화",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        current.Conversation.Add(new UserMessage("삭제할 기록"));

        await store.SaveSessionAsync(current);
        await store.AppendConversationAsync(current, 0);
        await store.SaveSessionAsync(other);
        await store.SetCurrentAsync(current);

        Assert.True(await store.DeleteSessionAsync(other.Id));
        Assert.Equal(current.Id, (await store.LoadCurrentAsync())?.Session.Id);
        Assert.True(await store.DeleteSessionAsync(current.Id));

        Assert.Null(await store.LoadConversationAsync(current.Id));
        Assert.Null(await store.LoadCurrentAsync());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "sessions", current.Id)));
        Assert.False(await store.DeleteSessionAsync(current.Id));
    }

    [Fact]
    public async Task DeleteWorkRemovesAllChildSessionsAndPreservesUnrelatedConversations()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileWorkSessionStore(directory.Path);
        var now = DateTimeOffset.UtcNow;
        var work = new Work
        {
            Id = "work-1",
            Title = "삭제할 Work",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var firstChild = new Session
        {
            Id = "child-1",
            Title = "첫 대화",
            WorkId = work.Id,
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        var secondChild = new Session
        {
            Id = "child-2",
            Title = "둘째 대화",
            WorkId = work.Id,
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        var standalone = new Session
        {
            Id = "standalone",
            Title = "독립 대화",
            WorkingDirectory = directory.Path,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Model = new ModelOptions("first"),
        };
        firstChild.Conversation.Add(new UserMessage("함께 삭제할 기록"));

        await store.SaveWorkAsync(work);
        await store.SaveSessionAsync(firstChild);
        await store.AppendConversationAsync(firstChild, 0);
        await store.SaveSessionAsync(secondChild);
        await store.SaveSessionAsync(standalone);
        await store.SetCurrentAsync(firstChild);

        Assert.True(await store.DeleteWorkAsync(work.Id));

        Assert.Empty(await store.LoadWorksAsync());
        Assert.Null(await store.LoadConversationAsync(firstChild.Id));
        Assert.Null(await store.LoadConversationAsync(secondChild.Id));
        Assert.NotNull(await store.LoadConversationAsync(standalone.Id));
        Assert.Null(await store.LoadCurrentAsync());
        Assert.False(await store.DeleteWorkAsync(work.Id));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-runtime-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
