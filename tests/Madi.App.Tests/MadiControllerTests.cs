using System.Runtime.CompilerServices;
using Madi.App;
using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Runtime.Providers;

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
    public async Task WorkPromptCanBeUpdatedAndIsExposedInState()
    {
        var now = DateTimeOffset.UtcNow;
        var work = new Work
        {
            Id = "work-1",
            Title = "작업",
            Context = "기존 프롬프트",
            WorkingDirectory = "C:\\work",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = Session("session-1", "대화", work.Id, "C:\\work", now);
        work.Sessions.Add(session);
        var store = new RecordingStore
        {
            Current = new CurrentConversation(work, session),
        };
        store.Works.Add(work);
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(
            new UpdateWorkPromptAction("  새로운 장기 목표와 제약  ")));

        Assert.Equal("새로운 장기 목표와 제약", work.Context);
        Assert.Equal("새로운 장기 목표와 제약", controller.State.Work?.Prompt);
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: true });
    }

    [Fact]
    public async Task ControllerUsesAgentInstructionsWithoutCopyingThemToSession()
    {
        var store = new RecordingStore();
        const string customInstructions = "Custom system instructions from SYSTEM.md";
        var controller = CreateController(store: store, systemInstructions: customInstructions);

        await CollectAsync(controller.ExecuteAsync("/new"));

        Assert.NotNull(store.LastSavedSession);
        Assert.Equal(customInstructions, controller.SystemInstructions);
    }

    [Fact]
    public void SystemInstructionsReflectRuntimeAgentChanges()
    {
        var agent = new StubAgent(
            static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([]),
            "first instructions");
        var controller = CreateController(agent);

        agent.SystemInstructions = "updated instructions";

        Assert.Equal("updated instructions", controller.SystemInstructions);
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
                new AgentToolExecutionCompleted(
                    "call-1",
                    "read",
                    "파일 결과",
                    false,
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200)),
                new AgentResponseCompleted("답변", TimeSpan.FromMilliseconds(300)),
            ];
        });
        var controller = CreateController(agent, store);

        var events = await CollectAsync(controller.ExecuteAsync("질문"));

        var completed = Assert.Single(events.OfType<AppToolCompleted>());
        Assert.Equal("파일 결과", completed.Result);
        Assert.Equal(TimeSpan.FromMilliseconds(100), completed.ToolDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(200), completed.ApiDuration);
        Assert.Contains("한글.txt", completed.Arguments, StringComparison.Ordinal);
        Assert.Equal(
            TimeSpan.FromMilliseconds(300),
            Assert.Single(events.OfType<AppAssistantCompleted>()).ApiDuration);
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
    public async Task ApiKeyModelInitializationDoesNotReadOAuthCredentials()
    {
        var authentication = new StubAuthentication();
        var apiModel = new AvailableModel
        {
            Name = "api-model",
            Provider = "OpenAI API",
            Api = OpenAIResponseProvider.ProviderId,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test-key",
            MaxContextTokens = 1000,
        };
        var runtime = new CatalogModelProvider([apiModel], authentication);
        var controller = new MadiController(
            new StubAgent(static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([])),
            new RecordingStore(),
            new ModelOptions(apiModel.Name, Provider: apiModel.Provider),
            [apiModel],
            runtime,
            "C:\\work");

        await controller.InitializeAsync();

        Assert.Equal(AuthenticationKind.ApiKey, controller.State.AuthenticationKind);
        Assert.Equal(AuthenticationState.Ready, controller.State.Authentication);
        Assert.Equal(0, authentication.AuthorizationCount);
    }

    [Fact]
    public async Task MissingApiKeyBlocksPromptBeforeCallingAgent()
    {
        var calls = 0;
        var apiModel = new AvailableModel
        {
            Name = "api-model",
            Provider = "OpenAI API",
            Api = OpenAIResponseProvider.ProviderId,
            BaseUrl = "https://api.openai.com/v1",
            MaxContextTokens = 1000,
        };
        var runtime = new CatalogModelProvider([apiModel], new StubAuthentication());
        var controller = new MadiController(
            new StubAgent((_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            }),
            new RecordingStore(),
            new ModelOptions(apiModel.Name, Provider: apiModel.Provider),
            [apiModel],
            runtime,
            "C:\\work");

        var events = await CollectAsync(controller.ExecuteAsync("질문"));

        Assert.Equal(0, calls);
        Assert.Contains(events.OfType<AppMessage>(), message =>
            message.Kind == AppMessageKind.Error
            && message.Text.Contains("apiKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppFactoryUsesCatalogAuthenticationForApiKeyDefaultModel()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "models.json"),
            """
            [
              {
                "provider": "OpenAI API",
                "api": "openai-responses",
                "baseUrl": "https://api.openai.com/v1",
                "apiKey": "sk-test-key",
                "models": [
                  {
                    "name": "api-model",
                    "supportsReasoning": true,
                    "maxContextTokens": 1000
                  }
                ]
              }
            ]
            """);

        var host = await MadiAppFactory.CreateAsync(
            storageRoot: directory.Path,
            workingDirectory: directory.Path);
        await host.Controller.InitializeAsync();

        Assert.Equal("OpenAI API/api-model", host.Controller.State.Model.QualifiedName);
        Assert.Equal(AuthenticationKind.ApiKey, host.Controller.State.AuthenticationKind);
        Assert.Equal(AuthenticationState.Ready, host.Controller.State.Authentication);
    }

    [Fact]
    public async Task TypedActionsUseTheSameModelSelectionBehaviorAsCommands()
    {
        var commandController = CreateController();
        var actionController = CreateController();

        var commandEvents = await CollectAsync(commandController.ExecuteAsync("/model second"));
        var actionEvents = await CollectAsync(
            actionController.ExecuteAsync(new SelectModelAction("second")));

        Assert.Equal("second", commandController.State.Model.Model);
        Assert.Equal(commandController.State.Model, actionController.State.Model);
        Assert.Equal(
            commandEvents.OfType<AppTurnCompleted>().Last(),
            actionEvents.OfType<AppTurnCompleted>().Last());
    }

    [Fact]
    public async Task ReasoningEffortActionUpdatesAndPersistsCurrentSession()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session("session-1", "대화", null, "C:\\work", now);
        session.Model = new ModelOptions("first", ReasoningEffort.Medium, "test");
        var store = new RecordingStore
        {
            Current = new CurrentConversation(null, session),
        };
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(
            new SetReasoningEffortAction(ReasoningEffort.Low)));

        Assert.Equal(ReasoningEffort.Low, controller.State.Model.ReasoningEffort);
        Assert.Equal(ReasoningEffort.Low, session.Model.ReasoningEffort);
        Assert.Same(session, store.LastSavedSession);
        Assert.True(session.UpdatedAtUtc > now);
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: true });
    }

    [Fact]
    public async Task ImmediateEffortIsPreservedBetweenReasoningModels()
    {
        var controller = CreateController();
        await CollectAsync(controller.ExecuteAsync(new SetReasoningEffortAction(null)));

        await CollectAsync(controller.ExecuteAsync(new SelectModelAction("second")));

        Assert.Null(controller.State.Model.ReasoningEffort);
    }

    [Fact]
    public async Task ReasoningModelUsesMediumWhenPreviousModelHasNoEffort()
    {
        var models = new[]
        {
            Model("plain", supportsReasoning: false),
            Model("reasoning", supportsReasoning: true),
        };
        var controller = new MadiController(
            new StubAgent(static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([])),
            new RecordingStore(),
            new ModelOptions("plain", Provider: "test"),
            models,
            new StubAuthentication(),
            "C:\\work");

        await CollectAsync(controller.ExecuteAsync(new SelectModelAction("reasoning")));

        Assert.Equal(ReasoningEffort.Medium, controller.State.Model.ReasoningEffort);
    }

    [Fact]
    public async Task ProviderChangeInActiveConversationIsRejectedAndRestoresState()
    {
        var oauthModel = new AvailableModel
        {
            Name = "oauth-model",
            Provider = "OpenAI OAuth",
            Api = OpenAIOAuthProvider.ProviderId,
            MaxContextTokens = 1000,
        };
        var apiModel = new AvailableModel
        {
            Name = "api-model",
            Provider = "OpenAI API",
            Api = OpenAIResponseProvider.ProviderId,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test-key",
            MaxContextTokens = 1000,
        };
        var now = DateTimeOffset.UtcNow;
        var session = Session("session-1", "대화", null, "C:\\work", now);
        session.Model = new ModelOptions(oauthModel.Name, Provider: oauthModel.Provider);
        session.Conversation.Add(new UserMessage("existing"));
        var store = new RecordingStore
        {
            Current = new CurrentConversation(null, session),
        };
        store.Sessions.Add(session);
        var authentication = new StubAuthentication();
        var runtime = new CatalogModelProvider(
            [oauthModel, apiModel],
            authentication);
        var controller = new MadiController(
            new StubAgent(static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([])),
            store,
            session.Model,
            [oauthModel, apiModel],
            runtime,
            "C:\\work");

        var events = await CollectAsync(controller.ExecuteAsync(
            new SelectModelAction(apiModel.QualifiedName)));

        Assert.Equal(oauthModel.QualifiedName, controller.State.Model.QualifiedName);
        Assert.Contains(events, item => item is AppMessage { Kind: AppMessageKind.Warning });
        Assert.All(
            events.OfType<AppStateChanged>(),
            stateChanged => Assert.Equal(
                oauthModel.QualifiedName,
                stateChanged.State.Model.QualifiedName));
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: false });
    }

    [Fact]
    public async Task LegacySessionInfersProviderFromPersistedOutputWhenModelNameIsAmbiguous()
    {
        var oauthModel = new AvailableModel
        {
            Name = "shared",
            Provider = "OpenAI OAuth",
            Api = OpenAIOAuthProvider.ProviderId,
            MaxContextTokens = 1000,
        };
        var apiModel = new AvailableModel
        {
            Name = "shared",
            Provider = "OpenAI API",
            Api = OpenAIResponseProvider.ProviderId,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test-key",
            MaxContextTokens = 1000,
        };
        var now = DateTimeOffset.UtcNow;
        var session = Session("legacy", "기존 대화", null, "C:\\work", now);
        session.Model = new ModelOptions("shared");
        session.Conversation.Add(new ModelAssistantOutput(
            OpenAIResponseProvider.ProviderId,
            "answer",
            "{\"type\":\"message\",\"content\":[]}"));
        var store = new RecordingStore
        {
            Current = new CurrentConversation(null, session),
        };
        store.Sessions.Add(session);
        var authentication = new StubAuthentication();
        var runtime = new CatalogModelProvider(
            [oauthModel, apiModel],
            authentication);
        var controller = new MadiController(
            new StubAgent(static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([])),
            store,
            new ModelOptions(oauthModel.Name, Provider: oauthModel.Provider),
            [oauthModel, apiModel],
            runtime,
            "C:\\work");

        await controller.InitializeAsync();

        Assert.Equal(apiModel.QualifiedName, controller.State.Model.QualifiedName);
        Assert.Equal(apiModel.Provider, store.LastSavedSession?.Model.Provider);
        Assert.Equal(0, authentication.AuthorizationCount);
    }

    [Fact]
    public async Task NavigationIsWorkspaceScopedSortedAndActivatesConversation()
    {
        var now = DateTimeOffset.UtcNow;
        var older = Session("older", "이전", "work-1", "C:\\work", now.AddMinutes(-2));
        var recent = Session("recent", "최근", "work-1", "C:\\work", now);
        recent.Model = new ModelOptions("second");
        recent.Conversation.Add(new UserMessage("복원 질문"));
        var other = Session("other", "다른 폴더", null, "C:\\other", now.AddMinutes(1));
        var standalone = Session("standalone", "독립", null, "C:\\work", now.AddMinutes(-1));
        var legacy = Session("legacy", "기존", null, null, now.AddMinutes(-3));
        var work = new Work
        {
            Id = "work-1",
            Title = "작업",
            WorkingDirectory = "C:\\work",
            CreatedAtUtc = older.CreatedAtUtc,
            UpdatedAtUtc = now,
        };
        work.Sessions.Add(older);
        work.Sessions.Add(recent);
        var store = new RecordingStore();
        store.Works.Add(work);
        store.Sessions.AddRange([older, recent, other, standalone, legacy]);
        var controller = CreateController(store: store);

        var navigation = await controller.GetNavigationAsync();
        var events = await CollectAsync(controller.ExecuteAsync(new ActivateSessionAction("recent")));

        Assert.Equal(["recent", "older"], navigation.Works[0].Sessions.Select(item => item.Id));
        Assert.Equal("standalone", Assert.Single(navigation.StandaloneSessions).Id);
        Assert.Equal(1, navigation.LegacyItemCount);
        Assert.Equal("second", controller.State.Model.Model);
        var loaded = Assert.Single(events.OfType<AppConversationLoaded>());
        Assert.Equal("복원 질문", Assert.Single(loaded.Conversation.Entries).Summary);
    }

    [Fact]
    public async Task DeletingCurrentSessionSelectsNewestReplacementThenClearsLastConversation()
    {
        var now = DateTimeOffset.UtcNow;
        var older = Session("older", "이전", "work-1", "C:\\work", now.AddMinutes(-1));
        var current = Session("current", "현재", "work-1", "C:\\work", now);
        var work = new Work
        {
            Id = "work-1",
            Title = "작업",
            WorkingDirectory = "C:\\work",
            CreatedAtUtc = now.AddMinutes(-2),
            UpdatedAtUtc = now,
        };
        work.Sessions.AddRange([older, current]);
        var store = new RecordingStore
        {
            Current = new CurrentConversation(work, current),
        };
        store.Works.Add(work);
        store.Sessions.AddRange([older, current]);
        var controller = CreateController(store: store);

        var replaced = await CollectAsync(
            controller.ExecuteAsync(new DeleteSessionAction(current.Id)));

        Assert.Equal(older.Id, controller.State.Session?.Id);
        Assert.Equal(older.Id, Assert.Single(replaced.OfType<AppConversationLoaded>()).Conversation.Session.Id);
        Assert.DoesNotContain(store.Sessions, item => item.Id == current.Id);

        var cleared = await CollectAsync(
            controller.ExecuteAsync(new DeleteSessionAction(older.Id)));

        Assert.Null(controller.State.Session);
        Assert.Null(controller.State.Work);
        Assert.Single(cleared.OfType<AppConversationCleared>());
        Assert.Empty(store.Sessions);
        Assert.Empty((await controller.GetNavigationAsync()).Works[0].Sessions);
    }

    [Fact]
    public async Task DeleteWorkRemovesChildConversationsAndSelectsOutsideReplacement()
    {
        var now = DateTimeOffset.UtcNow;
        var child = Session("child", "삭제될 대화", "work-1", "C:\\work", now);
        var replacement = Session("replacement", "남을 대화", null, "C:\\work", now.AddMinutes(-1));
        var work = new Work
        {
            Id = "work-1",
            Title = "삭제할 Work",
            WorkingDirectory = "C:\\work",
            CreatedAtUtc = now.AddMinutes(-2),
            UpdatedAtUtc = now,
        };
        work.Sessions.Add(child);
        var store = new RecordingStore
        {
            Current = new CurrentConversation(work, child),
        };
        store.Works.Add(work);
        store.Sessions.AddRange([child, replacement]);
        var controller = CreateController(store: store);

        var events = await CollectAsync(
            controller.ExecuteAsync(new DeleteWorkAction(work.Id)));

        Assert.DoesNotContain(store.Works, item => item.Id == work.Id);
        Assert.DoesNotContain(store.Sessions, item => item.WorkId == work.Id);
        Assert.Equal(replacement.Id, controller.State.Session?.Id);
        Assert.Null(controller.State.Work);
        Assert.Equal(replacement.Id,
            Assert.Single(events.OfType<AppConversationLoaded>()).Conversation.Session.Id);
        Assert.Empty((await controller.GetNavigationAsync()).Works);
    }

    [Fact]
    public async Task DeleteCurrentWorkWithoutReplacementClearsConversation()
    {
        var now = DateTimeOffset.UtcNow;
        var child = Session("only-child", "유일한 대화", "work-1", "C:\\work", now);
        var work = new Work
        {
            Id = "work-1",
            Title = "유일한 Work",
            WorkingDirectory = "C:\\work",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var store = new RecordingStore
        {
            Current = new CurrentConversation(work, child),
        };
        store.Works.Add(work);
        store.Sessions.Add(child);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(new DeleteWorkAction(work.Id)));

        Assert.Null(controller.State.Work);
        Assert.Null(controller.State.Session);
        Assert.Single(events.OfType<AppConversationCleared>());
        Assert.Empty(store.Works);
        Assert.Empty(store.Sessions);
    }

    [Fact]
    public async Task DeleteWorkRejectsAWorkFromAnotherWorkspace()
    {
        var work = new Work
        {
            Id = "other-work",
            Title = "다른 폴더",
            WorkingDirectory = "C:\\other",
        };
        var store = new RecordingStore();
        store.Works.Add(work);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(new DeleteWorkAction(work.Id)));

        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: false });
        Assert.Contains(store.Works, item => item.Id == work.Id);
    }

    [Fact]
    public async Task DeleteConversationFromUserMessageRemovesThatMessageAndEverythingAfterIt()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session("session-1", "기존 제목", null, "C:\\work", now);
        session.Conversation.AddRange([
            new UserMessage("A"),
            new AssistantMessage("a"),
            new UserMessage("B"),
            new AssistantMessage("b"),
            new UserMessage("C"),
            new AssistantMessage("c"),
            new UserMessage("D"),
            new AssistantMessage("d"),
        ]);
        var store = new RecordingStore
        {
            Current = new CurrentConversation(null, session),
        };
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(
            new DeleteConversationFromAction(session.Id, 4)));

        Assert.Collection(
            session.Conversation,
            item => Assert.Equal("A", Assert.IsType<UserMessage>(item).Text),
            item => Assert.Equal("a", Assert.IsType<AssistantMessage>(item).Text),
            item => Assert.Equal("B", Assert.IsType<UserMessage>(item).Text),
            item => Assert.Equal("b", Assert.IsType<AssistantMessage>(item).Text));
        Assert.Equal(1, store.RewriteConversationCount);
        Assert.Same(session, store.LastSavedSession);
        Assert.Equal("기존 제목", session.Title);
        Assert.Equal(4, Assert.Single(events.OfType<AppConversationLoaded>())
            .Conversation.Entries.Count);
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: true });
    }

    [Fact]
    public async Task DeleteConversationFromRestoresMemoryWhenRewriteFails()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session("session-1", "대화", null, "C:\\work", now);
        session.Conversation.AddRange([
            new UserMessage("A"),
            new AssistantMessage("a"),
            new UserMessage("B"),
            new AssistantMessage("b"),
        ]);
        var store = new RecordingStore
        {
            Current = new CurrentConversation(null, session),
            RewriteFailuresRemaining = 1,
        };
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(
            new DeleteConversationFromAction(session.Id, 2)));

        Assert.Equal(4, session.Conversation.Count);
        Assert.Equal("B", Assert.IsType<UserMessage>(session.Conversation[2]).Text);
        Assert.Equal(2, store.RewriteConversationCount);
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: false });
    }

    [Fact]
    public async Task DeleteConversationFromRejectsAssistantMessage()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session("session-1", "대화", null, "C:\\work", now);
        session.Conversation.AddRange([
            new UserMessage("A"),
            new AssistantMessage("a"),
        ]);
        var store = new RecordingStore
        {
            Current = new CurrentConversation(null, session),
        };
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(
            new DeleteConversationFromAction(session.Id, 1)));

        Assert.Equal(2, session.Conversation.Count);
        Assert.Equal(0, store.RewriteConversationCount);
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: false });
    }

    [Fact]
    public async Task DeleteSessionRejectsAConversationFromAnotherWorkspace()
    {
        var session = Session("other", "다른 폴더", null, "C:\\other", DateTimeOffset.UtcNow);
        var store = new RecordingStore();
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(
            controller.ExecuteAsync(new DeleteSessionAction(session.Id)));

        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: false });
        Assert.Contains(store.Sessions, item => item.Id == session.Id);
    }

    [Fact]
    public async Task StartupReplacesMissingModelWithinProviderAndPersistsNotification()
    {
        var session = Session("old", "대화", null, @"C:\work", DateTimeOffset.UtcNow);
        session.Model = new ModelOptions("removed", ReasoningEffort.High, "test");
        session.Conversation.Add(new UserMessage("existing conversation"));
        var store = new RecordingStore { Current = new CurrentConversation(null, session) };
        var controller = CreateController(store: store);

        await controller.InitializeAsync();

        Assert.Equal("test/first", controller.State.Model.QualifiedName);
        Assert.Equal(ReasoningEffort.High, controller.State.Model.ReasoningEffort);
        Assert.Equal(controller.State.Model, store.LastSavedSession?.Model);
        Assert.Single(session.Conversation);
        Assert.Contains("test/removed", controller.InitializationMessage!.Text);
        Assert.Contains("test/first", controller.InitializationMessage.Text);
    }

    [Fact]
    public async Task OpeningSessionReplacesMissingModelAndNotifiesAfterConversationLoaded()
    {
        var session = Session("old", "대화", null, @"C:\work", DateTimeOffset.UtcNow);
        session.Model = new ModelOptions("removed", Provider: "test");
        var store = new RecordingStore();
        store.Sessions.Add(session);
        var controller = CreateController(store: store);

        var events = await CollectAsync(controller.ExecuteAsync(new ActivateSessionAction(session.Id)));

        Assert.Equal("test/first", controller.State.Model.QualifiedName);
        var notification = Assert.Single(events.OfType<AppMessage>());
        Assert.Contains("자동 선택", notification.Text);
        Assert.True(events.FindIndex(item => item is AppConversationLoaded) < events.IndexOf(notification));
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: true });
        Assert.Equal("first", store.LastSavedSession?.Model.Model);

        var reopened = await CollectAsync(controller.ExecuteAsync(new ActivateSessionAction(session.Id)));
        Assert.DoesNotContain(reopened.OfType<AppMessage>(), item => item.Text.Contains("자동 선택"));
    }

    [Fact]
    public async Task MissingProviderFailsSessionOpenWithoutChangingCurrentSession()
    {
        var current = Session("current", "현재", null, @"C:\work", DateTimeOffset.UtcNow);
        var missing = Session("missing", "이전", null, @"C:\work", DateTimeOffset.UtcNow);
        missing.Model = new ModelOptions("first", Provider: "removed-provider");
        var store = new RecordingStore { Current = new CurrentConversation(null, current) };
        store.Sessions.Add(missing);
        var controller = CreateController(store: store);
        await controller.InitializeAsync();
        var originalModel = controller.State.Model;

        var events = await CollectAsync(controller.ExecuteAsync(new ActivateSessionAction(missing.Id)));

        Assert.Equal(current.Id, controller.State.Session?.Id);
        Assert.Equal(originalModel, controller.State.Model);
        Assert.Equal(current.Id, store.Current?.Session.Id);
        Assert.Contains(events, item => item is AppMessage { Kind: AppMessageKind.Error } message
            && message.Text.Contains("removed-provider"));
        Assert.Contains(events, item => item is AppTurnCompleted { Succeeded: false });
        Assert.DoesNotContain(events, item => item is AppConversationLoaded);
    }

    [Fact]
    public async Task MissingProviderFailsStartupWithReason()
    {
        var session = Session("old", "대화", null, @"C:\work", DateTimeOffset.UtcNow);
        session.Model = new ModelOptions("first", Provider: "removed-provider");
        var store = new RecordingStore { Current = new CurrentConversation(null, session) };
        var controller = CreateController(store: store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.InitializeAsync());

        Assert.Contains("removed-provider", exception.Message);
        Assert.Null(controller.State.Session);
        Assert.Null(controller.InitializationMessage);
        Assert.Equal("removed-provider", session.Model.Provider);
    }

    private static Session Session(
        string id,
        string title,
        string? workId,
        string? workingDirectory,
        DateTimeOffset updatedAt) => new()
        {
            Id = id,
            Title = title,
            WorkId = workId,
            WorkingDirectory = workingDirectory,
            CreatedAtUtc = updatedAt.AddMinutes(-1),
            UpdatedAtUtc = updatedAt,
            Model = new ModelOptions("first"),
        };

    [Fact]
    public async Task ProjectReadFailureIsVisibleAndNextTurnCanRunWithoutPersistingInstructions()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var path = Path.Combine(root, "AGENTS.md");
            await File.WriteAllBytesAsync(path, [0xff]);
            var provider = new ProjectRecordingProvider();
            var agent = new Agent(provider, "system", projectInstructionLoader:
                new Madi.Runtime.Persistence.FileProjectInstructionLoader(root));
            var store = new RecordingStore();
            var controller = CreateController(agent, store);
            var failed = await CollectAsync(controller.ExecuteAsync("first"));
            Assert.Contains(failed, item => item is AppMessage { Kind: AppMessageKind.Error } message
                && message.Text.Contains(path));
            Assert.Equal(AppRunState.Idle, controller.State.RunState);
            Assert.Empty(provider.Requests);
            await File.WriteAllTextAsync(path, "project snapshot");
            var completed = await CollectAsync(controller.ExecuteAsync("second"));
            Assert.Contains(completed, item => item is AppTurnCompleted { Succeeded: true });
            Assert.Equal(AppRunState.Idle, controller.State.RunState);
            Assert.Equal("second", Assert.Single(store.LastSavedSession!.Conversation.OfType<UserMessage>()).Text);
            Assert.Contains("project snapshot", Assert.Single(provider.Requests).WorkInstructions);
            await File.WriteAllTextAsync(path, "updated snapshot");
            var resumed = CreateController(agent, store);
            await CollectAsync(resumed.ExecuteAsync("third"));
            Assert.Contains("updated snapshot", provider.Requests[^1].WorkInstructions);
            Assert.All(store.LastSavedSession!.Conversation.OfType<UserMessage>(),
                message => Assert.DoesNotContain("snapshot", message.Text));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ProjectRecordingProvider : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelResponseCompleted(new ModelResponse("response", "done", []));
        }
    }

    private static MadiController CreateController(
        IAgent? agent = null,
        RecordingStore? store = null,
        StubAuthentication? authentication = null,
        string? systemInstructions = null) => new(
        agent ?? new StubAgent(
            static (_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([]),
            systemInstructions ?? "Helpful test assistant."),
        store ?? new RecordingStore(),
        new ModelOptions("first", ReasoningEffort.Medium),
        [
            Model("first"),
            Model("second"),
        ],
        authentication ?? new StubAuthentication(),
        "C:\\work");

    private static AvailableModel Model(string name, bool supportsReasoning = true) => new()
    {
        Name = name,
        Provider = "test",
        Api = "test",
        SupportsReasoning = supportsReasoning,
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
        Func<Session, CancellationToken, Task<IReadOnlyList<AgentEvent>>> run,
        string systemInstructions = "Helpful test assistant.") : IAgent
    {
        public string SystemInstructions { get; set; } = systemInstructions;

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
        public int RewriteConversationCount { get; private set; }
        public int RewriteFailuresRemaining { get; set; }
        public int SetCurrentCount { get; private set; }
        public List<Work> Works { get; } = [];
        public List<Session> Sessions { get; } = [];

        public Task<IReadOnlyList<Work>> LoadWorksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Work>>(Works);
        public Task<IReadOnlyList<Session>> LoadSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Session>>(Sessions);
        public Task<CurrentConversation?> LoadCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
        public Task<CurrentConversation?> LoadConversationAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current?.Session.Id == sessionId
                ? Current
                : Sessions.FirstOrDefault(item => item.Id == sessionId) is { } session
                    ? new CurrentConversation(
                        Works.FirstOrDefault(item => item.Id == session.WorkId),
                        session)
                    : null);
        public Task SaveWorkAsync(Work work, CancellationToken cancellationToken = default)
        {
            Works.RemoveAll(item => item.Id == work.Id);
            Works.Add(work);
            return Task.CompletedTask;
        }
        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default)
        {
            LastSavedSession = session;
            Sessions.RemoveAll(item => item.Id == session.Id);
            Sessions.Add(session);
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
        public Task RewriteConversationAsync(
            Session session,
            CancellationToken cancellationToken = default)
        {
            RewriteConversationCount++;
            if (RewriteFailuresRemaining > 0)
            {
                RewriteFailuresRemaining--;
                throw new IOException("Conversation rewrite failed.");
            }
            return Task.CompletedTask;
        }
        public Task<bool> DeleteSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            var removed = Sessions.RemoveAll(item => item.Id == sessionId) > 0;
            foreach (var work in Works)
            {
                work.Sessions.RemoveAll(item => item.Id == sessionId);
            }
            if (Current?.Session.Id == sessionId)
            {
                Current = null;
            }
            return Task.FromResult(removed);
        }
        public Task<bool> DeleteWorkAsync(
            string workId,
            CancellationToken cancellationToken = default)
        {
            if (!Works.Any(item => item.Id == workId))
            {
                return Task.FromResult(false);
            }

            Works.RemoveAll(item => item.Id == workId);
            Sessions.RemoveAll(item => item.WorkId == workId);
            if (Current?.Work?.Id == workId)
            {
                Current = null;
            }
            return Task.FromResult(true);
        }
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default)
        {
            SetCurrentCount++;
            Current = new CurrentConversation(
                Works.FirstOrDefault(item => item.Id == session.WorkId),
                session);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuthentication : IOAuthAuthentication
    {
        public Exception? LoginFailure { get; init; }
        public bool WaitForCancellation { get; init; }
        public int AuthorizationCount { get; private set; }
        public int LoginCount { get; private set; }
        public int LogoutCount { get; private set; }
        public TaskCompletionSource LoginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            return Task.FromResult("token");
        }

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
