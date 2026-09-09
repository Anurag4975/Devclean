using System.IO;
using System.Text.Json;
using DevClean.Models;

namespace DevClean.Cleanup;

public class QuarantineService
{
    private readonly string _quarantineRoot;
    private readonly string _metadataFile;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public QuarantineService(string driveRoot)
    {
        _quarantineRoot =
            Path.Combine(
                driveRoot,
                ".DevClean",
                "Quarantine");

        _metadataFile =
            Path.Combine(
                _quarantineRoot,
                "quarantine.json");

        Directory.CreateDirectory(
            _quarantineRoot);
    }

    public async Task<CleanupResult> QuarantineAsync(
        CleanupCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (candidate.IsFolder)
        {
            return await QuarantineFolderAsync(
                candidate,
                cancellationToken);
        }

        return await QuarantineFileAsync(
            candidate,
            cancellationToken);
    }

    private async Task<CleanupResult> QuarantineFileAsync(
        CleanupCandidate candidate,
        CancellationToken cancellationToken)
    {
        string originalPath =
            candidate.TargetPath;

        if (!File.Exists(originalPath))
        {
            return Failed(
                "File no longer exists.");
        }

        if (candidate.File.IsSystem)
        {
            return Failed(
                "System files cannot be quarantined.");
        }

        string id =
            Guid.NewGuid().ToString("N");

        string quarantineDirectory =
            Path.Combine(
                _quarantineRoot,
                id);

        Directory.CreateDirectory(
            quarantineDirectory);

        string fileName =
            Path.GetFileName(originalPath);

        string quarantinePath =
            Path.Combine(
                quarantineDirectory,
                fileName);

        try
        {
            File.Move(
                originalPath,
                quarantinePath);

            await AddMetadataAsync(
                CreateMetadata(
                    candidate,
                    id,
                    originalPath,
                    quarantinePath),
                cancellationToken);

            return Success(
                "File moved to quarantine.",
                id,
                candidate.Size);
        }
        catch (IOException)
        {
            try
            {
                await CopyAndVerifyAsync(
                    originalPath,
                    quarantinePath,
                    candidate.Size,
                    cancellationToken);

                File.Delete(
                    originalPath);

                await AddMetadataAsync(
                    CreateMetadata(
                        candidate,
                        id,
                        originalPath,
                        quarantinePath),
                    cancellationToken);

                return Success(
                    "File copied and verified before quarantine.",
                    id,
                    candidate.Size);
            }
            catch (Exception ex)
            {
                TryDeleteFile(
                    quarantinePath);

                return Failed(
                    $"Unable to quarantine file: {ex.Message}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(
                "Access denied. The file may be in use or require administrator permission.");
        }
    }

    private async Task<CleanupResult> QuarantineFolderAsync(
        CleanupCandidate candidate,
        CancellationToken cancellationToken)
    {
        string originalPath =
            candidate.TargetPath;

        if (!Directory.Exists(originalPath))
        {
            return Failed(
                "Folder no longer exists.");
        }

        if (IsProtectedFolder(originalPath))
        {
            return Failed(
                "Protected folders cannot be quarantined.");
        }

        string id =
            Guid.NewGuid().ToString("N");

        string quarantineDirectory =
            Path.Combine(
                _quarantineRoot,
                id);

        Directory.CreateDirectory(
            quarantineDirectory);

        string folderName =
            Path.GetFileName(
                originalPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));

        string quarantinePath =
            Path.Combine(
                quarantineDirectory,
                folderName);

        try
        {
            Directory.Move(
                originalPath,
                quarantinePath);

            await AddMetadataAsync(
                CreateMetadata(
                    candidate,
                    id,
                    originalPath,
                    quarantinePath),
                cancellationToken);

            return Success(
                "Folder moved to quarantine.",
                id,
                candidate.Size);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(
                "Access denied. The folder may contain files in use.");
        }
        catch (IOException ex)
        {
            return Failed(
                $"Unable to quarantine folder: {ex.Message}");
        }
    }

