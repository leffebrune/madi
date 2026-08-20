using System.Runtime.CompilerServices;
using Madi.App;
using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Services;

namespace Madi.App.Tests;

public sealed class MadiControllerTests
{
    [Fact]
    public void CommandCatalogHasTheSharedCommands()
    {
        var controller = CreateController();

        Assert.Equal(
            ["/model-list", "/model", "/work", "/new", "/login", "/logout", "/exit"],
            controller.Commands.All.Select(command => command.Name));
        Assert.Equal(["/model-list", "/model"],
            controller.Commands.FindByPrefix("/MoDe").Select(command => command.Name));
    }

    [Fact]
    public async Task ModelWorkAndSessionCommandsUpdateImmutableStateSnapshots()
    {
        var store = new RecordingStore();
        var controller = CreateController(store: store);

        await CollectAsync(controller.ExecuteAsync("/model second"));
        var modelState = controller.State;
        await CollectAsync(controller.ExecuteAsync("/work 한국어 작업"));
        var workState = controller.State;
        await CollectAsync(controller.ExecuteAsync("/new"));
        var sessionState = controller.State;

        Assert.Equal("second", modelState.Model.Model);
        Assert.Equal("한국어 작업", workState.Work?.Title);
        Assert.NotNull(workState.Session);
        Assert.Equal(workState.Work?.Id, workState.Session?.WorkId);
        Assert.NotEqual(workState.Session?.Id, sessionState.Session?.Id);
        Assert.Equal(AppRunState.Idle, sessionState.RunState);
        Assert.True(store.SetCurrentCount >= 2);
    }

