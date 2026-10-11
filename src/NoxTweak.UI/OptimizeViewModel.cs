using System.Collections.ObjectModel;
using System.Windows.Input;
using NoxTweak.Core.Engine;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;
using NoxTweak.Core.Startup;

namespace NoxTweak.UI;

public sealed class OptionViewModel : ObservableObject
{
    private bool _isOn;
    public string Label { get; }
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    public ICommand Command { get; }
    public OptionViewModel(string label, Action<OptionViewModel> onPick) { Label = label; Command = new RelayCommand(() => onPick(this)); }
}

public sealed class TabViewModel : ObservableObject
{
    private bool _isOn;
    public OptTab Tab { get; }
    public string Name { get; }
    public string Glyph { get; }
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    public ICommand Command { get; }
    public TabViewModel(OptTab tab, string name, string glyph, Action<TabViewModel> onPick) { Tab = tab; Name = name; Glyph = glyph; Command = new RelayCommand(() => onPick(this)); }
}

/// <summary>One card in the grid. IsOn always mirrors the real system state after each operation.</summary>
public sealed class CardViewModel : ObservableObject
{
    private bool _isOn, _isBusy, _isInfoOpen;
    private string _status = "";
    private readonly Func<CardViewModel, Task> _toggle;

    public OptimizationDefinition? Definition { get; }
    public StartupEntry? Startup { get; }
    public string Title { get; }
    public string Description { get; }
    public string Glyph { get; }
    public string? WarningText { get; }
    public bool HasWarning => WarningText is not null;
    public bool NoWarning => WarningText is null;
    public string WarningLabel => HasWarning ? "Warning" : "No Warnings";
    public string RiskText { get; }
    public string TierText { get; }
    public string InfoText { get; }
    public bool CanToggle { get; }
    public OptState State { get; private set; }

