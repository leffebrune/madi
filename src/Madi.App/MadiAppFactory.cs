using Madi.Core;
using Madi.Runtime.Agents;
using Madi.Runtime.Authentication;
using Madi.Runtime.Persistence;
using Madi.Runtime.Providers;
using Madi.Runtime.Tools;
using Madi.Services;

namespace Madi.App;

public sealed record MadiAppHost(
    MadiController Controller,
    UtilityInstaller UtilityInstaller);

public static class MadiAppFactory
{
    public static async Task<MadiAppHost> CreateAsync(
        string? storageRoot = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        storageRoot = ResolveStorageRoot(storageRoot);
        workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        var utilityBinDirectory = Path.Combine(storageRoot, "bin");

        var credentialStore = new FileCredentialStore();
        IOAuthAuthentication authentication = new OAuthAuthentication(
            credentialStore,
            OpenAIOAuthProvider.ProviderId);
        IWorkSessionStore store = new FileWorkSessionStore(storageRoot);
        IModelCatalog modelCatalog = new FileModelCatalog(storageRoot);
        var availableModels = await modelCatalog.LoadAsync(cancellationToken);
        ISubagentCatalog subagentCatalog = new FileSubagentCatalog(
            storageRoot,
            availableModels.FirstOrDefault()?.QualifiedName);
        var subagentDefinitions = await subagentCatalog.LoadAsync(cancellationToken);
        ISystemPromptCatalog systemPromptCatalog = new FileSystemPromptCatalog(storageRoot);
        var systemPrompt = await systemPromptCatalog.LoadAsync(cancellationToken);

        var tools = new ToolCatalog(
            workingDirectory,
            utilityBinDirectory: utilityBinDirectory);
        IModelProviderRuntime provider = new CatalogModelProvider(
            availableModels,
            authentication);
        var agentTools = tools.All.ToList();
        if (subagentDefinitions.Count > 0)
        {
            ISubagentRunner subagentRunner = new SubagentRunner(
                provider,
                subagentDefinitions,
                availableModels,
                tools.All);
            agentTools.Add(new SubagentTool(subagentRunner, subagentDefinitions));
        }

        var systemInstructions = SystemInstructionComposer.Compose(systemPrompt, agentTools);
        IAgent agent = new Agent(provider, systemInstructions, agentTools);
        var defaultAvailableModel = availableModels.FirstOrDefault()
            ?? throw new InvalidDataException("사용 가능한 모델이 없습니다.");
        var defaultModel = new ModelOptions(
            defaultAvailableModel.Name,
            defaultAvailableModel.SupportsReasoning ? ReasoningEffort.Medium : null,
            defaultAvailableModel.Provider);
        var controller = new MadiController(
            agent,
            store,
            defaultModel,
            availableModels,
            provider,
            workingDirectory);
        var utilityInstaller = new UtilityInstaller(
            new GitHubReleaseFileDownloader(),
            utilityBinDirectory);
        return new MadiAppHost(controller, utilityInstaller);
    }

    public static string ResolveStorageRoot(string? storageRoot = null)
    {
        storageRoot ??= Environment.GetEnvironmentVariable("MADI_HOME");
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            storageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "madi");
        }

        return Path.GetFullPath(storageRoot);
    }
}
