using System.Diagnostics;

namespace NoxTweak.Core.GameMode;

public sealed record CleanerCandidate(string ProcessName, string Title, long MemoryBytes);

/// <summary>
/// Closes only apps the user explicitly listed. Windows, security software, launchers and
/// anti-cheat components can never be closed, even if listed by mistake.
/// </summary>
public static class ProcessCleaner
{
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "lsass", "svchost", "system", "smss", "wininit",
        "taskmgr", "msmpeng", "securityhealthsystray", "securityhealthservice", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "sihost", "ctfmon", "fontdrvhost", "audiodg", "nvcontainer", "nvdisplay.container",
        "noxtweak", "steam", "steamwebhelper", "epicgameslauncher", "riotclientservices", "riotclientux", "battle.net",
        "easyanticheat", "easyanticheat_eos", "beservice", "vgc", "vgtray", "faceitclient", "faceit", "eaanticheat.gameservice",
    };

    public static bool IsProtected(string processName) => Protected.Contains(processName);

    public static IReadOnlyList<CleanerCandidate> Candidates()
    {
        var list = new List<CleanerCandidate>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || IsProtected(p.ProcessName) || p.Id == Environment.ProcessId) continue;
                list.Add(new CleanerCandidate(p.ProcessName, p.MainWindowTitle, p.WorkingSet64));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CleanerCandidate(g.Key, g.First().Title, g.Sum(x => x.MemoryBytes)))
            .OrderByDescending(c => c.MemoryBytes).ToList();
    }

    /// <summary>Asks apps to close (CloseMainWindow). Kills only when force is explicitly enabled.</summary>
    public static IReadOnlyList<string> Close(IEnumerable<string> names, bool force)
    {
        var report = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsProtected(name)) { report.Add($"{name}: protected, left alone"); continue; }
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0) { report.Add($"{name}: not running"); continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (!p.CloseMainWindow() || !p.WaitForExit(3000))
                    {
                        if (force && !p.HasExited) { p.Kill(); report.Add($"{name}: force closed"); }
                        else if (!p.HasExited) report.Add($"{name}: asked to close, still running (force close is off)");
                    }
                    else report.Add($"{name}: closed");
                }
                catch (Exception ex) { report.Add($"{name}: could not close ({ex.Message})"); }
                finally { p.Dispose(); }
            }
        }
        return report;
    }
}
