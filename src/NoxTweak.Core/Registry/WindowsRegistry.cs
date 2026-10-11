using System.Runtime.Versioning;
using Microsoft.Win32;
using NoxTweak.Core.Model;

namespace NoxTweak.Core.Registry;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    private static RegistryKey Base(RegHive h) => RegistryKey.OpenBaseKey(
        h == RegHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        RegistryView.Registry64);

    public RegValue? Read(RegHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, false);
        if (k is null) return null;
        if (!k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        return Convert(k, name);
    }

    private static RegValue Convert(RegistryKey k, string name)
    {
        var kind = k.GetValueKind(name);
        var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.DWord => new RegValue(RegKind.DWord, (int)data!),
            RegistryValueKind.String => new RegValue(RegKind.String, (string)data!),
            RegistryValueKind.Binary => new RegValue(RegKind.Binary, (byte[])data!),
            _ => throw new UnsupportedRegistryValueException(
                $"'{name}' has registry type {kind}, which NOX TWEAK does not modify.")
        };
    }

    public void Write(RegHive hive, string path, string name, RegValue value)
    {
        using var b = Base(hive);
        using var k = b.CreateSubKey(path, true) ?? throw new IOException("Cannot open or create registry key.");
        switch (value.Kind)
        {
            case RegKind.DWord: k.SetValue(name, (int)value.Data, RegistryValueKind.DWord); break;
            case RegKind.String: k.SetValue(name, (string)value.Data, RegistryValueKind.String); break;
            case RegKind.Binary: k.SetValue(name, (byte[])value.Data, RegistryValueKind.Binary); break;
        }
    }

    public void Delete(RegHive hive, string path, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, true);
        if (k is null) return;
        if (k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            k.DeleteValue(name, false);
    }

    public IReadOnlyList<(string Name, RegValue Value)> Enumerate(RegHive hive, string path)
    {
        var list = new List<(string, RegValue)>();
        using var b = Base(hive);
        using var k = b.OpenSubKey(path, false);
        if (k is null) return list;
        foreach (var n in k.GetValueNames())
        {
            try { list.Add((n, Convert(k, n))); }
            catch (UnsupportedRegistryValueException) { /* skip exotic types */ }
        }
        return list;
    }
}
