using NoxTweak.Core.Engine;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;

namespace NoxTweak.Core.Ai;

public enum AiStatus { NotConfigured, Offline, Available, Unavailable }

/// <summary>Plug-in point for an optional AI provider. The app never pretends one is connected.</summary>
public interface IAiAdvisor
{
    AiStatus Status { get; }
    string StatusText { get; }
    /// <summary>Returns ids of catalog optimizations the AI proposes. Every id is validated locally before use.</summary>
    Task<IReadOnlyList<string>> ProposeAsync(string diagnosticsSummary, CancellationToken ct);
}

public sealed class NoAiAdvisor : IAiAdvisor
{
    public AiStatus Status => AiStatus.NotConfigured;
    public string StatusText => "No AI provider configured. Recommendations come from the built-in local rules.";
    public Task<IReadOnlyList<string>> ProposeAsync(string diagnosticsSummary, CancellationToken ct) =>
        throw new InvalidOperationException("No AI provider is configured.");
}

public sealed record PlanValidation(IReadOnlyList<OptimizationDefinition> Accepted, IReadOnlyList<(string Id, string Reason)> Rejected);

/// <summary>
/// Gatekeeper between any proposal (AI or otherwise) and execution: unknown ids, inapplicable
/// optimizations and anything above the allowed tier are rejected. Free-form commands cannot pass.
/// </summary>
public static class PlanValidator
{
    public static PlanValidation Validate(IEnumerable<string> proposedIds, OptimizationEngine engine, Tier maxTier = Tier.Safe)
    {
        var ok = new List<OptimizationDefinition>();
        var bad = new List<(string, string)>();
        foreach (var id in proposedIds.Distinct())
        {
            var def = Catalog.Find(id);
            if (def is null) { bad.Add((id, "Not in the allow-list of reviewed optimizations.")); continue; }
            if (def.Tier > maxTier) { bad.Add((id, $"{def.Tier} optimizations need explicit user selection.")); continue; }
            if (!engine.IsApplicable(def, out var why)) { bad.Add((id, why)); continue; }
            ok.Add(def);
        }
        return new PlanValidation(ok, bad);
    }
}
