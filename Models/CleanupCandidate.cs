namespace DevClean.Models;

public class CleanupCandidate
{
    public FileItem File { get; set; } = new();

    public string TargetPath { get; set; } = string.Empty;

    public bool IsFolder { get; set; }

    public LocationType Location { get; set; }

    public FileCategory Category { get; set; }

    public List<string> Reasons { get; set; } = [];

    public int PriorityScore { get; set; }

    public long Size =>
        File.Size;
}