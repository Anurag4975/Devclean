namespace DevClean.Models;

public class InstalledApplication
{
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public long EstimatedSize { get; set; }
    public DateTime InstallDate { get; set; }
}