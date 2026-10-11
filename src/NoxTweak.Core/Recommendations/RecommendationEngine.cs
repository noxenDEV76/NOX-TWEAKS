using NoxTweak.Core.Engine;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;
using NoxTweak.Core.SystemInfo;

namespace NoxTweak.Core.Recommendations;

public sealed class RecItem
{
    public required PlanGroup Group { get; init; }
    public required string Title { get; init; }
    public required string Problem { get; init; }
    public required string Evidence { get; init; }
    public required string Reason { get; init; }
    public Confidence Confidence { get; init; } = Confidence.Medium;
    public OptimizationDefinition? Definition { get; init; }
    /// <summary>"Confirmed" findings come from measured values; "Hypothesis" ones need a test to confirm.</summary>
    public bool Confirmed { get; init; }
    /// <summary>Where the UI can send the user for items that are not one-click tweaks.</summary>
    public string? NavigateTo { get; init; }
}

public sealed class RecommendationPlan
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<RecItem> Items { get; init; } = Array.Empty<RecItem>();
    public IReadOnlyList<RecItem> Of(PlanGroup g) => Items.Where(i => i.Group == g).ToList();
    public IEnumerable<RecItem> Applicable => Items.Where(i => i.Group != PlanGroup.NotRecommended);
    public IEnumerable<OptimizationDefinition> SafeQuickSet =>
        Items.Where(i => i.Definition is { Tier: Tier.Safe, PowerPlanGuid: null } && i.Group is PlanGroup.HighPriority or PlanGroup.Recommended)
             .Select(i => i.Definition!).Where(d => d.Risk == RiskLevel.Low && !d.NeedsAdminRights);
}

