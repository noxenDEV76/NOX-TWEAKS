using System.Text.Json;

namespace NoxTweak.Core.Settings;

public sealed class ManualGame
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
}

public sealed class AppSettings
{
    public string PresetId { get; set; } = "balanced";
    /// <summary>Game Mode only reacts to games while it is activated.</summary>
    public bool GameModeActive { get; set; }
    /// <summary>When on (and Game Mode active), NOX TWEAK notices known games started outside the app. Polls every 5 s.</summary>
    public bool AutomaticMode { get; set; }
    public List<string> CustomActions { get; set; } = new();
    public List<string> CloseList { get; set; } = new();
    public bool ForceClose { get; set; }
    public List<ManualGame> ManualGames { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public List<string> GameModeOffFor { get; set; } = new();
    public Dictionary<string, DateTime> LastPlayed { get; set; } = new();
    public string? LastGameId { get; set; }
    public string PingTarget { get; set; } = "1.1.1.1";
    /// <summary>Changes that still need a restart or sign-out. Cleared automatically after Windows reboots.</summary>
    public List<string> PendingRestart { get; set; } = new();
    public DateTime? PendingSinceBootUtc { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    public SettingsStore(string path) { _path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }

    public AppSettings Load()
    {
        try { if (File.Exists(_path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new(); }
        catch { /* corrupted settings fall back to defaults */ }
        return new AppSettings();
    }

    public void Save(AppSettings s)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
