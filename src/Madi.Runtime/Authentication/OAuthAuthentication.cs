using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Madi.Runtime.Authentication;

public sealed class OAuthAuthentication : IOAuthAuthentication
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private static readonly Uri AuthorizeEndpoint = new("https://auth.openai.com/oauth/authorize");
    private static readonly Uri TokenEndpoint = new("https://auth.openai.com/oauth/token");
    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;
    private readonly string _providerId;

    public OAuthAuthentication(
        ICredentialStore credentialStore,
        string providerId,
        HttpClient? httpClient = null)
    {
        _credentialStore = credentialStore;
        _providerId = providerId;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        var credential = await _credentialStore.LoadAsync(_providerId, cancellationToken);
        if (credential is null)
        {
            throw new InvalidOperationException("로그인이 필요합니다.");
        }

        return credential.AccessToken;
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var state = CreateRandomUrlSafeValue();
        var verifier = CreateRandomUrlSafeValue();
        var challenge = CreateCodeChallenge(verifier);
        var authorizationUri = CreateAuthorizationUri(state, challenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:1455/");
        listener.Start();

        Process.Start(new ProcessStartInfo(authorizationUri.ToString()) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);

        var request = context.Request;
        if (request.Url?.AbsolutePath != "/auth/callback" || request.QueryString["state"] != state)
        {
            await WriteCallbackAsync(context.Response, 400, "로그인 검증에 실패했습니다.");
            throw new InvalidOperationException("OAuth callback state가 일치하지 않습니다.");
        }

        var code = request.QueryString["code"];
        if (string.IsNullOrWhiteSpace(code))
        {
            await WriteCallbackAsync(context.Response, 400, "인가 코드가 없습니다.");
            throw new InvalidOperationException("OAuth authorization code가 없습니다.");
        }

        await WriteCallbackAsync(context.Response, 200, "로그인이 완료되었습니다. 이 창을 닫아도 됩니다.");
        var token = await ExchangeCodeAsync(code, verifier, cancellationToken);
        await _credentialStore.SaveAsync(_providerId, token, cancellationToken);
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        _credentialStore.DeleteAsync(_providerId, cancellationToken);

    private static Uri CreateAuthorizationUri(string state, string challenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "madi",
        };

        var query = string.Join(
            "&",
            parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{AuthorizeEndpoint}?{query}");
    }

    private async Task<OAuthCredential> ExchangeCodeAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = RedirectUri,
            }),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.GetProperty("refresh_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
        {
            throw new InvalidOperationException("OAuth token 응답이 불완전합니다.");
        }

        return new OAuthCredential(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static string CreateRandomUrlSafeValue() =>
        ToBase64Url(RandomNumberGenerator.GetBytes(32));

    private static string CreateCodeChallenge(string verifier) =>
        ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WriteCallbackAsync(
        HttpListenerResponse response,
        int statusCode,
        string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes($"<p>{WebUtility.HtmlEncode(message)}</p>");
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
