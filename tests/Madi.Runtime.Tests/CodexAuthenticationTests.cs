using System.Net;
using System.Text;
using System.Text.Json;
using Madi.Runtime.Authentication;
using Madi.Runtime.Providers;

namespace Madi.Runtime.Tests;

public sealed class CodexAuthenticationTests
{
    private const string ProviderId = OpenAIOAuthProvider.ProviderId;
    private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    [Fact]
    public async Task LoginRegistersInstallationAndStoresIssuedClientAndScopes()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        Uri? authorization = null;
        Dictionary<string, string>? exchange = null;
        using var client = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://auth.openai.com/api/accounts/oauth/token", request.RequestUri!.AbsoluteUri);
            exchange = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            return TokenResponse();
        }));
        var authentication = new OAuthAuthentication(store, ProviderId, client, (uri, _) =>
        {
            authorization = uri;
            var values = ParseQuery(uri.Query);
            return Task.FromResult(new Uri($"{values["redirect_uri"]}?code=code&state={values["state"]}&client_id=oaiapp_madi"));
        });
        var before = DateTimeOffset.UtcNow;
        await authentication.LoginAsync();

        Assert.NotNull(authorization);
        Assert.Equal("https://auth.openai.com/api/accounts/authorize", authorization.GetLeftPart(UriPartial.Path));
        var query = ParseQuery(authorization.Query);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("madi", query["agent_name_hint"]);
        Assert.Equal($"urn:uuid:{await store.GetOrCreateDeviceIdAsync():D}", query["ext_agent_host_id"]);
        Assert.Equal("http://127.0.0.1:1455/auth/callback", query["redirect_uri"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Equal(Scopes, query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["nonce"]));
        Assert.NotNull(exchange);
        Assert.Equal("oaiapp_madi", exchange["client_id"]);
        Assert.Equal(query["resource"], exchange["resource"]);
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            Encoding.ASCII.GetBytes(exchange["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        var credential = Assert.IsType<OAuthCredential>(await store.LoadAsync(ProviderId));
        Assert.Equal("oaiapp_madi", credential.ClientId);
        Assert.Contains("chatgpt.tokens.use.direct", credential.Scopes!);
        Assert.InRange(credential.ExpiresAt, before.AddSeconds(3420), DateTimeOffset.UtcNow.AddSeconds(3420));
        Assert.Equal("new-access", await authentication.GetAuthorizationAsync());
        var reopened = new FileCredentialStore(directory.AuthPath);
        Assert.Equal(await store.GetOrCreateDeviceIdAsync(), await reopened.GetOrCreateDeviceIdAsync());
    }

    [Theory]
    [InlineData("state")]
    [InlineData("client_id")]
    [InlineData("code")]
    public async Task InvalidCallbackNeverExchangesOrStoresTokens(string missing)
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not exchange")));
        var authentication = new OAuthAuthentication(store, ProviderId, client, (uri, _) =>
        {
            var parameters = new Dictionary<string, string>
            {
                ["code"] = "code", ["state"] = ParseQuery(uri.Query)["state"], ["client_id"] = "issued",
            };
            parameters.Remove(missing);
            return Task.FromResult(new Uri("http://127.0.0.1:1455/auth/callback?" +
                string.Join('&', parameters.Select(pair => $"{pair.Key}={pair.Value}"))));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => authentication.LoginAsync());
        Assert.Null(await store.LoadAsync(ProviderId));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("refresh_token")]
    [InlineData("expires_in")]
    [InlineData("id_token")]
    public async Task RejectsIncompleteTokenResponse(string invalidField)
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse(invalidField))));
        var authentication = new OAuthAuthentication(store, ProviderId, client, (uri, _) =>
            Task.FromResult(new Uri($"http://127.0.0.1:1455/auth/callback?code=code&state={ParseQuery(uri.Query)["state"]}&client_id=issued")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authentication.LoginAsync());
        Assert.Null(await store.LoadAsync(ProviderId));
    }

    [Fact]
    public async Task RefreshIsSerializedAndPersistsRotationAfterRequestCancellation()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        await store.SaveAsync(ProviderId, ExpiredCredential());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            var form = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("issued-client", form["client_id"]);
            Assert.Equal("old-refresh", form["refresh_token"]);
            Assert.Equal("https://api.openai.com/v1", form["resource"]);
            Assert.False(form.ContainsKey("scope"));
            started.SetResult();
            await finish.Task.WaitAsync(cancellationToken);
            return TokenResponse();
        }));
        using var cancel = new CancellationTokenSource();
        var first = new OAuthAuthentication(store, ProviderId, client).GetAuthorizationAsync(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = new OAuthAuthentication(new FileCredentialStore(directory.AuthPath), ProviderId, client).GetAuthorizationAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        finish.SetResult();
        Assert.Equal("new-access", await second);
        Assert.Equal(1, calls);
        Assert.Equal("new-refresh", (await store.LoadAsync(ProviderId))!.RefreshToken);
    }

    [Fact]
    public async Task LogoutWaitsForRefreshAndDoesNotRestoreCredential()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        await store.SaveAsync(ProviderId, ExpiredCredential());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, cancellationToken) =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(cancellationToken);
            return TokenResponse();
        }));
        var authentication = new OAuthAuthentication(store, ProviderId, client);
        var refresh = authentication.GetAuthorizationAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = authentication.LogoutAsync();
        finish.SetResult();
        await Task.WhenAll(refresh, logout);
        Assert.Null(await store.LoadAsync(ProviderId));
    }

    [Fact]
    public async Task RefreshRequiresReplacementRefreshTokenAndKeepsStoredCredentialOnFailure()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        await store.SaveAsync(ProviderId, ExpiredCredential());
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse("refresh_token"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OAuthAuthentication(store, ProviderId, client).GetAuthorizationAsync());
        Assert.Equal("old-refresh", (await store.LoadAsync(ProviderId))!.RefreshToken);
    }

    [Fact]
    public async Task StoreWaitsForFileLockAndHonorsCancellation()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        await store.SaveAsync(ProviderId, ExpiredCredential());
        using (var heldLock = new FileStream($"{directory.AuthPath}.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancel = new CancellationTokenSource();
            var pending = store.LoadAsync(ProviderId, cancel.Token);
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.NotNull(await store.LoadAsync(ProviderId));
    }

    [Fact]
    public async Task LegacyCredentialRequiresNewLogin()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        await store.SaveAsync(ProviderId, new OAuthCredential("old", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OAuthAuthentication(store, ProviderId).GetAuthorizationAsync());
        Assert.Contains("다시 로그인", exception.Message);
    }

    [Fact]
    public async Task DirectNetworkConfigurationUsesOpaqueBearerWithoutCodexHeadersOrCompression()
    {
        using var directory = new TestDirectory();
        var store = new FileCredentialStore(directory.AuthPath);
        await store.SaveAsync(ProviderId, ExpiredCredential() with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var options = OpenAINetworkConfiguration.Create(new OAuthAuthentication(store, ProviderId));
        Assert.Equal("https://api.openai.com/v1/responses", options.Endpoint.AbsoluteUri);
        Assert.False(options.UseBrotliCompression);
        Assert.False(options.DefaultHeaders.ContainsKey("OpenAI-Beta"));
        Assert.False(options.DefaultHeaders.ContainsKey("originator"));
        var headers = await options.GetHeadersAsync!(CancellationToken.None);
        Assert.Equal("Bearer old-access", headers["Authorization"]);
        Assert.False(headers.ContainsKey("chatgpt-account-id"));
    }

    private static OAuthCredential ExpiredCredential() => new("old-access", "old-refresh",
        DateTimeOffset.UtcNow, "issued-client", Scopes.Split(' '));

    private static HttpResponseMessage TokenResponse(string? invalidField = null)
    {
        var values = new Dictionary<string, object>
        {
            ["access_token"] = "new-access", ["refresh_token"] = "new-refresh",
            ["expires_in"] = 3600, ["scope"] = Scopes, ["id_token"] = "id-token",
        };
        if (invalidField == "scope") values["scope"] = "openid resource.invoke";
        else if (invalidField == "expires_in") values["expires_in"] = 0;
        else if (invalidField is not null) values.Remove(invalidField);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json"),
        };
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "madi-codex-tests", Guid.NewGuid().ToString("N"));
        public string AuthPath => Path.Combine(_path, "auth.json");
        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
