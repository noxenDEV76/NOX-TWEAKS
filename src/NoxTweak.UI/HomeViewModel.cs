using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using NoxTweak.Core.Model;
using NoxTweak.Core.Recommendations;
using NoxTweak.Core.SystemInfo;

namespace NoxTweak.UI;

public sealed class SpecViewModel(string label, string value, string status, string glyph)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Status { get; } = status;
    public string Glyph { get; } = glyph;
}

public sealed class RecCardViewModel
{
    public RecItem Item { get; }
    public string GroupLabel { get; }
    public string Title => Item.Title;
    public string Problem => Item.Problem;
    public string Reason => Item.Reason;
    public string Evidence => "Evidence: " + Item.Evidence;
    public string ConfidenceText => $"{Item.Confidence} confidence · {(Item.Confirmed ? "Confirmed" : "Hypothesis")}";
    public bool HasAction => Item.Definition is not null || Item.NavigateTo is not null;
    public string ActionText => Item.Definition is not null ? "Review" : "Open";
    public ICommand ActionCommand { get; }

    public RecCardViewModel(RecItem item, Func<RecItem, Task> act)
    {
        Item = item;
        GroupLabel = item.Group switch
        {
            PlanGroup.HighPriority => "HIGH PRIORITY", PlanGroup.Recommended => "RECOMMENDED", PlanGroup.Optional => "OPTIONAL",
            PlanGroup.Advanced => "ADVANCED", _ => "NOT RECOMMENDED"
        };
        ActionCommand = new AsyncCommand(() => act(item));
    }
}

public sealed class HomeViewModel : ObservableObject, IPage
{
    private const int Samples = 60;
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private readonly ChangeRunner _runner;
    private readonly Action<string> _navigate;
    private readonly CpuSampler _cpu = new();
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _ramHistory = new();
    private IDisposable? _timer;
    private string _metric = "CPU";
    private bool _scanning;
    private string _line = "", _area = "", _value = "";

    public string Welcome => $"Welcome back, {_svc.UserName}!";
    public string Subtitle => "Ready to enhance your system performance?";

    public bool IsScanning { get => _scanning; private set { if (Set(ref _scanning, value)) { Raise(nameof(ScanButtonText)); } } }
    public string ScanButtonText => IsScanning ? "Scanning…" : (_svc.Snapshot is null ? "Scan my PC" : "Scan again");
    public string ScanStatus => _svc.Snapshot is null ? "Not scanned yet. Nothing is shown until a real scan has run." : $"Last scan: {_svc.Snapshot.TakenAt:g}";
    public string BackupsText => $"{_svc.Backups.List().Count(b => b.Status == "Active")} restorable change(s)";

    public string Metric { get => _metric; private set { if (Set(ref _metric, value)) { Raise(nameof(IsCpu)); Raise(nameof(IsRam)); Raise(nameof(IsGpu)); Redraw(); } } }
    public bool IsCpu => _metric == "CPU";
    public bool IsRam => _metric == "RAM";
    public bool IsGpu => _metric == "GPU";
    public string ChartTitle => _svc.Snapshot?.Cpu is { HasValue: true } c ? c.Value : "Live usage (last 60 seconds)";
    public string ChartLine { get => _line; private set => Set(ref _line, value); }
    public string ChartArea { get => _area; private set => Set(ref _area, value); }
    public string CurrentValue { get => _value; private set => Set(ref _value, value); }
    public string ChartNote => IsGpu ? "GPU utilization is not available: reading it reliably needs vendor tooling, and NOX TWEAK does not guess." : "";
    public bool HasChartNote => IsGpu;

    // Game Mode panel
    public bool GameModeActive => _svc.Settings.GameModeActive;
    public string GameModeBadge => GameModeActive ? "Active" : "Off";
    public string GameModeButton => GameModeActive ? "Disable" : "Activate";
    public string DetectedGame => _svc.Controller.CurrentGame?.Name ?? LastGame()?.Name ?? "No game yet";
    public string DetectedGameSub => _svc.Controller.CurrentGame is { } c ? $"Running · {c.LauncherLabel}" : LastGame() is { } l ? $"Last played · {l.LauncherLabel}" : "Launch one from Games";
    public string PresetName => Core.GameMode.GameModePresets.Selected(_svc.Settings).Name;