    public bool IsOn { get => _isOn; private set => Set(ref _isOn, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public bool IsInfoOpen { get => _isInfoOpen; set => Set(ref _isInfoOpen, value); }
    public string Status { get => _status; private set { if (Set(ref _status, value)) { Raise(nameof(HasStatus)); Raise(nameof(StatusIsError)); } } }
    public bool HasStatus => _status.Length > 0;
    public bool StatusIsError => _status.StartsWith("Failed", StringComparison.Ordinal);

    public ICommand ToggleCommand { get; }
    public ICommand InfoCommand { get; }

    public CardViewModel(OptimizationDefinition def, OptState state, Func<CardViewModel, Task> toggle)
    {
        Definition = def; _toggle = toggle; State = state;
        Title = def.Title; Description = def.Description; WarningText = def.Warning;
        RiskText = $"Risk: {def.Risk}"; TierText = def.Tier.ToString().ToUpperInvariant();
        Glyph = GlyphFor(def);
        CanToggle = state != OptState.Unsupported;
        InfoText = BuildInfo(def);
        _isOn = state == OptState.Applied;
        _status = StatusFor(state, def);
        ToggleCommand = new AsyncCommand(() => _toggle(this), () => CanToggle);
        InfoCommand = new RelayCommand(() => IsInfoOpen = !IsInfoOpen);
    }

    /// <summary>Startup app card: ON means the app launches with Windows.</summary>
    public CardViewModel(StartupEntry entry, Func<CardViewModel, Task> toggle)
    {
        Startup = entry; _toggle = toggle;
        Title = entry.Name;
        Description = entry.Command.Length > 120 ? entry.Command[..120] + "…" : entry.Command;
        Glyph = "";
        WarningText = entry.CanManage ? null : "System-wide entry: manage it in Task Manager > Startup.";
        RiskText = "Risk: Low"; TierText = entry.CanManage ? "SAFE" : "READ-ONLY";
        CanToggle = entry.CanManage;
        InfoText = $"Command: {entry.Command}\nLocation: {(entry.Hive == RegHive.CurrentUser ? "Current user" : "All users")} Run key.\n" +
                   "Disabling writes a 'disabled' marker, the same as Task Manager. The entry is not deleted and can be re-enabled any time.";
        _isOn = entry.Enabled; State = OptState.NotApplied;
        _status = entry.Enabled ? "Starts with Windows" : "Disabled at startup";
        ToggleCommand = new AsyncCommand(() => _toggle(this), () => CanToggle);
        InfoCommand = new RelayCommand(() => IsInfoOpen = !IsInfoOpen);
    }

    public void SetBusy(bool busy) { IsBusy = busy; if (busy) Status = "Working…"; }

    public void SetState(OptState state, string? note = null)
    {
        State = state;
        IsOn = state == OptState.Applied;
        Status = note ?? (Definition is null ? Status : StatusFor(state, Definition));
        Raise(nameof(IsOn)); // re-sync the switch with reality even when the value did not change
    }

    public void SetStartupEnabled(bool enabled, string? note = null)
    {
        IsOn = enabled; Raise(nameof(IsOn));
        Status = note ?? (enabled ? "Starts with Windows" : "Disabled at startup");
    }

    public void ShowFailure(string message) { Status = "Failed: " + message; }

    private static string StatusFor(OptState s, OptimizationDefinition d) => s switch
    {
        OptState.Applied => d.Restart == RestartNeed.None ? "Applied" : "Applied",
        OptState.Partial => "Partly applied",
        OptState.Unsupported => "Not supported on this PC",
        _ => ""
    };

    private static string GlyphFor(OptimizationDefinition d) => d.Tab switch
    {
        OptTab.Powerplan => "",
        OptTab.Privacy => "",
        OptTab.Qol => "",
        _ => d.Id switch
        {
            "core.gamemode" or "core.gamedvr" => "",
            "core.notifications" => "",
            "core.animations" or "core.transparency" => "",
            "adv.hags" => "",
            _ => ""
        }
    };

    private static string BuildInfo(OptimizationDefinition d)
    {
        var lines = new List<string>
        {
            d.Technical, "",
            "Why: " + d.Justification,
            $"Risk: {d.Risk} · Level: {d.Tier}",
            d.NeedsAdminRights ? "Needs administrator rights." : "Does not need administrator rights.",
            d.Restart switch { RestartNeed.None => "No restart needed.", RestartNeed.SignOut => "Sign out and back in to apply.", _ => "Restart Windows to apply." },
        };
        if (d.Warning is not null) lines.Add("Warning: " + d.Warning);
        lines.Add("Verification: " + d.Verification);
        lines.Add("Restore: the exact original value is saved before the change; if it did not exist it is removed again.");
        if (d.MinBuild > 0) lines.Add($"Requires Windows build {d.MinBuild} or newer.");
        return string.Join("\n", lines);
    }
}

/// <summary>Grid page for General (tabs) and Advanced.</summary>
public sealed class OptimizeViewModel : ObservableObject, IPage
{
    private readonly AppServices _svc;
    private readonly IUi _ui;
    private readonly ChangeRunner _runner;
    private readonly OptPage _page;
    private OptTab _tab = OptTab.Core;
    private string _search = "";
    private string _filter = "All";
    private string _sort = "Name (A-Z)";
    private bool _filterOpen, _sortOpen, _loading;
    private List<CardViewModel> _all = new();

    public string Title { get; }
    public string Subtitle { get; }
    public bool HasTabs { get; }
    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<OptionViewModel> FilterOptions { get; } = new();
    public ObservableCollection<OptionViewModel> SortOptions { get; } = new();
    public ObservableCollection<CardViewModel> Cards { get; } = new();

    public string SectionTitle => HasTabs ? Tabs.FirstOrDefault(t => t.Tab == _tab)?.Name ?? "" : Title;
    public string SearchText { get => _search; set { if (Set(ref _search, value)) ApplyView(); } }
    public string FilterLabel => _filter == "All" ? "Filter" : _filter;
    public string SortLabel => _sort;
    public bool IsFilterOpen { get => _filterOpen; set => Set(ref _filterOpen, value); }
    public bool IsSortOpen { get => _sortOpen; set => Set(ref _sortOpen, value); }
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public bool IsEmpty => Cards.Count == 0 && !IsLoading;
    public string EmptyText => _tab == OptTab.Apps && string.IsNullOrEmpty(_search) && _filter == "All"
        ? "No per-user startup apps were found."
        : "No optimizations match your search or filter.";
    public bool IsAppsTab => HasTabs && _tab == OptTab.Apps;
    public string AppsNote => "Only per-user startup apps can be changed here. Turning one off does not uninstall it.";

    public ICommand ToggleFilterCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public ICommand RefreshCommand { get; }

    public OptimizeViewModel(AppServices svc, IUi ui, ChangeRunner runner, OptPage page)
    {
        _svc = svc; _ui = ui; _runner = runner; _page = page;
        HasTabs = page == OptPage.General;
        Title = page == OptPage.General ? "General" : "Advanced";
        Subtitle = page == OptPage.General ? "Enhance your system performance." : "Carefully selected settings that need more context. Nothing here is applied automatically.";

        if (HasTabs)
        {
            void Pick(TabViewModel t) { SelectTab(t.Tab); }
            Tabs.Add(new TabViewModel(OptTab.Core, "Core", "", Pick));
            Tabs.Add(new TabViewModel(OptTab.Privacy, "Privacy", "", Pick));
            Tabs.Add(new TabViewModel(OptTab.Qol, "QOL", "", Pick));
            Tabs.Add(new TabViewModel(OptTab.Apps, "Apps", "", Pick));
            Tabs.Add(new TabViewModel(OptTab.Powerplan, "Powerplan", "", Pick));
            Tabs[0].IsOn = true;
        }

        foreach (var f in new[] { "All", "Safe only", "Applied", "Not applied", "With warnings", "No warnings" })
            FilterOptions.Add(new OptionViewModel(f, o => { _filter = o.Label; MarkOptions(FilterOptions, _filter); Raise(nameof(FilterLabel)); IsFilterOpen = false; ApplyView(); }));
        foreach (var s in new[] { "Name (A-Z)", "Risk (low first)", "Applied first" })
            SortOptions.Add(new OptionViewModel(s, o => { _sort = o.Label; MarkOptions(SortOptions, _sort); Raise(nameof(SortLabel)); IsSortOpen = false; ApplyView(); }));
        MarkOptions(FilterOptions, _filter); MarkOptions(SortOptions, _sort);

        ToggleFilterCommand = new RelayCommand(() => { IsSortOpen = false; IsFilterOpen = !IsFilterOpen; });
        ToggleSortCommand = new RelayCommand(() => { IsFilterOpen = false; IsSortOpen = !IsSortOpen; });
        RefreshCommand = new AsyncCommand(LoadAsync);

        _svc.StateChanged += () => _ui.Post(() => _ = ReloadStatesAsync());
    }

    private static void MarkOptions(IEnumerable<OptionViewModel> opts, string label) { foreach (var o in opts) o.IsOn = o.Label == label; }

    public void SelectTab(OptTab tab)
    {
        if (!HasTabs) return;
        _tab = tab;
        foreach (var t in Tabs) t.IsOn = t.Tab == tab;
        Raise(nameof(SectionTitle)); Raise(nameof(IsAppsTab));
        _ = LoadAsync();
    }

    public void Activated() => _ = LoadAsync();
    public void Deactivated() { IsFilterOpen = false; IsSortOpen = false; }

    public async Task LoadAsync()
    {
        IsLoading = true; Raise(nameof(IsEmpty));
        try
        {
            if (IsAppsTab)
            {
                var entries = await Task.Run(() => StartupService.List(_svc.Registry));
                _all = entries.Select(e => new CardViewModel(e, ToggleAsync)).ToList();
            }
            else
            {
                var defs = Catalog.All.Where(d => d.Page == _page && (!HasTabs || d.Tab == _tab)).ToList();
                var states = await Task.Run(() => defs.Select(d => (d, _svc.Engine.GetState(d))).ToList());
                _all = states.Select(x => new CardViewModel(x.d, x.Item2, ToggleAsync)).ToList();
            }
        }
        finally { IsLoading = false; }
        ApplyView();
    }

    private async Task ReloadStatesAsync()
    {
        // Re-read the real state of every visible card (another page or Game Mode may have changed something).
        foreach (var c in _all.ToList())
        {
            if (c.Definition is not null)
            {
                var def = c.Definition;
                var st = await Task.Run(() => _svc.Engine.GetState(def));
                // Only touch cards whose real state changed, so a failure message stays visible.
                if (st != c.State) c.SetState(st);
            }
        }
        if (IsAppsTab) await LoadAsync();
        else ApplyView();
    }

    public void ApplyView()
    {
        IEnumerable<CardViewModel> q = _all;
        if (!string.IsNullOrWhiteSpace(_search))
            q = q.Where(c => c.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) || c.Description.Contains(_search, StringComparison.OrdinalIgnoreCase));
        q = _filter switch
        {
            "Safe only" => q.Where(c => c.Definition is null || c.Definition.Tier == Tier.Safe),
            "Applied" => q.Where(c => c.IsOn),
            "Not applied" => q.Where(c => !c.IsOn),
            "With warnings" => q.Where(c => c.HasWarning),
            "No warnings" => q.Where(c => !c.HasWarning),
            _ => q
        };
        q = _sort switch
        {
            "Risk (low first)" => q.OrderBy(c => c.Definition?.Risk ?? RiskLevel.Low).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase),
            "Applied first" => q.OrderByDescending(c => c.IsOn).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
        };
        Cards.Clear();
        foreach (var c in q) Cards.Add(c);
        Raise(nameof(IsEmpty)); Raise(nameof(EmptyText));
    }

