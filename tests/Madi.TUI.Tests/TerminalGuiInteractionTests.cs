using System.Runtime.CompilerServices;
using Madi.App;
using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Services;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;

namespace Madi.TUI.Tests;

public sealed class TerminalGuiInteractionTests
{
    [Fact]
    public void AnsiTestDriverInjectsUnicodeMultilineAndSuggestionPriorityKeys()
    {
        using var directory = new TemporaryDirectory();
        using IApplication app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        using var window = CreateWindow(app, directory.Path);
        var session = app.Begin(window)
            ?? throw new InvalidOperationException("Terminal.Gui test session did not start.");
        try
        {
            window.FocusInputForTest();
            app.InjectKey(new Key('/'));
            app.InjectKey(new Key('m'));
            app.InjectKey(new Key('o'));
            app.InjectKey(new Key('d'));
            app.RaiseIteration();
            SpinWait.SpinUntil(() => window.SuggestionsVisibleForTest, 1000);
            Assert.True(window.SuggestionsVisibleForTest);

            app.InjectKey(Key.CursorDown);
            app.InjectKey(Key.Tab);
            Assert.StartsWith("/model", window.DraftForTest, StringComparison.Ordinal);

            app.InjectKey(Key.Enter.WithAlt);
            app.InjectKey(new Key('한'));
            app.InjectKey(new Key('글'));
            Assert.Contains("\n한글", window.DraftForTest, StringComparison.Ordinal);
            Assert.Equal(2, window.InputHeightForTest);

            for (var index = 0; index < 8; index++)
            {
                app.InjectKey(Key.Enter.WithAlt);
                app.InjectKey(new Key('줄'));
            }
            Assert.Equal(6, window.InputHeightForTest);
        }
        finally
        {
            app.End(session);
        }
    }

    [Fact]
    public void TestDriverControlsScrollingAndActivityDetails()
    {
        using var directory = new TemporaryDirectory();
        using IApplication app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        using var window = CreateWindow(app, directory.Path);
        var session = app.Begin(window)
            ?? throw new InvalidOperationException("Terminal.Gui test session did not start.");
        try
        {
            window.FocusInputForTest();
            for (var index = 0; index < 30; index++)
            {
                window.ApplyEventForTest(new AppMessage($"timeline line {index}"));
            }
            window.ApplyEventForTest(new AppToolStarted(
                "call-1",
                "read",
                "{\"path\":\"sample.txt\"}"));
            window.ApplyEventForTest(new AppToolCompleted(
                "call-1",
                "read",
                "{\"path\":\"sample.txt\"}",
                "tool result",
                false));

            app.InjectKey(Key.PageUp);
            Assert.False(window.FollowLatestForTest);
            app.InjectKey(Key.End);
            Assert.True(window.FollowLatestForTest);

            app.InjectKey(Key.CursorUp.WithCtrl);
            app.InjectKey(Key.O.WithCtrl);
            Assert.True(window.HasExpandedActivityForTest);
            Assert.Contains("tool result", window.TimelineTextForTest, StringComparison.Ordinal);
        }
        finally
        {
            app.End(session);
        }
    }

    [Fact]
    public void ResponsiveLayoutKeepsInputAndStatusAtTheBottom()
    {
        using var directory = new TemporaryDirectory();
        using IApplication app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        using var window = CreateWindow(app, directory.Path);
        var session = app.Begin(window)
            ?? throw new InvalidOperationException("Terminal.Gui test session did not start.");
        try
        {
            window.Frame = new System.Drawing.Rectangle(0, 0, 100, 32);
            window.SetRelativeLayout(new System.Drawing.Size(100, 32));
            app.LayoutAndDraw(true);
            var frames = window.GetFramesForTest();

            Assert.Equal(frames.Status.Y, frames.Input.Bottom);
            Assert.Equal(frames.Input.Y, frames.Timeline.Bottom);
            Assert.True(frames.Timeline.Height > frames.Input.Height);
        }
        finally
        {
            app.End(session);
        }
    }

    private static MadiWindow CreateWindow(IApplication app, string path) => new(
        app,
        new MadiController(
            new EmptyAgent(),
            new EmptyStore(),
            new ModelOptions("gpt-alpha", ReasoningEffort.Medium),
            [
                new AvailableModel
                {
                    Name = "gpt-alpha",
                    Provider = "test",
                    Api = "test",
                    MaxContextTokens = 1000,
                },
            ],
            new Authentication(),
            path),
        new UtilityInstaller(new NoOpDownloader(), path));

    private sealed class EmptyAgent : IAgent
    {
        public async IAsyncEnumerable<AgentEvent> RespondAsync(
            Work? work,
            Session session,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EmptyStore : IWorkSessionStore
    {
        public Task<IReadOnlyList<Work>> LoadWorksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Work>>([]);
        public Task<IReadOnlyList<Session>> LoadSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<CurrentConversation?> LoadCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<CurrentConversation?>(null);
        public Task<CurrentConversation?> LoadConversationAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<CurrentConversation?>(null);
        public Task SaveWorkAsync(Work work, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendConversationAsync(Session session, int startIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Authentication : IOAuthAuthentication
    {
        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
        public Task LoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpDownloader : IGitHubReleaseFileDownloader
    {
        public Task DownloadLatestReleaseFileAsync(GitHubReleaseFileRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"madi-tui-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "fd.exe"), "present");
            File.WriteAllText(System.IO.Path.Combine(Path, "rg.exe"), "present");
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
