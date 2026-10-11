using System.Collections.ObjectModel;
using System.Windows.Input;
using NoxTweak.Core.GameMode;
using NoxTweak.Core.Games;

namespace NoxTweak.UI;

public sealed class PresetViewModel : ObservableObject
{
    private bool _isOn;
    public GameModePreset Preset { get; }
    public string Id => Preset.Id;
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public string CountText => Preset.Id == "custom" ? $"{Preset.Count} selected" : Preset.CountText;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    public ICommand SelectCommand { get; }
    public PresetViewModel(GameModePreset preset, Action<PresetViewModel> pick) { Preset = preset; SelectCommand = new RelayCommand(() => pick(this)); }
}

public sealed class ToggleRowViewModel : ObservableObject
{
    private bool _isOn;
    private readonly Action<ToggleRowViewModel> _changed;
    public string Key { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    public ICommand ToggleCommand { get; }
    public ToggleRowViewModel(string key, string title, string detail, bool on, Action<ToggleRowViewModel> changed)
    {
        Key = key; Title = title; Detail = detail; _isOn = on; _changed = changed;
        ToggleCommand = new RelayCommand(() => { IsOn = !IsOn; _changed(this); });
    }
}

public sealed class GameModeViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private readonly Action<string> _navigate;
    private bool _configuring;
    private string _lastEvent = "";

    public string Title => "NOX Game Mode";
    public string Subtitle => "Optimize your PC for maximum gaming performance in real time.";

    public bool IsActive => _svc.Settings.GameModeActive;
    public string RingText => IsActive ? "Active" : "Activate";
    public string RingHint => IsActive
        ? "Game Mode will apply your preset when you launch a game from NOX TWEAK and restore everything when the game closes."
        : "Game Mode is off. Nothing is changed when you launch games.";

    public string DetectedGame => _svc.Controller.CurrentGame?.Name ?? LastGame()?.Name ?? "None yet";
    public string DetectedGameLauncher => _svc.Controller.CurrentGame?.LauncherLabel ?? LastGame()?.LauncherLabel ?? "";
    public string DetectedGameInitials => _svc.Controller.CurrentGame?.Initials ?? LastGame()?.Initials ?? "?";
    public string SessionLine => _svc.Controller.SessionActive ? "Session running: settings will be restored when the game closes." : (_lastEvent.Length > 0 ? _lastEvent : "No session running.");

    public bool AutomaticMode => _svc.Settings.AutomaticMode;
    public string AutomaticHint => "Notice supported games started outside NOX TWEAK. Checks every 5 seconds while Game Mode is active; no background activity otherwise.";

    public ObservableCollection<PresetViewModel> Presets { get; } = new();
    public ObservableCollection<ToggleRowViewModel> ActionRows { get; } = new();
    public ObservableCollection<ToggleRowViewModel> AppRows { get; } = new();

    public bool IsConfiguring { get => _configuring; private set => Set(ref _configuring, value); }
    public string ConfigureLabel => IsConfiguring ? "Done" : "Configure";
    public bool ForceClose => _svc.Settings.ForceClose;
    public string ForceHint => "Force close ends apps that ignore a normal close request. Unsaved work in those apps is lost. Off by default.";
    public bool IsCustom => _svc.Settings.PresetId == "custom";

    public ICommand ToggleActiveCommand { get; }
    public ICommand ToggleAutomaticCommand { get; }
    public ICommand ToggleForceCommand { get; }
    public ICommand ConfigureCommand { get; }
    public ICommand AddGamesCommand { get; }
    public ICommand RefreshAppsCommand { get; }
    public ICommand OpenGamesCommand { get; }

