using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevClean.Models;

namespace DevClean.Scanner;

public class DiskScanner
{
    private long _filesScanned;
    private long _foldersScanned;

    public List<(string Path, long Size)> Scan(
        string drivePath,
        int workerCount = 4,
        Action<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _filesScanned = 0;
        _foldersScanned = 0;

        var folders = new ConcurrentBag<(string Path, long Size)>();

        string[] directories;

        try
        {
            directories = Directory
                .EnumerateDirectories(drivePath)
                .Where(directory => !IsDevCleanPath(directory))
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read drive: {ex.Message}");
            return [];
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = cancellationToken
        };

        Parallel.ForEach(
            directories,
            options,
            directory =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsDevCleanPath(directory))
                {
                    return;
                }

                long size = GetFolderSize(
                    directory,
                    cancellationToken);

                folders.Add((directory, size));

                progress?.Invoke(new ScanProgress
                {
                    FoldersScanned = Interlocked.Read(ref _foldersScanned),
                    FilesScanned = Interlocked.Read(ref _filesScanned),
                    CurrentPath = directory
                });
            });

        return folders
            .OrderByDescending(x => x.Size)
            .ToList();
    }

    private long GetFolderSize(
        string path,
        CancellationToken cancellationToken)
    {
        long total = 0;

        if (IsDevCleanPath(path))
        {
            return 0;
        }

        try
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(path);

            // Do not follow symbolic links or junctions.
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return 0;
            }

            Interlocked.Increment(ref _foldersScanned);

            foreach (FileInfo file in directoryInfo.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Never scan DevClean's own files.
                    if (IsDevCleanPath(file.FullName))
                    {
                        continue;
                    }

                    // Ignore reparse-point files.
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    total += file.Length;

                    Interlocked.Increment(ref _filesScanned);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // File may be locked or inaccessible.
                }
            }

            foreach (DirectoryInfo subDirectory in directoryInfo.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Never scan DevClean's own directory.
                    if (IsDevCleanPath(subDirectory.FullName))
                    {
                        continue;
                    }

                    // Do not follow junctions or symbolic links.
                    if ((subDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    total += GetFolderSize(
                        subDirectory.FullName,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Directory may be inaccessible.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Folder may be inaccessible.
        }

        return total;
    }

    public Task<List<(string Path, long Size)>> ScanAsync(
        string drivePath,
        int workerCount = 4,
        Action<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Scan(
                drivePath,
                workerCount,
                progress,
                cancellationToken),
            cancellationToken);
    }

    public List<FileItem> ScanFiles(
        string drivePath,
        CancellationToken cancellationToken = default)
    {
        var files = new List<FileItem>();

        ScanFilesRecursive(
            drivePath,
            files,
            cancellationToken);

        return files;
    }

    public Task<List<FileItem>> ScanFilesAsync(
        string drivePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => ScanFiles(
                drivePath,
                cancellationToken),
            cancellationToken);
    }

    private void ScanFilesRecursive(
        string path,
        List<FileItem> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Never scan DevClean's own directory.
        if (IsDevCleanPath(path))
        {
            return;
        }

        try
        {
            DirectoryInfo directory = new DirectoryInfo(path);

            // Never follow junctions or symbolic links.
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            foreach (FileInfo file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Never scan DevClean's own files.
                    if (IsDevCleanPath(file.FullName))
                    {
                        continue;
                    }

                    // Ignore reparse-point files.
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    files.Add(new FileItem
                    {
                        Path = file.FullName,
                        ParentDirectory = file.DirectoryName ?? string.Empty,
                        Name = file.Name,
                        Size = file.Length,
                        Extension = file.Extension,
                        Created = file.CreationTime,
                        LastModified = file.LastWriteTime,
                        LastAccessed = file.LastAccessTime,
                        IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
                        IsSystem = (file.Attributes & FileAttributes.System) != 0
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // File may be locked or inaccessible.
                }
            }

            foreach (DirectoryInfo subDirectory in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Never scan DevClean's own directory.
                    if (IsDevCleanPath(subDirectory.FullName))
                    {
                        continue;
                    }

                    // Never follow junctions or symbolic links.
                    if ((subDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    ScanFilesRecursive(
                        subDirectory.FullName,
                        files,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Directory may be inaccessible.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Directory may be inaccessible.
        }
    }

    private static bool IsDevCleanPath(string path)
    {
        return path.Contains(
            $"{Path.DirectorySeparatorChar}.DevClean{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ||
            path.EndsWith(
                $"{Path.DirectorySeparatorChar}.DevClean",
                StringComparison.OrdinalIgnoreCase);
    }
}

