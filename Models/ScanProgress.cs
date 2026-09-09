namespace DevClean.Models;

public class ScanProgress
{
    public long FoldersScanned { get; set; }

    public long FilesScanned { get; set; }

    public string CurrentPath { get; set; } = string.Empty;
}   