using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Networking;
using Madi.Runtime.Authentication;

namespace Madi.Runtime.Providers;

/// <summary>Resolves a catalog model to its concrete provider and authentication mechanism.</summary>
public sealed class CatalogModelProvider : IModelProviderRuntime
{
    public const string ProviderId = "catalog";

    private readonly IReadOnlyList<AvailableModel> _models;
    private readonly IOAuthAuthentication _oauthAuthentication;
    private readonly Func<AvailableModel, IModelProvider>? _providerFactory;
    private readonly Dictionary<ProviderKey, IModelProvider> _providers = [];
    private readonly object _providerLock = new();

    public CatalogModelProvider(
        IReadOnlyList<AvailableModel> models,
        IOAuthAuthentication oauthAuthentication,
        Func<AvailableModel, IModelProvider>? providerFactory = null)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _oauthAuthentication = oauthAuthentication
            ?? throw new ArgumentNullException(nameof(oauthAuthentication));
        _providerFactory = providerFactory;

        var duplicate = models
            .GroupBy(static model => model.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"The model catalog contains duplicate model '{duplicate.Key}'.");
        }
    }

    public string Id => ProviderId;

    public ModelOptions ResolveModel(ModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var model = ResolveAvailableModel(options);
        return options with
        {
            Model = model.Name,
            Provider = model.Provider,
            ReasoningEffort = model.SupportsReasoning ? options.ReasoningEffort : null,
        };
    }

    public async ValueTask<ModelAuthenticationInfo> GetAuthenticationAsync(
        ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveAvailableModel(options);
        switch (model.Api.ToLowerInvariant())
        {
            case OpenAIOAuthProvider.ProviderId:
                try
                {
                    _ = await _oauthAuthentication.GetAuthorizationAsync(cancellationToken);
                    return new ModelAuthenticationInfo(
                        ModelAuthenticationKind.OAuth,
                        ModelAuthenticationStatus.Ready);
                }
                catch (InvalidOperationException)
                {
                    return new ModelAuthenticationInfo(
                        ModelAuthenticationKind.OAuth,
                        ModelAuthenticationStatus.ActionRequired,
                        "선택한 모델을 사용하려면 OAuth 로그인이 필요합니다.");
                }

            case OpenAIResponseProvider.ProviderId:
                if (string.IsNullOrWhiteSpace(model.BaseUrl))
                {
                    return new ModelAuthenticationInfo(
                        ModelAuthenticationKind.ApiKey,
                        ModelAuthenticationStatus.ActionRequired,
                        $"모델 '{model.QualifiedName}'의 baseUrl을 models.json에 설정하세요.");
                }
                if (string.IsNullOrWhiteSpace(model.ApiKey))
                {
                    return new ModelAuthenticationInfo(
                        ModelAuthenticationKind.ApiKey,
                        ModelAuthenticationStatus.ActionRequired,
                        $"모델 '{model.QualifiedName}'의 apiKey를 models.json에 설정하세요.");
                }
                try
                {
                    _ = OpenAIResponseNetworkConfiguration.Create(model.BaseUrl, model.ApiKey);
                    return new ModelAuthenticationInfo(
                        ModelAuthenticationKind.ApiKey,
                        ModelAuthenticationStatus.Ready);
                }
                catch (ArgumentException exception)
                {
                    return new ModelAuthenticationInfo(
                        ModelAuthenticationKind.ApiKey,
                        ModelAuthenticationStatus.ActionRequired,
                        $"모델 '{model.QualifiedName}'의 API 설정이 잘못되었습니다: {exception.Message}");
                }

            default:
                return new ModelAuthenticationInfo(
                    ModelAuthenticationKind.None,
                    ModelAuthenticationStatus.ActionRequired,
                    $"지원하지 않는 provider API입니다: {model.Api}");
        }
    }

    public Task LoginAsync(
        ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveAvailableModel(options);
        if (!string.Equals(
                model.Api,
                OpenAIOAuthProvider.ProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{model.QualifiedName}'은 OAuth 로그인을 지원하지 않습니다.");
        }

        return _oauthAuthentication.LoginAsync(cancellationToken);
    }

    public Task LogoutAsync(
        ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveAvailableModel(options);
        if (!string.Equals(
                model.Api,
                OpenAIOAuthProvider.ProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{model.QualifiedName}'은 OAuth 로그아웃을 지원하지 않습니다.");
        }

        return _oauthAuthentication.LogoutAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = ResolveAvailableModel(request.Options);
        var provider = GetOrCreateProvider(model);
        var routedRequest = request with
        {
            Options = request.Options with
            {
                Model = model.Name,
                Provider = model.Provider,
            },
        };

        await foreach (var item in provider.StreamAsync(routedRequest, cancellationToken))
        {
            yield return item;
        }
    }

    private AvailableModel ResolveAvailableModel(ModelOptions options)
    {
        IEnumerable<AvailableModel> matches = _models.Where(model =>
            string.Equals(model.Name, options.Model, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(options.Provider))
        {
            matches = matches.Where(model => string.Equals(
                model.Provider,
                options.Provider,
                StringComparison.OrdinalIgnoreCase));
        }

        var result = matches.Take(2).ToArray();
        return result.Length switch
        {
            1 => result[0],
            0 => throw new InvalidOperationException(
                $"사용할 수 없는 모델입니다: {options.QualifiedName}"),
            _ => throw new InvalidOperationException(
                $"모델 이름이 중복됩니다. provider/model 형식으로 선택하세요: {options.Model}"),
        };
    }

    private IModelProvider GetOrCreateProvider(AvailableModel model)
    {
        var key = new ProviderKey(
            model.Provider,
            model.Api,
            model.BaseUrl,
            model.ApiKey);
        lock (_providerLock)
        {
            if (_providers.TryGetValue(key, out var provider))
            {
                return provider;
            }

            provider = CreateProvider(model);
            _providers.Add(key, provider);
            return provider;
        }
    }

    private IModelProvider CreateProvider(AvailableModel model)
    {
        if (_providerFactory is not null)
        {
            return _providerFactory(model)
                ?? throw new InvalidOperationException(
                    $"Provider factory returned null for '{model.QualifiedName}'.");
        }

        return model.Api.ToLowerInvariant() switch
        {
            OpenAIOAuthProvider.ProviderId => new OpenAIOAuthProvider(
                new NetworkClient(OpenAINetworkConfiguration.Create(_oauthAuthentication))),
            OpenAIResponseProvider.ProviderId => new OpenAIResponseProvider(
                model.BaseUrl
                    ?? throw new InvalidOperationException(
                        $"모델 '{model.QualifiedName}'의 baseUrl이 필요합니다."),
                model.ApiKey
                    ?? throw new InvalidOperationException(
                        $"모델 '{model.QualifiedName}'의 apiKey가 필요합니다.")),
            _ => throw new InvalidOperationException(
                $"지원하지 않는 provider API입니다: {model.Api}"),
        };
    }

    private sealed record ProviderKey(
        string Provider,
        string Api,
        string? BaseUrl,
        string? ApiKey);
}
