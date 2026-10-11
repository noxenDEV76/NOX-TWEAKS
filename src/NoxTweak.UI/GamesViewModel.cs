using System.Collections.ObjectModel;
using System.Windows.Input;
using NoxTweak.Core.GameMode;
using NoxTweak.Core.Games;

namespace NoxTweak.UI;

public sealed class GameCardViewModel : ObservableObject
{
    private readonly AppServices _svc;
    public Game Game { get; }
    public string Name => Game.Name;
    public string Initials => Game.Initials;
    public string Launcher => Game.LauncherLabel;
    public string ArtStart { get; }
    public string ArtEnd { get; }
    public bool IsLastPlayed { get; }
    public bool IsFavorite => _svc.Settings.Favorites.Contains(Game.Id);
    public bool GameModeOn => !_svc.Settings.GameModeOffFor.Contains(Game.Id);
    public string GameModeText => GameModeOn ? "Game Mode on" : "Game Mode off";
    public bool IsRunning => _svc.Controller.CurrentGame?.Id == Game.Id;
    public string LaunchText => IsRunning ? "Running…" : "Launch Game";
    public string Location => Game.InstallDir ?? Game.ExePath ?? "";
    public ICommand LaunchCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand GameModeCommand { get; }

    public GameCardViewModel(AppServices svc, Game game, Func<GameCardViewModel, Task> launch, bool lastPlayed)
    {
        _svc = svc; Game = game; IsLastPlayed = lastPlayed;
        (ArtStart, ArtEnd) = Colors(game.Name);
        LaunchCommand = new AsyncCommand(() => launch(this), () => !IsRunning);
        FavoriteCommand = new RelayCommand(() =>
        {
            if (!_svc.Settings.Favorites.Remove(Game.Id)) _svc.Settings.Favorites.Add(Game.Id);
            _svc.SaveSettings(); Raise(nameof(IsFavorite));
        });
        GameModeCommand = new RelayCommand(() =>
        {
            if (!_svc.Settings.GameModeOffFor.Remove(Game.Id)) _svc.Settings.GameModeOffFor.Add(Game.Id);
            _svc.SaveSettings(); Raise(nameof(GameModeOn)); Raise(nameof(GameModeText));
        });
    }

    public void RefreshRunning() { Raise(nameof(IsRunning)); Raise(nameof(LaunchText)); }

    /// <summary>Stable, original gradient per title (no third-party artwork is bundled or downloaded).</summary>
    private static (string, string) Colors(string name)
    {
        var h = 17;
        foreach (var ch in name) h = unchecked(h * 31 + ch);
        var hue = (uint)h % 360;
        return (Hsl(hue, 0.55, 0.32), Hsl((hue + 40) % 360, 0.60, 0.14));
    }

    private static string Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = l - c / 2;
        (double r, double g, double b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return $"#{(int)((r + m) * 255):X2}{(int)((g + m) * 255):X2}{(int)((b + m) * 255):X2}";
    }
}

