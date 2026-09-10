namespace DevClean.ModernJunk;

/// <summary>A modern-junk target that traditional cleaners miss.</summary>
public sealed class ModernJunkItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long EstimatedSizeBytes { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>Plain-English action the user should take (never auto-executed).</summary>
    public string RecommendedAction { get; set; } = string.Empty;
    /// <summary>True when DevClean can clean this safely itself; false when it needs the app's own tool.</summary>
    public bool SafeToCleanInApp { get; set; }
    public bool Detected { get; set; }
}

/// <summary>
/// Fast detection of modern, high-value junk that traditional cleaners ignore.
/// This is detection + advice only — nothing is deleted here. Items that need an
/// app-native cleanup (Docker, WSL) are flagged with instructions instead.
/// </summary>
public static class ModernJunkScanner
{
    public static List<ModernJunkItem> Scan()
    {
        var results = new List<ModernJunkItem>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Docker Desktop (WSL2 virtual disks — grow but never shrink)
        string docker = Path.Combine(localAppData, "Docker", "wsl");
        AddIfExists(results, new ModernJunkItem
        {
            Name = "Docker Desktop virtual disks",
            Path = docker,
            Description = "Docker stores Linux containers in virtual disks that grow automatically but never shrink. They can occupy 20-60 GB.",
            RecommendedAction = "Open Docker Desktop → Troubleshoot → 'Clean / Purge data', or run 'docker system prune -a' in a terminal.",
            SafeToCleanInApp = false
        });

        // WSL2 distributions
        string wsl = Path.Combine(userProfile, "AppData", "Local", "Packages");
        AddIfExists(results, new ModernJunkItem
        {
            Name = "WSL Linux distributions",
            Path = wsl,
            Description = "Windows Subsystem for Linux stores each distro as a virtual disk (ext4.vhdx). Old, unused distros waste a lot of space.",
            RecommendedAction = "Run 'wsl --list' in a terminal, then 'wsl --unregister <DistroName>' for distros you no longer use.",
            SafeToCleanInApp = false
        });

        // npm / pnpm / yarn caches
        AddIfExists(results, new ModernJunkItem
        {
            Name = "npm package cache",
            Path = Path.Combine(localAppData, "npm-cache"),
            Description = "Downloaded copies of Node.js packages. Safe to clear.",
            RecommendedAction = "DevClean can remove this directly, or run 'npm cache clean --force'.",
            SafeToCleanInApp = true
        });
        AddIfExists(results, new ModernJunkItem
        {
            Name = "NuGet package cache",
            Path = Path.Combine(userProfile, ".nuget", "packages"),
            Description = "Downloaded .NET packages. Safe to clear — they are restored on build.",
            RecommendedAction = "DevClean can remove this directly, or run 'dotnet nuget locals all --clear'.",
            SafeToCleanInApp = true
        });

        // iOS backups (iTunes)
        AddIfExists(results, new ModernJunkItem
        {
            Name = "iPhone / iPad backups",
            Path = Path.Combine(appData, "Apple Computer", "MobileSync", "Backup"),
            Description = "Local iOS device backups. Often 10-30 GB each.",
            RecommendedAction = "Review in iTunes/Finder. Remove only if you back up to iCloud or no longer need the backup.",
            SafeToCleanInApp = false
        });

        // Android SDK / emulators
        AddIfExists(results, new ModernJunkItem
        {
            Name = "Android SDK & emulators",
            Path = Path.Combine(localAppData, "Android", "Sdk"),
            Description = "Android SDK, system images and emulators can be 10-40 GB. Old system images are often forgotten.",
            RecommendedAction = "Open Android Studio → SDK Manager → remove old system images you don't test on.",
            SafeToCleanInApp = false
        });

        // Visual Studio caches
        AddIfExists(results, new ModernJunkItem
        {
            Name = "Visual Studio cache",
            Path = Path.Combine(localAppData, "Microsoft", "VisualStudio"),
            Description = "Cached components, MEF caches and old component catalogs from Visual Studio.",
            RecommendedAction = "DevClean can remove cache subfolders safely. Never delete the entire folder.",
            SafeToCleanInApp = true
        });

        // Teams / Slack / Discord caches
        AddIfExists(results, new ModernJunkItem
        {
            Name = "Teams cache",
            Path = Path.Combine(appData, "Microsoft", "Teams"),
            Description = "Teams caches media and files. Can grow to several GB.",
            RecommendedAction = "DevClean can remove the Cache subfolders safely.",
            SafeToCleanInApp = true
        });

        // Windows.old
        string windowsOld = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Windows.old");
        AddIfExists(results, new ModernJunkItem
        {
            Name = "Previous Windows installation (Windows.old)",
            Path = windowsOld,
            Description = "Backup of your previous Windows version, usually 10-30 GB. Removing it prevents rolling back.",
            RecommendedAction = "Use Disk Cleanup (cleanmgr) → 'Clean up system files' → Previous Windows installation(s).",
            SafeToCleanInApp = false
        });

        return results;
    }

    private static void AddIfExists(List<ModernJunkItem> results, ModernJunkItem item)
    {
        try
        {
            if (Directory.Exists(item.Path))
            {
                item.Detected = true;
                item.EstimatedSizeBytes = EstimateSize(item.Path);
                results.Add(item);
            }
        }
        catch
        {
            // Skip anything we cannot read.
        }
    }

    /// <summary>Quick size estimate — only sums top-level files and one level deep to stay fast.</summary>
    private static long EstimateSize(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            foreach (string dir in Directory.EnumerateDirectories(path))
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        try { total += new FileInfo(file).Length; } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return total;
    }
}
