using System;
using System.IO;
using DevClean.Models;

namespace DevClean.Safety;

public class SafetyEngine
{
    public SafetyResult Analyze(FileItem file)
    {
        string path = file.Path;

        // Temporary Windows files.
        if (IsTemporaryFile(file))
        {
            return new SafetyResult
            {
                Level = SafetyLevel.Safe,
                Reason = "Temporary file",
                Explanation = "This file appears to be temporary data that can usually be recreated."
            };
        }

        // User documents and personal files should be protected.
        if (IsUserDocument(file))
        {
            return new SafetyResult
            {
                Level = SafetyLevel.DoNotDelete,
                Reason = "User document",
                Explanation = "This appears to be a personal file. DevClean will not recommend deleting it automatically."
            };
        }

        // Unknown files require user review.
        return new SafetyResult
        {
            Level = SafetyLevel.Review,
            Reason = "Unknown file",
            Explanation = "DevClean does not have enough information to determine whether this file is safe to delete."
        };
    }

    private bool IsTemporaryFile(FileItem file)
    {
        string extension = file.Extension.ToLowerInvariant();

        if (extension is ".tmp" or ".temp")
        {
            return true;
        }

        string path = file.Path.ToLowerInvariant();

        return path.Contains("\\temp\\") ||
               path.Contains("\\tmp\\");
    }

    private bool IsUserDocument(FileItem file)
    {
        string extension = file.Extension.ToLowerInvariant();

        return extension is
            ".doc" or
            ".docx" or
            ".pdf" or
            ".xls" or
            ".xlsx" or
            ".ppt" or
            ".pptx" or
            ".txt";
    }
}