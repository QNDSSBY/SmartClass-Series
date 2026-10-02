using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SmartClassNight;

public partial class App : Application
{
    private Window? mainWindow;

    public App()
    {
        this.InitializeComponent();

        // 崩溃排查：任何未处理异常都写日志（kiosk 无人值守，必须留下现场）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog("Task", e.Exception);
            try { e.SetObserved(); } catch { }
        };

        this.UnhandledException += (sender, e) =>
        {
            CrashLog("Xaml", e.Exception);
            Debug.WriteLine($"[未处理异常] {e.Exception.Message}");
            e.Handled = true;      // UI 线程异常不让程序退出
        };
    }

    /// <summary>把异常写入 %LOCALAPPDATA%\金华一中科技校园套件\crash.log（同时写 island.log）。</summary>
    internal static void CrashLog(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "金华一中科技校园套件");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {ex}{Environment.NewLine}");
            MainWindow.IslandLog($"异常（{source}）：{ex?.GetType().Name} {ex?.Message}");
        }
        catch { }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 现场排查用诊断开关：智慧课堂系统.exe /testsound
        // 只播放一次新消息提示音（不受提示音开关影响），5 秒后自动退出，不显示主界面。
        // 播放结果写入 %LOCALAPPDATA%\金华一中科技校园套件\island.log（含实际使用的音频文件）。
        if (Environment.GetCommandLineArgs()
                       .Skip(1)
                       .Any(a => a.Equals("/testsound", StringComparison.OrdinalIgnoreCase)))
        {
            RunSoundTest();
            return;
        }

        mainWindow = new MainWindow();
        mainWindow.Activate();
    }

    private static void RunSoundTest()
    {
        NotificationSound.Preview();
        var timer = new System.Threading.Timer(_ => Environment.Exit(0), null, 5000, System.Threading.Timeout.Infinite);
        GC.KeepAlive(timer);
    }
}
