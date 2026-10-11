using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using WinReg = Microsoft.Win32.Registry;
using NoxTweak.Core.Engine;
using NoxTweak.Core.Model;

namespace NoxTweak.Core.SystemInfo;

public sealed class WindowsHostInfo : IHostInfo
{
    public int WindowsBuild { get; } = ReadBuild();
    public bool IsAdmin { get; } = ReadAdmin();

    private static int ReadBuild()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 0;
            using var k = WinReg.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        }
        catch { return 0; }
    }

    private static bool ReadAdmin()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

/// <summary>
/// Genuine local scan using documented Windows interfaces (registry, Win32, .NET networking).
/// Values that cannot be read reliably are reported as Unavailable rather than guessed.
/// </summary>
public static class SystemInventory
{
    public static SystemSnapshot Scan(IPowerPlanService power, IHostInfo host)
    {
        if (!OperatingSystem.IsWindows())
            return new SystemSnapshot
            {
                Os = Fact.Unsupported("NOX TWEAK scans Windows only."),
                IsAdmin = false, WindowsBuild = 0
            };
        return ScanWindows(power, host);
    }

    [SupportedOSPlatform("windows")]
    private static SystemSnapshot ScanWindows(IPowerPlanService power, IHostInfo host)
    {
        var cpu = Safe(() =>
        {
            using var k = WinReg.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var n = (k?.GetValue("ProcessorNameString") as string)?.Trim();
            return string.IsNullOrEmpty(n) ? Fact.Unavailable("Registry did not contain a CPU name.") : Fact.Verified(Regex1(n));
        });

        var logical = Fact.Verified(Environment.ProcessorCount.ToString());
        var cores = Safe(() =>
        {
            var c = CountPhysicalCores();
            return c > 0 ? Fact.Verified(c.ToString()) : Fact.Unavailable("Windows did not report core topology.");
        });

        long total = 0, avail = 0;
        var ram = Safe(() =>
        {
            var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            if (!Native.GlobalMemoryStatusEx(ref m)) return Fact.Unavailable("GlobalMemoryStatusEx failed.");
            total = (long)m.ullTotalPhys; avail = (long)m.ullAvailPhys;
            return Fact.Verified($"{Gb(total):0.#} GB", $"{Gb(avail):0.#} GB available");
        });

        var os = Safe(() =>
        {
            using var k = WinReg.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k is null) return Fact.Unavailable("Registry key missing.");
            var build = int.TryParse(k.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0;
            var product = (k.GetValue("ProductName") as string) ?? "Windows";
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            var ver = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string ?? "";
            var ubr = k.GetValue("UBR");
            return Fact.Verified($"{product} {ver}".Trim(), $"Build {build}{(ubr is null ? "" : "." + ubr)}");
        });

        var board = Safe(() =>
        {
            using var k = WinReg.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var m = k?.GetValue("BaseBoardManufacturer") as string;
            var p = k?.GetValue("BaseBoardProduct") as string;
            var sys = k?.GetValue("SystemProductName") as string;
            var text = string.Join(" ", new[] { m, p }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(text)) text = sys ?? "";
            return string.IsNullOrWhiteSpace(text) ? Fact.Unavailable("Firmware did not expose a board name.") : Fact.Verified(text.Trim());
        });

        var bios = Safe(() =>
        {
            using var k = WinReg.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var v = k?.GetValue("BIOSVersion") as string;
            var d = k?.GetValue("BIOSReleaseDate") as string;
            return string.IsNullOrWhiteSpace(v) ? Fact.Unavailable("Firmware did not expose a BIOS version.") : Fact.Verified(v.Trim(), d);
        });

        string? guid = null;
        var plan = Safe(() =>
        {
            guid = power.GetActiveGuid();
            var name = power.List().FirstOrDefault(p => p.Guid == guid)?.Name;
            return guid is null ? Fact.Unavailable("powercfg returned no active scheme.") : Fact.Verified(name ?? guid);
        });

        bool? laptop = null, onBattery = null;
        try
        {
            if (Native.GetSystemPowerStatus(out var ps))
            {
                var noBattery = (ps.BatteryFlag & 128) != 0;
                laptop = !noBattery;
                onBattery = !noBattery && ps.ACLineStatus == 0;
            }
        }
        catch { }

        return new SystemSnapshot
        {
            Cpu = cpu, LogicalProcessors = logical, PhysicalCores = cores,
            Ram = ram, RamTotalBytes = total, RamAvailableBytes = avail,
            Gpus = ReadGpus(), Os = os, WindowsBuild = host.WindowsBuild,
            Board = board, Bios = bios, Drives = ReadDrives(), Adapters = ReadAdapters(),
            PowerPlan = plan, PowerPlanGuid = guid, IsLaptop = laptop, OnBattery = onBattery,
            IsAdmin = host.IsAdmin, RestorePoint = ReadRestorePoint(),
            TopMemoryProcesses = ReadTopProcesses(),
        };
    }

