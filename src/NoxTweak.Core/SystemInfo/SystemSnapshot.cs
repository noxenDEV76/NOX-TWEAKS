using NoxTweak.Core.Model;

namespace NoxTweak.Core.SystemInfo;

public sealed record GpuInfo(string Name, Fact Driver, Fact Vram);
public sealed record DriveInfoFact(string Name, long TotalBytes, long FreeBytes, string Format, Fact MediaType)
{
    public double FreePercent => TotalBytes <= 0 ? 0 : 100.0 * FreeBytes / TotalBytes;
}
public sealed record AdapterInfo(string Name, string Kind, bool IsUp, long SpeedBps, IReadOnlyList<string> Dns, string? Gateway);
public sealed record ProcessUsage(string Name, int Pid, long WorkingSetBytes);

/// <summary>Everything a scan found. Every field says how trustworthy it is.</summary>
public sealed class SystemSnapshot
{
    public DateTime TakenAt { get; init; } = DateTime.Now;
    public Fact Cpu { get; init; } = Fact.Unavailable("not scanned");
    public Fact LogicalProcessors { get; init; } = Fact.Unavailable("not scanned");
    public Fact PhysicalCores { get; init; } = Fact.Unavailable("not scanned");
    public Fact Ram { get; init; } = Fact.Unavailable("not scanned");
    public long RamTotalBytes { get; init; }
    public long RamAvailableBytes { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public Fact Os { get; init; } = Fact.Unavailable("not scanned");
    public int WindowsBuild { get; init; }
    public Fact Board { get; init; } = Fact.Unavailable("not scanned");
    public Fact Bios { get; init; } = Fact.Unavailable("not scanned");
    public IReadOnlyList<DriveInfoFact> Drives { get; init; } = Array.Empty<DriveInfoFact>();
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = Array.Empty<AdapterInfo>();
    public Fact PowerPlan { get; init; } = Fact.Unavailable("not scanned");
    public string? PowerPlanGuid { get; init; }
    public bool? IsLaptop { get; init; }
    public bool? OnBattery { get; init; }
    public bool IsAdmin { get; init; }
    public Fact Temperatures { get; init; } = Fact.Unavailable("No reliable sensor API without a vendor driver; NOX TWEAK does not guess.");
    public Fact GpuUtilization { get; init; } = Fact.Unavailable("Per-GPU utilization needs vendor tooling and is not read.");
    public Fact RestorePoint { get; init; } = Fact.Unavailable("not scanned");
    public IReadOnlyList<ProcessUsage> TopMemoryProcesses { get; init; } = Array.Empty<ProcessUsage>();

    public double RamAvailablePercent => RamTotalBytes <= 0 ? 0 : 100.0 * RamAvailableBytes / RamTotalBytes;
    public bool HasDiscreteOrAnyGpu => Gpus.Any(g => !g.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase));
    public AdapterInfo? ActiveAdapter => Adapters.FirstOrDefault(a => a.IsUp && a.Gateway is not null);
}
