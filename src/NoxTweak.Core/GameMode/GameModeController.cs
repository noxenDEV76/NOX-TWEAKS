using System.Diagnostics;
using NoxTweak.Core.Games;
using NoxTweak.Core.Logging;
using NoxTweak.Core.Settings;

namespace NoxTweak.Core.GameMode;

public sealed class SessionEventArgs(string message, Game? game) : EventArgs
{
    public string Message { get; } = message;
    public Game? Game { get; } = game;
}

/// <summary>
/// Launches games and runs Game Mode sessions around them. A single background timer exists only
/// while a game is being watched or Automatic Mode is on; otherwise the app is fully idle.
/// </summary>
public sealed class GameModeController : IDisposable
{
    private readonly GameModeSession _session;
    private readonly SettingsStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<Game>> _games;
    private readonly Func<(bool? Laptop, bool? Battery)> _power;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private Timer? _timer;
    private Game? _current;
    private DateTime _launchedAt;
    private bool _seenRunning;
    private bool _priorityTried;

    public event EventHandler<SessionEventArgs>? Changed;

    public GameModeController(GameModeSession session, SettingsStore store, Func<AppSettings> settings,
        Func<IReadOnlyList<Game>> games, Func<(bool? Laptop, bool? Battery)> power, IAppLog log)
    {
        _session = session; _store = store; _settings = settings; _games = games; _power = power; _log = log;
    }

    public Game? CurrentGame { get { lock (_gate) return _current; } }
    public bool SessionActive => _session.IsActive;

    /// <summary>Apply Game Mode (if enabled for this game) and start the game through its launcher.</summary>
    public IReadOnlyList<SessionActionResult> Launch(Game game)
    {
        var s = _settings();
        IReadOnlyList<SessionActionResult> results = [];
        var useMode = s.GameModeActive && !s.GameModeOffFor.Contains(game.Id);
        if (useMode)
        {
            var (laptop, battery) = _power();
            results = _session.Start(game.Name, GameModePresets.Selected(s), s, laptop, battery);
        }

        try
        {
            GameLauncher.Launch(game);
        }
        catch (Exception ex)
        {
            _log.Error($"Launch failed for {game.Name}: {ex.Message}");
            if (useMode) _session.End();
            Raise($"Could not launch {game.Name}: {ex.Message}", game);
            throw;
        }

        s.LastGameId = game.Id; s.LastPlayed[game.Id] = DateTime.Now; _store.Save(s);
        _log.Info($"Launched {game.Name} via {game.Launcher}");
        if (useMode) Watch(game);
        Raise($"{game.Name} launched.", game);
        return results;
    }

    private void Watch(Game game)
    {
        lock (_gate)
        {
            _current = game; _launchedAt = DateTime.UtcNow; _seenRunning = false; _priorityTried = false;
            _timer?.Dispose();
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }
    }

    private void Tick()
    {
        Game? g;
        lock (_gate) g = _current;
        if (g is null) return;

        using var proc = GameLauncher.FindRunningProcess(g);
        if (proc is not null)
        {
            _seenRunning = true;
            if (!_priorityTried && PresetWantsPriority())
            {
                _priorityTried = true;
                try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; _log.Info($"Raised priority of {g.Name} to Above Normal"); }
                catch (Exception ex) { _log.Warn($"Priority change refused for {g.Name}: {ex.Message}"); }
            }
            return;
        }

        var waited = DateTime.UtcNow - _launchedAt;
        if (_seenRunning || waited > TimeSpan.FromMinutes(5)) EndSession(_seenRunning ? $"{g.Name} closed." : $"{g.Name} never started; settings restored.");
    }

    private bool PresetWantsPriority() => GameModePresets.Selected(_settings()).Actions.Contains(GameModeAction.RaiseGamePriority);

    /// <summary>Starts or stops the light 5-second watcher that notices games started outside NOX TWEAK.</summary>
    public void ApplyAutomaticMode()
    {
        var s = _settings();
        lock (_gate)
        {
            if (_current is not null) return; // a session is already being watched
            _timer?.Dispose(); _timer = null;
            if (s.GameModeActive && s.AutomaticMode)
                _timer = new Timer(_ => AutoTick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    private void AutoTick()
    {
        lock (_gate) if (_current is not null) return;
        var s = _settings();
        if (!s.GameModeActive || !s.AutomaticMode) return;
        foreach (var g in _games())
        {
            if (s.GameModeOffFor.Contains(g.Id)) continue;
            using var p = GameLauncher.FindRunningProcess(g);
            if (p is null) continue;
            var (laptop, battery) = _power();
            _session.Start(g.Name, GameModePresets.Selected(s), s, laptop, battery);
            lock (_gate)
            {
                _current = g; _launchedAt = DateTime.UtcNow; _seenRunning = true; _priorityTried = false;
                _timer?.Dispose();
                _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            }
            Raise($"Game Mode applied automatically for {g.Name}.", g);
            return;
        }
    }

    public void EndSession(string message)
    {
        Game? g;
        lock (_gate)
        {
            g = _current; _current = null;
            _timer?.Dispose(); _timer = null;
        }
        _session.End();
        Raise(message, g);
        ApplyAutomaticMode();
    }

    private void Raise(string msg, Game? g) => Changed?.Invoke(this, new SessionEventArgs(msg, g));

    public void Dispose()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
        _session.End(); // never leave changes behind when the app exits
    }
}
