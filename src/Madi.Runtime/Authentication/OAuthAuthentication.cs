/*
Adapted from pi packages/ai/src/auth/oauth/openai-chatgpt.ts.

MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Madi.Runtime.Authentication;

/// <summary>Sign in with ChatGPT for the Codex provider's direct Responses API access.</summary>
public sealed class OAuthAuthentication : IOAuthAuthentication
{
    private const string RedirectUri = "http://127.0.0.1:1455/auth/callback";
    private const string Resource = "https://api.openai.com/v1";
    private const string DirectTokenScope = "chatgpt.tokens.use.direct";
    private const string Scope = "openid profile email offline_access resource.invoke " + DirectTokenScope;
    private static readonly Uri AuthorizeEndpoint = new("https://auth.openai.com/api/accounts/authorize");
    private static readonly Uri TokenEndpoint = new("https://auth.openai.com/api/accounts/oauth/token");
    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;
    private readonly string _providerId;
    private readonly Func<Uri, CancellationToken, Task<Uri>> _authorize;

    public OAuthAuthentication(
        ICredentialStore credentialStore,
        string providerId,
        HttpClient? httpClient = null,
        Func<Uri, CancellationToken, Task<Uri>>? authorize = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _providerId = providerId;
        _httpClient = httpClient ?? new HttpClient();
        _authorize = authorize ?? AuthorizeInBrowserAsync;
    }

    public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) =>
        ResolveAuthorizationAsync(cancellationToken).WaitAsync(cancellationToken);

    private async Task<string> ResolveAuthorizationAsync(CancellationToken cancellationToken)
    {
        var credential = RequireCredential(await _credentialStore.LoadAsync(_providerId, cancellationToken));
        if (credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return credential.AccessToken;

        credential = RequireCredential(await _credentialStore.UpdateAsync(_providerId, async current =>
        {
            var stored = RequireCredential(current);
            if (stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return stored;
            // Once refresh starts, only its own timeout may interrupt it, not request cancellation.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = stored.ClientId!,
                ["refresh_token"] = stored.RefreshToken,
                ["resource"] = Resource,
            }, stored.ClientId!, requireIdToken: false, timeout.Token);
        }, cancellationToken));
        return credential.AccessToken;
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var deviceId = await _credentialStore.GetOrCreateDeviceIdAsync(cancellationToken);
        var state = CreateRandomUrlSafeValue();
        var verifier = CreateRandomUrlSafeValue();
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = "dynamic_agent_client",
            ["agent_name_hint"] = "madi",
            ["ext_agent_host_id"] = $"urn:uuid:{deviceId:D}",
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["resource"] = Resource,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["nonce"] = CreateRandomUrlSafeValue(),
        };
        var query = string.Join("&", parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        var callback = await _authorize(new Uri($"{AuthorizeEndpoint}?{query}"), cancellationToken);
        if (callback.GetLeftPart(UriPartial.Path) != RedirectUri)
            throw new InvalidOperationException("OAuth callback URL이 올바르지 않습니다.");
        var callbackParameters = ParseQuery(callback);
        if (callbackParameters.GetValueOrDefault("state") != state)
            throw new InvalidOperationException("OAuth callback state가 일치하지 않습니다.");
        if (callbackParameters.TryGetValue("error", out var error))
            throw new InvalidOperationException($"ChatGPT 인증에 실패했습니다: {error}");
        var code = RequireParameter(callbackParameters, "code");
        var clientId = RequireParameter(callbackParameters, "client_id");
        var credential = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri,
            ["resource"] = Resource,
        }, clientId, requireIdToken: true, cancellationToken);
        await _credentialStore.SaveAsync(_providerId, credential, cancellationToken);
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        _credentialStore.DeleteAsync(_providerId, cancellationToken);

    private static OAuthCredential RequireCredential(OAuthCredential? credential)
    {
        if (credential is null) throw new InvalidOperationException("로그인이 필요합니다.");
        if (string.IsNullOrWhiteSpace(credential.ClientId)
            || string.IsNullOrWhiteSpace(credential.AccessToken)
            || string.IsNullOrWhiteSpace(credential.RefreshToken)
            || credential.Scopes?.Contains(DirectTokenScope, StringComparer.Ordinal) != true)
        {
            throw new InvalidOperationException("ChatGPT에 다시 로그인해야 합니다. 기존 Codex 인증은 사용할 수 없습니다.");
        }
        return credential;
    }

    private async Task<OAuthCredential> RequestTokenAsync(
        Dictionary<string, string> parameters,
        string clientId,
        bool requireIdToken,
        CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(parameters);
        using var response = await _httpClient.PostAsync(TokenEndpoint, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ChatGPT OAuth token 요청 실패 ({(int)response.StatusCode}): {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var access = RequireTokenString(root, "access_token");
        var refresh = RequireTokenString(root, "refresh_token");
        var scopes = RequireTokenString(root, "scope").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(DirectTokenScope, StringComparer.Ordinal))
            throw new InvalidOperationException($"ChatGPT 인증에 {DirectTokenScope} 권한이 없습니다.");
        if (requireIdToken) _ = RequireTokenString(root, "id_token");
        if (!root.TryGetProperty("expires_in", out var expiry)
            || expiry.ValueKind != JsonValueKind.Number
            || !expiry.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidOperationException("OAuth token 응답의 expires_in이 올바르지 않습니다.");
        return new OAuthCredential(access, refresh, DateTimeOffset.UtcNow.AddSeconds(seconds).AddMinutes(-3), clientId, scopes);
    }

    private static string RequireTokenString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"OAuth token 응답에 {name}이 없습니다.");

    private static string RequireParameter(Dictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"OAuth callback에 {name}이 없습니다.");

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var name = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!parameters.TryAdd(name, value))
                throw new InvalidOperationException($"OAuth callback에 {name}이 중복되었습니다.");
        }
        return parameters;
    }

    private static async Task<Uri> AuthorizeInBrowserAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:1455/");
        try { listener.Start(); }
        catch (HttpListenerException error)
        {
            throw new InvalidOperationException("ChatGPT 로그인 콜백 서버를 시작할 수 없습니다. 포트 1455를 사용하는 다른 로그인이나 Codex CLI를 종료한 후 다시 시도하세요.", error);
        }
        Process.Start(new ProcessStartInfo(authorizationUri.ToString()) { UseShellExecute = true });
        var expectedState = ParseQuery(authorizationUri)["state"];
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var callback = context.Request.Url;
            if (callback?.GetLeftPart(UriPartial.Path) != RedirectUri)
            {
                await WriteCallbackAsync(context.Response, 404, "로그인 콜백 경로가 아닙니다.");
                continue;
            }
            Dictionary<string, string> parameters;
            try { parameters = ParseQuery(callback); }
            catch (InvalidOperationException)
            {
                await WriteCallbackAsync(context.Response, 400, "로그인 콜백 매개변수가 중복되었습니다.");
                continue;
            }
            if (parameters.GetValueOrDefault("state") != expectedState
                || (!parameters.ContainsKey("error")
                    && (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("code"))
                        || string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("client_id")))))
            {
                await WriteCallbackAsync(context.Response, 400, "로그인 콜백이 올바르지 않습니다.");
                continue;
            }
            await WriteCallbackAsync(context.Response, parameters.ContainsKey("error") ? 400 : 200,
                parameters.ContainsKey("error") ? "ChatGPT 인증에 실패했습니다." : "ChatGPT 인증을 받았습니다. 이 창을 닫아도 됩니다.");
            return callback;
        }
    }

    private static string CreateRandomUrlSafeValue() => ToBase64Url(RandomNumberGenerator.GetBytes(32));
    private static string ToBase64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WriteCallbackAsync(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes($"<p>{WebUtility.HtmlEncode(message)}</p>");
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
