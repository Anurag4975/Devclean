using DevClean.Models;
using DevClean.Safety;
using DevClean.Smart;

namespace DevClean.AI;

/// <summary>
/// Local (on-device, no cloud) safety analyzer.
/// Upgrade: now backed by the external SafetyRuleDatabase (modern junk categories)
/// and the UserPreferenceStore learning loop (paths you restored are never suggested again).
/// Public signature is unchanged so existing UI code compiles without edits.
/// </summary>
public class LocalSafetyAnalyzer : IAiSafetyAnalyzer
{
    public Task<SafetyAnalysis> AnalyzeAsync(
        FileItem file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // -------------------------------------------------
        // 0. LEARNING LOOP
        //    If the user restored this path (or a parent)
        //    from quarantine before, never suggest it again.
        // -------------------------------------------------
        if (UserPreferenceStore.Instance.IsSuppressed(file.Path))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.DoNotDelete,
                Confidence = 100,
                Reason = "You chose to keep this before",
                Explanation = "You restored this item from quarantine previously. DevClean learned your preference and will not suggest removing it again.",
                Risk = "None — protected by your choice",
                Evidence = ["Restored by user in a previous session"]
            });
        }

        // -------------------------------------------------
        // 1. RULE DATABASE (modern junk + hard protections)
        //    Highest-confidence matching rule wins.
        // -------------------------------------------------
        SafetyRule? rule = SafetyRuleDatabase.Match(file);
        if (rule is not null)
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = rule.Level,
                Confidence = rule.Confidence,
                Reason = rule.Reason,
                Explanation = rule.Explanation,
                Risk = rule.Risk,
                Evidence = [$"Rule: {rule.Id}", $"Confidence: {rule.Confidence}%"]
            });
        }

        // -------------------------------------------------
        // 2. FALLBACK HEURISTICS (unchanged behaviour for
        //    files no rule covers)
        // -------------------------------------------------
        string path = file.Path.ToLowerInvariant();
        string extension = file.Extension.ToLowerInvariant();

        if (file.IsSystem)
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.DoNotDelete,
                Confidence = 99,
                Reason = "Windows system file",
                Explanation = "This file has the Windows System attribute and should not be deleted automatically.",
                Risk = "High",
                Evidence = ["Windows System attribute detected"]
            });
        }

        if (IsWindowsLocation(path))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.DoNotDelete,
                Confidence = 98,
                Reason = "Windows system location",
                Explanation = "This file is inside a Windows system directory. Removing it could affect Windows.",
                Risk = "Very High",
                Evidence = ["Windows system directory detected"]
            });
        }

        if (IsDevelopmentLocation(path))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Caution,
                Confidence = 90,
                Reason = "Development project data",
                Explanation = "This file belongs to a development project. Removing it may affect a project or require files to be regenerated.",
                Risk = "High",
                Evidence = ["Development-related directory detected"]
            });
        }

        if (IsApplicationData(path))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Caution,
                Confidence = 90,
                Reason = "Application data",
                Explanation = "This appears to be application data. Deleting it may affect settings, accounts, cached state, or stored information.",
                Risk = "Medium to High",
                Evidence = ["Application data directory detected"]
            });
        }

        if (extension is ".tmp" or ".temp" || path.Contains(@"\temp\") || path.Contains(@"\tmp\"))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Safe,
                Confidence = 90,
                Reason = "Temporary data",
                Explanation = "This file appears to be temporary data that applications can normally recreate.",
                Risk = "Low",
                Evidence = ["Temporary file or directory detected", "Likely recreatable data"]
            });
        }

        if (path.Contains(@"\cache\") || path.Contains(@"\caches\"))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Safe,
                Confidence = 85,
                Reason = "Cache data",
                Explanation = "This file appears to be cached data. Applications can often recreate cache files when needed.",
                Risk = "Low",
                Evidence = ["Cache directory detected", "Likely recreatable data"]
            });
        }

        if (IsPersonalMedia(extension))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Review,
                Confidence = 95,
                Reason = "Personal media",
                Explanation = "This appears to be a personal photo, video, or audio file. Review it before deletion.",
                Risk = "High",
                Evidence = [$"Media extension: {extension}", "Potentially user-created or personally important"]
            });
        }

        if (IsPersonalDocument(extension))
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Review,
                Confidence = 95,
                Reason = "Personal document",
                Explanation = "This appears to be a personal document. Deleting it may permanently remove important information.",
                Risk = "High",
                Evidence = [$"Document extension: {extension}", "Potentially user-created information"]
            });
        }

        DateTime now = DateTime.Now;
        TimeSpan age = now - file.LastModified;
        if (age.TotalDays > 365)
        {
            return Task.FromResult(new SafetyAnalysis
            {
                Level = SafetyLevel.Review,
                Confidence = 75,
                Reason = "Old file",
                Explanation = "This file has not been modified for more than one year. Age alone is not enough to delete it.",
                Risk = "Medium",
                Evidence = [$"Last modified: {file.LastModified:yyyy-MM-dd}", $"Age: {(int)age.TotalDays:N0} days"]
            });
        }

        return Task.FromResult(new SafetyAnalysis
        {
            Level = SafetyLevel.Review,
            Confidence = 50,
            Reason = "Insufficient information",
            Explanation = "DevClean does not have enough information to determine whether deleting this file is appropriate.",
            Risk = "Unknown",
            Evidence = ["No high-confidence safety rule matched", $"Last modified: {file.LastModified:yyyy-MM-dd}"]
        });
    }

    private bool IsWindowsLocation(string path)
    {
        return path.Contains(@"\windows\") ||
               path.Contains(@"\system32\") ||
               path.Contains(@"\syswow64\");
    }

    private bool IsDevelopmentLocation(string path)
    {
        return path.Contains(@"\.git\") ||
               path.Contains(@"\node_modules\") ||
               path.Contains(@"\.dart_tool\") ||
               path.Contains(@"\build\") ||
               path.Contains(@"\bin\") ||
               path.Contains(@"\obj\") ||
               path.Contains(@"\venv\") ||
               path.Contains(@"\__pycache__\");
    }

    private bool IsApplicationData(string path)
    {
        return path.Contains(@"\appdata\") ||
               path.Contains(@"\program files\") ||
               path.Contains(@"\program files (x86)\");
    }

    private bool IsPersonalMedia(string extension)
    {
        return extension is
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tiff" or ".raw" or ".heic" or
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".mts" or ".m4v" or ".3gp" or
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg";
    }

    private bool IsPersonalDocument(string extension)
    {
        return extension is
            ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".pdf" or
            ".txt" or ".csv" or ".rtf" or ".odt" or ".ods" or ".odp";
    }
}