    /// <summary>Cheap live check used by Game Mode (battery state can change between scans).</summary>
    public static (bool? Laptop, bool? OnBattery) ReadPowerState()
    {
        try
        {
            if (OperatingSystem.IsWindows() && Native.GetSystemPowerStatus(out var ps))
            {
                var noBattery = (ps.BatteryFlag & 128) != 0;
                return (!noBattery, !noBattery && ps.ACLineStatus == 0);
            }
        }
        catch { }
        return (null, null);
    }

    private static string Regex1(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
    private static double Gb(long b) => b / 1024.0 / 1024.0 / 1024.0;

    private static Fact Safe(Func<Fact> f)
    {
        try { return f(); }
        catch (UnauthorizedAccessException) { return Fact.Denied("Windows denied access to this information."); }
        catch (System.Security.SecurityException) { return Fact.Denied("Windows denied access to this information."); }
        catch (Exception ex) { return Fact.Unavailable(ex.Message); }
    }

    [SupportedOSPlatform("windows")]
    private static int CountPhysicalCores()
    {
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(0 /*RelationProcessorCore*/, IntPtr.Zero, ref len);
        if (len == 0) return 0;
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(0, buf, ref len)) return 0;
            var offset = 0; var count = 0;
            while (offset < len)
            {
                var rel = Marshal.ReadInt32(buf, offset);
                var size = Marshal.ReadInt32(buf, offset + 4);
                if (size <= 0) break;
                if (rel == 0) count++;
                offset += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = WinReg.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return list;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                var name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var drv = k!.GetValue("DriverVersion") as string;
                Fact vram = Fact.Unavailable("Not exposed by this driver.");
                var mem = k.GetValue("HardwareInformation.qwMemorySize");
                if (mem is long l && l > 0) vram = Fact.Verified($"{Gb(l):0.#} GB");
                else if (mem is byte[] bytes && bytes.Length >= 8 && BitConverter.ToInt64(bytes, 0) > 0)
                    vram = Fact.Verified($"{Gb(BitConverter.ToInt64(bytes, 0)):0.#} GB");
                list.Add(new GpuInfo(name.Trim(),
                    string.IsNullOrWhiteSpace(drv) ? Fact.Unavailable("Driver version not reported.") : Fact.Verified(drv),
                    vram));
            }
        }
        catch { }
        return list;
    }

    private static IReadOnlyList<DriveInfoFact> ReadDrives()
    {
        var list = new List<DriveInfoFact>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveInfoFact(d.Name, d.TotalSize, d.AvailableFreeSpace, d.DriveFormat,
                    Fact.Unavailable("SSD/HDD type needs a storage IOCTL query that is not implemented.")));
            }
            catch { }
        }
        return list;
    }

    private static IReadOnlyList<AdapterInfo> ReadAdapters()
    {
        var list = new List<AdapterInfo>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = n.GetIPProperties();
                var kind = n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    _ => n.NetworkInterfaceType.ToString()
                };
                var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString();
                list.Add(new AdapterInfo(n.Name, kind, n.OperationalStatus == OperationalStatus.Up, n.Speed,
                    props.DnsAddresses.Select(a => a.ToString()).ToList(), gw));
            }
        }
        catch { }
        return list;
    }

    [SupportedOSPlatform("windows")]
    private static Fact ReadRestorePoint()
    {
        try
        {
            using var k = WinReg.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            if (k is null) return Fact.Unavailable("System Restore configuration not found.");
            var disabled = k.GetValue("DisableSR");
            if (disabled is int i && i == 1) return Fact.Verified("Disabled by policy");
            return Fact.Estimated("Possibly available", "Windows only confirms when a restore point is actually created.");
        }
        catch { return Fact.Unavailable("Could not read System Restore settings."); }
    }

    private static IReadOnlyList<ProcessUsage> ReadTopProcesses()
    {
        var list = new List<ProcessUsage>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add(new ProcessUsage(p.ProcessName, p.Id, p.WorkingSet64)); }
            catch { }
            finally { p.Dispose(); }
        }
        return list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUsage(g.Key, g.First().Pid, g.Sum(x => x.WorkingSetBytes)))
            .OrderByDescending(p => p.WorkingSetBytes).Take(8).ToList();
    }
}

/// <summary>CPU load from GetSystemTimes deltas. Cheap enough to call once per second.</summary>
public sealed class CpuSampler
{
    private ulong _idle, _total;
    private bool _primed;

    public double? Sample()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!Native.GetSystemTimes(out var i, out var k, out var u)) return null;
        var idle = i.Value; var total = k.Value + u.Value; // kernel time includes idle
        double? result = null;
        if (_primed && total > _total)
            result = Math.Clamp(100.0 * (1.0 - (double)(idle - _idle) / (total - _total)), 0, 100);
        _idle = idle; _total = total; _primed = true;
        return result;
    }

    public static (long Total, long Available)? Memory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref m) ? ((long)m.ullTotalPhys, (long)m.ullAvailPhys) : null;
    }
}
