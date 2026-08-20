using Madi.App;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Madi.TUI;

public static class TuiApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine("사용법: Madi.TUI");
            return 1;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine(
                "Madi.TUI는 대화형 터미널이 필요합니다. 리디렉션 환경에서는 기존 Madi를 사용하세요.");
            return 1;
        }

        var host = await MadiAppFactory.CreateAsync();
        using IApplication app = Application.Create();
        app.Init();
        using Window window = new MadiWindow(
            app,
            host.Controller,
            host.UtilityInstaller);

        try
        {
            app.Run(window);
            return 0;
        }
        finally
        {
            // Disposing the Window and IApplication restores the terminal in all exit paths.
        }
    }
}
