using NoxTweak.Core.Model;

namespace NoxTweak.Core.Optimizations;

public sealed record RegistryChange(RegHive Hive, string Path, string Name, RegKind Kind, object Value)
{
    public string Display => $"{(Hive == RegHive.CurrentUser ? "HKCU" : "HKLM")}\\{Path}  ->  {Name}";
}

/// <summary>
/// A reviewed, typed optimization. The UI and the (optional) AI layer can only choose among these;
/// nothing outside this catalog can ever be executed.
/// </summary>
public sealed class OptimizationDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Everyday-language explanation (card body).</summary>
    public required string Description { get; init; }
    /// <summary>Mechanism and compatibility (info popup).</summary>
    public required string Technical { get; init; }
    public required string Justification { get; init; }
    public OptPage Page { get; init; } = OptPage.General;
    public OptTab Tab { get; init; } = OptTab.Core;
    public Tier Tier { get; init; } = Tier.Safe;
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    /// <summary>Side effect shown on the card as a warning. Null = "No Warnings".</summary>
    public string? Warning { get; init; }
    public bool RequiresAdmin { get; init; }
    public RestartNeed Restart { get; init; } = RestartNeed.None;
    public int MinBuild { get; init; }
    public int MaxBuild { get; init; } = int.MaxValue;
    public IReadOnlyList<RegistryChange> Changes { get; init; } = Array.Empty<RegistryChange>();
    /// <summary>When set, this optimization switches the Windows power plan (allow-listed GUIDs only).</summary>
    public string? PowerPlanGuid { get; init; }
    public IReadOnlyList<string> ConflictsWith { get; init; } = Array.Empty<string>();
    public string Verification => PowerPlanGuid is null
        ? "Every value is read back from the registry after writing and compared with the proposed value."
        : "The active power plan is read back with powercfg after switching.";

    public bool NeedsAdminRights => RequiresAdmin || Changes.Any(c => c.Hive == RegHive.LocalMachine);
}
