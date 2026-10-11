using System.Text.Json;
using NoxTweak.Core.Engine;
using NoxTweak.Core.Logging;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;
using NoxTweak.Core.Settings;

namespace NoxTweak.Core.GameMode;

public sealed record SessionActionResult(GameModeAction Action, bool Done, string Message);

public sealed class SessionState
{
    public string GameName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    /// <summary>Only backups created by this session. Settings that were already on are never touched.</summary>
    public List<string> BackupIds { get; set; } = new();
}

/// <summary>
/// Applies a preset around one game session and restores exactly what it changed afterwards.
/// The session state is written to disk so a crash or reboot can be recovered on next launch.
/// </summary>
public sealed class GameModeSession
{
    private static readonly Dictionary<GameModeAction, string> ToOptimization = new()
    {
        [GameModeAction.WindowsGameMode] = "core.gamemode",
        [GameModeAction.DisableGameDvr] = "core.gamedvr",
        [GameModeAction.MuteNotifications] = "core.notifications",
        [GameModeAction.HighPerformancePlan] = "power.high",
    };

    private readonly OptimizationEngine _engine;
    private readonly IAppLog _log;
    private readonly string _stateFile;
    private SessionState? _state;

    public GameModeSession(OptimizationEngine engine, IAppLog log, string stateFile)
    {
        _engine = engine; _log = log; _stateFile = stateFile;
    }

    public bool IsActive => _state is not null;

    public IReadOnlyList<SessionActionResult> Start(string gameName, GameModePreset preset, AppSettings settings, bool? isLaptop, bool? onBattery)
    {
        var results = new List<SessionActionResult>();
        if (_state is not null) End();
        _state = new SessionState { GameName = gameName, StartedUtc = DateTime.UtcNow };
        Persist();

        foreach (var action in preset.Actions)
        {
            try
            {
                if (ToOptimization.TryGetValue(action, out var id))
                {
                    if (action == GameModeAction.HighPerformancePlan && (onBattery == true))
                    {
                        results.Add(new(action, false, "Skipped: running on battery."));
                        continue;
                    }
                    var def = Catalog.Find(id)!;
                    if (_engine.GetState(def) == OptState.Applied)
                    {
                        results.Add(new(action, true, "Already set - left as it was."));
                        continue;
                    }
                    var r = _engine.Apply(def);
                    if (r.Ok && r.BackupId is not null)
                    {
                        _state.BackupIds.Add(r.BackupId);
                        Persist();
                    }
                    results.Add(new(action, r.Ok, r.Message));
                }
                else if (action == GameModeAction.CloseBackgroundApps)
                {
                    if (settings.CloseList.Count == 0) { results.Add(new(action, false, "No apps selected under Configure.")); continue; }
                    var rep = ProcessCleaner.Close(settings.CloseList, settings.ForceClose);
                    results.Add(new(action, true, string.Join("; ", rep)));
                }
                else if (action == GameModeAction.RaiseGamePriority)
                {
                    results.Add(new(action, false, "Applied once the game process appears."));
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Game Mode action {action} failed: {ex.Message}");
                results.Add(new(action, false, ex.Message));
            }
        }
        _log.Info($"Game Mode session started for '{gameName}' ({results.Count(r => r.Done)}/{results.Count} actions done)");
        return results;
    }

    /// <summary>Restores every change this session made. Safe to call twice.</summary>
    public IReadOnlyList<string> End()
    {
        var report = new List<string>();
        if (_state is null) return report;
        foreach (var id in _state.BackupIds.AsEnumerable().Reverse())
        {
            var r = _engine.RestoreBackup(id);
            report.Add($"{id}: {r.Message}");
            if (!r.Ok) _log.Warn($"Session restore problem for {id}: {r.Message}");
        }
        _log.Info($"Game Mode session ended for '{_state.GameName}'");
        _state = null;
        try { if (File.Exists(_stateFile)) File.Delete(_stateFile); } catch { }
        return report;
    }

    /// <summary>Call at startup: if the app closed or crashed mid-session, undo that session's changes.</summary>
    public IReadOnlyList<string> RecoverIfPending()
    {
        try
        {
            if (!File.Exists(_stateFile)) return [];
            var st = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_stateFile));
            if (st is null) return [];
            _state = st;
            _log.Warn($"Recovering unfinished Game Mode session for '{st.GameName}'");
            return End();
        }
        catch (Exception ex) { _log.Error($"Session recovery failed: {ex.Message}"); return [ex.Message]; }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex) { _log.Warn($"Could not persist session state: {ex.Message}"); }
    }
}
