using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Runtime.Providers;

namespace Madi.Runtime.Tests;

public sealed class CatalogModelProviderTests
{
    [Fact]
    public async Task ApiKeyModelIsReadyWithoutAccessingOAuthCredentials()
    {
        var authentication = new RecordingOAuthAuthentication { IsSignedIn = false };
        var runtime = new CatalogModelProvider([ApiModel("api-model")], authentication);

        var status = await runtime.GetAuthenticationAsync(
            new ModelOptions("api-model", Provider: "OpenAI API"));

        Assert.Equal(ModelAuthenticationKind.ApiKey, status.Kind);
        Assert.Equal(ModelAuthenticationStatus.Ready, status.Status);
        Assert.Equal(0, authentication.AuthorizationCount);
    }

    [Fact]
    public async Task OAuthCredentialsAreCheckedOnlyForOAuthModel()
    {
        var authentication = new RecordingOAuthAuthentication { IsSignedIn = false };
        var runtime = new CatalogModelProvider(
            [OAuthModel("oauth-model"), ApiModel("api-model")],
            authentication);

        var apiStatus = await runtime.GetAuthenticationAsync(
            new ModelOptions("api-model", Provider: "OpenAI API"));
        var oauthStatus = await runtime.GetAuthenticationAsync(
            new ModelOptions("oauth-model", Provider: "OpenAI OAuth"));

        Assert.Equal(ModelAuthenticationStatus.Ready, apiStatus.Status);
        Assert.Equal(ModelAuthenticationKind.OAuth, oauthStatus.Kind);
        Assert.Equal(ModelAuthenticationStatus.ActionRequired, oauthStatus.Status);
        Assert.Equal(1, authentication.AuthorizationCount);
    }

    [Fact]
    public async Task ApiKeyModelReportsMissingBaseUrl()
    {
        var model = ApiModel("api-model") with { BaseUrl = null };
        var runtime = new CatalogModelProvider([model], new RecordingOAuthAuthentication());

        var status = await runtime.GetAuthenticationAsync(
            new ModelOptions("api-model", Provider: model.Provider));

        Assert.Equal(ModelAuthenticationStatus.ActionRequired, status.Status);
        Assert.Contains("baseUrl", status.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "apiKey")]
    [InlineData("invalid", "API 설정")]
    public async Task ApiKeyModelReportsMissingOrInvalidConfiguration(
        string? apiKey,
        string expectedMessage)
    {
        var model = ApiModel("api-model") with { ApiKey = apiKey };
        var runtime = new CatalogModelProvider([model], new RecordingOAuthAuthentication());

        var status = await runtime.GetAuthenticationAsync(
            new ModelOptions("api-model", Provider: model.Provider));

        Assert.Equal(ModelAuthenticationStatus.ActionRequired, status.Status);
        Assert.Contains(expectedMessage, status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamRoutesRequestToSelectedCatalogProvider()
    {
        var oauthProvider = new RecordingProvider("oauth");
        var apiProvider = new RecordingProvider("api");
        var runtime = new CatalogModelProvider(
            [OAuthModel("oauth-model"), ApiModel("api-model")],
            new RecordingOAuthAuthentication(),
            model => model.Api == OpenAIOAuthProvider.ProviderId
                ? oauthProvider
                : apiProvider);
        var request = new ModelRequest(
            [new UserMessage("hello")],
            [],
            new ModelOptions("api-model", Provider: "OpenAI API"),
            "instructions",
            "cache-key");

        await foreach (var _ in runtime.StreamAsync(request))
        {
        }

        Assert.Empty(oauthProvider.Requests);
        var routed = Assert.Single(apiProvider.Requests);
        Assert.Equal("OpenAI API/api-model", routed.Options.QualifiedName);
        Assert.Null(routed.MaxOutputTokens);
    }

    [Fact]
    public async Task StreamRoutesConfiguredOutputTokenLimitToResponsesProvider()
    {
        var provider = new RecordingProvider("api");
        var model = ApiModel("claude-model") with { MaxOutputTokens = 128000 };
        var runtime = new CatalogModelProvider(
            [model],
            new RecordingOAuthAuthentication(),
            _ => provider);
        var request = new ModelRequest(
            [new UserMessage("hello")], [],
            new ModelOptions(model.Name, Provider: model.Provider),
            "instructions", "cache-key");

        await foreach (var _ in runtime.StreamAsync(request))
        {
        }

        Assert.Equal(128000, Assert.Single(provider.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task StreamDoesNotChangeCodexRequestsWithOutputTokenLimit()
    {
        var provider = new RecordingProvider("oauth");
        var model = OAuthModel("oauth-model") with { MaxOutputTokens = 128000 };
        var runtime = new CatalogModelProvider(
            [model], new RecordingOAuthAuthentication(), _ => provider);
        var request = new ModelRequest(
            [new UserMessage("hello")], [],
            new ModelOptions(model.Name, Provider: model.Provider),
            "instructions", "cache-key");

        await foreach (var _ in runtime.StreamAsync(request))
        {
        }

        Assert.Null(Assert.Single(provider.Requests).MaxOutputTokens);
    }

    [Fact]
    public void ResolveModelRequiresProviderWhenModelNameIsAmbiguous()
    {
        var runtime = new CatalogModelProvider(
        [
            OAuthModel("shared"),
            ApiModel("shared"),
        ],
            new RecordingOAuthAuthentication());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            runtime.ResolveModel(new ModelOptions("shared")));
        var resolved = runtime.ResolveModel(
            new ModelOptions("shared", Provider: "OpenAI API"));

        Assert.Contains("provider/model", exception.Message, StringComparison.Ordinal);
        Assert.Equal("OpenAI API", resolved.Provider);
    }

    [Fact]
    public void ResolveModelClearsUnsupportedReasoningEffort()
    {
        var model = ApiModel("plain-model") with { SupportsReasoning = false };
        var runtime = new CatalogModelProvider([model], new RecordingOAuthAuthentication());

        var resolved = runtime.ResolveModel(new ModelOptions(
            model.Name,
            ReasoningEffort.High,
            model.Provider));

        Assert.Null(resolved.ReasoningEffort);
    }

    [Fact]
    public void DuplicateQualifiedModelsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new CatalogModelProvider(
            [ApiModel("same"), ApiModel("same")],
            new RecordingOAuthAuthentication()));
    }

    private static AvailableModel OAuthModel(string name) => new()
    {
        Name = name,
        Provider = "OpenAI OAuth",
        Api = OpenAIOAuthProvider.ProviderId,
        MaxContextTokens = 1000,
    };

    private static AvailableModel ApiModel(string name) => new()
    {
        Name = name,
        Provider = "OpenAI API",
        Api = OpenAIResponseProvider.ProviderId,
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "sk-test-key",
        MaxContextTokens = 1000,
    };

    private sealed class RecordingProvider(string id) : IModelProvider
    {
        public string Id { get; } = id;
        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelResponseCompleted(
                new ModelResponse("response-1", string.Empty, []));
        }
    }

    private sealed class RecordingOAuthAuthentication : IOAuthAuthentication
    {
        public bool IsSignedIn { get; init; } = true;
        public int AuthorizationCount { get; private set; }

        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            return IsSignedIn
                ? Task.FromResult("token")
                : Task.FromException<string>(new InvalidOperationException("로그인이 필요합니다."));
        }

        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task LogoutAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
