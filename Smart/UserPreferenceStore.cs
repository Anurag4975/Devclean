using System.Text.Json;

namespace DevClean.Smart;

/// <summary>
/// The "learning loop". Remembers paths the user RESTORED from quarantine so DevClean
/// never suggests them again. Stored in %AppData%/DevClean/preferences.json.
/// Fail-safe: all I/O errors are swallowed and an empty store is used.
/// </summary>
public sealed class UserPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lazy<UserPreferenceStore> _instance = new(Load);
    private readonly object _lock = new();
    private HashSet<string> _suppressedPatterns = new(StringComparer.OrdinalIgnoreCase);

    public static UserPreferenceStore Instance => _instance.Value;

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DevClean");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "preferences.json");
        }
    }

    private static UserPreferenceStore Load()
    {
        var store = new UserPreferenceStore();
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<StoredData>(
                    File.ReadAllText(FilePath), JsonOptions);
                if (data?.SuppressedPatterns is not null)
                {
                    store._suppressedPatterns = new HashSet<string>(
                        data.SuppressedPatterns, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Empty store on any failure.
        }
        return store;
    }

    /// <summary>Call when the user restores an item — DevClean will not suggest this path again.</summary>
    public void RecordRestore(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return;
        string pattern = Normalize(originalPath);
        lock (_lock)
        {
            if (_suppressedPatterns.Add(pattern))
            {
                SaveInternal();
            }
        }
    }

    /// <summary>True if the user previously restored this exact path or one of its parent folders.</summary>
    public bool IsSuppressed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = Normalize(path);
        lock (_lock)
        {
            foreach (string pattern in _suppressedPatterns)
            {
                if (string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (normalized.StartsWith(pattern + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private void SaveInternal()
    {
        try
        {
            var data = new StoredData { SuppressedPatterns = _suppressedPatterns.ToList() };
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Ignore save failures.
        }
    }

    private sealed class StoredData
    {
        public List<string> SuppressedPatterns { get; set; } = [];
    }
}
