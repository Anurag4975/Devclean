using DevClean.Models;

namespace DevClean.Safety;

/// <summary>
/// Synchronous safety engine. Upgraded to use the external SafetyRuleDatabase.
/// Public signature (Analyze(FileItem) → SafetyResult) is unchanged.
/// </summary>
public class SafetyEngine
{
    public SafetyResult Analyze(FileItem file)
    {
        SafetyRule? rule = SafetyRuleDatabase.Match(file);
        if (rule is not null)
        {
            return new SafetyResult
            {
                Level = rule.Level,
                Confidence = rule.Confidence,
                Reason = rule.Reason,
                Explanation = rule.Explanation,
                Risk = rule.Risk,
                Evidence = [$"Rule: {rule.Id}"]
            };
        }

        string extension = file.Extension.ToLowerInvariant();

        if (extension is ".tmp" or ".temp")
        {
            return new SafetyResult
            {
                Level = SafetyLevel.Safe,
                Confidence = 90,
                Reason = "Temporary file",
                Explanation = "This file appears to be temporary data that can usually be recreated.",
                Risk = "Low",
                Evidence = ["Temporary extension"]
            };
        }

        string path = file.Path.ToLowerInvariant();
        if (path.Contains(@"\temp\") || path.Contains(@"\tmp\"))
        {
            return new SafetyResult
            {
                Level = SafetyLevel.Safe,
                Confidence = 88,
                Reason = "Temporary location",
                Explanation = "This file is in a temporary folder and can usually be removed safely.",
                Risk = "Low",
                Evidence = ["Temporary directory"]
            };
        }

        if (IsUserDocument(extension))
        {
            return new SafetyResult
            {
                Level = SafetyLevel.DoNotDelete,
                Confidence = 95,
                Reason = "User document",
                Explanation = "This appears to be a personal file. DevClean will not recommend deleting it automatically.",
                Risk = "High",
                Evidence = ["Document extension"]
            };
        }

        return new SafetyResult
        {
            Level = SafetyLevel.Review,
            Confidence = 50,
            Reason = "Unknown file",
            Explanation = "DevClean does not have enough information to determine whether this file is safe to delete.",
            Risk = "Unknown",
            Evidence = ["No rule matched"]
        };
    }

    private static bool IsUserDocument(string extension)
    {
        return extension is
            ".doc" or ".docx" or ".pdf" or ".xls" or ".xlsx" or
            ".ppt" or ".pptx" or ".txt" or ".csv" or ".rtf";
    }
}
