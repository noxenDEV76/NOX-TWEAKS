using NoxTweak.Core.Model;

namespace NoxTweak.Core.Registry;

/// <summary>Registry double used by the self-tests and for dry runs on non-Windows hosts.</summary>
public sealed class InMemoryRegistry : IRegistry
{
    private readonly Dictionary<string, RegValue> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _silentlyIgnoredWrites = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegHive h, string p, string n) => $"{h}\\{p}\\{n}";

    public void Seed(RegHive h, string p, string n, RegValue v) => _values[K(h, p, n)] = v;
    public void FailWritesTo(RegHive h, string p, string n) => _failWrites.Add(K(h, p, n));
    /// <summary>Simulates a value that cannot be changed: write "succeeds" but read-back differs.</summary>
    public void IgnoreWritesTo(RegHive h, string p, string n) => _silentlyIgnoredWrites.Add(K(h, p, n));
    public int Count => _values.Count;

    public RegValue? Read(RegHive hive, string path, string name) =>
        _values.TryGetValue(K(hive, path, name), out var v) ? v : null;

    public void Write(RegHive hive, string path, string name, RegValue value)
    {
        var key = K(hive, path, name);
        if (_failWrites.Contains(key)) throw new UnauthorizedAccessException("Simulated write failure");
        if (_silentlyIgnoredWrites.Contains(key)) return;
        _values[key] = value;
    }

    public void Delete(RegHive hive, string path, string name) => _values.Remove(K(hive, path, name));

    public IReadOnlyList<(string Name, RegValue Value)> Enumerate(RegHive hive, string path)
    {
        var prefix = $"{hive}\\{path}\\";
        return _values.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                   && !kv.Key[prefix.Length..].Contains('\\'))
            .Select(kv => (kv.Key[prefix.Length..], kv.Value)).ToList();
    }
}
