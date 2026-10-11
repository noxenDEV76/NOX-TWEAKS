namespace NoxTweak.UI;

/// <summary>Everything the view-models need from the windowing system. Implemented by the WPF app and by a fake in tests.</summary>
public interface IUi
{
    Task<bool> ConfirmAsync(string title, string message, string primary, string? secondary = "Cancel", IReadOnlyList<string>? details = null);
    Task InfoAsync(string title, string message, IReadOnlyList<string>? details = null);
    /// <summary>Lets the user pick a game executable. Null when cancelled.</summary>
    string? PickExecutable();
    void OpenFolder(string path);
    void RestartAsAdministrator();
    void RestartWindows();
    IDisposable StartTimer(TimeSpan interval, Action tick);
    /// <summary>Runs on the UI thread.</summary>
    void Post(Action action);
    void Toast(string text);
}

/// <summary>Pages that want to know when they become visible (to start/stop their light-weight timers).</summary>
public interface IPage
{
    void Activated();
    void Deactivated();
}
