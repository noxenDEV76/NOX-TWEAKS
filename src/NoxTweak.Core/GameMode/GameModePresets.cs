using NoxTweak.Core.Settings;

namespace NoxTweak.Core.GameMode;

public enum GameModeAction
{
    WindowsGameMode,
    DisableGameDvr,
    MuteNotifications,
    HighPerformancePlan,
    CloseBackgroundApps,
    RaiseGamePriority,
}

public sealed record GameModePreset(string Id, string Name, string Description, IReadOnlyList<GameModeAction> Actions)
{
    public int Count => Actions.Count;
    public string CountText => $"{Actions.Count} optimization{(Actions.Count == 1 ? "" : "s")}";
}

public static class GameModePresets
{
    public static readonly IReadOnlyDictionary<GameModeAction, (string Title, string Detail)> Info =
        new Dictionary<GameModeAction, (string, string)>
        {
            [GameModeAction.WindowsGameMode] = ("Windows Game Mode", "Make sure Windows Game Mode is on while you play."),
            [GameModeAction.DisableGameDvr] = ("Pause Game DVR recording", "Stop background capture while the game runs."),
            [GameModeAction.MuteNotifications] = ("Mute notifications", "Hide pop-ups during the session."),
            [GameModeAction.HighPerformancePlan] = ("High Performance power plan", "Skipped automatically on battery power."),
            [GameModeAction.CloseBackgroundApps] = ("Close selected background apps", "Only the apps you chose; politely, never system processes."),
            [GameModeAction.RaiseGamePriority] = ("Raise game priority", "Above Normal. No FPS gain is promised; skipped if Windows refuses."),
        };

    public static readonly GameModePreset Balanced = new("balanced", "Balanced",
        "Safe, reversible Windows settings for casual play.",
        [GameModeAction.WindowsGameMode, GameModeAction.DisableGameDvr]);

    public static readonly GameModePreset Performance = new("performance", "Performance",
        "Balanced plus power plan, muted notifications and your background-app list.",
        [GameModeAction.WindowsGameMode, GameModeAction.DisableGameDvr, GameModeAction.HighPerformancePlan,
         GameModeAction.MuteNotifications, GameModeAction.CloseBackgroundApps]);

    public static GameModePreset Custom(AppSettings s) => new("custom", "Custom",
        "Exactly the actions you pick under Configure.",
        s.CustomActions.Select(n => Enum.TryParse<GameModeAction>(n, out var a) ? (GameModeAction?)a : null)
            .Where(a => a.HasValue).Select(a => a!.Value).Distinct().ToList());

    public static IReadOnlyList<GameModePreset> All(AppSettings s) => [Balanced, Performance, Custom(s)];

    public static GameModePreset Selected(AppSettings s) => All(s).FirstOrDefault(p => p.Id == s.PresetId) ?? Balanced;
}
