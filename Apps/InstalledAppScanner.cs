using Microsoft.Win32;
using System.Diagnostics;
using DevClean.Models;

namespace DevClean.Apps;

/// <summary>
/// Reads installed applications from the Windows registry and can launch their
/// native uninstaller. Never force-deletes — uses the app's own uninstall string,
/// which is Microsoft-Store-policy compliant.
/// </summary>
public static class InstalledAppScanner
{
    private static readonly string[] Keys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public static List<InstalledApplication> Scan()
    {
        var results = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);

        foreach (string keyPath in Keys)
        {
            ReadHive(Registry.LocalMachine, keyPath, results);
            ReadHive(Registry.CurrentUser, keyPath, results);
        }

        return results.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !a.Name.StartsWith("KB"))
            .OrderByDescending(a => a.EstimatedSize)
            .ThenBy(a => a.Name)
            .ToList();
    }

    private static void ReadHive(RegistryKey hive, string keyPath, Dictionary<string, InstalledApplication> results)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(keyPath);
            if (key is null) return;
            foreach (string subName in key.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? sub = key.OpenSubKey(subName);
                    if (sub is null) continue;

                    string? name = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (results.ContainsKey(name)) continue;

                    string? uninstall = sub.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(uninstall)) continue; // Windows Store apps — skip

                    long size = 0;
                    if (sub.GetValue("EstimatedSize") is int kb) size = kb * 1024L;

                    results[name] = new InstalledApplication
                    {
                        Name = name,
                        Publisher = sub.GetValue("Publisher") as string ?? string.Empty,
                        Version = sub.GetValue("DisplayVersion") as string ?? string.Empty,
                        InstallLocation = sub.GetValue("InstallLocation") as string ?? string.Empty,
                        UninstallString = uninstall,
                        EstimatedSize = size,
                        InstallDate = TryParseDate(sub.GetValue("InstallDate") as string)
                    };
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Launches the app's own uninstaller. Returns false if no uninstall string.</summary>
    public static bool Uninstall(InstalledApplication app)
    {
        if (string.IsNullOrWhiteSpace(app.UninstallString)) return false;
        try
        {
            string us = app.UninstallString.Trim('"');
            if (us.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("msiexec.exe", app.UninstallString) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(us) { UseShellExecute = true });
            }
            return true;
        }
        catch { return false; }
    }

    private static DateTime TryParseDate(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) && s.Length == 8 &&
            DateTime.TryParseExact(s, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime d))
        {
            return d;
        }
        return DateTime.MinValue;
    }
}
