using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Madi.GUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        AppWindow.Closing += OnClosing;
        RootFrame.Navigate(typeof(MainPage));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (RootFrame.Content is MainPage page)
        {
            page.CancelAndDispose();
        }
    }
}
