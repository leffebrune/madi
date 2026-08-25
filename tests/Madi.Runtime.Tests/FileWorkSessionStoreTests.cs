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
            Model = new ModelOptions("first"),
        };
        session.Conversation.Add(new ToolResult("call-1", "failed", IsError: true));

        await store.SaveWorkAsync(work);
        await store.SaveSessionAsync(session);
        await store.AppendConversationAsync(session, 0);
        var loaded = await store.LoadConversationAsync(session.Id);

        Assert.Equal(Path.GetFullPath(directory.Path), loaded?.Session.WorkingDirectory);
        Assert.Equal("장기 목표와 제약", loaded?.Work?.Context);
        Assert.Equal(now, loaded?.Session.CreatedAtUtc);
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
