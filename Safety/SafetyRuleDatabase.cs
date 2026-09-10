using System.Text.Json;
using DevClean.Models;

namespace DevClean.Safety;

/// <summary>
/// A single safety rule. A file matches when its path contains ANY entry of
/// PathContains (case-insensitive) OR its extension matches ANY entry of Extensions.
/// When multiple rules match, the one with the highest Confidence wins.
/// </summary>
public sealed class SafetyRule
{
    public string Id { get; set; } = string.Empty;
    public List<string> PathContains { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
    public SafetyLevel Level { get; set; } = SafetyLevel.Review;
    public int Confidence { get; set; } = 70;
    public string Reason { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Risk { get; set; } = "Low";
    /// <summary>Bonus applied to a candidate's priority score when this rule matches.</summary>
    public int ScoreBonus { get; set; }
}

/// <summary>
/// External, updatable safety-rule database. Built-in defaults are always loaded;
/// an optional Rules/safety-rules.json file (copied next to the executable) can add
/// new rules or override built-in ones by Id.
/// Fail-safe: JSON errors are ignored and only built-in rules are used.
/// </summary>
public static class SafetyRuleDatabase
{
    private static readonly Lazy<IReadOnlyList<SafetyRule>> _rules = new(Load);
    public static IReadOnlyList<SafetyRule> Rules => _rules.Value;

    /// <summary>Returns the highest-confidence matching rule, or null when none match.</summary>
    public static SafetyRule? Match(FileItem file)
    {
        string path = file.Path.Replace('/', '\\').ToLowerInvariant();
        string ext = file.Extension.ToLowerInvariant();
        SafetyRule? best = null;
        foreach (SafetyRule rule in Rules)
        {
            bool pathMatch = rule.PathContains.Any(p =>
                !string.IsNullOrWhiteSpace(p) && path.Contains(p.ToLowerInvariant()));
            bool extMatch = rule.Extensions.Any(e =>
                !string.IsNullOrWhiteSpace(e) && string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
            if (!pathMatch && !extMatch) continue;
            if (best is null || rule.Confidence > best.Confidence) best = rule;
        }
        return best;
    }

    private static IReadOnlyList<SafetyRule> Load()
    {
        List<SafetyRule> rules = BuiltInRules();
        try
        {
            string jsonPath = Path.Combine(AppContext.BaseDirectory, "Rules", "safety-rules.json");
            if (File.Exists(jsonPath))
            {
                var extra = JsonSerializer.Deserialize<List<SafetyRule>>(File.ReadAllText(jsonPath));
                if (extra is not null)
                {
                    var byId = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (SafetyRule r in extra)
                    {
                        if (string.IsNullOrWhiteSpace(r.Id)) continue;
                        byId[r.Id] = r; // override or append
                    }
                    rules = byId.Values.ToList();
                }
            }
        }
        catch
        {
            // Use built-in rules only.
        }
        return rules;
    }

    private static List<SafetyRule> BuiltInRules() => new()
    {
        // ============================================================
        // DO NOT DELETE — highest protection, highest confidence
        // ============================================================
        new()
        {
            Id = "source-control",
            PathContains = new() { @"\.git\", @"\.svn\", @"\.hg\", @"\.git" },
            Level = SafetyLevel.DoNotDelete, Confidence = 99,
            Reason = "Source-control repository",
            Explanation = "This folder holds your project's full version history. Deleting it cannot be undone. DevClean will never suggest removing it.",
            Risk = "Critical"
        },
        new()
        {
            Id = "source-code",
            Extensions = new() { ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".kts",
                ".swift", ".go", ".rs", ".rb", ".php", ".cpp", ".cc", ".c", ".h", ".hpp", ".cshtml",
                ".razor", ".xaml", ".sh", ".ps1", ".bat", ".cmd", ".sql", ".scala", ".clj", ".ex",
                ".exs", ".erl", ".hs", ".lua", ".pl", ".r", ".m", ".mm", ".groovy", ".gradle",
                ".csproj", ".sln", ".vcxproj", ".pyproj", ".editorconfig", ".gitignore",
                ".dockerfile", ".dockerignore", ".makefile", ".cmake" },
            Level = SafetyLevel.DoNotDelete, Confidence = 94,
            Reason = "Source code or project config",
            Explanation = "This looks like source code or a project configuration file. DevClean never recommends deleting source files.",
            Risk = "High"
        },
        new()
        {
            Id = "windows-managed",
            PathContains = new() { @"\windows\", @"\system32\", @"\syswow64\", @"\winsxs",
                @"\program files\", @"\program files (x86)\", @"\programdata\microsoft" },
            Level = SafetyLevel.DoNotDelete, Confidence = 97,
            Reason = "Windows or program files",
            Explanation = "These files belong to Windows or installed programs. Remove software through Settings → Apps, not by deleting files.",
            Risk = "Very High"
        },
        new()
        {
            Id = "windows-paging",
            PathContains = new() { "hiberfil.sys", "pagefile.sys", "swapfile.sys" },
            Level = SafetyLevel.DoNotDelete, Confidence = 99,
            Reason = "Windows-managed system file",
            Explanation = "Hibernation and paging files are managed by Windows. Do not delete them manually.",
            Risk = "Critical"
        },

        // ============================================================
        // SAFE TO CLEAN — recreatable junk, high confidence
        // ============================================================
        new()
        {
            Id = "node-modules",
            PathContains = new() { @"\node_modules" },
            Level = SafetyLevel.Safe, Confidence = 95, ScoreBonus = 80,
            Reason = "Node.js packages folder",
            Explanation = "Recreated by running 'npm install' (or pnpm/yarn install). Safe to remove if you don't need offline access.",
            Risk = "Low"
        },
        new()
        {
            Id = "temp-folders",
            PathContains = new() { @"\temp\", @"\tmp\", @"\appdata\local\temp", @"\temporary internet files" },
            Level = SafetyLevel.Safe, Confidence = 92, ScoreBonus = 75,
            Reason = "Temporary files",
            Explanation = "Programs created these files while running and forgot to clean up. Removing them is always safe.",
            Risk = "Low"
        },
        new()
        {
            Id = "browser-cache",
            PathContains = new() { @"\google\chrome\user data\default\cache", @"\google\chrome\user data\default\code cache",
                @"\microsoft\edge\user data\default\cache", @"\microsoft\edge\user data\default\code cache",
                @"\mozilla\firefox\profiles", @"\opera software\opera stable\cache" },
            Level = SafetyLevel.Safe, Confidence = 90, ScoreBonus = 70,
            Reason = "Browser cache",
            Explanation = "Web pages stored locally to load faster. Clearing it will NOT log you out of websites — only cache folders are targeted.",
            Risk = "Low"
        },
        new()
        {
            Id = "chat-app-cache",
            PathContains = new() { @"\teams\cache", @"\slack\cache", @"\discord\cache",
                @"\microsoft\teams\cache", @"\slack\service worker\cachestor" },
            Level = SafetyLevel.Safe, Confidence = 88, ScoreBonus = 65,
            Reason = "Chat app cache",
            Explanation = "Cached media and files from Teams, Slack or Discord. The app will rebuild what it needs.",
            Risk = "Low"
        },
        new()
        {
            Id = "package-caches",
            PathContains = new() { @"\npm-cache", @"\pnpm-store", @"\yarn\cache", @"\.nuget\packages",
                @"\.gradle\caches", @"\.m2\repository", @"\cargo\registry" },
            Level = SafetyLevel.Safe, Confidence = 88, ScoreBonus = 60,
            Reason = "Package manager cache",
            Explanation = "Downloaded copies of development packages. They will be re-downloaded when needed.",
            Risk = "Low"
        },
        new()
        {
            Id = "windows-update-cache",
            PathContains = new() { @"\softwaredistribution\download" },
            Level = SafetyLevel.Safe, Confidence = 90, ScoreBonus = 70,
            Reason = "Windows Update download cache",
            Explanation = "Already-installed Windows Update files. Safe to remove; Windows will re-download only what is needed.",
            Risk = "Low"
        },
        new()
        {
            Id = "recycle-bin",
            PathContains = new() { @"\$recycle.bin\" },
            Level = SafetyLevel.Safe, Confidence = 95, ScoreBonus = 80,
            Reason = "Recycle Bin",
            Explanation = "Files you already deleted. Emptying the Recycle Bin is always safe.",
            Risk = "Low"
        },
        new()
        {
            Id = "generic-cache",
            PathContains = new() { @"\cache\", @"\caches\", @"\cached\" },
            Level = SafetyLevel.Safe, Confidence = 82, ScoreBonus = 55,
            Reason = "Cached data",
            Explanation = "Temporary cached data. Applications will rebuild it when needed.",
            Risk = "Low"
        },

        // ============================================================
        // REVIEW — user must decide
        // ============================================================
        new()
        {
            Id = "windows-old",
            PathContains = new() { @"\windows.old" },
            Level = SafetyLevel.Review, Confidence = 85, ScoreBonus = 90,
            Reason = "Previous Windows installation",
            Explanation = "Backup of your previous Windows version. Removing it frees a lot of space but means you cannot roll back.",
            Risk = "Medium"
        },
        new()
        {
            Id = "virtual-disks",
            Extensions = new() { ".vhd", ".vhdx", ".vmdk", ".iso" },
            Level = SafetyLevel.Review, Confidence = 75, ScoreBonus = 60,
            Reason = "Virtual machine / disk image",
            Explanation = "These can be very large (Docker, WSL, VMs). Compact them from the app that created them — do not delete manually unless you are sure.",
            Risk = "Medium"
        },
        new()
        {
            Id = "ios-backups",
            PathContains = new() { @"\mobilesync\backup" },
            Level = SafetyLevel.Review, Confidence = 80, ScoreBonus = 50,
            Reason = "iPhone / iPad backup",
            Explanation = "Backups of an iOS device. They can be 10-30 GB. Remove only if you back up to iCloud or no longer need them.",
            Risk = "Medium"
        },
        new()
        {
            Id = "installers",
            Extensions = new() { ".msi", ".msix", ".appx", ".exe" },
            PathContains = new() { @"\downloads\", @"\temp\" },
            Level = SafetyLevel.Review, Confidence = 72, ScoreBonus = 30,
            Reason = "Installer file",
            Explanation = "A program installer. It can usually be re-downloaded, but check you don't need it for repair/uninstall.",
            Risk = "Low"
        },
        new()
        {
            Id = "archives",
            Extensions = new() { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz" },
            Level = SafetyLevel.Review, Confidence = 70, ScoreBonus = 25,
            Reason = "Archive file",
            Explanation = "A compressed archive. Make sure you have already extracted anything you need from it.",
            Risk = "Low"
        },
        new()
        {
            Id = "logs",
            Extensions = new() { ".log", ".old", ".bak", ".backup" },
            Level = SafetyLevel.Safe, Confidence = 80, ScoreBonus = 40,
            Reason = "Log or backup file",
            Explanation = "Old log or backup file. Safe to remove if you don't need it for troubleshooting.",
            Risk = "Low"
        },
    };
}
