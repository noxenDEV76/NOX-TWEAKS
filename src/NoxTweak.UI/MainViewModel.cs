using System.Collections.ObjectModel;
using System.Windows.Input;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;

namespace NoxTweak.UI;

public sealed class NavItemViewModel : ObservableObject
{
    private bool _isOn;
    public string Key { get; }
    public string Name { get; }
    public string Glyph { get; }
    public bool Highlight { get; }
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    public ICommand Command { get; }
    public NavItemViewModel(string key, string name, string glyph, Action<string> go, bool highlight = false)
    {
        Key = key; Name = name; Glyph = glyph; Highlight = highlight; Command = new RelayCommand(() => go(key));
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private object? _current;
    private string _toast = "";
    private IDisposable? _toastTimer;
    private readonly Dictionary<string, object> _pages = new();
    private string _currentKey = "";

    public ChangeRunner Runner { get; }
    public ObservableCollection<NavItemViewModel> GeneralNav { get; } = new();
    public ObservableCollection<NavItemViewModel> MainNav { get; } = new();
    public NavItemViewModel GameModeNav { get; }
    public object? Current { get => _current; private set => Set(ref _current, value); }
    public string ToastText { get => _toast; private set { if (Set(ref _toast, value)) Raise(nameof(HasToast)); } }
    public bool HasToast => _toast.Length > 0;
    public string UserName => _svc.UserName;
    public string UserRole => _svc.Host.IsAdmin ? "Administrator" : "Standard session";
    public string UserInitial => _svc.UserName.Length > 0 ? _svc.UserName[..1].ToUpperInvariant() : "?";
    public string AdminBadge => _svc.Host.IsAdmin ? "ADMIN" : "";
    public bool IsAdmin => _svc.Host.IsAdmin;

    public ICommand OptimizeCommand { get; }
    public ICommand ElevateCommand { get; }

    public HomeViewModel Home { get; }
    public GameModeViewModel GameMode { get; }
    public GamesViewModel Games { get; }
    public OptimizeViewModel General { get; }
    public OptimizeViewModel Advanced { get; }
    public HardwareViewModel Hardware { get; }
    public NetworkViewModel Network { get; }
    public BackupsViewModel Backups { get; }
    public LogsViewModel Logs { get; }

    public MainViewModel(AppServices svc, IUi ui)
    {
        _svc = svc; _ui = ui;
        Runner = new ChangeRunner(svc, ui);
        OptimizeCommand = new AsyncCommand(OptimizeMyPcAsync);
        ElevateCommand = new RelayCommand(_ => _ui.RestartAsAdministrator(), _ => !_svc.Host.IsAdmin);

        Home = new HomeViewModel(svc, ui, Runner, Navigate, OptimizeCommand);
        GameMode = new GameModeViewModel(svc, ui, Navigate);
        Games = new GamesViewModel(svc, ui, Navigate);
        General = new OptimizeViewModel(svc, ui, Runner, OptPage.General);
        Advanced = new OptimizeViewModel(svc, ui, Runner, OptPage.Advanced);
        Hardware = new HardwareViewModel(svc, ui);
        Network = new NetworkViewModel(svc, ui);
        Backups = new BackupsViewModel(svc, ui);
        Logs = new LogsViewModel(svc, ui);

        _pages["Home"] = Home; _pages["GameMode"] = GameMode; _pages["Games"] = Games; _pages["General"] = General;
        _pages["Advanced"] = Advanced; _pages["Hardware"] = Hardware; _pages["Network"] = Network;
        _pages["Backups"] = Backups; _pages["Logs"] = Logs;

        GameModeNav = new NavItemViewModel("GameMode", "NOX Game Mode", "", Navigate, highlight: true);
        GeneralNav.Add(new NavItemViewModel("Home", "Home", "", Navigate));
        GeneralNav.Add(new NavItemViewModel("Games", "Games", "", Navigate));
        GeneralNav.Add(new NavItemViewModel("Backups", "Backups", "", Navigate));
        GeneralNav.Add(new NavItemViewModel("Logs", "Logs & About", "", Navigate));
        MainNav.Add(new NavItemViewModel("General", "General", "", Navigate));
        MainNav.Add(new NavItemViewModel("Hardware", "Hardware", "", Navigate));
        MainNav.Add(new NavItemViewModel("Network", "Network", "", Navigate));
        MainNav.Add(new NavItemViewModel("Advanced", "Advanced", "", Navigate));

        AsyncCommand.UnhandledError = ex =>
        {
            _svc.Log.Error("Unhandled: " + ex);
            _ui.Post(() => _ = _ui.InfoAsync("Something went wrong", ex.Message + "\n\nNo further action is needed; details were written to the log."));
        };
        _svc.Controller.Changed += (_, e) => _ui.Post(() => Toast(e.Message));
        Navigate("Home");
    }

    public void Toast(string text)
    {
        ToastText = text;
        _toastTimer?.Dispose();
        _toastTimer = _ui.StartTimer(TimeSpan.FromSeconds(5), () => { ToastText = ""; _toastTimer?.Dispose(); _toastTimer = null; });
    }

    /// <summary>Keys: Home, GameMode, Games, General, General:Apps, Hardware, Network, Advanced, Backups, Logs.</summary>
    public void Navigate(string key)
    {
        var parts = key.Split(':');
        var page = parts[0];
        if (!_pages.TryGetValue(page, out var vm)) return;

        if (_currentKey != page && _pages.TryGetValue(_currentKey, out var old) && old is IPage oldPage) oldPage.Deactivated();
        _currentKey = page;
        foreach (var n in GeneralNav.Concat(MainNav).Append(GameModeNav)) n.IsOn = n.Key == page;
        Current = vm;
        if (page == "General" && parts.Length > 1 && Enum.TryParse<OptTab>(parts[1], true, out var tab)) General.SelectTab(tab);
        else if (vm is IPage p) p.Activated();
    }

    /// <summary>OPTIMIZE MY PC: quick, low-risk, reversible recommendations only, after showing the user exactly what will change.</summary>
    public async Task OptimizeMyPcAsync()
    {
        if (_svc.Snapshot is null) await _svc.ScanAsync();
        var plan = _svc.Plan;
        var set = plan?.SafeQuickSet.ToList() ?? new List<OptimizationDefinition>();
        if (set.Count == 0)
        {
            await _ui.InfoAsync("Nothing to optimize",
                "NOX TWEAK found no low-risk change that would help this PC right now. That is a good result: it does not apply tweaks just to look busy.\n\nOptional settings (privacy, mouse, visual effects) are on the General page if you want them.");
            return;
        }

        var details = set.Select(d => $"{d.Title}: {d.Description}" + (d.Warning is null ? "" : $"  (Note: {d.Warning})")).ToList();
        var ok = await _ui.ConfirmAsync("Optimize my PC",
            $"NOX TWEAK will apply {set.Count} low-risk change(s) chosen from your scan. Each one is backed up first, verified after, and can be restored from Backups. None needs administrator rights.",
            "Apply", "Cancel", details);
        if (!ok) return;

        var applied = new List<string>(); var problems = new List<string>(); var restart = false;
        foreach (var d in set)
        {
            var r = await Runner.ApplyAsync(d, confirm: false, announce: false);
            if (r.Ok) { applied.Add(d.Title); if (r.Restart != RestartNeed.None) restart = true; }
            else problems.Add($"{d.Title}: {r.Message}");
        }
        _svc.NotifyStateChanged();
        var summary = new List<string>();
        if (applied.Count > 0) summary.Add($"Applied and verified: {string.Join(", ", applied)}.");
        if (problems.Count > 0) summary.Add("Not applied:\n" + string.Join("\n", problems));
        if (restart) summary.Add("Some changes finish after you sign out or restart Windows. NOX TWEAK will not restart it for you.");
        summary.Add("Performance impact has not been measured. Use Restore any time from Backups.");
        await _ui.InfoAsync(problems.Count == 0 ? "Done" : "Finished with some problems", string.Join("\n\n", summary));
    }
}
