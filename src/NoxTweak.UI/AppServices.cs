using NoxTweak.Core.Ai;
using NoxTweak.Core.Engine;
using NoxTweak.Core.GameMode;
using NoxTweak.Core.Games;
using NoxTweak.Core.Logging;
using NoxTweak.Core.Model;
using NoxTweak.Core.Recommendations;
using NoxTweak.Core.Registry;
using NoxTweak.Core.Settings;
using NoxTweak.Core.Startup;
using NoxTweak.Core.SystemInfo;

namespace NoxTweak.UI;

public sealed class AppPaths
{
    public required string Root { get; init; }
    public string Backups => Path.Combine(Root, "backups");
    public string Logs => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string SessionFile => Path.Combine(Root, "session.json");

    public static AppPaths Default() => new()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoxTweak")
    };
}

/// <summary>Composition root: one place that wires the engine, stores and game-mode controller together.</summary>
public sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; }
    public IAppLog Log { get; }
    public IRegistry Registry { get; }
    public IPowerPlanService Power { get; }
    public IHostInfo Host { get; }
    public BackupStore Backups { get; }
    public OptimizationEngine Engine { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public GameModeSession Session { get; }
    public GameModeController Controller { get; }
    public GameSources Sources { get; }
    public IAiAdvisor Ai { get; set; } = new NoAiAdvisor();

    public IReadOnlyList<Game> Games { get; private set; } = Array.Empty<Game>();
    public DateTime? GamesScannedAt { get; private set; }
    public SystemSnapshot? Snapshot { get; private set; }
    public RecommendationPlan? Plan { get; private set; }
    public string UserName { get; } = Environment.UserName;

    /// <summary>Raised after any optimization is applied or restored so every page can re-read real state.</summary>
    public event Action? StateChanged;
    public event Action? GamesChanged;
    public event Action? ScanCompleted;
    public event Action? SettingsChanged;

    public AppServices(AppPaths paths, IRegistry registry, IPowerPlanService power, IHostInfo host, GameSources sources)
    {
        Paths = paths; Registry = registry; Power = power; Host = host; Sources = sources;
        Directory.CreateDirectory(paths.Root);
        Log = new AppLog(paths.Logs);
        Backups = new BackupStore(paths.Backups);
        Engine = new OptimizationEngine(registry, power, Backups, host, Log);
        SettingsStore = new SettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load();
        Session = new GameModeSession(Engine, Log, paths.SessionFile);
        Controller = new GameModeController(Session, SettingsStore, () => Settings, () => Games, SystemInventory.ReadPowerState, Log);
        ClearPendingRestartIfRebooted();
    }

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) { Log.Error($"Could not save settings: {ex.Message}"); }
        SettingsChanged?.Invoke();
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();

    // -------------------------------------------------------------- scanning
    public async Task<SystemSnapshot> ScanAsync()
    {
        Log.Info("Scan started");
        var snap = await Task.Run(() => SystemInventory.Scan(Power, Host));
        var startupCount = await Task.Run(() => StartupService.List(Registry).Count(e => e.CanManage && e.Enabled));
        var plan = await Task.Run(() => RecommendationEngine.Build(snap, Engine, startupCount));
        Snapshot = snap; Plan = plan;
        Log.Info($"Scan finished: CPU '{snap.Cpu.Value}' ({snap.Cpu.StatusLabel}), RAM '{snap.Ram.Value}', {snap.Gpus.Count} GPU(s), {plan.Applicable.Count()} findings");
        ScanCompleted?.Invoke();
        return snap;
    }

    /// <summary>Rebuilds the plan from the last snapshot after settings changed.</summary>
    public async Task RefreshPlanAsync()
    {
        if (Snapshot is null) return;
        var snap = Snapshot;
        var count = await Task.Run(() => StartupService.List(Registry).Count(e => e.CanManage && e.Enabled));
        Plan = await Task.Run(() => RecommendationEngine.Build(snap, Engine, count));
    }

    // ----------------------------------------------------------------- games
    public async Task RefreshGamesAsync()
    {
        var manual = Settings.ManualGames.ToList();
        Games = await Task.Run(() => GameDetector.DetectAll(Sources, manual));
        GamesScannedAt = DateTime.Now;
        Log.Info($"Game discovery: {Games.Count} game(s)");
        GamesChanged?.Invoke();
    }

    public void AddManualGame(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        if (!Settings.ManualGames.Any(m => string.Equals(m.ExePath, exePath, StringComparison.OrdinalIgnoreCase)))
            Settings.ManualGames.Add(new ManualGame { Name = name, ExePath = exePath });
        SaveSettings();
    }

    public void SetGameModeActive(bool active)
    {
        Settings.GameModeActive = active;
        SaveSettings();
        Controller.ApplyAutomaticMode();
    }

    // -------------------------------------------------------- restart tracking
    public void AddPendingRestart(string description, RestartNeed need)
    {
        if (need == RestartNeed.None) return;
        var label = need == RestartNeed.SignOut ? "sign out" : "restart";
        var line = $"{description} ({label})";
        if (!Settings.PendingRestart.Contains(line)) Settings.PendingRestart.Add(line);
        Settings.PendingSinceBootUtc = BootTimeUtc();
        SaveSettings();
    }

    private static DateTime BootTimeUtc()
    {
        var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new DateTime(boot.Ticks - boot.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    private void ClearPendingRestartIfRebooted()
    {
        if (Settings.PendingRestart.Count == 0) return;
        var since = Settings.PendingSinceBootUtc;
        // Windows has been restarted since the change if the boot time moved by more than a couple of minutes.
        if (since is null || Math.Abs((BootTimeUtc() - since.Value).TotalMinutes) > 3)
        {
            Settings.PendingRestart.Clear(); Settings.PendingSinceBootUtc = null; SaveSettings();
        }
    }

    public void Dispose() => Controller.Dispose();
}
