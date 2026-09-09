namespace DevClean.Cleanup;

public class CleanupResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? QuarantineId { get; set; }

    public long BytesProcessed { get; set; }
}