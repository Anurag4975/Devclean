using DevClean.Models;

namespace DevClean.Safety;

public class LocationClassifier
{
    public LocationType Classify(FileItem file)
    {
        string path = file.Path.ToLowerInvariant();

        // Windows system locations
        if (path.Contains(@"\windows\") ||
            path.Contains(@"\system32\") ||
            path.Contains(@"\syswow64\"))
        {
            return LocationType.WindowsSystem;
        }

        // Recycle Bin
        if (path.Contains(@"\$recycle.bin\"))
        {
            return LocationType.RecycleBin;
        }

        // Temporary locations
        if (path.Contains(@"\temp\") ||
            path.Contains(@"\tmp\") ||
            path.Contains(@"\temporary internet files\"))
        {
            return LocationType.Temp;
        }

        // Cache locations
        if (path.Contains(@"\cache\") ||
            path.Contains(@"\caches\") ||
            path.Contains(@"\cached\"))
        {
            return LocationType.Cache;
        }

        // Downloads
        if (path.Contains(@"\downloads\") ||
            path.Contains(@"\download\"))
        {
            return LocationType.Downloads;
        }

        // Program files
        if (path.Contains(@"\program files\") ||
            path.Contains(@"\program files (x86)\"))
        {
            return LocationType.ProgramFiles;
        }

        // Application data
        if (path.Contains(@"\appdata\"))
        {
            return LocationType.ApplicationData;
        }

        // Development projects
        if (path.Contains(@"\.git\") ||
            path.Contains(@"\node_modules\") ||
            path.Contains(@"\.dart_tool\") ||
            path.Contains(@"\build\") ||
            path.Contains(@"\bin\") ||
            path.Contains(@"\obj\") ||
            path.Contains(@"\venv\") ||
            path.Contains(@"\__pycache__\"))
        {
            return LocationType.DevelopmentProject;
        }

        // Backups
        if (path.Contains(@"\backup\") ||
            path.Contains(@"\backups\") ||
            path.Contains(@"\bak\"))
        {
            return LocationType.Backup;
        }

        // User media
        string extension = file.Extension.ToLowerInvariant();

        if (IsMedia(extension))
        {
            return LocationType.UserMedia;
        }

        // User documents
        if (IsDocument(extension))
        {
            return LocationType.UserDocuments;
        }

        return LocationType.Unknown;
    }

    private bool IsMedia(string extension)
    {
        return extension is
            ".jpg" or ".jpeg" or ".png" or ".gif" or
            ".webp" or ".bmp" or ".tiff" or ".raw" or
            ".heic" or
            ".mp4" or ".mkv" or ".avi" or ".mov" or
            ".wmv" or ".webm" or ".mts" or ".m4v" or
            ".3gp" or
            ".mp3" or ".wav" or ".flac" or
            ".aac" or ".m4a" or ".ogg";
    }

    private bool IsDocument(string extension)
    {
        return extension is
            ".doc" or ".docx" or
            ".xls" or ".xlsx" or
            ".ppt" or ".pptx" or
            ".pdf" or
            ".txt" or ".csv" or
            ".rtf" or
            ".odt" or ".ods" or ".odp";
    }
}