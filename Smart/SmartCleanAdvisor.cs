using DevClean.Models;

namespace DevClean.Smart;

/// <summary>The result of a smart-clean plan.</summary>
public sealed class SmartCleanPlan
{
    /// <summary>Items that are safe to clean (SafetyLevel.Safe, high confidence), parent/child de-duplicated.</summary>
    public List<CleanupCandidate> SafeItems { get; init; } = [];
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    /// <summary>Plain-English summary for non-technical users.</summary>
    public string Summary { get; init; } = string.Empty;
    /// <summary>Number of items deliberately excluded because they need user review.</summary>
    public int ReviewItemsExcluded { get; init; }
}

/// <summary>
/// Builds a one-click "Smart Clean" plan: selects ONLY items rated Safe with high
/// confidence, de-duplicates parent/child folders, and produces a plain-English
/// summary. Never includes Review / Caution / DoNotDelete items.
/// </summary>
public static class SmartCleanAdvisor
{
    private const int MinConfidence = 80;

    public static SmartCleanPlan Plan(IEnumerable<(CleanupCandidate Candidate, SafetyAnalysis Analysis)> items)
    {
        var all = items.ToList();

        var safe = all
            .Where(x => x.Analysis.Level == SafetyLevel.Safe && x.Analysis.Confidence >= MinConfidence)
            .Select(x => x.Candidate)
            .ToList();

        int reviewExcluded = all.Count - safe.Count;

        // Parent/child de-duplication: shortest paths first; if a parent is selected,
        // drop all descendants (avoids double-counting).
        var selected = new List<CleanupCandidate>();
        foreach (var candidate in safe.OrderBy(x => Normalize(x.TargetPath).Length))
        {
            string path = Normalize(candidate.TargetPath);
            bool covered = selected.Any(s => IsSameOrDescendant(path, Normalize(s.TargetPath)));
            if (covered) continue;
            selected.Add(candidate);
        }

        long totalBytes = selected.Sum(x => x.Size);
        int folders = selected.Count(x => x.IsFolder);
        int files = selected.Count - folders;

        string summary = selected.Count == 0
            ? "No automatically-safe items were found. Review items manually, or scan a different drive."
            : $"You can safely free {FormatSize(totalBytes)} by cleaning {files:N0} file(s) and {folders:N0} folder(s). " +
              $"Nothing personal, no settings, no logins will be touched. {reviewExcluded:N0} item(s) need your review and were left out.";

        return new SmartCleanPlan
        {
            SafeItems = selected,
            TotalBytes = totalBytes,
            FileCount = files,
            FolderCount = folders,
            Summary = summary,
            ReviewItemsExcluded = reviewExcluded
        };
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static bool IsSameOrDescendant(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
        return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.0} {units[unit]}";
    }
}
