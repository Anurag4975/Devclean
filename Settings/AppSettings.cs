using System.Text.Json;

namespace DevClean.Settings;

/// <summary>
/// Persisted user settings, stored in %AppData%/DevClean/settings.json.
/// Fail-safe: if the file cannot be read or written, built-in defaults are used and
/// no exception ever propagates to the UI.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lazy<AppSettings> _instance = new(Load);
    private static readonly object _lock = new();

    public static AppSettings Instance => _instance.Value;

    /// <summary>Days an item stays in quarantine before it can be auto-purged. Default: 30.</summary>
    public int QuarantineRetentionDays { get; set; } = 30;

    /// <summary>When true, expired quarantine items are permanently removed on app start.</summary>
    public bool AutoPurgeExpiredQuarantine { get; set; } = false;

    /// <summary>Simple one-button mode for non-technical users.</summary>
    public bool SimpleMode { get; set; } = false;

    /// <summary>Include modern-junk targets (Docker, WSL, package caches, etc.) in scans.</summary>
    public bool ScanModernJunk { get; set; } = true;

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevClean");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings — fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Atomically saves settings. Never throws.</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                // Never crash the app because settings could not be saved.
            }
        }
    }
}
