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
        var app = new App(folder, uitest);
        return app.Run();
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
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += (_, ex) =>
        {
            Ui.Message(ex.Exception is AppError ? ex.Exception.Message : $"Something went wrong:\n{ex.Exception.Message}", "AFAN Traders");
            ex.Handled = true;
        };
        try { services = new AppServices(folder); }
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
    }
}
