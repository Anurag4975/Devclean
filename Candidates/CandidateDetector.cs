using System.IO;
using DevClean.Models;
using DevClean.Safety;

namespace DevClean.Candidates;

public class CandidateDetector
{
    private readonly LocationClassifier _locationClassifier;

    public CandidateDetector()
    {
        _locationClassifier = new LocationClassifier();
    }

    // ---------------------------------------------------------
    // ALL FILES
    //
    // Every scanned file becomes a result.
    // Safety/category/size are properties of the result,
    // not conditions for whether the file is displayed.
    // ---------------------------------------------------------

    public List<CleanupCandidate> FindCandidates(
        IEnumerable<FileItem> files)
    {
        var candidates = new List<CleanupCandidate>();

        foreach (FileItem file in files)
        {
            if (file.IsSystem)
            {
                continue;
            }

            string path = file.Path;

            if (IsProtectedPath(path))
            {
                continue;
            }

            string extension =
                file.Extension.ToLowerInvariant();

            LocationType location =
                _locationClassifier.Classify(file);

            var reasons = new List<string>();

            int score = 0;

            // -------------------------------------------------
            // Temporary
            // -------------------------------------------------

            if (extension is ".tmp" or ".temp")
            {
                reasons.Add("Temporary file");
                score += 80;
            }

            if (location == LocationType.Temp)
            {
                reasons.Add("Temporary directory");
                score += 70;
            }

            // -------------------------------------------------
            // Cache
            // -------------------------------------------------

            if (location == LocationType.Cache)
            {
                reasons.Add("Cache directory");
                score += 60;
            }

            // -------------------------------------------------
            // Development
            // -------------------------------------------------

            if (location == LocationType.DevelopmentProject)
            {
                reasons.Add("Development/project data");
                score += 45;
            }

            // -------------------------------------------------
            // Downloads
            // -------------------------------------------------

            if (location == LocationType.Downloads)
            {
                reasons.Add("Located in Downloads");
                score += 20;
            }

            // -------------------------------------------------
            // Installer
            // -------------------------------------------------

            if (extension is
                ".exe" or
                ".msi" or
                ".msix" or
                ".appx")
            {
                reasons.Add("Installer file");
                score += 25;
            }

            // -------------------------------------------------
            // Archive
            // -------------------------------------------------

            if (extension is
                ".zip" or
                ".rar" or
                ".7z" or
                ".tar" or
                ".gz")
            {
                reasons.Add("Archive file");
                score += 20;
            }

            // -------------------------------------------------
            // Backup
            // -------------------------------------------------

            if (extension is
                ".bak" or
                ".old" or
                ".backup")
            {
                reasons.Add("Backup file");
                score += 30;
            }

            if (location == LocationType.Backup)
            {
                reasons.Add("Backup directory");
                score += 30;
            }

            // -------------------------------------------------
            // Logs
            // -------------------------------------------------

            if (extension == ".log")
            {
                reasons.Add("Application log");
                score += 25;
            }

            // -------------------------------------------------
            // Size
            // -------------------------------------------------

            if (file.Size >= 10L * 1024 * 1024 * 1024)
            {
                reasons.Add("Extremely large file");
                score += 100;
            }
            else if (file.Size >= 5L * 1024 * 1024 * 1024)
            {
                reasons.Add("Very large file");
                score += 90;
            }
            else if (file.Size >= 2L * 1024 * 1024 * 1024)
            {
                reasons.Add("Large file");
                score += 75;
            }
            else if (file.Size >= 1L * 1024 * 1024 * 1024)
            {
                reasons.Add("Large file");
                score += 60;
            }
            else if (file.Size >= 500L * 1024 * 1024)
            {
                reasons.Add("Large file");
                score += 45;
            }
            else if (file.Size >= 100L * 1024 * 1024)
            {
                reasons.Add("Large file");
                score += 20;
            }

            // -------------------------------------------------
            // Personal media
            // -------------------------------------------------

            if (location == LocationType.UserMedia)
            {
                reasons.Add("Personal media");
                score += 10;
            }

            // -------------------------------------------------
            // Personal documents
            // -------------------------------------------------

            if (location == LocationType.UserDocuments)
            {
                reasons.Add("Personal document");
                score += 10;
            }

            // -------------------------------------------------
            // Application data
            // -------------------------------------------------

            if (location == LocationType.ApplicationData)
            {
                reasons.Add("Application data");
                score += 10;
            }

            reasons = reasons
                .Distinct()
                .ToList();

            FileCategory category =
                DeterminePrimaryCategory(
                    location,
                    extension,
                    file.Size,
                    reasons);

            candidates.Add(
                new CleanupCandidate
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

    // ---------------------------------------------------------
    // ALL FOLDERS
    // ---------------------------------------------------------

    public List<CleanupCandidate> FindFolderCandidates(
        IEnumerable<FileItem> files)
    {
        List<FileItem> fileList =
            files.ToList();

        if (fileList.Count == 0)
        {
            return [];
        }

        string driveRoot =
            Path.GetPathRoot(fileList[0].Path)
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return [];
        }

        List<string> directories = [];

        try
        {
            directories =
                Directory
                    .EnumerateDirectories(
                        driveRoot,
                        "*",
                        SearchOption.AllDirectories)
                    .Where(path =>
                        !IsDevCleanPath(path))
                    .ToList();
        }
        catch
        {
        }

        Dictionary<string, long> folderSizes =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (FileItem file in fileList)
        {
            string? directory =
                file.ParentDirectory;

            while (!string.IsNullOrWhiteSpace(directory))
            {
                string normalized =
                    NormalizePath(directory);

                if (!folderSizes.ContainsKey(normalized))
                {
                    folderSizes[normalized] = 0;
                }

                folderSizes[normalized] += file.Size;

                string? parent =
                    Directory.GetParent(normalized)?.FullName;

                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(
                        NormalizePath(parent),
                        normalized,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directory = parent;
            }
        }

        List<CleanupCandidate> candidates = [];

        foreach (string directory in directories)
        {
            if (IsProtectedPath(directory))
            {
                continue;
            }

            string path =
                NormalizePath(directory);

            long size =
                folderSizes.TryGetValue(
                    path,
                    out long folderSize)
                        ? folderSize
                        : 0;

            DirectoryInfo directoryInfo;

            try
            {
                directoryInfo =
                    new DirectoryInfo(path);
            }
            catch
            {
                continue;
            }

            FileItem folderFile =
                new()
                {
                    Path = path,
                    ParentDirectory =
                        directoryInfo.Parent?.FullName
                        ?? string.Empty,
                    Name = directoryInfo.Name,
                    Size = size,
                    Extension = string.Empty,
                    Created = GetDirectoryCreationTime(path),
                    LastModified = GetDirectoryLastModified(path),
                    LastAccessed = GetDirectoryLastAccessed(path),
                    IsHidden =
                        (directoryInfo.Attributes &
                         FileAttributes.Hidden) != 0,
                    IsSystem =
                        (directoryInfo.Attributes &
                         FileAttributes.System) != 0
                };

            candidates.Add(
                new CleanupCandidate
                {
                    File = folderFile,
                    TargetPath = path,
                    IsFolder = true,
                    Location = LocationType.Unknown,
                    Category = FileCategory.Unknown,
                    Reasons = [],
                    PriorityScore = 0
                });
        }

        return candidates
            .OrderByDescending(x => x.Size)
            .ThenBy(x => x.TargetPath)
            .ToList();
    }

    // ---------------------------------------------------------
    // DevClean's own folder must never appear as a target.
    // ---------------------------------------------------------

    private static bool IsDevCleanPath(
        string path)
    {
        string normalized =
            path.Replace('/', '\\')
                .TrimEnd('\\');

        return
            normalized.EndsWith(
                @"\.DevClean",
                StringComparison.OrdinalIgnoreCase)
            ||
            normalized.Contains(
                @"\.DevClean\",
                StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------
    // DIRECTORY DATES
    // ---------------------------------------------------------

    private static DateTime GetDirectoryCreationTime(
        string path)
    {
        try
        {
            return Directory.GetCreationTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime GetDirectoryLastModified(
        string path)
    {
        try
        {
            return Directory.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime GetDirectoryLastAccessed(
        string path)
    {
        try
        {
            return Directory.GetLastAccessTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    // ---------------------------------------------------------
    // PATH NORMALIZATION
    // ---------------------------------------------------------

    private static string NormalizePath(
        string path)
    {
        try
        {
            return Path
                .GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }

    // ---------------------------------------------------------
    // FILE CATEGORY
    // ---------------------------------------------------------

    private static FileCategory DeterminePrimaryCategory(
        LocationType location,
        string extension,
        long size,
        List<string> reasons)
    {
        if (size >= 500L * 1024 * 1024)
        {
            return FileCategory.LargeFile;
        }

        if (location == LocationType.Temp ||
            extension is ".tmp" or ".temp")
        {
            return FileCategory.Temporary;
        }

        if (location == LocationType.Cache)
        {
            return FileCategory.Cache;
        }

        if (location == LocationType.DevelopmentProject)
        {
            return FileCategory.DevelopmentArtifact;
        }

        if (extension is ".bak" or ".old" or ".backup")
        {
            return FileCategory.Backup;
        }

        if (extension is ".exe" or ".msi" or ".msix" or ".appx")
        {
            return FileCategory.Installer;
        }

        if (location == LocationType.Downloads)
        {
            return FileCategory.Download;
        }

        if (location == LocationType.UserMedia)
        {
            return FileCategory.PersonalMedia;
        }

        if (location == LocationType.UserDocuments)
        {
            return FileCategory.PersonalDocument;
        }

        if (location == LocationType.ApplicationData)
        {
            return FileCategory.ApplicationData;
        }

        return FileCategory.Unknown;
    }

    // ---------------------------------------------------------
    // PROTECTED PATHS
    // ---------------------------------------------------------

    private static bool IsProtectedPath(
        string path)
    {
        string normalized =
            path.Replace('/', '\\')
                .TrimEnd('\\');

        string lower =
            normalized.ToLowerInvariant();

        return
            lower.Contains(@"\windows") ||
            lower.Contains(@"\system32") ||
            lower.Contains(@"\syswow64") ||
            lower.Contains(@"\$recycle.bin") ||
            lower.Contains(@"\.devclean");
    }
}