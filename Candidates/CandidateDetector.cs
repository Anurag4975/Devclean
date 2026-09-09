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
            //
            // Size is informational AND contributes to priority.
            // It never determines whether the file is displayed.
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

            // -------------------------------------------------
            // IMPORTANT
            //
            // There is intentionally NO:
            //
            // if (score < 10) continue;
            //
            // Every scanned file is returned.
            // -------------------------------------------------

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
    // FOLDER CANDIDATES
    // ---------------------------------------------------------

    public List<CleanupCandidate> FindFolderCandidates(
        IEnumerable<FileItem> files)
    {
        var candidates = new List<CleanupCandidate>();

        var folderPaths =
            files
                .Select(x => x.ParentDirectory)
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        foreach (string folderPath in folderPaths)
        {
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            if (IsProtectedPath(folderPath))
            {
                continue;
            }

            DirectoryInfo directory;

            try
            {
                directory =
                    new DirectoryInfo(folderPath);
            }
            catch
            {
                continue;
            }

            string folderName =
                directory.Name;

            if (!IsDisposableFolderName(folderName))
            {
                continue;
            }

            if (HasDisposableParent(directory))
            {
                continue;
            }

            long totalSize =
                CalculateFolderSize(directory);

            if (totalSize <= 0)
            {
                continue;
            }

            string lowerName =
                folderName.ToLowerInvariant();

            var reasons = new List<string>();

            int score = 0;

            if (IsDevelopmentFolder(lowerName))
            {
                score += 100;

                reasons.Add(
                    "Known regeneratable development folder");
            }

            if (IsCacheFolder(lowerName))
            {
                score += 90;

                reasons.Add("Cache folder");
            }

            if (IsTempFolder(lowerName))
            {
                score += 90;

                reasons.Add("Temporary folder");
            }

            if (lowerName.Contains("backup"))
            {
                score += 40;

                reasons.Add("Backup folder");
            }

            if (totalSize >= 10L * 1024 * 1024 * 1024)
            {
                score += 40;
                reasons.Add("Extremely large folder");
            }
            else if (totalSize >= 5L * 1024 * 1024 * 1024)
            {
                score += 35;
                reasons.Add("Very large folder");
            }
            else if (totalSize >= 1L * 1024 * 1024 * 1024)
            {
                score += 25;
                reasons.Add("Large folder");
            }
            else if (totalSize >= 500L * 1024 * 1024)
            {
                score += 15;
                reasons.Add("Large folder");
            }

            if (score < 30)
            {
                continue;
            }

            var folderFile =
                new FileItem
                {
                    Path = folderPath,

                    ParentDirectory =
                        directory.Parent?.FullName
                        ?? string.Empty,

                    Name = folderName,

                    Size = totalSize,

                    Extension = string.Empty,

                    Created =
                        directory.CreationTime,

                    LastModified =
                        directory.LastWriteTime,

                    LastAccessed =
                        directory.LastAccessTime,

                    IsHidden =
                        (directory.Attributes &
                         FileAttributes.Hidden) != 0,

                    IsSystem =
                        (directory.Attributes &
                         FileAttributes.System) != 0
                };

            LocationType location =
                DetermineFolderLocation(
                    lowerName);

            FileCategory category =
                DetermineFolderCategory(
                    lowerName);

            candidates.Add(
                new CleanupCandidate
                {
                    File = folderFile,
                    TargetPath = folderPath,
                    IsFolder = true,
                    Location = location,
                    Category = category,
                    Reasons = reasons
                        .Distinct()
                        .ToList(),
                    PriorityScore = score
                });
        }

        return candidates
            .OrderByDescending(
                x => x.PriorityScore)
            .ThenByDescending(
                x => x.Size)
            .ToList();
    }

    // ---------------------------------------------------------
    // FOLDER HELPERS
    // ---------------------------------------------------------

    private static bool IsDisposableFolderName(
        string folderName)
    {
        string name =
            folderName.ToLowerInvariant();

        return
            IsDevelopmentFolder(name) ||
            IsCacheFolder(name) ||
            IsTempFolder(name) ||
            name.Contains("backup");
    }

    private static bool IsDevelopmentFolder(
        string name)
    {
        return name is
            "node_modules" or
            ".dart_tool" or
            "build" or
            "bin" or
            "obj" or
            "__pycache__" or
            "venv" or
            ".venv";
    }

    private static bool IsCacheFolder(
        string name)
    {
        return
            name == "cache" ||
            name == "caches" ||
            name.Contains("cache");
    }

    private static bool IsTempFolder(
        string name)
    {
        return
            name == "temp" ||
            name == "tmp" ||
            name == "temporary";
    }

    private static bool HasDisposableParent(
        DirectoryInfo directory)
    {
        DirectoryInfo? parent =
            directory.Parent;

        while (parent != null)
        {
            if (IsDisposableFolderName(parent.Name))
            {
                return true;
            }

            parent = parent.Parent;
        }

        return false;
    }

    // ---------------------------------------------------------
    // FOLDER SIZE
    // ---------------------------------------------------------

    private static long CalculateFolderSize(
        DirectoryInfo directory)
    {
        long total = 0;

        try
        {
            if ((directory.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                return 0;
            }

            foreach (FileInfo file in
                     directory.EnumerateFiles())
            {
                try
                {
                    if ((file.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    total += file.Length;
                }
                catch
                {
                }
            }

            foreach (DirectoryInfo child in
                     directory.EnumerateDirectories())
            {
                try
                {
                    if ((child.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (string.Equals(
                        child.Name,
                        ".DevClean",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    total +=
                        CalculateFolderSize(child);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return total;
    }

    // ---------------------------------------------------------
    // LOCATION
    // ---------------------------------------------------------

    private static LocationType DetermineFolderLocation(
        string name)
    {
        if (IsDevelopmentFolder(name))
        {
            return LocationType.DevelopmentProject;
        }

        if (IsCacheFolder(name))
        {
            return LocationType.Cache;
        }

        if (IsTempFolder(name))
        {
            return LocationType.Temp;
        }

        if (name.Contains("backup"))
        {
            return LocationType.Backup;
        }

        return LocationType.Unknown;
    }

    // ---------------------------------------------------------
    // CATEGORY
    // ---------------------------------------------------------

    private static FileCategory DetermineFolderCategory(
        string name)
    {
        if (IsDevelopmentFolder(name))
        {
            return FileCategory.DevelopmentArtifact;
        }

        if (IsCacheFolder(name))
        {
            return FileCategory.Cache;
        }

        if (IsTempFolder(name))
        {
            return FileCategory.Temporary;
        }

        if (name.Contains("backup"))
        {
            return FileCategory.Backup;
        }

        return FileCategory.Unknown;
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
        if (size >=
            500L * 1024 * 1024)
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

        if (location ==
            LocationType.DevelopmentProject)
        {
            return FileCategory.DevelopmentArtifact;
        }

        if (extension is
            ".bak" or ".old" or ".backup")
        {
            return FileCategory.Backup;
        }

        if (extension is
            ".exe" or
            ".msi" or
            ".msix" or
            ".appx")
        {
            return FileCategory.Installer;
        }

        if (location ==
            LocationType.Downloads)
        {
            return FileCategory.Download;
        }

        if (location ==
            LocationType.UserMedia)
        {
            return FileCategory.PersonalMedia;
        }

        if (location ==
            LocationType.UserDocuments)
        {
            return FileCategory.PersonalDocument;
        }

        if (location ==
            LocationType.ApplicationData)
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