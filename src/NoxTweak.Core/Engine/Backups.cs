using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoxTweak.Core.Model;
using NoxTweak.Core.Registry;

namespace NoxTweak.Core.Engine;

public sealed class BackupEntry
{
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>False when the value did not exist: restoring must delete it, not invent a default.</summary>
    public bool Existed { get; set; }
    public string? Kind { get; set; }
    public string? Data { get; set; }

    public RegHive HiveEnum => Enum.Parse<RegHive>(Hive);
    public RegValue? OriginalValue => Existed ? RegValue.Deserialize(Enum.Parse<RegKind>(Kind!), Data ?? "") : null;
}

public sealed class BackupRecord
{
    public string Id { get; set; } = "";
    public string OptimizationId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>Active (restorable) | Restored | Failed</summary>
    public string Status { get; set; } = "Active";
    public List<BackupEntry> Entries { get; set; } = new();
    public string? PreviousPowerPlan { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Origin { get; set; }

    public string ComputeHash()
    {
        var sb = new StringBuilder();
        sb.Append(Id).Append('|').Append(OptimizationId).Append('|').Append(CreatedUtc.ToString("O")).Append('|').Append(PreviousPowerPlan);
        foreach (var e in Entries)
            sb.Append('\n').Append(e.Hive).Append('|').Append(e.Path).Append('|').Append(e.Name).Append('|')
              .Append(e.Existed).Append('|').Append(e.Kind).Append('|').Append(e.Data);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>Application-level backups: one small JSON file per operation with an integrity hash.</summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;

    public BackupStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    public void Save(BackupRecord r)
    {
        r.Sha256 = r.ComputeHash();
        var tmp = Path.Combine(_dir, r.Id + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
        File.Move(tmp, Path.Combine(_dir, r.Id + ".json"), overwrite: true);
    }

    public void SetStatus(BackupRecord r, string status)
    {
        r.Status = status;
        Save(r); // hash excludes Status, so integrity stays valid
    }

    public BackupRecord? Load(string id)
    {
        var p = Path.Combine(_dir, id + ".json");
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(p)); }
        catch { return null; }
    }

    public bool VerifyIntegrity(BackupRecord r) =>
        !string.IsNullOrEmpty(r.Sha256) && string.Equals(r.Sha256, r.ComputeHash(), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<BackupRecord> List()
    {
        var list = new List<BackupRecord>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(f));
                if (r is not null) list.Add(r);
            }
            catch { /* unreadable file: ignored here, surfaced by VerifyAll */ }
        }
        return list.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    public BackupRecord? LatestActive(string optimizationId) =>
        List().FirstOrDefault(r => r.OptimizationId == optimizationId && r.Status == "Active");
}
