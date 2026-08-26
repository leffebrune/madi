using Madi.Core;
using Madi.Runtime.Authentication;

namespace Madi.Runtime.Providers;

/// <summary>Compatibility coordinator for hosts whose entire model catalog uses one OAuth account.</summary>
public sealed class OAuthModelAuthenticationCoordinator : IModelAuthenticationCoordinator
{
    private readonly IReadOnlyList<AvailableModel> _models;
    private readonly IOAuthAuthentication _authentication;

    public OAuthModelAuthenticationCoordinator(
        IReadOnlyList<AvailableModel> models,
        IOAuthAuthentication authentication)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    public ModelOptions ResolveModel(ModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var matches = _models.Where(model =>
            string.Equals(model.Name, options.Model, StringComparison.OrdinalIgnoreCase)
            && (options.Provider is null || string.Equals(
                model.Provider,
                options.Provider,
                StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => options with
            {
                Model = matches[0].Name,
                Provider = matches[0].Provider,
                ReasoningEffort = matches[0].SupportsReasoning
                    ? options.ReasoningEffort
                    : null,
            },
            0 => throw new InvalidOperationException($"사용할 수 없는 모델입니다: {options.QualifiedName}"),
            _ => throw new InvalidOperationException(
                $"모델 이름이 중복됩니다. provider/model 형식으로 선택하세요: {options.Model}"),
        };
    }

    public async ValueTask<ModelAuthenticationInfo> GetAuthenticationAsync(
        ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        _ = ResolveModel(options);
        try
        {
            _ = await _authentication.GetAuthorizationAsync(cancellationToken);
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
    }

    public Task LoginAsync(ModelOptions options, CancellationToken cancellationToken = default)
    {
        _ = ResolveModel(options);
        return _authentication.LoginAsync(cancellationToken);
    }

    public Task LogoutAsync(ModelOptions options, CancellationToken cancellationToken = default)
    {
        _ = ResolveModel(options);
        return _authentication.LogoutAsync(cancellationToken);
    }
}
