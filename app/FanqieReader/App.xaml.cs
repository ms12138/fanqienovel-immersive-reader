using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FanqieReader;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\FanqieReader.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "沉浸式阅读器已经在运行了。\n\nWebView2 的登录配置不能同时被两个窗口占用，请先关闭已有窗口。",
                "沉浸式阅读器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Keeps the very first frame from flashing white before the page paints.
        // WebView2 reads this once, at creation time.
        // WebView2 reads this once, when its browser process starts: it removes the
        // white flash before the page paints. 00 alpha keeps the surface transparent.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never let a network/WebView edge case take down the reading window.
        e.Handled = true;
        LogException(e.Exception);
        try
        {
            MessageBox.Show(
                "阅读器遇到一个小问题，但运行没有中断：\n\n" + e.Exception.Message,
                "沉浸式阅读器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // ignore
        }
    }

    private static void LogException(Exception exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FanqieReader",
                "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "reader.log"),
                $"{DateTime.Now:HH:mm:ss.fff} unhandled: {exception}{Environment.NewLine}");
        }
        catch
        {
            // logging must never throw
        }
    }
}
