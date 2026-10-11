using NoxTweak.Core.Logging;
using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;
using NoxTweak.Core.Registry;

namespace NoxTweak.Core.Engine;

public interface IHostInfo
{
    int WindowsBuild { get; }
    bool IsAdmin { get; }
}

public sealed class FixedHostInfo(int build, bool admin) : IHostInfo
{
    public int WindowsBuild { get; set; } = build;
    public bool IsAdmin { get; set; } = admin;
}

public sealed class OperationResult
{
    public OpOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public RestartNeed Restart { get; init; }
    public string? BackupId { get; init; }
    public bool Ok => Outcome == OpOutcome.Success;
    /// <summary>True when the blocker is missing administrator rights, so the UI can offer to relaunch elevated.</summary>
    public bool NeedsElevation { get; init; }
}

/// <summary>
/// Transactional executor. Every change: validate -> back up -> apply -> read back -> record.
/// Any failure rolls back what was already written. Restoration returns the exact original state.
/// </summary>
public sealed class OptimizationEngine
{
    private readonly IRegistry _reg;
    private readonly IPowerPlanService _power;
    private readonly BackupStore _backups;
    private readonly IHostInfo _host;
    private readonly IAppLog _log;

    public OptimizationEngine(IRegistry reg, IPowerPlanService power, BackupStore backups, IHostInfo host, IAppLog log)
    {
        _reg = reg; _power = power; _backups = backups; _host = host; _log = log;
    }

    public BackupStore Backups => _backups;
    public IHostInfo Host => _host;

    // ------------------------------------------------------------------ queries

    public bool IsApplicable(OptimizationDefinition d, out string reason)
    {
        reason = "";
        var b = _host.WindowsBuild;
        if (b > 0 && b < d.MinBuild) { reason = "Requires a newer version of Windows."; return false; }
        if (b > 0 && b > d.MaxBuild) { reason = "Not applicable to this version of Windows."; return false; }
        return true;
    }

    public OptState GetState(OptimizationDefinition d)
    {
        if (!IsApplicable(d, out _)) return OptState.Unsupported;
        try
        {
            if (d.PowerPlanGuid is not null)
                return string.Equals(_power.GetActiveGuid(), d.PowerPlanGuid, StringComparison.OrdinalIgnoreCase)
                    ? OptState.Applied : OptState.NotApplied;

            var matches = 0;
            foreach (var c in d.Changes)
            {
                var cur = _reg.Read(c.Hive, c.Path, c.Name);
                if (cur is not null && cur.SameAs(Proposed(c))) matches++;
            }
            if (matches == d.Changes.Count) return OptState.Applied;
            return matches == 0 ? OptState.NotApplied : OptState.Partial;
        }
        catch (Exception ex)
        {
            _log.Warn($"State check failed for {d.Id}: {ex.Message}");
            return OptState.Unsupported;
        }
    }

    private static RegValue Proposed(RegistryChange c) => new(c.Kind, c.Value);

    // -------------------------------------------------------------------- apply

