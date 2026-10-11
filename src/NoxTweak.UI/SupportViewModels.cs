using System.Collections.ObjectModel;
using System.Windows.Input;
using NoxTweak.Core.Engine;
using NoxTweak.Core.Model;
using NoxTweak.Core.Network;
using NoxTweak.Core.SystemInfo;

namespace NoxTweak.UI;

// ------------------------------------------------------------------ Backups
public sealed class BackupRowViewModel
{
    public BackupRecord Record { get; }
    public string Title => Record.Title;
    public string Date => Record.CreatedUtc.ToLocalTime().ToString("g");
    public string Status { get; }
    public string Integrity { get; }
    public string Location { get; }
    public bool CanRestore { get; }
    public ICommand RestoreCommand { get; }

    public BackupRowViewModel(BackupRecord r, bool intact, string folder, Func<BackupRecord, Task> restore)
    {
        Record = r; Status = r.Status; Integrity = intact ? "Integrity OK" : "DAMAGED";
        Location = Path.Combine(folder, r.Id + ".json");
        CanRestore = r.Status == "Active" && intact;
        RestoreCommand = new AsyncCommand(() => restore(r), () => CanRestore);
    }
}

public sealed class BackupsViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private string _info = "";
    public string Title => "Backups & Restore";
    public string Subtitle => "Every change made by NOX TWEAK is saved here first and can be put back exactly as it was.";
    public ObservableCollection<BackupRowViewModel> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
    public string Explanation => "NOX TWEAK backups record the exact previous value of each setting it changes (or that the value did not exist). Windows System Restore is separate: it protects system files and drivers and may be turned off. Use both for big changes.";
    public string RestorePointInfo { get => _info; private set => Set(ref _info, value); }
    public string Folder => _svc.Paths.Backups;
    public ICommand RefreshCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RestorePointCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public BackupsViewModel(AppServices svc, IUi ui)
    {
        _svc = svc; _ui = ui;
        RefreshCommand = new RelayCommand(Load);
        VerifyCommand = new AsyncCommand(VerifyAsync);
        RestorePointCommand = new AsyncCommand(RestorePointAsync);
        OpenFolderCommand = new RelayCommand(() => _ui.OpenFolder(_svc.Paths.Backups));
        _svc.StateChanged += () => _ui.Post(Load);
    }

    public void Activated() => Load();
    public void Deactivated() { }

    public void Load()
    {
        Rows.Clear();
        foreach (var r in _svc.Backups.List())
            Rows.Add(new BackupRowViewModel(r, _svc.Backups.VerifyIntegrity(r), _svc.Paths.Backups, RestoreAsync));
        Raise(nameof(IsEmpty));
    }

    private async Task RestoreAsync(BackupRecord r)
    {
        if (!await _ui.ConfirmAsync("Restore setting", $"Put '{r.Title}' back exactly as it was on {r.CreatedUtc.ToLocalTime():g}?", "Restore", "Cancel")) return;
        var res = await Task.Run(() => _svc.Engine.RestoreBackup(r.Id));
        if (res.NeedsElevation)
        {
            if (await _ui.ConfirmAsync("Administrator rights needed", res.Message + " Nothing was changed.", "Restart as administrator", "Not now")) _ui.RestartAsAdministrator();
            return;
        }
        _ui.Toast(res.Ok ? $"{r.Title}: restored and verified." : $"{r.Title}: {res.Message}");
        _svc.NotifyStateChanged();
        Load();
    }

    private async Task VerifyAsync()
    {
        var all = await Task.Run(() => _svc.Backups.List().Select(b => (b, ok: _svc.Backups.VerifyIntegrity(b))).ToList());
        var bad = all.Count(x => !x.ok);
        await _ui.InfoAsync("Backup integrity", bad == 0 ? $"All {all.Count} backup(s) passed the integrity check." : $"{bad} of {all.Count} backup(s) failed the integrity check and will not be used for restoring.");
        Load();
    }

    private async Task RestorePointAsync()
    {
        if (!await _ui.ConfirmAsync("Create a Windows restore point", "This asks Windows to create a System Restore point now. It needs administrator rights, and Windows may refuse if System Restore is off or a restore point was already created in the last 24 hours.", "Create", "Cancel")) return;
        var r = await Task.Run(() => RestorePointService.TryCreate(_svc.Host));
        RestorePointInfo = r.Message;
        if (!r.Created && !_svc.Host.IsAdmin && await _ui.ConfirmAsync("Administrator rights needed", r.Message, "Restart as administrator", "Not now")) _ui.RestartAsAdministrator();
        else _ui.Toast(r.Message);
    }
}

