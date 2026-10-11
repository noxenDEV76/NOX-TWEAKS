using System.Diagnostics;
using NoxTweak.Core.SystemInfo;

namespace NoxTweak.Core.Games;

/// <summary>Starts games through their own launcher or executable. Never injects or modifies anything.</summary>
public static class GameLauncher
{
    /// <summary>Builds the exact launch request so it can be validated and unit-tested without starting anything.</summary>
    public static ProcessStartInfo BuildStartInfo(Game g)
    {
        switch (g.Launcher)
        {
            case GameLauncherKind.Steam:
                if (g.LaunchToken is null || !g.LaunchToken.All(char.IsDigit)) throw new InvalidOperationException("Invalid Steam app id.");
                return new ProcessStartInfo($"steam://rungameid/{g.LaunchToken}") { UseShellExecute = true };

            case GameLauncherKind.Epic:
                if (g.LaunchToken is not null)
                    return new ProcessStartInfo($"com.epicgames.launcher://apps/{g.LaunchToken}?action=launch&silent=true") { UseShellExecute = true };
                if (g.ExePath is not null && File.Exists(g.ExePath))
                    return Exe(g.ExePath);
                throw new InvalidOperationException("This Epic game has no valid launch information.");

            case GameLauncherKind.Riot:
                if (g.ExePath is null || !File.Exists(g.ExePath) || g.LaunchToken is null) throw new InvalidOperationException("Riot Client not found.");
                var psi = new ProcessStartInfo(g.ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(g.ExePath)! };
                psi.ArgumentList.Add($"--launch-product={g.LaunchToken}");
                psi.ArgumentList.Add("--launch-patchline=live");
                return psi;

            default:
                if (g.ExePath is null || !File.Exists(g.ExePath)) throw new FileNotFoundException("The game's executable no longer exists.", g.ExePath);
                return Exe(g.ExePath);
        }
    }

    private static ProcessStartInfo Exe(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only .exe files can be launched.");
        return new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
    }

    public static void Launch(Game g)
    {
        using var _ = Process.Start(BuildStartInfo(g));
    }

    /// <summary>Finds a running process that belongs to the game (executable inside its install folder).</summary>
    public static Process? FindRunningProcess(Game g)
    {
        if (g.InstallDir is null && g.ExePath is null) return null;
        var dir = g.InstallDir is null ? null : g.InstallDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = Native.GetProcessPath(p.Id);
                if (path is null) { p.Dispose(); continue; }
                var inside = dir is not null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
                var isRiotClient = g.Launcher == GameLauncherKind.Riot && g.ExePath is not null && path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase);
                // A launcher's own executable is not "the game running".
                if (inside && !isRiotClient) return p;
            }
            catch { }
            p.Dispose();
        }
        return null;
    }
}
