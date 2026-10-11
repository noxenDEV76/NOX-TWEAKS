using System.Text.Json;
using System.Text.RegularExpressions;
using NoxTweak.Core.Settings;

namespace NoxTweak.Core.Games;

public enum GameLauncherKind { Steam, Epic, Riot, Manual }

public sealed class Game
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required GameLauncherKind Launcher { get; init; }
    public string? InstallDir { get; init; }
    public string? ExePath { get; init; }
    /// <summary>Launcher-specific token: Steam appid, Epic URI path, or Riot product code.</summary>
    public string? LaunchToken { get; init; }

    public string LauncherLabel => Launcher switch
    {
        GameLauncherKind.Steam => "STEAM", GameLauncherKind.Epic => "EPIC",
        GameLauncherKind.Riot => "RIOT", _ => "MANUAL"
    };

    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "?" : string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }
}

public sealed record GameSources(string? SteamPath, string? EpicManifestDir, string? RiotInstallsJson, string? RiotMetadataDir)
{
    public static GameSources FromWindows()
    {
        string? steam = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steam = (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            }
        }
        catch { }
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new GameSources(steam,
            Path.Combine(pd, "Epic", "EpicGamesLauncher", "Data", "Manifests"),
            Path.Combine(pd, "Riot Games", "RiotClientInstalls.json"),
            Path.Combine(pd, "Riot Games", "Metadata"));
    }
}

/// <summary>Reads legitimate launcher metadata only. Never touches game files.</summary>
public static class GameDetector
{
    private static readonly HashSet<string> SteamIgnoredIds = ["228980"];
    private static readonly string[] SteamIgnoredPrefixes = ["Steamworks", "Steam Linux Runtime", "Proton", "Steam Controller"];

    public static List<Game> DetectAll(GameSources src, IEnumerable<ManualGame>? manual = null)
    {
        var games = new List<Game>();
        Try(() => games.AddRange(DetectSteam(src.SteamPath)));
        Try(() => games.AddRange(DetectEpic(src.EpicManifestDir)));
        Try(() => games.AddRange(DetectRiot(src.RiotInstallsJson, src.RiotMetadataDir)));
        foreach (var m in manual ?? [])
            if (File.Exists(m.ExePath))
                games.Add(ManualGameFor(m));
        return games.GroupBy(g => g.Id).Select(g => g.First()).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Game ManualGameFor(ManualGame m) => new()
    {
        Id = "manual:" + m.ExePath.ToLowerInvariant(), Name = m.Name, Launcher = GameLauncherKind.Manual,
        ExePath = m.ExePath, InstallDir = Path.GetDirectoryName(m.ExePath)
    };

    private static void Try(Action a) { try { a(); } catch { /* one broken launcher must not hide the others */ } }

    // ---------------------------------------------------------------- Steam
    public static IReadOnlyList<string> ParseSteamLibraries(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    public static (string AppId, string Name, string InstallDir)? ParseSteamManifest(string acf)
    {
        string? Get(string key) => Regex.Match(acf, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        var id = Get("appid"); var name = Get("name"); var dir = Get("installdir");
        return id is null || name is null || dir is null ? null : (id, name, dir);
    }

    public static IEnumerable<Game> DetectSteam(string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath)) yield break;
        var libs = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libs.AddRange(ParseSteamLibraries(File.ReadAllText(vdf)));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var f in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var m = ParseSteamManifest(File.ReadAllText(f));
                if (m is null) continue;
                var (id, name, dir) = m.Value;
                if (!id.All(char.IsDigit) || SteamIgnoredIds.Contains(id)) continue;
                if (SteamIgnoredPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                var install = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(install)) continue; // manifest of an uninstalled / partial game
                yield return new Game { Id = "steam:" + id, Name = name, Launcher = GameLauncherKind.Steam, InstallDir = install, LaunchToken = id };
            }
        }
    }

    // ----------------------------------------------------------------- Epic
    public static IEnumerable<Game> DetectEpic(string? manifestDir)
    {
        if (string.IsNullOrWhiteSpace(manifestDir) || !Directory.Exists(manifestDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            var g = ParseEpicManifest(File.ReadAllText(f));
            if (g is not null) yield return g;
        }
    }

    public static Game? ParseEpicManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;

        var name = S("DisplayName"); var dir = S("InstallLocation");
        var ns = S("CatalogNamespace"); var item = S("CatalogItemId"); var app = S("AppName");
        if (name is null || dir is null || app is null || !Directory.Exists(dir)) return null;
        var launchExe = S("LaunchExecutable");
        // Only well-formed tokens are ever put into a launch URI.
        string? token = ns is not null && item is not null && IsToken(ns) && IsToken(item) && IsToken(app)
            ? $"{ns}%3A{item}%3A{app}" : null;
        return new Game
        {
            Id = "epic:" + app, Name = name, Launcher = GameLauncherKind.Epic, InstallDir = dir,
            ExePath = launchExe is null ? null : Path.Combine(dir, launchExe), LaunchToken = token
        };
    }

    private static bool IsToken(string s) => s.Length is > 0 and < 100 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    // ----------------------------------------------------------------- Riot
    private static readonly (string Code, string Name, string Metadata)[] RiotProducts =
    [
        ("valorant", "VALORANT", "valorant.live"),
        ("league_of_legends", "League of Legends", "league_of_legends.live"),
    ];

    public static IEnumerable<Game> DetectRiot(string? installsJson, string? metadataDir)
    {
        if (string.IsNullOrWhiteSpace(installsJson) || !File.Exists(installsJson) || string.IsNullOrWhiteSpace(metadataDir)) yield break;
        string? client = null;
        using (var doc = JsonDocument.Parse(File.ReadAllText(installsJson)))
            if (doc.RootElement.TryGetProperty("rc_default", out var rc) && rc.ValueKind == JsonValueKind.String)
                client = rc.GetString();
        if (client is null || !File.Exists(client)) yield break;

        foreach (var (code, name, meta) in RiotProducts)
        {
            var yaml = Path.Combine(metadataDir, meta, meta + ".product_settings.yaml");
            if (!File.Exists(yaml)) continue;
            var m = Regex.Match(File.ReadAllText(yaml), @"product_install_full_path:\s*""?([^""\r\n]+)""?");
            var dir = m.Success ? m.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar) : null;
            if (dir is null || !Directory.Exists(dir)) continue;
            yield return new Game { Id = "riot:" + code, Name = name, Launcher = GameLauncherKind.Riot, InstallDir = dir, ExePath = client, LaunchToken = code };
        }
    }
}
