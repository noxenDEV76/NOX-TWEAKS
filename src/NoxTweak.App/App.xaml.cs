using System.Threading;
using System.Windows;
using NoxTweak.Core.Engine;
using NoxTweak.Core.Games;
using NoxTweak.Core.Registry;
using NoxTweak.Core.SystemInfo;
using NoxTweak.UI;

namespace NoxTweak.App;

public partial class App : Application
{
    private Mutex? _single;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\NoxTweak.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("NOX TWEAK is already running.", "NOX TWEAK", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _services?.Log.Error("Unhandled UI exception: " + args.Exception);
            MessageBox.Show("Something unexpected happened:\n\n" + args.Exception.Message +
                            "\n\nYour settings were not changed by this error. Details are in the log (Logs & About).",
                "NOX TWEAK", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        var services = new AppServices(AppPaths.Default(), new WindowsRegistry(), new PowerPlanService(), new WindowsHostInfo(), GameSources.FromWindows());
        _services = services;
        var ui = new WpfUi();

        // If the app closed or crashed in the middle of a Game Mode session, put those settings back first.
        var recovered = services.Session.RecoverIfPending();

        var vm = new MainViewModel(services, ui);
        ui.ToastSink = vm.Toast;
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        ui.Owner = window;
        window.Show();

        if (recovered.Count > 0) vm.Toast("Settings from an unfinished Game Mode session were restored.");
        _ = services.RefreshGamesAsync();          // one local read of launcher metadata
        services.Controller.ApplyAutomaticMode();  // starts the 5 s watcher only if the user enabled it
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // ends any active Game Mode session so nothing is left changed
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
