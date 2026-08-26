using Madi.Core;

namespace Madi.Runtime.Providers;

public enum ModelAuthenticationKind
{
    None,
    OAuth,
    ApiKey,
}

public enum ModelAuthenticationStatus
{
    Unknown,
    NotRequired,
    ActionRequired,
    Ready,
}

public sealed record ModelAuthenticationInfo(
    ModelAuthenticationKind Kind,
    ModelAuthenticationStatus Status,
    string? Message = null);

public interface IModelAuthenticationCoordinator
{
    ModelOptions ResolveModel(ModelOptions options);

    ValueTask<ModelAuthenticationInfo> GetAuthenticationAsync(
        ModelOptions options,
        CancellationToken cancellationToken = default);

    Task LoginAsync(ModelOptions options, CancellationToken cancellationToken = default);

    Task LogoutAsync(ModelOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Routes model requests and manages authentication for the selected model provider.</summary>
public interface IModelProviderRuntime : IModelProvider, IModelAuthenticationCoordinator;
