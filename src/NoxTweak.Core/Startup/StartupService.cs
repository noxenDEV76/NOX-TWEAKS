using NoxTweak.Core.Model;
using NoxTweak.Core.Optimizations;
using NoxTweak.Core.Registry;

namespace NoxTweak.Core.Startup;

public sealed record StartupEntry(string Name, string Command, RegHive Hive, bool Enabled, bool CanManage, OptimizationDefinition? Definition);

/// <summary>
/// Lists apps that launch at sign-in and lets the user disable/enable the per-user ones using the same
/// StartupApproved mechanism as Task Manager. Machine-wide entries are shown read-only.
/// </summary>
public static class StartupService
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static byte[] DisabledMarker(DateTime? now = null)
    {
        var b = new byte[12];
        b[0] = 0x03;
        BitConverter.GetBytes((now ?? DateTime.UtcNow).ToFileTimeUtc()).CopyTo(b, 4);
        return b;
    }

    public static IReadOnlyList<StartupEntry> List(IRegistry reg)
    {
        var result = new List<StartupEntry>();
        foreach (var hive in new[] { RegHive.CurrentUser, RegHive.LocalMachine })
        {
            var approved = reg.Enumerate(hive, ApprovedPath).ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in reg.Enumerate(hive, RunPath))
            {
                var cmd = value.Kind == RegKind.String ? (string)value.Data : "";
                var enabled = !(approved.TryGetValue(name, out var a) && a.Kind == RegKind.Binary && ((byte[])a.Data).Length > 0 && (((byte[])a.Data)[0] & 1) == 1);
                var manage = hive == RegHive.CurrentUser;
                result.Add(new StartupEntry(name, cmd, hive, enabled, manage, manage ? DefinitionFor(name) : null));
            }
        }
        return result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"Applied" for this definition means: the app is disabled at startup.</summary>
    public static OptimizationDefinition DefinitionFor(string entryName) => new()
    {
        Id = "startup." + Sanitize(entryName),
        Title = $"Do not start {entryName} with Windows",
        Description = "Stops this app from launching when you sign in. You can still open it yourself at any time.",
        Technical = $"Writes a 'disabled' marker for '{entryName}' under HKCU\\{ApprovedPath} (the mechanism used by Task Manager > Startup). The Run entry itself is not deleted.",
        Justification = "Fewer launch-time processes means less background activity. Benefit depends on the app.",
        Tab = OptTab.Apps, Risk = RiskLevel.Low,
        Changes = [new RegistryChange(RegHive.CurrentUser, ApprovedPath, entryName, RegKind.Binary, DisabledMarker(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)))],
    };

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    /// <summary>Re-enables an entry that was disabled outside NOX TWEAK (no NOX backup exists to restore).</summary>
    public static OptimizationDefinition DefinitionEnable(string entryName) => new()
    {
        Id = "startup-on." + Sanitize(entryName),
        Title = $"Start {entryName} with Windows",
        Description = "Lets this app launch when you sign in again.",
        Technical = $"Writes an 'enabled' marker for '{entryName}' under HKCU\\{ApprovedPath}.",
        Justification = "Reverses a startup block that was set in Task Manager or Settings.",
        Tab = OptTab.Apps, Risk = RiskLevel.Low,
        Changes = [new RegistryChange(RegHive.CurrentUser, ApprovedPath, entryName, RegKind.Binary, new byte[12] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })],
    };

    /// <summary>Marker with the real timestamp, used when actually disabling (the catalog copy has a fixed one for comparison).</summary>
    public static OptimizationDefinition DefinitionForNow(string entryName)
    {
        var d = DefinitionFor(entryName);
        return new OptimizationDefinition
        {
            Id = d.Id, Title = d.Title, Description = d.Description, Technical = d.Technical, Justification = d.Justification,
            Tab = d.Tab, Risk = d.Risk,
            Changes = [new RegistryChange(RegHive.CurrentUser, ApprovedPath, entryName, RegKind.Binary, DisabledMarker())],
        };
    }
}
