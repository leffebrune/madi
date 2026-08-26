using System.Runtime.CompilerServices;
using Madi.App;
using Madi.Core;
using Madi.Runtime.Authentication;

namespace Madi.TUI.Tests;

public sealed class SuggestionEngineTests
{
    [Fact]
    public async Task CommandPrefixFilteringIsCaseInsensitiveAndIncludesUsage()
    {
        var controller = CreateController();

        var suggestions = await SuggestionEngine.GetSuggestionsAsync(controller, "/MoDe");

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("/model-list ", suggestions[0].CompletionText);
        Assert.Contains("사용 가능한 모델", suggestions[0].DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelSuggestionsAreDynamicAndMarkTheCurrentModel()
    {
        var controller = CreateController();

        var suggestions = await SuggestionEngine.GetSuggestionsAsync(controller, "/model test/gpt-");

        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, suggestion => suggestion.DisplayText.Contains("[현재]"));
        Assert.All(suggestions, suggestion => Assert.StartsWith("/model test/gpt-", suggestion.CompletionText));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("/work 자유 텍스트")]
    [InlineData("/unknown")]
    public async Task NonCompletableInputsPreserveTextAndHaveNoSuggestions(string input)
    {
        var suggestions = await SuggestionEngine.GetSuggestionsAsync(CreateController(), input);

        Assert.Empty(suggestions);
    }

    private static MadiController CreateController() => new(
        new EmptyAgent(),
        new EmptyStore(),
        new ModelOptions("gpt-alpha", ReasoningEffort.Medium),
        [Model("gpt-alpha"), Model("gpt-beta")],
        new Authentication(),
        "C:\\한국어");

    private static AvailableModel Model(string name) => new()
    {
        Name = name,
        Provider = "test",
        Api = "test",
        MaxContextTokens = 1000,
    };

    private sealed class EmptyAgent : IAgent
    {
        public string SystemInstructions { get; set; } = "Helpful assistant.";

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
        public Task<bool> DeleteWorkAsync(string workId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Authentication : IOAuthAuthentication
    {
        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
        public Task LoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
