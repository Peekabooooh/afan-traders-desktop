using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using AfanTraders.Core;

namespace AfanTraders.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // One copy at a time (two copies writing the same data would conflict).
        using var mutex = new Mutex(true, @"Local\AFAN-Traders-Desktop", out var first);
        var folder = args.SkipWhile(a => a != "--data").Skip(1).FirstOrDefault();
        var uitest = args.SkipWhile(a => a != "--uitest").Skip(1).FirstOrDefault();
        if (uitest != null) folder = Path.Combine(uitest, "data");
        if (!first && folder == null)
        {
            MessageBox.Show("AFAN Traders is already open.", "AFAN Traders", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        // Anything unexpected is written to crash.log in the data folder (helps diagnose problems).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception, "unhandled");
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception, "background task"); e.SetObserved(); };
        var app = new App(folder, uitest);
        var code = app.Run();
        return code;
    }
}

static class CrashLog
{
    public static string? Folder;

    public static void Write(Exception? e, string where)
    {
        try
        {
            var dir = Folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFAN Traders");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {e}\r\n\r\n");
        }
        catch { }
    }
}

sealed class App : Application
{
    readonly string? folder, uitest;
    AppServices? services;

    public App(string? folder, string? uitest = null) { this.folder = folder; this.uitest = uitest; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Light;   // Windows 11 Fluent look
#pragma warning restore WPF0001
        Theme.Apply(this);             // AFAN look on top: squircles, indigo accent
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += (_, ex) =>
        {
            CrashLog.Write(ex.Exception, "screen");
            Ui.Message(ex.Exception is AppError ? ex.Exception.Message : $"Something went wrong:\n{ex.Exception.Message}", "AFAN Traders");
            ex.Handled = true;
        };
        try { services = new AppServices(folder); CrashLog.Folder = services.Store.DataFolder; }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the database:\n{ex.Message}", "AFAN Traders", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (uitest != null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(async () => Shutdown(await UiTest.Run(uitest)));
            return;
        }
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        services?.Shutdown();
        base.OnExit(e);
        // Everything is saved and closed. End the process now: Windows' own late clean-up of its
        // PDF and network components can crash after the app is finished (seen on test PCs).
        TerminateProcess(GetCurrentProcess(), (uint)e.ApplicationExitCode);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
