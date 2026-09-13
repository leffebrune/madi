using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Madi.GUI;

public sealed partial class MainWindow : Window
{
    private const uint WmSetIcon = 0x0080;
    private static readonly nint IconSmall = 0;
    private static readonly nint IconBig = 1;

    private nint _largeIcon;
    private nint _smallIcon;
    private bool _closeApproved;
    private bool _closeInProgress;

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIconFromExecutable();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        RootFrame.Navigate(typeof(MainPage));
    }

    private void SetWindowIconFromExecutable()
    {
        string? executablePath = Environment.ProcessPath;
        if (executablePath is null || ExtractIconEx(executablePath, 0, out _largeIcon, out _smallIcon, 1) == 0)
        {
            return;
        }

        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_largeIcon != 0)
        {
            SendMessage(windowHandle, WmSetIcon, IconBig, _largeIcon);
        }

        if (_smallIcon != 0)
        {
            SendMessage(windowHandle, WmSetIcon, IconSmall, _smallIcon);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        args.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        try
        {
        if (RootFrame.Content is MainPage page)
        {
            if (!await page.TryCloseAsync()) return;
        }
        _closeApproved = true;
        Close();
        }
        finally { _closeInProgress = false; }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_largeIcon != 0)
        {
            DestroyIcon(_largeIcon);
            _largeIcon = 0;
        }

        if (_smallIcon != 0)
        {
            DestroyIcon(_smallIcon);
            _smallIcon = 0;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        out nint largeIcon,
        out nint smallIcon,
        uint iconCount);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