    public OperationResult Apply(OptimizationDefinition d, IEnumerable<OptimizationDefinition>? catalogForConflicts = null)
    {
        if (!IsApplicable(d, out var why))
            return Blocked(d, why);

        var state = GetState(d);
        if (state == OptState.Applied)
            return new OperationResult { Outcome = OpOutcome.Success, Message = "Already applied. Nothing changed." };

        if (d.NeedsAdminRights && !_host.IsAdmin)
            return new OperationResult
            {
                Outcome = OpOutcome.Blocked, NeedsElevation = true,
                Message = "This change needs administrator rights. Nothing was changed."
            };

        foreach (var other in catalogForConflicts ?? Catalog.All)
        {
            if (d.ConflictsWith.Contains(other.Id) && GetState(other) == OptState.Applied)
                return Blocked(d, $"Conflicts with '{other.Title}', which is currently applied. Restore it first.");
        }

        // 1) snapshot
        var record = new BackupRecord
        {
            Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{d.Id}",
            OptimizationId = d.Id, Title = d.Title, CreatedUtc = DateTime.UtcNow, Status = "Active",
        };
        try
        {
            foreach (var c in d.Changes)
            {
                var cur = _reg.Read(c.Hive, c.Path, c.Name); // throws if the existing type is unsupported
                if (cur is not null && cur.Kind != c.Kind)
                    return Blocked(d, $"'{c.Name}' exists with a different type ({cur.Kind}); refusing to overwrite it.");
                record.Entries.Add(new BackupEntry
                {
                    Hive = c.Hive.ToString(), Path = c.Path, Name = c.Name,
                    Existed = cur is not null, Kind = cur?.Kind.ToString(), Data = cur?.Serialize()
                });
            }
            if (d.PowerPlanGuid is not null)
                record.PreviousPowerPlan = _power.GetActiveGuid();
            _backups.Save(record);
        }
        catch (Exception ex)
        {
            _log.Error($"Backup failed for {d.Id}: {ex.Message}");
            return new OperationResult { Outcome = OpOutcome.Blocked, Message = $"Backup could not be created, so nothing was changed. ({ex.Message})" };
        }

        // 2) apply + 3) verify
        var written = new List<int>();
        try
        {
            if (d.PowerPlanGuid is not null)
            {
                _power.SetActive(d.PowerPlanGuid);
                var active = _power.GetActiveGuid();
                if (!string.Equals(active, d.PowerPlanGuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows did not confirm the new power plan.");
            }
            else
            {
                for (var i = 0; i < d.Changes.Count; i++)
                {
                    var c = d.Changes[i];
                    _reg.Write(c.Hive, c.Path, c.Name, Proposed(c));
                    written.Add(i);
                    var back = _reg.Read(c.Hive, c.Path, c.Name);
                    if (back is null || !back.SameAs(Proposed(c)))
                        throw new InvalidOperationException($"Read-back of '{c.Name}' did not match the new value.");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Apply failed for {d.Id}: {ex.Message} - rolling back");
            var rollbackOk = Rollback(record);
            _backups.SetStatus(record, "Failed");
            return new OperationResult
            {
                Outcome = rollbackOk ? OpOutcome.RolledBack : OpOutcome.Failed,
                BackupId = record.Id,
                Message = rollbackOk
                    ? $"The change failed and was undone, so your settings are as before. ({ex.Message})"
                    : $"The change failed and could not be fully undone. Open Backups & Restore to review. ({ex.Message})"
            };
        }

        _log.Info($"Applied {d.Id} (backup {record.Id})");
        return new OperationResult { Outcome = OpOutcome.Success, BackupId = record.Id, Restart = d.Restart, Message = "Applied and verified." };
    }

    // ------------------------------------------------------------------ restore

    public OperationResult Restore(OptimizationDefinition d)
    {
        var rec = _backups.LatestActive(d.Id);
        if (rec is null)
            return new OperationResult
            {
                Outcome = OpOutcome.Blocked,
                Message = "No NOX TWEAK backup exists for this setting (it was already set before NOX TWEAK, so there is no original to restore)."
            };
        return RestoreBackup(rec.Id);
    }

    public OperationResult RestoreBackup(string backupId)
    {
        var rec = _backups.Load(backupId);
        if (rec is null)
            return new OperationResult { Outcome = OpOutcome.Blocked, Message = "Backup file not found or unreadable." };
        if (!_backups.VerifyIntegrity(rec))
            return new OperationResult { Outcome = OpOutcome.Blocked, Message = "Backup failed its integrity check and was NOT used." };
        if (rec.Status != "Active")
            return new OperationResult { Outcome = OpOutcome.Blocked, Message = $"This backup is {rec.Status.ToLowerInvariant()} and cannot be restored." };

        var def = Catalog.Find(rec.OptimizationId);
        var needsAdmin = rec.Entries.Any(e => e.HiveEnum == RegHive.LocalMachine);
        if (needsAdmin && !_host.IsAdmin)
            return new OperationResult { Outcome = OpOutcome.Blocked, NeedsElevation = true, Message = "Restoring this setting needs administrator rights." };

        if (!Rollback(rec))
            return new OperationResult { Outcome = OpOutcome.Failed, Message = "Restore did not fully succeed. Check the logs." };

        _backups.SetStatus(rec, "Restored");
        _log.Info($"Restored {rec.OptimizationId} from {rec.Id}");
        return new OperationResult { Outcome = OpOutcome.Success, Restart = def?.Restart ?? RestartNeed.None, Message = "Original setting restored and verified." };
    }

    /// <summary>Puts every entry back exactly as recorded and verifies it. Returns false if anything differs.</summary>
    private bool Rollback(BackupRecord rec)
    {
        var ok = true;
        foreach (var e in rec.Entries)
        {
            try
            {
                if (e.Existed) _reg.Write(e.HiveEnum, e.Path, e.Name, e.OriginalValue!);
                else _reg.Delete(e.HiveEnum, e.Path, e.Name);

                var back = _reg.Read(e.HiveEnum, e.Path, e.Name);
                var good = e.Existed ? back is not null && back.SameAs(e.OriginalValue) : back is null;
                if (!good) { ok = false; _log.Error($"Rollback verify mismatch: {e.Path}\\{e.Name}"); }
            }
            catch (Exception ex)
            {
                ok = false;
                _log.Error($"Rollback error on {e.Path}\\{e.Name}: {ex.Message}");
            }
        }
        if (rec.PreviousPowerPlan is not null)
        {
            try
            {
                _power.SetActive(rec.PreviousPowerPlan);
                if (!string.Equals(_power.GetActiveGuid(), rec.PreviousPowerPlan, StringComparison.OrdinalIgnoreCase)) ok = false;
            }
            catch (Exception ex) { ok = false; _log.Error($"Power plan rollback failed: {ex.Message}"); }
        }
        return ok;
    }

    private OperationResult Blocked(OptimizationDefinition d, string why)
    {
        _log.Warn($"Blocked {d.Id}: {why}");
        return new OperationResult { Outcome = OpOutcome.Blocked, Message = why };
    }
}
