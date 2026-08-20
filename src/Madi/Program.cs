using Madi.App;
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

        try
        {
            var host = await MadiAppFactory.CreateAsync(
                cancellationToken: cancellation.Token);
            IUserInterface ui = new ConsoleUserInterface();
            var application = new ConsoleApplication(
                host.Controller,
                host.UtilityInstaller,
                ui);
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
