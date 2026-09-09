namespace DevClean.Models;

public class FileItem
{
    public string Path { get; set; } = string.Empty;
    public string ParentDirectory { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Extension { get; set; } = string.Empty;

    public DateTime LastModified { get; set; }
    public DateTime Created { get; set; }

    public DateTime LastAccessed { get; set; }

    public bool IsHidden { get; set; }

    public bool IsSystem { get; set; }
}