    private async Task ToggleAsync(CardViewModel card)
    {
        card.SetBusy(true);
        try
        {
            if (card.Startup is not null) await ToggleStartupAsync(card);
            else await ToggleDefinitionAsync(card);
        }
        finally { card.SetBusy(false); }
    }

    private async Task ToggleDefinitionAsync(CardViewModel card)
    {
        var def = card.Definition!;
        var current = await Task.Run(() => _svc.Engine.GetState(def));
        OperationResult r;
        if (current == OptState.Applied)
        {
            r = await _runner.RestoreAsync(def);
            if (r.Outcome == OpOutcome.Blocked && !r.NeedsElevation)
                await _ui.InfoAsync(def.Title, r.Message + "\n\nIf you want a different value, change it in Windows Settings.");
        }
        else r = await _runner.ApplyAsync(def);

        // Never trust the toggle: re-read the real state.
        var after = await Task.Run(() => _svc.Engine.GetState(def));
        card.SetState(after, r.Ok ? null : (r.Outcome == OpOutcome.Blocked ? null : "Failed: " + r.Message));
    }

    private async Task ToggleStartupAsync(CardViewModel card)
    {
        var entry = card.Startup!;
        var wantEnabled = !card.IsOn;
        OperationResult r;
        if (wantEnabled)
        {
            var disableId = StartupService.DefinitionFor(entry.Name);
            r = _svc.Backups.LatestActive(disableId.Id) is not null
                ? await _runner.RestoreAsync(disableId, announce: false)
                : await _runner.ApplyAsync(StartupService.DefinitionEnable(entry.Name), confirm: false, announce: false);
        }
        else r = await _runner.ApplyAsync(StartupService.DefinitionForNow(entry.Name), confirm: false, announce: false);

        var now = await Task.Run(() => StartupService.List(_svc.Registry).FirstOrDefault(e => e.Name == entry.Name && e.Hive == entry.Hive));
        if (now is not null) card.SetStartupEnabled(now.Enabled, r.Ok ? null : "Failed: " + r.Message);
        else card.ShowFailure("entry no longer exists");
        if (r.Ok) _ui.Toast($"{entry.Name}: {(now?.Enabled == true ? "will start with Windows" : "will no longer start with Windows")}.");
    }
}
