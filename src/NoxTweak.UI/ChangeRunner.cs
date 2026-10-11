using NoxTweak.Core.Engine;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;

namespace NoxTweak.UI;

/// <summary>
/// The single path every UI action takes to change the system: confirm when needed, run through the
/// engine off the UI thread, then tell the user honestly what happened and what to do next.
/// </summary>
public sealed class ChangeRunner
{
    private readonly AppServices _svc;
    private readonly IUi _ui;

    public ChangeRunner(AppServices svc, IUi ui) { _svc = svc; _ui = ui; }

    public static bool NeedsConfirmation(OptimizationDefinition d) =>
        d.Tier != Tier.Safe || d.Risk != RiskLevel.Low || d.Restart is RestartNeed.Required or RestartNeed.Recommended;

    public async Task<OperationResult> ApplyAsync(OptimizationDefinition d, bool confirm = true, bool announce = true)
    {
        if (confirm && NeedsConfirmation(d))
        {
            var details = new List<string>();
            if (d.Warning is not null) details.Add("Side effect: " + d.Warning);
            details.Add($"Risk: {d.Risk}.  Level: {d.Tier}.");
            if (d.Restart != RestartNeed.None) details.Add(d.Restart == RestartNeed.SignOut ? "You will need to sign out and back in." : "A restart is needed for this to take effect. NOX TWEAK will not restart Windows for you.");
            details.Add("The original value is backed up first and can be restored from Backups.");
            if (!await _ui.ConfirmAsync(d.Title, d.Description, "Apply", "Cancel", details))
                return new OperationResult { Outcome = OpOutcome.Blocked, Message = "Cancelled." };
        }
        var r = await Task.Run(() => _svc.Engine.Apply(d));
        await AfterAsync(d, r, "applied", announce);
        return r;
    }

    public async Task<OperationResult> RestoreAsync(OptimizationDefinition d, bool announce = true)
    {
        var r = await Task.Run(() => _svc.Engine.Restore(d));
        await AfterAsync(d, r, "restored", announce);
        return r;
    }

    private async Task AfterAsync(OptimizationDefinition d, OperationResult r, string verb, bool announce)
    {
        if (r.Ok)
        {
            if (r.Restart != RestartNeed.None) _svc.AddPendingRestart(d.Title, r.Restart);
            if (announce)
                _ui.Toast(r.Restart switch
                {
                    RestartNeed.None => $"{d.Title}: {verb} and verified.",
                    RestartNeed.SignOut => $"{d.Title}: {verb}. Sign out and back in to finish.",
                    _ => $"{d.Title}: {verb}. Restart Windows to finish."
                });
            _svc.NotifyStateChanged();
            return;
        }

        if (r.NeedsElevation)
        {
            if (await _ui.ConfirmAsync("Administrator rights needed",
                    $"'{d.Title}' changes a machine-wide setting. Nothing was changed. NOX TWEAK only asks for administrator rights when a specific change needs them.",
                    "Restart as administrator", "Not now"))
                _ui.RestartAsAdministrator();
            return;
        }

        var next = r.Outcome switch
        {
            OpOutcome.RolledBack => "Your settings are as they were before. You can try again.",
            OpOutcome.Failed => "Open Backups & Restore to review the change, or check Logs for details.",
            _ => "No change was made."
        };
        if (announce) await _ui.InfoAsync(d.Title, $"{r.Message}\n\n{next}");
        _svc.NotifyStateChanged();
    }
}
