using Madi.Core;
using Madi.Networking;
using Madi.Runtime.Authentication;
using Madi.Runtime.Persistence;
using Madi.Runtime.Providers;
using Madi.Runtime.Tools;
using Madi.UI;

namespace Madi;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var credentialStore = new FileCredentialStore();
        IOAuthAuthentication authentication = new OAuthAuthentication(
            credentialStore,
            OpenAIOAuthProvider.ProviderId);
        var tools = new ToolCatalog(Environment.CurrentDirectory);
        INetworkClient client = new NetworkClient(
            OpenAINetworkConfiguration.Create(authentication));
        IModelProvider provider = new OpenAIOAuthProvider(client);
        IAgent agent = new Agent(provider, tools.All);

        var storageRoot = Environment.GetEnvironmentVariable("MADI_HOME");
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            storageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "madi");
        }

        IWorkSessionStore store = new FileWorkSessionStore(storageRoot);
        var defaultModel = new ModelOptions(
            "gpt-5.6-luna",
            ReasoningEffort.Medium);

        IUserInterface ui = new ConsoleUserInterface();
        var application = new Application(
            agent,
            store,
            defaultModel,
            authentication,
            ui);
        try
        {
            return await application.RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
