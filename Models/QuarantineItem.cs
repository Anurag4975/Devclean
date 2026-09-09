namespace DevClean.Models;

public class QuarantineItem
{
    public string Id { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public string QuarantinePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime QuarantinedAt { get; set; }

    public FileCategory Category { get; set; }

    public SafetyLevel SafetyLevel { get; set; }

    public string Reason { get; set; } = string.Empty;

    public bool IsFolder { get; set; }
}