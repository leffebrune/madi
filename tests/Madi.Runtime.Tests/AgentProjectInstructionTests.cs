using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;
using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class AgentProjectInstructionTests
{
    [Fact]
    public async Task LoadsOncePerTurnAndPreservesWorkAndConversationAcrossToolRounds()
    {
        var snapshot = new ProjectInstructions("/AGENTS.md", "project first");
        var loader = new Loader(() => Task.FromResult<ProjectInstructions?>(snapshot));
        var provider = new Provider();
        var agent = new Agent(provider, "system", [new Tool()], projectInstructionLoader: loader);
        var work = new Work { Id = "work", Context = "  original work  " };
        var session = NewSession("one");
        session.WorkId = work.Id;
        await Run(agent, work, session);
        Assert.Equal(1, loader.Count);
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request =>
        {
            Assert.Equal(WorkInstructionComposer.Compose(snapshot, work.Context), request.WorkInstructions);
            Assert.Equal("system", request.Instructions);
            Assert.Single(request.Input.OfType<UserMessage>());
        });
        Assert.Equal("  original work  ", work.Context);
        Assert.Equal("prompt", Assert.Single(session.Conversation.OfType<UserMessage>()).Text);
        snapshot = new("/AGENTS.md", "project second");
        await Run(agent, work, session);
        Assert.Equal(2, loader.Count);
        Assert.Contains(provider.Requests, r => r.WorkInstructions!.Contains("project second"));
    }

    [Fact]
    public async Task FileChangesApplyOnNextTurnWithoutWorkOrConversationInjection()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var provider = new Provider();
            var agent = new Agent(provider, "system", [new Tool()],
                projectInstructionLoader: new FileProjectInstructionLoader(root));
            var session = NewSession("one");
            foreach (var content in new[] { "first", "second" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), content);
                await Run(agent, null, session);
                Assert.Contains(provider.Requests, r => r.WorkInstructions?.EndsWith(content) == true);
            }
            File.Delete(Path.Combine(root, "AGENTS.md"));
            await Run(agent, null, session);
            Assert.Contains(provider.Requests, r => r.WorkInstructions is null);
            Assert.Equal(3, session.Conversation.OfType<UserMessage>().Count());
            Assert.All(session.Conversation.OfType<UserMessage>(), m => Assert.Equal("prompt", m.Text));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SavedWorkAndResumedSessionDoNotAccumulateProjectInstructions()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var path = Path.Combine(root, "AGENTS.md");
            await File.WriteAllTextAsync(path, "first snapshot");
            var provider = new Provider();
            var agent = new Agent(provider, "system", [new Tool()],
                projectInstructionLoader: new FileProjectInstructionLoader(root));
            var store = new FileWorkSessionStore(Path.Combine(root, "storage"));
            var work = new Work { Id = "work", Context = "  original context  " };
            var session = NewSession("saved");
            session.WorkId = work.Id;
            await Run(agent, work, session);
            await store.SaveWorkAsync(work);
            await store.SaveSessionAsync(session);
            await store.AppendConversationAsync(session, 0);
            var resumed = (await store.LoadConversationAsync(session.Id))!;
            Assert.Equal("  original context  ", resumed.Work!.Context);
            Assert.Equal("prompt", Assert.Single(resumed.Session.Conversation.OfType<UserMessage>()).Text);
            await File.WriteAllTextAsync(path, "next snapshot");
            var savedCount = resumed.Session.Conversation.Count;
            await Run(agent, resumed.Work, resumed.Session);
            await store.SaveSessionAsync(resumed.Session);
            await store.AppendConversationAsync(resumed.Session, savedCount);
            Assert.Equal(2, provider.Requests.Count(r => r.WorkInstructions!.Contains("next snapshot")));
            var reloaded = (await store.LoadConversationAsync(session.Id))!;
            Assert.Equal(2, reloaded.Session.Conversation.OfType<UserMessage>().Count());
            Assert.All(reloaded.Session.Conversation.OfType<UserMessage>(), m => Assert.Equal("prompt", m.Text));
            Assert.Equal(work.Context, reloaded.Work!.Context);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailureHappensBeforeProviderAndDoesNotCommitConversation()
    {
        var provider = new Provider();
        var agent = new Agent(provider, "system", projectInstructionLoader:
            new Loader(() => Task.FromException<ProjectInstructions?>(new IOException("read error"))));
        var session = NewSession("one");
        await Assert.ThrowsAsync<IOException>(() => Run(agent, null, session));
        Assert.Empty(provider.Requests);
        Assert.Empty(session.Conversation);
    }

    [Fact]
    public async Task ParallelTurnsKeepIndependentSnapshotsAndCaptureWorkBeforeAwait()
    {
        var first = new TaskCompletionSource<ProjectInstructions?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var loader = new Loader(() =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.SetResult();
                return first.Task;
            }
            return Task.FromResult<ProjectInstructions?>(new("/AGENTS.md", "second"));
        });
        var provider = new Provider();
        var agent = new Agent(provider, "system", [new Tool()], projectInstructionLoader: loader);
        var work = new Work { Id = "work", Context = "before" };
        var session = NewSession("first");
        session.WorkId = work.Id;
        var firstRun = Run(agent, work, session);
        await entered.Task;
        work.Context = "after";
        await Run(agent, null, NewSession("second"));
        first.SetResult(new("/AGENTS.md", "first"));
        await firstRun;
        Assert.Equal(2, loader.Count);
        Assert.All(provider.Requests.Where(r => r.WorkInstructions!.Contains("# Work instructions")),
            r => Assert.Equal(WorkInstructionComposer.Compose(new("/AGENTS.md", "first"), "before"), r.WorkInstructions));
        Assert.Equal(2, provider.Requests.Count(r => r.WorkInstructions ==
            WorkInstructionComposer.Compose(new("/AGENTS.md", "second"), null)));
    }

    private static Session NewSession(string id) => new() { Id = id, Model = new ModelOptions("test") };
    private static async Task Run(Agent agent, Work? work, Session session)
    {
        await foreach (var _ in agent.RespondAsync(work, session, "prompt")) { }
    }

    private sealed class Loader(Func<Task<ProjectInstructions?>> load) : IProjectInstructionLoader
    {
        public int Count;
        public Task<ProjectInstructions?> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return load();
        }
    }

    private sealed class Provider : IModelProvider
    {
        public string Id => "test";
        public ConcurrentBag<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            var needsTool = request.Input.Last() is UserMessage;
            yield return new ModelResponseCompleted(new ModelResponse("response", "done",
                needsTool ? [new ModelToolCall("tool", "call", "test", "{}", "{}")] : []));
        }
    }

    private sealed class Tool : ITool
    {
        public string Name => "test";
        public string Description => "test";
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { });
        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) => Task.FromResult("done");
    }
}