    [Fact]
    public async Task CompletedResponsePublishesToolResultAndPersistsConversation()
    {
        var store = new RecordingStore();
        var agent = new StubAgent(static async (session, cancellationToken) =>
        {
            await Task.Yield();
            session.Conversation.Add(new UserMessage("질문"));
            session.Conversation.Add(new AssistantMessage("답변"));
            return
            [
                new AgentTextDelta("답"),
                new AgentToolCallCompleted("call-1", "read", "{\"path\":\"한글.txt\"}"),
                new AgentToolExecutionCompleted("call-1", "read", "파일 결과", false),
                new AgentResponseCompleted("답변"),
            ];
        });
        var controller = CreateController(agent, store);

        var events = await CollectAsync(controller.ExecuteAsync("질문"));

        var completed = Assert.Single(events.OfType<AppToolCompleted>());
        Assert.Equal("파일 결과", completed.Result);
        Assert.Contains("한글.txt", completed.Arguments, StringComparison.Ordinal);
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: true });
        Assert.Single(store.AppendStartIndices);
        Assert.Equal(2, store.LastSavedSession?.Conversation.Count);
        Assert.Equal("질문", store.LastSavedSession?.Title);
    }

    [Fact]
    public async Task CanceledResponseRollsBackAndDoesNotAppendConversation()
    {
        var store = new RecordingStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new StubAgent(async (session, cancellationToken) =>
        {
            session.Conversation.Add(new UserMessage("임시"));
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var controller = CreateController(agent, store);
        using var cancellation = new CancellationTokenSource();
        var collecting = CollectAsync(controller.ExecuteAsync("취소할 질문", cancellation.Token));
        await started.Task;

        cancellation.Cancel();
        var events = await collecting;

        Assert.Contains(events, item => item is AppTurnCompleted
            { Succeeded: false, Canceled: true });
        Assert.Empty(store.AppendStartIndices);
        Assert.Empty(store.LastSavedSession?.Conversation ?? []);
    }

    [Fact]
    public async Task ConcurrentInputIsRejectedWithoutWaiting()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new StubAgent(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var controller = CreateController(agent);
        using var cancellation = new CancellationTokenSource();
        var first = CollectAsync(controller.ExecuteAsync("첫 입력", cancellation.Token));
        await started.Task;

        var second = await CollectAsync(controller.ExecuteAsync("보존할 초안"));
        cancellation.Cancel();
        await first;

        var warning = Assert.Single(second.OfType<AppMessage>());
        Assert.Equal(AppMessageKind.Warning, warning.Kind);
        Assert.Contains("실행 중", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationReportsSuccessFailureCancellationAndLogout()
    {
        var authentication = new StubAuthentication();
        var controller = CreateController(authentication: authentication);

        var login = await CollectAsync(controller.ExecuteAsync("/login"));
        var logout = await CollectAsync(controller.ExecuteAsync("/logout"));

        Assert.Contains(login, item => item is AppAuthenticationProgress
            { Kind: AuthenticationProgressKind.Started });
        Assert.Contains(login, item => item is AppAuthenticationProgress
            { Kind: AuthenticationProgressKind.Succeeded });
        Assert.Contains(logout, item => item is AppAuthenticationProgress
            { Kind: AuthenticationProgressKind.SignedOut });
        Assert.Equal(1, authentication.LoginCount);
        Assert.Equal(1, authentication.LogoutCount);

        var failing = CreateController(authentication: new StubAuthentication
        {
            LoginFailure = new InvalidOperationException("실패"),
        });
        var failed = await CollectAsync(failing.ExecuteAsync("/login"));
        Assert.Contains(failed, item => item is AppAuthenticationProgress
            { Kind: AuthenticationProgressKind.Failed });

        var cancelingAuthentication = new StubAuthentication { WaitForCancellation = true };
        var canceling = CreateController(authentication: cancelingAuthentication);
        using var cancellation = new CancellationTokenSource();
        var cancelTask = CollectAsync(canceling.ExecuteAsync("/login", cancellation.Token));
        await cancelingAuthentication.LoginStarted.Task;
        cancellation.Cancel();
        var canceled = await cancelTask;
        Assert.Contains(canceled, item => item is AppAuthenticationProgress
            { Kind: AuthenticationProgressKind.Canceled });
    }

    [Fact]
    public async Task HelloArgumentIsUnknownAndUsageNoLongerAdvertisesIt()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "fd.exe"), "present");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "rg.exe"), "present");
        var ui = new RecordingUserInterface();
        var application = new ConsoleApplication(
            CreateController(),
            new UtilityInstaller(new NoOpDownloader(), directory.Path),
            ui);

        var exitCode = await application.RunAsync(["hello"]);

        Assert.Equal(1, exitCode);
        var usage = Assert.Single(ui.Lines);
        Assert.Contains("사용법:", usage, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", usage, StringComparison.OrdinalIgnoreCase);
    }

    private static MadiController CreateController(
        IAgent? agent = null,
        RecordingStore? store = null,
        StubAuthentication? authentication = null) => new(
        agent ?? new StubAgent(static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([])),
        store ?? new RecordingStore(),
        new ModelOptions("first", ReasoningEffort.Medium),
        [
            Model("first"),
            Model("second"),
        ],
        authentication ?? new StubAuthentication(),
        "C:\\work");

    private static AvailableModel Model(string name) => new()
    {
        Name = name,
        Provider = "test",
        Api = "test",
        MaxContextTokens = 1000,
    };

    private static async Task<List<AppEvent>> CollectAsync(IAsyncEnumerable<AppEvent> source)
    {
        var result = new List<AppEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }
        return result;
    }

    private sealed class StubAgent(
        Func<Session, CancellationToken, Task<IReadOnlyList<AgentEvent>>> run) : IAgent
    {
        public async IAsyncEnumerable<AgentEvent> RespondAsync(
            Work? work,
            Session session,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = await run(session, cancellationToken);
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class RecordingStore : IWorkSessionStore
    {
        public CurrentConversation? Current { get; set; }
        public Session? LastSavedSession { get; private set; }
        public List<int> AppendStartIndices { get; } = [];
        public int SetCurrentCount { get; private set; }

        public Task<IReadOnlyList<Work>> LoadWorksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Work>>([]);
        public Task<IReadOnlyList<Session>> LoadSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<CurrentConversation?> LoadCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
        public Task SaveWorkAsync(Work work, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default)
        {
            LastSavedSession = session;
            return Task.CompletedTask;
        }
        public Task AppendConversationAsync(
            Session session,
            int startIndex,
            CancellationToken cancellationToken = default)
        {
            AppendStartIndices.Add(startIndex);
            return Task.CompletedTask;
        }
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default)
        {
            SetCurrentCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuthentication : IOAuthAuthentication
    {
        public Exception? LoginFailure { get; init; }
        public bool WaitForCancellation { get; init; }
        public int LoginCount { get; private set; }
        public int LogoutCount { get; private set; }
        public TaskCompletionSource LoginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public async Task LoginAsync(CancellationToken cancellationToken = default)
        {
            LoginCount++;
            LoginStarted.TrySetResult();
            if (LoginFailure is not null)
            {
                throw LoginFailure;
            }
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUserInterface : IUserInterface
    {
        public List<string> Lines { get; } = [];
        public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public void Write(string message) { }
        public void WriteLine(string message) => Lines.Add(message);
    }

    private sealed class NoOpDownloader : IGitHubReleaseFileDownloader
    {
        public Task DownloadLatestReleaseFileAsync(
            GitHubReleaseFileRequest request,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-app-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