// ----------------------------------------------------------------- Hardware
public sealed class FactRowViewModel(string label, string value, string status, string note)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Status { get; } = status;
    public string Note { get; } = note;
    public bool HasNote => Note.Length > 0;
}

public sealed class HardwareViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    public string Title => "Hardware";
    public string Subtitle => "What NOX TWEAK found on this PC. Anything it cannot read reliably says so instead of guessing.";
    public ObservableCollection<FactRowViewModel> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
    public string EmptyText => "No scan yet. Run a scan to read your hardware.";
    public ICommand ScanCommand { get; }
    public HardwareViewModel(AppServices svc, IUi ui)
    {
        _svc = svc; _ui = ui;
        ScanCommand = new AsyncCommand(async () => { await _svc.ScanAsync(); });
        _svc.ScanCompleted += () => _ui.Post(Load);
    }
    public void Activated() => Load();
    public void Deactivated() { }

    private static FactRowViewModel Row(string label, Fact f) => new(label, f.HasValue ? f.Value : f.Value, f.StatusLabel, f.Note ?? "");

    public void Load()
    {
        Rows.Clear();
        var s = _svc.Snapshot;
        if (s is not null)
        {
            Rows.Add(Row("Operating system", s.Os));
            Rows.Add(Row("Processor", s.Cpu));
            Rows.Add(Row("Physical cores", s.PhysicalCores));
            Rows.Add(Row("Logical processors", s.LogicalProcessors));
            Rows.Add(Row("Memory", s.Ram));
            foreach (var g in s.Gpus)
            {
                Rows.Add(new FactRowViewModel("Graphics", g.Name, "Verified", ""));
                Rows.Add(Row("  Driver version", g.Driver));
                Rows.Add(Row("  Video memory", g.Vram));
            }
            if (s.Gpus.Count == 0) Rows.Add(new FactRowViewModel("Graphics", "Unavailable", "Unavailable", "No display adapter was reported by Windows."));
            Rows.Add(Row("Motherboard", s.Board));
            Rows.Add(Row("BIOS", s.Bios));
            foreach (var d in s.Drives)
                Rows.Add(new FactRowViewModel($"Drive {d.Name}", $"{d.FreeBytes / 1073741824} GB free of {d.TotalBytes / 1073741824} GB ({d.Format})", "Verified", d.MediaType.Note ?? ""));
            foreach (var a in s.Adapters.Where(a => a.IsUp))
                Rows.Add(new FactRowViewModel("Network", $"{a.Kind} · {a.Name}" + (a.SpeedBps > 0 ? $" · {a.SpeedBps / 1_000_000} Mbps link" : ""), "Verified", a.Dns.Count == 0 ? "" : "DNS: " + string.Join(", ", a.Dns)));
            Rows.Add(Row("Power plan", s.PowerPlan));
            Rows.Add(new FactRowViewModel("Form factor", s.IsLaptop is null ? "Unavailable" : s.IsLaptop == true ? "Laptop (battery found)" : "Desktop (no battery)", s.IsLaptop is null ? "Unavailable" : "Estimated", "Inferred from battery presence."));
            Rows.Add(new FactRowViewModel("Administrator", s.IsAdmin ? "Yes" : "No (not needed for most tweaks)", "Verified", ""));
            Rows.Add(Row("Temperatures", s.Temperatures));
            Rows.Add(Row("GPU utilization", s.GpuUtilization));
            Rows.Add(Row("System Restore", s.RestorePoint));
        }
        Raise(nameof(IsEmpty));
    }
}

// ------------------------------------------------------------------ Network
public sealed class AdapterRowViewModel(AdapterInfo a)
{
    public string Name { get; } = a.Name;
    public string Kind { get; } = a.Kind;
    public string State { get; } = a.IsUp ? "Connected" : "Disconnected";
    public string Speed { get; } = a.SpeedBps > 0 ? $"{a.SpeedBps / 1_000_000} Mbps" : "Unknown";
    public string Dns { get; } = a.Dns.Count == 0 ? "None reported" : string.Join(", ", a.Dns);
    public string Gateway { get; } = a.Gateway ?? "None";
}

