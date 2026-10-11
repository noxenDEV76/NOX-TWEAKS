using NoxTweak.Core.Model;

namespace NoxTweak.Core.Registry;

public sealed record RegValue(RegKind Kind, object Data)
{
    public bool SameAs(RegValue? other)
    {
        if (other is null || other.Kind != Kind) return false;
        return Kind switch
        {
            RegKind.DWord => Convert.ToInt32(Data) == Convert.ToInt32(other.Data),
            RegKind.String => string.Equals((string)Data, (string)other.Data, StringComparison.Ordinal),
            RegKind.Binary => ((byte[])Data).AsSpan().SequenceEqual((byte[])other.Data),
            _ => false
        };
    }

    public string Serialize() => Kind switch
    {
        RegKind.DWord => Convert.ToInt32(Data).ToString(),
        RegKind.String => (string)Data,
        RegKind.Binary => Convert.ToBase64String((byte[])Data),
        _ => ""
    };

    public static RegValue Deserialize(RegKind kind, string data) => kind switch
    {
        RegKind.DWord => new RegValue(kind, int.Parse(data)),
        RegKind.String => new RegValue(kind, data),
        RegKind.Binary => new RegValue(kind, Convert.FromBase64String(data)),
        _ => throw new NotSupportedException()
    };

    public override string ToString() => Kind == RegKind.Binary ? $"binary({((byte[])Data).Length} bytes)" : Serialize();
}

/// <summary>Thrown when an existing value has a type NOX TWEAK refuses to touch.</summary>
public sealed class UnsupportedRegistryValueException(string message) : Exception(message);

public interface IRegistry
{
    /// <summary>Returns null when the key or the value does not exist.</summary>
    RegValue? Read(RegHive hive, string path, string name);
    void Write(RegHive hive, string path, string name, RegValue value);
    /// <summary>Deletes the value only (never the key). No error when it is already missing.</summary>
    void Delete(RegHive hive, string path, string name);
    IReadOnlyList<(string Name, RegValue Value)> Enumerate(RegHive hive, string path);
}