    public async Task<List<QuarantineItem>> GetItemsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_metadataFile))
        {
            return [];
        }

        try
        {
            string json =
                await File.ReadAllTextAsync(
                    _metadataFile,
                    cancellationToken);

            return JsonSerializer.Deserialize<
                       List<QuarantineItem>>(
                       json,
                       _jsonOptions)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<CleanupResult> RestoreAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<QuarantineItem> items =
            await GetItemsAsync(
                cancellationToken);

        QuarantineItem? item =
            items.FirstOrDefault(
                x => string.Equals(
                    x.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase));

        if (item == null)
        {
            return Failed(
                "Quarantine item was not found.");
        }

        bool exists =
            item.IsFolder
                ? Directory.Exists(item.QuarantinePath)
                : File.Exists(item.QuarantinePath);

        if (!exists)
        {
            return Failed(
                "Quarantined item no longer exists.");
        }

        if (item.IsFolder)
        {
            return await RestoreFolderAsync(
                item,
                items,
                cancellationToken);
        }

        return await RestoreFileAsync(
            item,
            items,
            cancellationToken);
    }

    private async Task<CleanupResult> RestoreFileAsync(
        QuarantineItem item,
        List<QuarantineItem> items,
        CancellationToken cancellationToken)
    {
        if (File.Exists(item.OriginalPath))
        {
            return Failed(
                "A file already exists at the original location. Restore was cancelled to prevent overwriting it.");
        }

        try
        {
            string? directory =
                Path.GetDirectoryName(
                    item.OriginalPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failed(
                    "Original directory could not be determined.");
            }

            Directory.CreateDirectory(
                directory);

            File.Move(
                item.QuarantinePath,
                item.OriginalPath);

            items.Remove(item);

            await SaveMetadataAsync(
                items,
                cancellationToken);

            TryRemoveEmptyQuarantineDirectory(
                item.QuarantinePath);

            return Success(
                "File restored successfully.",
                item.Id,
                item.Size);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(
                "Access denied while restoring the file.");
        }
        catch (IOException ex)
        {
            return Failed(
                $"Unable to restore file: {ex.Message}");
        }
    }

    private async Task<CleanupResult> RestoreFolderAsync(
        QuarantineItem item,
        List<QuarantineItem> items,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(item.OriginalPath))
        {
            return Failed(
                "A folder already exists at the original location. Restore was cancelled.");
        }

        try
        {
            string? parent =
                Path.GetDirectoryName(
                    item.OriginalPath);

            if (string.IsNullOrWhiteSpace(parent))
            {
                return Failed(
                    "Original folder location could not be determined.");
            }

            Directory.CreateDirectory(parent);

            Directory.Move(
                item.QuarantinePath,
                item.OriginalPath);

            items.Remove(item);

            await SaveMetadataAsync(
                items,
                cancellationToken);

            TryRemoveEmptyQuarantineDirectory(
                item.QuarantinePath);

            return Success(
                "Folder restored successfully.",
                item.Id,
                item.Size);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(
                "Access denied while restoring the folder.");
        }
        catch (IOException ex)
        {
            return Failed(
                $"Unable to restore folder: {ex.Message}");
        }
    }

    public async Task<CleanupResult> PermanentlyDeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<QuarantineItem> items =
            await GetItemsAsync(
                cancellationToken);

        QuarantineItem? item =
            items.FirstOrDefault(
                x => string.Equals(
                    x.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase));

        if (item == null)
        {
            return Failed(
                "Quarantine item was not found.");
        }

        try
        {
            if (item.IsFolder)
            {
                if (Directory.Exists(
                    item.QuarantinePath))
                {
                    Directory.Delete(
                        item.QuarantinePath,
                        true);
                }
            }
            else
            {
                if (File.Exists(
                    item.QuarantinePath))
                {
                    File.Delete(
                        item.QuarantinePath);
                }
            }

            items.Remove(item);

            await SaveMetadataAsync(
                items,
                cancellationToken);

            TryRemoveEmptyQuarantineDirectory(
                item.QuarantinePath);

            return Success(
                item.IsFolder
                    ? "Folder permanently deleted."
                    : "File permanently deleted.",
                item.Id,
                item.Size);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(
                "Access denied while permanently deleting the item.");
        }
        catch (IOException ex)
        {
            return Failed(
                $"Unable to permanently delete the item: {ex.Message}");
        }
    }

    private async Task AddMetadataAsync(
        QuarantineItem item,
        CancellationToken cancellationToken)
    {
        List<QuarantineItem> items =
            await GetItemsAsync(
                cancellationToken);

        items.Add(item);

        await SaveMetadataAsync(
            items,
            cancellationToken);
    }

    private async Task SaveMetadataAsync(
        List<QuarantineItem> items,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(
            _quarantineRoot);

        string json =
            JsonSerializer.Serialize(
                items,
                _jsonOptions);

        await File.WriteAllTextAsync(
            _metadataFile,
            json,
            cancellationToken);
    }

    private static QuarantineItem CreateMetadata(
        CleanupCandidate candidate,
        string id,
        string originalPath,
        string quarantinePath)
    {
        return new QuarantineItem
        {
            Id = id,
            OriginalPath = originalPath,
            QuarantinePath = quarantinePath,
            FileName = candidate.File.Name,
            Size = candidate.Size,
            QuarantinedAt = DateTime.Now,
            Category = candidate.Category,
            SafetyLevel = SafetyLevel.Review,
            Reason = string.Join(
                "; ",
                candidate.Reasons),
            IsFolder = candidate.IsFolder
        };
    }

    private static async Task CopyAndVerifyAsync(
        string source,
        string destination,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await using FileStream sourceStream =
            new(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);

        await using FileStream destinationStream =
            new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.SequentialScan);

        await sourceStream.CopyToAsync(
            destinationStream,
            cancellationToken);

        FileInfo copiedFile =
            new(destination);

        if (copiedFile.Length != expectedSize)
        {
            throw new IOException(
                "Verification failed: copied file size does not match the original.");
        }
    }

    private static bool IsProtectedFolder(
        string path)
    {
        string lower =
            path.ToLowerInvariant();

        return
            lower.Contains(@"\windows") ||
            lower.Contains(@"\system32") ||
            lower.Contains(@"\syswow64") ||
            lower.Contains(@"\$recycle.bin") ||
            lower.Contains(@"\.devclean");
    }

    private static void TryDeleteFile(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryRemoveEmptyQuarantineDirectory(
        string quarantinePath)
    {
        try
        {
            string? directory =
                Path.GetDirectoryName(
                    quarantinePath);

            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(
                    directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
        }
    }

    private static CleanupResult Success(
        string message,
        string id,
        long size)
    {
        return new CleanupResult
        {
            Success = true,
            Message = message,
            QuarantineId = id,
            BytesProcessed = size
        };
    }

    private static CleanupResult Failed(
        string message)
    {
        return new CleanupResult
        {
            Success = false,
            Message = message
        };
    }
}