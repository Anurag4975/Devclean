using System.IO;
using DevClean.Models;
using DevClean.Safety;
using DevClean.Smart;

namespace DevClean.Candidates;

/// <summary>
/// Detects cleanup candidates. Upgrades over the original:
///  - Paths the user previously restored are suppressed (learning loop).
///  - SafetyRuleDatabase ScoreBonus is applied to known modern-junk categories.
///  - Age-based scoring (older files rank higher).
///  - Folder candidates now get a priority score from matching rules.
///  - The folder-candidate directory walk now skips protected system trees
///    (Windows, WinSxS, etc.) and doesn't descend past known dev-cache folders
///    (node_modules, .git, venv, etc.) — this walk previously duplicated the
///    scanner's own traversal with none of its protections, which was a major
///    source of lag on large/dev-heavy drives.
/// All original public signatures are preserved.
/// </summary>
public class CandidateDetector
{
    private readonly LocationClassifier _locationClassifier;

    public CandidateDetector()
    {
        _locationClassifier = new LocationClassifier();
    }

    public List<CleanupCandidate> FindCandidates(IEnumerable<FileItem> files)
    {
        var candidates = new List<CleanupCandidate>();
        foreach (FileItem file in files)
        {
            if (file.IsSystem) continue;
            if (IsProtectedPath(file.Path)) continue;

            // LEARNING LOOP: user restored this before — don't suggest again.
            if (UserPreferenceStore.Instance.IsSuppressed(file.Path)) continue;

            string path = file.Path;
            string extension = file.Extension.ToLowerInvariant();
            LocationType location = _locationClassifier.Classify(file);

            var reasons = new List<string>();
            int score = 0;

            // Temporary
            if (extension is ".tmp" or ".temp") { reasons.Add("Temporary file"); score += 80; }
            if (location == LocationType.Temp) { reasons.Add("Temporary directory"); score += 70; }

            // Cache
            if (location == LocationType.Cache) { reasons.Add("Cache directory"); score += 60; }

            // Development
            if (location == LocationType.DevelopmentProject) { reasons.Add("Development/project data"); score += 45; }

            // Downloads
            if (location == LocationType.Downloads) { reasons.Add("Located in Downloads"); score += 20; }

            // Installer
            if (extension is ".exe" or ".msi" or ".msix" or ".appx") { reasons.Add("Installer file"); score += 25; }

            // Archive
            if (extension is ".zip" or ".rar" or ".7z" or ".tar" or ".gz") { reasons.Add("Archive file"); score += 20; }

            // Backup
            if (extension is ".bak" or ".old" or ".backup") { reasons.Add("Backup file"); score += 30; }
            if (location == LocationType.Backup) { reasons.Add("Backup directory"); score += 30; }

            // Logs
            if (extension == ".log") { reasons.Add("Application log"); score += 25; }

            // Size
            if (file.Size >= 10L * 1024 * 1024 * 1024) { reasons.Add("Extremely large file"); score += 100; }
            else if (file.Size >= 5L * 1024 * 1024 * 1024) { reasons.Add("Very large file"); score += 90; }
            else if (file.Size >= 2L * 1024 * 1024 * 1024) { reasons.Add("Large file"); score += 75; }
            else if (file.Size >= 1L * 1024 * 1024 * 1024) { reasons.Add("Large file"); score += 60; }
            else if (file.Size >= 500L * 1024 * 1024) { reasons.Add("Large file"); score += 45; }
            else if (file.Size >= 100L * 1024 * 1024) { reasons.Add("Large file"); score += 20; }

            // Age (new)
            double ageDays = (DateTime.Now - file.LastModified).TotalDays;
            if (ageDays > 730) { reasons.Add("Not modified in over 2 years"); score += 30; }
            else if (ageDays > 365) { reasons.Add("Not modified in over 1 year"); score += 15; }

            // Personal media / documents / app data (low score, review only)
            if (location == LocationType.UserMedia) { reasons.Add("Personal media"); score += 10; }
            if (location == LocationType.UserDocuments) { reasons.Add("Personal document"); score += 10; }
            if (location == LocationType.ApplicationData) { reasons.Add("Application data"); score += 10; }

            // RULE DATABASE bonus (new — modern junk categories)
            SafetyRule? rule = SafetyRuleDatabase.Match(file);
            if (rule is not null && rule.ScoreBonus > 0)
            {
                score += rule.ScoreBonus;
                if (!reasons.Contains(rule.Reason)) reasons.Add(rule.Reason);
            }

            reasons = reasons.Distinct().ToList();

            FileCategory category = DeterminePrimaryCategory(location, extension, file.Size, reasons);

            candidates.Add(new CleanupCandidate
            {
                File = file,
                TargetPath = file.Path,
                IsFolder = false,
                Location = location,
                Category = category,
                Reasons = reasons,
                PriorityScore = score
            });
        }

        return candidates
            .OrderByDescending(x => x.PriorityScore)
            .ThenByDescending(x => x.Size)
            .ToList();
    }

    public List<CleanupCandidate> FindFolderCandidates(IEnumerable<FileItem> files)
    {
        List<FileItem> fileList = files.ToList();
        if (fileList.Count == 0) return [];

        string driveRoot = Path.GetPathRoot(fileList[0].Path) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(driveRoot)) return [];