public sealed class NetworkViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private CancellationTokenSource? _cts;
    private string _target;
    private string _result = "";
    private string _kind = "";
    private bool _running;
    private int _progress;

    public string Title => "Network";
    public string Subtitle => "Diagnose your connection. Nothing here changes your network settings.";
    public ObservableCollection<AdapterRowViewModel> Adapters { get; } = new();
    public string Target { get => _target; set => Set(ref _target, value); }
    public bool IsRunning { get => _running; private set { if (Set(ref _running, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !IsRunning;
    public string Progress => IsRunning ? $"{_progress} / 10 pings" : "";
    public string ResultText { get => _result; private set { if (Set(ref _result, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => _result.Length > 0;
    public string KindText { get => _kind; private set => Set(ref _kind, value); }
    public string Honesty => "A ping to your router measures your local network. A ping to a public address measures the route to that address. Neither is your in-game ping: enter your game server's address to test that. Changing DNS does not necessarily lower in-game ping.";
    public ICommand PingCommand { get; }
    public ICommand PingRouterCommand { get; }
    public ICommand PingPublicCommand { get; }
    public ICommand CancelCommand { get; }

    public NetworkViewModel(AppServices svc, IUi ui)
    {
        _svc = svc; _ui = ui; _target = svc.Settings.PingTarget;
        PingCommand = new AsyncCommand(() => RunAsync(Target, "Latency to the address you entered (use your game server's address for a meaningful game ping)."));
        PingRouterCommand = new AsyncCommand(RouterAsync);
        PingPublicCommand = new AsyncCommand(() => RunAsync("1.1.1.1", "Latency to a public test endpoint (Cloudflare 1.1.1.1). This is not a game server."));
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        _svc.ScanCompleted += () => _ui.Post(Load);
    }

    public void Activated() => Load();
    public void Deactivated() { _cts?.Cancel(); }

    public void Load()
    {
        Adapters.Clear();
        foreach (var a in (_svc.Snapshot?.Adapters ?? SystemInventory.Scan(_svc.Power, _svc.Host).Adapters)) Adapters.Add(new AdapterRowViewModel(a));
    }

    private async Task RouterAsync()
    {
        var gw = (_svc.Snapshot ?? await _svc.ScanAsync()).ActiveAdapter?.Gateway;
        if (gw is null) { ResultText = "No default gateway was found. Check that you are connected."; KindText = ""; return; }
        await RunAsync(gw, "Latency to your router (local network only).");
    }

    private async Task RunAsync(string host, string kind)
    {
        if (!NetworkDiagnostics.IsValidTarget(host)) { ResultText = "Enter a valid host name or IP address."; KindText = ""; return; }
        _svc.Settings.PingTarget = host == "1.1.1.1" ? _svc.Settings.PingTarget : host; _svc.SaveSettings();
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        IsRunning = true; _progress = 0; KindText = kind; ResultText = "";
        var progress = new Progress<int>(p => { _progress = p; Raise(nameof(Progress)); });
        try
        {
            var r = await NetworkDiagnostics.PingAsync(host, 10, progress, _cts.Token);
            ResultText = r.Error is not null && r.Received == 0
                ? r.Error
                : $"{r.Target}: {r.Received}/{r.Sent} replies · loss {r.LossPercent:0}% · min {r.MinMs:0} ms · avg {r.AvgMs:0.#} ms · max {r.MaxMs:0} ms · jitter {r.JitterMs:0.#} ms";
        }
        finally { IsRunning = false; Raise(nameof(Progress)); }
    }
}

// --------------------------------------------------------------------- Logs
public sealed class LogsViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    public string Title => "Logs & About";
    public string Subtitle => "Everything NOX TWEAK does is recorded locally. Nothing is uploaded.";
    public ObservableCollection<string> Lines { get; } = new();
    public string Version => "NOX TWEAK 1.0.0";
    public string Privacy => "Local-first: scans, backups and logs stay on this PC. No telemetry. No account. No remote scripts are ever downloaded or run.";
    public string AiStatus => _svc.Ai.StatusText;
    public string DataFolder => _svc.Paths.Root;
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public LogsViewModel(AppServices svc, IUi ui)
    {
        _svc = svc; _ui = ui;
        RefreshCommand = new RelayCommand(Load);
        OpenFolderCommand = new RelayCommand(() => _ui.OpenFolder(_svc.Paths.Root));
    }
    public void Activated() => Load();
    public void Deactivated() { }
    public void Load()
    {
        Lines.Clear();
        foreach (var e in _svc.Log.Recent.Reverse()) Lines.Add(e.ToString());
        if (Lines.Count == 0) Lines.Add("No events yet in this session.");
    }
}
