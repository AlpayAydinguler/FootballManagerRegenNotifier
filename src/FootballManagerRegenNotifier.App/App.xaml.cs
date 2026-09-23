using System.IO;
using System.Windows;
using System.Windows.Threading;
using FootballManagerRegenNotifier.Core.Settings;
using Serilog;

namespace FootballManagerRegenNotifier.App;

public partial class App : System.Windows.Application
{
    /// <summary>Where the user-editable country spreadsheet lives.</summary>
    public static string CatalogPath { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "data", "countries.xlsx");

    public static string TessDataPath { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    public static string LogDirectory { get; private set; } =
        Path.Combine(SettingsStore.DefaultDirectory(), "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(LogDirectory);

        // The rolling file log deliberately records events and diagnostics, never
        // raw OCR text from an ungated read. See GameGate: the sampler only runs
        // while Football Manager is in the foreground, precisely so this file
        // cannot accumulate whatever else happened to be on screen.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(LogDirectory, "fmrn-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

        Log.Information("FM Regen Notifier starting.");

        // A crash in a background alert or a GDI hiccup must not take the whole
        // app down silently: report it, log it, and keep going where we can.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception.");

        // Created here rather than via StartupUri so the logger and the handlers
        // above are both in place first: a startup failure should be reported,
        // not leave a running process with no window and no explanation.
        var window = new Views.MainWindow();
        MainWindow = window;
        window.Show();

        // --screenshot=<path> renders the window to a PNG and exits. Used to
        // regenerate the README images, and the only reliable way to capture a
        // hardware-composited WPF window unattended.
        string? shot = e.Args.FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.Ordinal));
        if (shot is not null)
        {
            string path = shot["--screenshot=".Length..];
            string? tabArg = e.Args.FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.Ordinal));
            if (tabArg is not null && int.TryParse(tabArg["--tab=".Length..], out int tab))
            {
                window.SelectTab(tab);
            }
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try
                {
                    Services.VisualCapture.SavePng(window, path);
                    Log.Information("Wrote screenshot to {Path}.", path);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Screenshot failed.");
                }
                Shutdown();
            });
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception.");

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe app will try to keep running. " +
            $"Details are in {LogDirectory}.",
            "FM Regen Notifier",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("FM Regen Notifier exiting.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
