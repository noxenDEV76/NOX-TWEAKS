using System.Diagnostics;
using System.Text.RegularExpressions;
using NoxTweak.Core.Optimizations;

namespace NoxTweak.Core.Engine;

public sealed record PowerPlan(string Guid, string Name);

public interface IPowerPlanService
{
    string? GetActiveGuid();
    IReadOnlyList<PowerPlan> List();
    /// <summary>Only allow-listed scheme GUIDs can be activated. Throws otherwise.</summary>
    void SetActive(string guid);
}

/// <summary>powercfg wrapper with a fixed, allow-listed argument set. No free-form commands.</summary>
public sealed class PowerPlanService : IPowerPlanService
{
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    public string? GetActiveGuid()
    {
        var o = Run("/getactivescheme");
        var m = GuidRx.Match(o);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public IReadOnlyList<PowerPlan> List()
    {
        var list = new List<PowerPlan>();
        foreach (var line in Run("/list").Split('\n'))
        {
            var m = GuidRx.Match(line);
            if (!m.Success) continue;
            var open = line.IndexOf('(');
            var close = line.LastIndexOf(')');
            var name = open >= 0 && close > open ? line[(open + 1)..close] : m.Value;
            list.Add(new PowerPlan(m.Value.ToLowerInvariant(), name));
        }
        return list;
    }

    public void SetActive(string guid)
    {
        guid = guid.ToLowerInvariant();
        if (!PowerGuids.Allowed.Contains(guid))
            throw new InvalidOperationException("Power scheme is not on the allow-list.");

        if (guid == PowerGuids.Ultimate && !List().Any(p => p.Guid == guid))
            Run("-duplicatescheme", PowerGuids.Ultimate);

        Run("/setactive", guid);
    }

    private static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("powercfg.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powercfg could not start.");
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException("powercfg did not answer in time.");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"powercfg failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd().Trim()}");
        return output;
    }
}

/// <summary>Test double.</summary>
public sealed class FakePowerPlanService : IPowerPlanService
{
    public string Active { get; set; } = PowerGuids.Balanced;
    public bool FailNext { get; set; }
    public string? GetActiveGuid() => Active;
    public IReadOnlyList<PowerPlan> List() => PowerGuids.Allowed.Select(g => new PowerPlan(g, g)).ToList();
    public void SetActive(string guid)
    {
        if (FailNext) { FailNext = false; throw new InvalidOperationException("Simulated powercfg failure"); }
        if (!PowerGuids.Allowed.Contains(guid)) throw new InvalidOperationException("Not allow-listed");
        Active = guid;
    }
}