/// <summary>
/// Deterministic, rules-based planner. Recommends nothing when the configuration is already fine,
/// separates confirmed findings from hypotheses, and cites the measured evidence for each item.
/// </summary>
public static class RecommendationEngine
{
    public static RecommendationPlan Build(SystemSnapshot s, OptimizationEngine engine, int enabledStartupApps)
    {
        var items = new List<RecItem>();
        OptState State(string id) => engine.GetState(Catalog.Find(id)!);

        // --- Windows gaming settings
        if (State("core.gamemode") == OptState.NotApplied)
            items.Add(Def("core.gamemode", PlanGroup.Recommended, "Windows Game Mode is off",
                "Game Mode setting read from the registry: not enabled.",
                "With Game Mode on, Windows holds back background installs and driver updates while a game is in front. Effect varies by game.",
                Confidence.Medium, confirmed: true));

        if (State("core.gamedvr") == OptState.NotApplied)
            items.Add(Def("core.gamedvr", PlanGroup.Recommended, "Background game recording may be enabled",
                "Game DVR / app capture settings are not set to off.",
                "Background capture can use the GPU video encoder and disk writes while you play. Skip this if you clip with Win+Alt+G.",
                Confidence.Medium, confirmed: true));

        // --- Power
        var plan = s.PowerPlanGuid;
        if (plan == PowerGuids.Balanced && State("power.high") == OptState.NotApplied)
        {
            if (s.IsLaptop == false)
                items.Add(Def("power.high", PlanGroup.Recommended, "Desktop PC on the Balanced power plan",
                    $"Active plan: {s.PowerPlan.Value}. No battery detected, so this looks like a desktop.",
                    "High Performance keeps clocks responsive. Cost: higher idle power and heat. Test with a before/after run.",
                    Confidence.Low, confirmed: false));
            else if (s.IsLaptop == true)
                items.Add(new RecItem
                {
                    Group = PlanGroup.NotRecommended, Title = "High Performance plan on a laptop",
                    Problem = "A battery was detected.", Evidence = $"Active plan: {s.PowerPlan.Value}; battery present; on battery: {(s.OnBattery == true ? "yes" : "no")}.",
                    Reason = "Keeping Balanced protects battery life and thermals. Game Mode can switch plans only while a game runs and only on AC power.",
                    Confidence = Confidence.High, Confirmed = true,
                });
        }

        // --- Startup
        if (enabledStartupApps >= 8)
            items.Add(new RecItem
            {
                Group = PlanGroup.Recommended, Title = "Many apps start with Windows",
                Problem = $"{enabledStartupApps} per-user startup apps are enabled.",
                Evidence = "Counted from the Run registry key at scan time.",
                Reason = "Each one competes for CPU, memory and disk after sign-in. Review the list and disable only what you do not need.",
                Confidence = Confidence.Medium, Confirmed = true, NavigateTo = "General:Apps",
            });

        // --- Memory
        if (s.RamTotalBytes > 0 && s.RamAvailablePercent < 15)
            items.Add(new RecItem
            {
                Group = PlanGroup.HighPriority, Title = "Memory is almost full",
                Problem = $"Only {s.RamAvailablePercent:0}% of RAM was available during the scan.",
                Evidence = $"{s.Ram.Value} installed; top consumer: {s.TopMemoryProcesses.FirstOrDefault()?.Name ?? "unknown"}.",
                Reason = "When memory runs out Windows swaps to disk, which causes stutter. Close heavy apps before playing (Game Mode can do this for apps you choose).",
                Confidence = Confidence.High, Confirmed = true, NavigateTo = "GameMode",
            });
        else if (s.RamTotalBytes > 0 && s.RamTotalBytes < 8L * 1024 * 1024 * 1024)
            items.Add(new RecItem
            {
                Group = PlanGroup.Optional, Title = "Installed RAM is limited",
                Problem = $"{s.Ram.Value} of RAM installed.",
                Evidence = "Read from Windows.",
                Reason = "For many modern games more physical RAM will help more than any software tweak. NOX TWEAK does not offer RAM 'cleaners' because they do not help.",
                Confidence = Confidence.Medium, Confirmed = true,
            });

        // --- Storage
        foreach (var d in s.Drives.Where(d => d.FreePercent < 10 && d.TotalBytes > 0))
            items.Add(new RecItem
            {
                Group = PlanGroup.HighPriority, Title = $"Low free space on {d.Name}",
                Problem = $"{d.FreePercent:0}% free ({d.FreeBytes / 1024 / 1024 / 1024} GB).",
                Evidence = "Read from the file system.",
                Reason = "Nearly full drives slow down installs, updates and game loading. Free space with Windows Storage Sense or uninstall unused games.",
                Confidence = Confidence.High, Confirmed = true,
            });

        // --- Network
        if (s.ActiveAdapter is { Kind: "Wi-Fi" } wifi)
            items.Add(new RecItem
            {
                Group = PlanGroup.Optional, Title = "Connected over Wi-Fi",
                Problem = $"Active adapter '{wifi.Name}' is wireless.",
                Evidence = "Adapter type and gateway read from Windows.",
                Reason = "A wired Ethernet cable is usually steadier for online games. Run the ping test on the Network page to see your own packet loss and jitter.",
                Confidence = Confidence.Medium, Confirmed = true, NavigateTo = "Network",
            });

        // --- Honest CPU/GPU-bottleneck statement
        items.Add(new RecItem
        {
            Group = PlanGroup.Optional, Title = "CPU- or GPU-limited? Not determined yet",
            Problem = "Not enough data to name a bottleneck.",
            Evidence = "NOX TWEAK has no per-game frame-time or GPU-utilization data. High CPU use alone does not prove a CPU limit.",
            Reason = "No change is recommended on a guess. Watch the Home chart while playing and compare with the in-game GPU usage readout.",
            Confidence = Confidence.High, Confirmed = false,
        });

        // --- Cosmetic / convenience / privacy (optional)
        foreach (var id in new[] { "qol.mouseaccel", "core.notifications", "core.animations", "core.transparency", "priv.adid", "priv.tips", "priv.tailored", "priv.websearch", "qol.stickykeys", "qol.fileext", "qol.menudelay" })
        {
            var def = Catalog.Find(id)!;
            if (State(id) == OptState.NotApplied)
                items.Add(new RecItem
                {
                    Group = PlanGroup.Optional, Title = def.Title, Problem = "Optional preference, not a performance problem.",
                    Evidence = "Setting currently differs from the proposed value.", Reason = def.Description,
                    Confidence = Confidence.Low, Definition = def,
                });
        }

        // --- Advanced
        if (State("adv.hags") == OptState.NotApplied && s.HasDiscreteOrAnyGpu)
            items.Add(Def("adv.hags", PlanGroup.Advanced, "Hardware-accelerated GPU scheduling is off",
                $"GPU detected: {s.Gpus.First().Name}.", "May help or hurt depending on game and driver. Needs administrator rights and a restart; only keep it if a before/after test is better.",
                Confidence.Low, confirmed: false));
        if (State("adv.bgapps") == OptState.NotApplied)
            items.Add(Def("adv.bgapps", PlanGroup.Advanced, "Store apps may run in the background (Windows 10)",
                "Global background-apps switch is on.", "Stops Store apps from running unseen. Some apps stop updating until opened.", Confidence.Low, confirmed: false));

        // --- Things we refuse
        foreach (var (title, reason) in Catalog.Refused)
            items.Add(new RecItem
            {
                Group = PlanGroup.NotRecommended, Title = title, Problem = "Offered by many other optimizers.",
                Evidence = "NOX TWEAK policy.", Reason = reason, Confidence = Confidence.High, Confirmed = true,
            });

        return new RecommendationPlan { Items = items };
    }

    private static RecItem Def(string id, PlanGroup g, string problem, string evidence, string reason, Confidence c, bool confirmed)
    {
        var d = Catalog.Find(id)!;
        return new RecItem { Group = g, Title = d.Title, Problem = problem, Evidence = evidence, Reason = reason, Confidence = c, Definition = d, Confirmed = confirmed };
    }
}