        List<string> directories = EnumerateDirectoriesSafe(driveRoot)
            .Where(path => !IsDevCleanPath(path))
            .Where(path => !IsProtectedSystemPath(path))
            .Where(path => !UserPreferenceStore.Instance.IsSuppressed(path)) // learning loop
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<string, long> folderSizes = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileItem file in fileList)
        {
            string? directory = file.ParentDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string normalized = NormalizePath(directory);
                if (IsDevCleanPath(normalized)) break;
                if (!folderSizes.ContainsKey(normalized)) folderSizes[normalized] = 0;
                folderSizes[normalized] += file.Size;
                string? parent;
                try { parent = Directory.GetParent(normalized)?.FullName; }
                catch { break; }
                if (string.IsNullOrWhiteSpace(parent)) break;
                string normalizedParent = NormalizePath(parent);
                if (string.Equals(normalizedParent, normalized, StringComparison.OrdinalIgnoreCase)) break;
                directory = normalizedParent;
            }
        }

        List<CleanupCandidate> candidates = [];
        foreach (string directory in directories)
        {
            string path = NormalizePath(directory);
            long size = folderSizes.TryGetValue(path, out long folderSize) ? folderSize : 0;

            DirectoryInfo directoryInfo;
            try { directoryInfo = new DirectoryInfo(path); }
            catch { continue; }

            FileAttributes attributes;
            try { attributes = directoryInfo.Attributes; }
            catch { attributes = FileAttributes.Normal; }

            FileItem folderFile = new()
            {
                Path = path,
                ParentDirectory = directoryInfo.Parent?.FullName ?? string.Empty,
                Name = directoryInfo.Name,
                Size = size,
                Extension = string.Empty,
                Created = GetDirectoryCreationTime(path),
                LastModified = GetDirectoryLastModified(path),
                LastAccessed = GetDirectoryLastAccessed(path),
                IsHidden = (attributes & FileAttributes.Hidden) != 0,
                IsSystem = (attributes & FileAttributes.System) != 0
            };

            // RULE DATABASE bonus for folders (new — e.g. node_modules, caches)
            int score = 0;
            var reasons = new List<string>();
            SafetyRule? rule = SafetyRuleDatabase.Match(folderFile);
            if (rule is not null)
            {
                score += rule.ScoreBonus;
                if (!string.IsNullOrWhiteSpace(rule.Reason)) reasons.Add(rule.Reason);
            }

            candidates.Add(new CleanupCandidate
            {
                File = folderFile,
                TargetPath = path,
                IsFolder = true,
                Location = LocationType.Unknown,
                Category = FileCategory.Unknown,
                Reasons = reasons,
                PriorityScore = score
            });
        }

        return candidates
            .OrderByDescending(x => x.Size)
            .ThenBy(x => x.TargetPath)
            .ToList();
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> subDirectories;
            try { subDirectories = Directory.EnumerateDirectories(current); }
            catch { continue; }
            foreach (string directory in subDirectories)
            {
                if (IsDevCleanPath(directory)) continue;
                if (IsProtectedSystemPath(directory)) continue;
                DirectoryInfo info;
                try
                {
                    info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                yield return directory;
                // Still yield the container folder itself (so it shows up as a candidate),
                // but don't push its insides onto the stack — no point walking thousands
                // of nested node_modules/.git subdirectories a second time.
                if (!IsContainerFolder(directory)) pending.Push(directory);
            }
        }
    }

    private static readonly string[] ProtectedSegments =
    [
        $"{Path.DirectorySeparatorChar}Windows{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}$Recycle.Bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}System Volume Information{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Recovery{Path.DirectorySeparatorChar}",
    ];

    private static bool IsProtectedSystemPath(string path)
    {
        foreach (string segment in ProtectedSegments)
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly HashSet<string> ContainerFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "venv", ".venv", "__pycache__", ".pytest_cache", ".mypy_cache",
        "dist", "build", "target", "obj", ".next", ".nuxt", ".gradle", ".cache", ".nuget", "packages"
    };

    private static bool IsContainerFolder(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return ContainerFolderNames.Contains(name);
    }

    private static bool IsDevCleanPath(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        return normalized.EndsWith(@"\.DevClean", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\.DevClean\", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime GetDirectoryCreationTime(string path)
    { try { return Directory.GetCreationTime(path); } catch { return DateTime.MinValue; } }

    private static DateTime GetDirectoryLastModified(string path)
    { try { return Directory.GetLastWriteTime(path); } catch { return DateTime.MinValue; } }

    private static DateTime GetDirectoryLastAccessed(string path)
    { try { return Directory.GetLastAccessTime(path); } catch { return DateTime.MinValue; } }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static FileCategory DeterminePrimaryCategory(
        LocationType location, string extension, long size, List<string> reasons)
    {
        if (size >= 500L * 1024 * 1024) return FileCategory.LargeFile;
        if (location == LocationType.Temp || extension is ".tmp" or ".temp") return FileCategory.Temporary;
        if (location == LocationType.Cache) return FileCategory.Cache;
        if (location == LocationType.DevelopmentProject) return FileCategory.DevelopmentArtifact;
        if (extension is ".bak" or ".old" or ".backup") return FileCategory.Backup;
        if (extension is ".exe" or ".msi" or ".msix" or ".appx") return FileCategory.Installer;
        if (location == LocationType.Downloads) return FileCategory.Download;
        if (location == LocationType.UserMedia) return FileCategory.PersonalMedia;
        if (location == LocationType.UserDocuments) return FileCategory.PersonalDocument;
        if (location == LocationType.ApplicationData) return FileCategory.ApplicationData;
        return FileCategory.Unknown;
    }

    private static bool IsProtectedPath(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        string lower = normalized.ToLowerInvariant();
        return lower.Contains(@"\windows") ||
               lower.Contains(@"\system32") ||
               lower.Contains(@"\syswow64") ||
               lower.Contains(@"\$recycle.bin") ||
               lower.Contains(@"\.devclean");
    }
}