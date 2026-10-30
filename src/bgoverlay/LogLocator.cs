using System.Diagnostics;
using Microsoft.Win32;

namespace BgOverlay;

/// <summary>
/// Finds the current Power.log. The client writes one folder per launch,
/// &lt;install&gt;\Logs\Hearthstone_YYYY_MM_DD_HH_MM_SS, and every game of that
/// launch goes into the same file.
/// </summary>
internal static class LogLocator
{
    public static string? InstallDir { get; private set; }

    public static string? LatestPowerLog()
    {
        string? dir = FindInstallDir();
        if (dir is null) return null;
        InstallDir = dir;
        if (Settings.Current.InstallDir != dir)
        {
            Settings.Current.InstallDir = dir;
            Settings.Current.Save();
        }

        string logs = Path.Combine(dir, "Logs");
        if (!Directory.Exists(logs)) return null;
        // The timestamped names sort chronologically as plain strings.
        foreach (string session in Directory.GetDirectories(logs, "Hearthstone_*").OrderByDescending(d => d, StringComparer.Ordinal))
        {
            string p = Path.Combine(session, "Power.log");
            if (File.Exists(p)) return p;
        }
        string flat = Path.Combine(logs, "Power.log");
        return File.Exists(flat) ? flat : null;
    }

    /// <summary>
    /// When the client started this log: the folder is named for it, and the
    /// lines themselves carry only a time of day.
    /// </summary>
    public static DateTime SessionStart(string logPath)
    {
        string name = Path.GetFileName(Path.GetDirectoryName(logPath) ?? "") ?? "";
        return DateTime.TryParseExact(name, "'Hearthstone_'yyyy_MM_dd_HH_mm_ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
            ? at
            : DateTime.Now;
    }

    private static string? FindInstallDir()
    {
        foreach (Process p in Process.GetProcessesByName("Hearthstone"))
        {
            using (p)
            {
                try
                {
                    string? exe = p.MainModule?.FileName;
                    if (exe is not null) return Path.GetDirectoryName(exe);
                }
                catch { /* elevated game: fall through to the registry */ }
            }
        }
        if (InstallDir is not null) return InstallDir;

        foreach (string key in new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone",
        })
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(key);
            if (k?.GetValue("InstallLocation") is string loc && Directory.Exists(loc)) return loc;
        }
        return Settings.Current.InstallDir is { } saved && Directory.Exists(saved) ? saved : null;
    }

    /// <summary>
    /// Turns on verbose Power logging in log.config. Returns true when the
    /// file had to change, which only takes effect after a game restart.
    /// </summary>
    public static bool EnsurePowerLogging()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blizzard", "Hearthstone", "log.config");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        (string Key, string Value)[] want = { ("LogLevel", "1"), ("FilePrinting", "True"), ("Verbose", "True") };
        bool changed = false;

        int start = lines.FindIndex(l => l.Trim().Equals("[Power]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            lines.Add("[Power]");
            foreach (var (k, v) in want) lines.Add($"{k}={v}");
            changed = true;
        }
        else
        {
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            foreach (var (k, v) in want)
            {
                int i = lines.FindIndex(start + 1, end - start - 1,
                    l => l.TrimStart().StartsWith(k + "=", StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                {
                    lines.Insert(end++, $"{k}={v}");
                    changed = true;
                }
                else if (!lines[i].Trim().Equals($"{k}={v}", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{k}={v}";
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
        }
        return changed;
    }
}