public sealed class GamesViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private readonly Action<string> _navigate;
    private bool _loading;

    public string Title => "Games Library";
    public string Subtitle => "Browse, launch and optimize all your games in one place. Game Mode is applied around each launch and undone when the game closes.";
    public string UpdatedText => _svc.GamesScannedAt is { } t ? $"Updated {Ago(t)}" : "Not scanned yet";
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) Raise(nameof(IsEmpty)); } }
    public ObservableCollection<GameCardViewModel> Games { get; } = new();
    public ObservableCollection<PresetViewModel> Presets { get; } = new();
    public bool IsEmpty => Games.Count == 0 && !IsLoading;
    public string EmptyText => "No games found yet. NOX TWEAK reads Steam, Epic Games and Riot launcher data. For anything else (Battle.net, Xbox app, standalone), use Add Game.";
    public string GameCountText => $"{Games.Count} game{(Games.Count == 1 ? "" : "s")}";

    public bool GameModeActive => _svc.Settings.GameModeActive;
    public string GameModeStatus => GameModeActive ? "Active" : "Off";
    public string GameModeText => GameModeActive
        ? $"Applies the {GameModePresets.Selected(_svc.Settings).Name} preset around each launch."
        : "Off: games launch without any changes. Turn it on to use a preset.";
    public string SelectedPresetName => GameModePresets.Selected(_svc.Settings).Name;

    public ICommand AddGameCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand EditModeCommand { get; }
    public ICommand ToggleModeCommand { get; }

    public GamesViewModel(AppServices svc, IUi ui, Action<string> navigate)
    {
        _svc = svc; _ui = ui; _navigate = navigate;
        AddGameCommand = new AsyncCommand(async () =>
        {
            var exe = _ui.PickExecutable();
            if (exe is null) return;
            _svc.AddManualGame(exe);
            await _svc.RefreshGamesAsync();
        });
        RescanCommand = new AsyncCommand(RescanAsync);
        EditModeCommand = new RelayCommand(() => _navigate("GameMode"));
        ToggleModeCommand = new RelayCommand(() => { _svc.SetGameModeActive(!_svc.Settings.GameModeActive); Rebuild(); });

        _svc.GamesChanged += () => _ui.Post(Rebuild);
        _svc.SettingsChanged += () => _ui.Post(RebuildPresets);
        _svc.Controller.Changed += (_, _) => _ui.Post(() => { foreach (var g in Games) g.RefreshRunning(); });
        RebuildPresets();
    }

    public async Task RescanAsync()
    {
        IsLoading = true;
        try { await _svc.RefreshGamesAsync(); }
        finally { IsLoading = false; }
    }

    public void Activated() { if (_svc.GamesScannedAt is null) _ = RescanAsync(); else Rebuild(); }
    public void Deactivated() { }

    private void RebuildPresets()
    {
        Presets.Clear();
        foreach (var p in GameModePresets.All(_svc.Settings))
            Presets.Add(new PresetViewModel(p, pick => { _svc.Settings.PresetId = pick.Id; _svc.SaveSettings(); }) { IsOn = p.Id == _svc.Settings.PresetId });
        foreach (var n in new[] { nameof(GameModeActive), nameof(GameModeStatus), nameof(GameModeText), nameof(SelectedPresetName) }) Raise(n);
    }

    private void Rebuild()
    {
        Games.Clear();
        var s = _svc.Settings;
        var ordered = _svc.Games
            .OrderByDescending(g => s.Favorites.Contains(g.Id))
            .ThenByDescending(g => s.LastPlayed.TryGetValue(g.Id, out var t) ? t : DateTime.MinValue)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var g in ordered)
            Games.Add(new GameCardViewModel(_svc, g, LaunchAsync, g.Id == s.LastGameId));
        foreach (var n in new[] { nameof(IsEmpty), nameof(UpdatedText), nameof(GameCountText) }) Raise(n);
        RebuildPresets();
    }

    private async Task LaunchAsync(GameCardViewModel card)
    {
        if (!File.Exists(card.Game.ExePath ?? "x") && card.Game.Launcher == GameLauncherKind.Manual)
        {
            await _ui.InfoAsync("Game not found", $"The executable for {card.Name} no longer exists. Remove it and add it again.");
            return;
        }
        try
        {
            var results = await Task.Run(() => _svc.Controller.Launch(card.Game));
            card.RefreshRunning();
            var done = results.Count(r => r.Done);
            _ui.Toast(results.Count == 0
                ? $"{card.Name} launched."
                : $"{card.Name} launched with Game Mode ({done}/{results.Count} actions done). Settings return to normal when it closes.");
        }
        catch (Exception ex)
        {
            await _ui.InfoAsync($"Could not launch {card.Name}", ex.Message + "\n\nNo Game Mode changes were left behind. Check that the launcher is installed and you are signed in.");
        }
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? "just now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago" : $"{(int)d.TotalHours} hr ago";
    }
}