    public string AiStatus => _svc.Ai.StatusText;
    public string PendingRestartText => _svc.Settings.PendingRestart.Count == 0 ? "" :
        "Some changes still need a restart or sign-out: " + string.Join("; ", _svc.Settings.PendingRestart) + ". NOX TWEAK never restarts Windows by itself.";
    public bool HasPendingRestart => _svc.Settings.PendingRestart.Count > 0;

    public ObservableCollection<SpecViewModel> Specs { get; } = new();
    public ObservableCollection<RecCardViewModel> Recommendations { get; } = new();
    public string RecommendationsHeader => _svc.Plan is null ? "Recommendations" : $"Recommendations · {_svc.Plan.Applicable.Count()} finding(s)";
    public string RecommendationsEmpty => _svc.Plan is null ? "Run a scan to get recommendations based on your actual PC." : "Nothing to change: your configuration already looks fine.";
    public bool NoRecommendations => Recommendations.Count == 0;
    public bool NoSpecs => Specs.Count == 0;

    public ICommand ScanCommand { get; }
    public ICommand SelectMetricCommand { get; }
    public ICommand ToggleGameModeCommand { get; }
    public ICommand OpenBackupsCommand { get; }
    public ICommand OpenGameModeCommand { get; }
    public ICommand OptimizeCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand DismissRestartCommand { get; }

    public HomeViewModel(AppServices svc, IUi ui, ChangeRunner runner, Action<string> navigate, ICommand optimize)
    {
        _svc = svc; _ui = ui; _runner = runner; _navigate = navigate; OptimizeCommand = optimize;
        ScanCommand = new AsyncCommand(ScanAsync);
        SelectMetricCommand = new RelayCommand(p => Metric = (string)p!);
        ToggleGameModeCommand = new RelayCommand(() => { _svc.SetGameModeActive(!_svc.Settings.GameModeActive); RefreshGameMode(); });
        OpenBackupsCommand = new RelayCommand(() => _navigate("Backups"));
        OpenGameModeCommand = new RelayCommand(() => _navigate("GameMode"));
        RestartCommand = new AsyncCommand(async () =>
        {
            if (await _ui.ConfirmAsync("Restart Windows?",
                    "Some changes need a restart to take effect. Save your work first: Windows will restart in 15 seconds (you can cancel with 'shutdown /a').",
                    "Restart now", "Remind me later", _svc.Settings.PendingRestart.ToList()))
                _ui.RestartWindows();
        });
        DismissRestartCommand = new RelayCommand(() => { _svc.Settings.PendingRestart.Clear(); _svc.Settings.PendingSinceBootUtc = null; _svc.SaveSettings(); RefreshGameMode(); });
        _svc.ScanCompleted += () => _ui.Post(RefreshAfterScan);
        _svc.StateChanged += () => _ui.Post(() => { Raise(nameof(BackupsText)); Raise(nameof(PendingRestartText)); Raise(nameof(HasPendingRestart)); _ = RebuildPlanAsync(); });
        _svc.SettingsChanged += () => _ui.Post(RefreshGameMode);
        _svc.Controller.Changed += (_, _) => _ui.Post(RefreshGameMode);
        _svc.GamesChanged += () => _ui.Post(RefreshGameMode);
        Redraw();
    }

    private Core.Games.Game? LastGame() => _svc.Settings.LastGameId is { } id ? _svc.Games.FirstOrDefault(g => g.Id == id) : null;

    public void RefreshGameMode()
    {
        foreach (var n in new[] { nameof(GameModeActive), nameof(GameModeBadge), nameof(GameModeButton), nameof(DetectedGame), nameof(DetectedGameSub), nameof(PresetName), nameof(PendingRestartText), nameof(HasPendingRestart) })
            Raise(n);
    }

    public async Task ScanAsync()
    {
        IsScanning = true;
        try { await _svc.ScanAsync(); }
        finally { IsScanning = false; Raise(nameof(ScanButtonText)); }
    }