    public GameModeViewModel(AppServices svc, IUi ui, Action<string> navigate)
    {
        _svc = svc; _ui = ui; _navigate = navigate;
        ToggleActiveCommand = new RelayCommand(() => { _svc.SetGameModeActive(!_svc.Settings.GameModeActive); Refresh(); });
        ToggleAutomaticCommand = new RelayCommand(() => { _svc.Settings.AutomaticMode = !_svc.Settings.AutomaticMode; _svc.SaveSettings(); _svc.Controller.ApplyAutomaticMode(); Refresh(); });
        ToggleForceCommand = new RelayCommand(() => { _svc.Settings.ForceClose = !_svc.Settings.ForceClose; _svc.SaveSettings(); Raise(nameof(ForceClose)); });
        ConfigureCommand = new RelayCommand(() => { IsConfiguring = !IsConfiguring; Raise(nameof(ConfigureLabel)); if (IsConfiguring) LoadApps(); });
        AddGamesCommand = new AsyncCommand(AddGameAsync);
        RefreshAppsCommand = new RelayCommand(LoadApps);
        OpenGamesCommand = new RelayCommand(() => _navigate("Games"));

        BuildPresets(); BuildActionRows();
        _svc.SettingsChanged += () => _ui.Post(Refresh);
        _svc.GamesChanged += () => _ui.Post(Refresh);
        _svc.Controller.Changed += (_, e) => _ui.Post(() => { _lastEvent = e.Message; Refresh(); });
    }

    private Game? LastGame() => _svc.Settings.LastGameId is { } id ? _svc.Games.FirstOrDefault(g => g.Id == id) : null;

    public void Activated() { Refresh(); }
    public void Deactivated() { }

    public void Refresh()
    {
        foreach (var n in new[] { nameof(IsActive), nameof(RingText), nameof(RingHint), nameof(DetectedGame), nameof(DetectedGameLauncher), nameof(DetectedGameInitials),
                                  nameof(SessionLine), nameof(AutomaticMode), nameof(ForceClose), nameof(IsCustom) }) Raise(n);
        BuildPresets();
    }

    private void BuildPresets()
    {
        Presets.Clear();
        foreach (var p in GameModePresets.All(_svc.Settings))
            Presets.Add(new PresetViewModel(p, Pick) { IsOn = p.Id == _svc.Settings.PresetId });
    }

    private void Pick(PresetViewModel p)
    {
        _svc.Settings.PresetId = p.Id; _svc.SaveSettings(); Refresh();
    }

    private void BuildActionRows()
    {
        ActionRows.Clear();
        foreach (var kv in GameModePresets.Info)
            ActionRows.Add(new ToggleRowViewModel(kv.Key.ToString(), kv.Value.Title, kv.Value.Detail, _svc.Settings.CustomActions.Contains(kv.Key.ToString()), r =>
            {
                if (r.IsOn) { if (!_svc.Settings.CustomActions.Contains(r.Key)) _svc.Settings.CustomActions.Add(r.Key); }
                else _svc.Settings.CustomActions.Remove(r.Key);
                _svc.Settings.PresetId = "custom"; // editing actions means you are building a custom preset
                _svc.SaveSettings(); Refresh();
            }));
    }

    private void LoadApps()
    {
        AppRows.Clear();
        var chosen = _svc.Settings.CloseList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = ProcessCleaner.Candidates();
        foreach (var c in names)
            AppRows.Add(MakeAppRow(c.ProcessName, $"{c.MemoryBytes / 1048576} MB · {Trim(c.Title)}", chosen.Contains(c.ProcessName)));
        foreach (var n in chosen.Where(n => names.All(c => !c.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase))))
            AppRows.Add(MakeAppRow(n, "Not running right now", true));
    }

    private ToggleRowViewModel MakeAppRow(string name, string detail, bool on) => new(name, name, detail, on, r =>
    {
        var l = _svc.Settings.CloseList;
        if (r.IsOn) { if (!l.Contains(r.Key, StringComparer.OrdinalIgnoreCase)) l.Add(r.Key); }
        else l.RemoveAll(x => x.Equals(r.Key, StringComparison.OrdinalIgnoreCase));
        _svc.SaveSettings();
    });

    private static string Trim(string s) => s.Length > 50 ? s[..50] + "…" : s;

    private async Task AddGameAsync()
    {
        var exe = _ui.PickExecutable();
        if (exe is null) return;
        _svc.AddManualGame(exe);
        await _svc.RefreshGamesAsync();
        _ui.Toast("Game added.");
    }
}
