using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace SmartClassNight;

public partial class App : Application
{
    private Window? mainWindow;

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += (sender, e) =>
        {
            Debug.WriteLine($"[未处理异常] {e.Exception.Message}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        mainWindow = new MainWindow();
        mainWindow.Activate();
    }
}