    private void RefreshAfterScan()
    {
        var s = _svc.Snapshot;
        Specs.Clear();
        if (s is not null)
        {
            Specs.Add(new SpecViewModel("CPU", Show(s.Cpu), s.Cpu.StatusLabel, ""));
            Specs.Add(new SpecViewModel("GPU", s.Gpus.Count == 0 ? "Unavailable" : string.Join(" + ", s.Gpus.Select(g => g.Name)), s.Gpus.Count == 0 ? "Unavailable" : "Verified", ""));
            Specs.Add(new SpecViewModel("Memory", Show(s.Ram) + (s.Ram.Note is null ? "" : $" ({s.Ram.Note})"), s.Ram.StatusLabel, ""));
            Specs.Add(new SpecViewModel("Windows", Show(s.Os) + (s.Os.Note is null ? "" : $" · {s.Os.Note}"), s.Os.StatusLabel, ""));
            Specs.Add(new SpecViewModel("Motherboard", Show(s.Board), s.Board.StatusLabel, ""));
            Specs.Add(new SpecViewModel("Storage", s.Drives.Count == 0 ? "Unavailable" : string.Join(" · ", s.Drives.Select(d => $"{d.Name} {d.FreeBytes / 1073741824} GB free of {d.TotalBytes / 1073741824} GB")), s.Drives.Count == 0 ? "Unavailable" : "Verified", ""));
            var net = s.ActiveAdapter;
            Specs.Add(new SpecViewModel("Network", net is null ? "No active connection found" : $"{net.Kind} · {net.Name}", net is null ? "Unavailable" : "Verified", ""));
            Specs.Add(new SpecViewModel("Power plan", Show(s.PowerPlan) + (s.IsLaptop == true ? " · laptop" : s.IsLaptop == false ? " · desktop" : ""), s.PowerPlan.StatusLabel, ""));
        }
        Recommendations.Clear();
        if (_svc.Plan is not null)
            foreach (var item in _svc.Plan.Applicable.OrderBy(i => i.Group).Take(6))
                Recommendations.Add(new RecCardViewModel(item, ActOnAsync));
        foreach (var n in new[] { nameof(ScanStatus), nameof(ScanButtonText), nameof(ChartTitle), nameof(RecommendationsHeader), nameof(RecommendationsEmpty), nameof(NoRecommendations), nameof(NoSpecs), nameof(AiStatus) })
            Raise(n);
    }

    private async Task RebuildPlanAsync()
    {
        if (_svc.Snapshot is null) return;
        await _svc.RefreshPlanAsync();
        RefreshAfterScan();
    }

    private static string Show(Fact f) => f.HasValue ? f.Value : $"{f.Value}" + (f.Note is null ? "" : $" ({f.Note})");

    private async Task ActOnAsync(RecItem item)
    {
        if (item.Definition is not null)
        {
            var def = item.Definition;
            var state = _svc.Engine.GetState(def);
            if (state == OptState.Applied) { _ui.Toast("Already applied."); return; }
            var r = await _runner.ApplyAsync(def);
            if (r.Ok) await RebuildPlanAsync();
        }
        else if (item.NavigateTo is not null) _navigate(item.NavigateTo);
    }

    // ------------------------------------------------------------- live chart
    public void Activated()
    {
        _cpu.Sample(); // prime the delta
        _timer?.Dispose();
        _timer = _ui.StartTimer(TimeSpan.FromSeconds(1), Tick);
        RefreshGameMode();
        Raise(nameof(BackupsText));
    }

    public void Deactivated() { _timer?.Dispose(); _timer = null; }

    public void Tick()
    {
        var c = _cpu.Sample();
        if (c is { } cv) Push(_cpuHistory, cv);
        if (CpuSampler.Memory() is { } m && m.Total > 0) Push(_ramHistory, 100.0 * (m.Total - m.Available) / m.Total);
        Redraw();
    }

    private static void Push(Queue<double> q, double v) { q.Enqueue(v); while (q.Count > Samples) q.Dequeue(); }

    private void Redraw()
    {
        var data = _metric switch { "CPU" => _cpuHistory, "RAM" => _ramHistory, _ => null };
        if (data is null || data.Count == 0)
        {
            ChartLine = ""; ChartArea = "";
            CurrentValue = IsGpu ? "Unavailable" : "Measuring…";
            Raise(nameof(ChartNote)); Raise(nameof(HasChartNote));
            return;
        }
        // Fixed 600x160 coordinate space, newest sample on the right.
        var pts = new List<string>();
        var step = 600.0 / (Samples - 1);
        var offset = (Samples - data.Count) * step;
        var i = 0;
        foreach (var v in data)
            pts.Add(string.Create(CultureInfo.InvariantCulture, $"{offset + i++ * step:0.#},{160 - Math.Clamp(v, 0, 100) * 1.6:0.#}"));
        ChartLine = string.Join(" ", pts);
        ChartArea = $"{offset:0.#},160 " + ChartLine + string.Create(CultureInfo.InvariantCulture, $" {offset + (data.Count - 1) * step:0.#},160");
        CurrentValue = $"{data.Last():0}%";
        Raise(nameof(ChartNote)); Raise(nameof(HasChartNote));
    }
}
