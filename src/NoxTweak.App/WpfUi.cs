using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NoxTweak.UI;

namespace NoxTweak.App;

/// <summary>Windows implementation of the view-model UI contract.</summary>
public sealed class WpfUi : IUi
{
    public Window? Owner { get; set; }
    public Action<string>? ToastSink { get; set; }

    private static Dispatcher Disp => Application.Current.Dispatcher;

    public Task<bool> ConfirmAsync(string title, string message, string primary, string? secondary = "Cancel", IReadOnlyList<string>? details = null) =>
        Disp.InvokeAsync(() => DialogWindow.Ask(Owner, title, message, primary, secondary, details)).Task;

    public Task InfoAsync(string title, string message, IReadOnlyList<string>? details = null) =>
        Disp.InvokeAsync(() => { DialogWindow.Ask(Owner, title, message, "OK", null, details); }).Task;

    public string? PickExecutable()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose the game's executable",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) { Toast("Could not open the folder: " + ex.Message); }
    }

    public void RestartAsAdministrator()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the application path.");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            using var _ = Process.Start(psi);
            Application.Current.Shutdown(); // OnExit ends any Game Mode session cleanly first
        }
        catch (Win32Exception)
        {
            Toast("Administrator permission was not granted, so nothing changed.");
        }
        catch (Exception ex)
        {
            Toast("Could not restart as administrator: " + ex.Message);
        }
    }

    public void RestartWindows()
    {
        // 15 second delay so the user can cancel with 'shutdown /a'. Only runs after an explicit "Restart now" click.
        var psi = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/r", "/t", "15" }) psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, Disp) { Interval = interval };
        t.Tick += (_, _) => tick();
        t.Start();
        return new TimerHandle(t);
    }

    public void Post(Action action)
    {
        if (Disp.CheckAccess()) action(); else Disp.BeginInvoke(action);
    }

    public void Toast(string text) => Post(() => ToastSink?.Invoke(text));

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
