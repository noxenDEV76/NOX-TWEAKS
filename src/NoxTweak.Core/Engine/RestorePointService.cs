using System.Diagnostics;

namespace NoxTweak.Core.Engine;

public sealed record RestorePointResult(bool Created, string Message);

/// <summary>
/// Creates a Windows System Restore point with a fixed command (no user text is ever passed to the shell).
/// System Restore protects Windows system files and settings; it is separate from NOX TWEAK's own per-change backups.
/// </summary>
public static class RestorePointService
{
    public static RestorePointResult TryCreate(IHostInfo host)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "System Restore is only available on Windows.");
        if (!host.IsAdmin) return new(false, "Creating a restore point needs administrator rights.");
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "Checkpoint-Computer -Description 'NOX TWEAK restore point' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return new(false, "Windows did not finish in time."); }
            if (p.ExitCode == 0) return new(true, "Restore point created.");
            var err = p.StandardError.ReadToEnd().Trim();
            if (err.Length > 300) err = err[..300] + "…";
            return new(false, string.IsNullOrWhiteSpace(err)
                ? "Windows refused to create a restore point. System Restore may be turned off, or one was already created in the last 24 hours."
                : $"Windows refused to create a restore point: {err}");
        }
        catch (Exception ex) { return new(false, $"Could not start the restore-point command: {ex.Message}"); }
    }
}
