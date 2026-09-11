using System.Net.Http;
using System.Text;
using System.Text.Json;
using DevClean.Models;

namespace DevClean.AI;

/// <summary>One AI-recommended cleanup item.</summary>
public sealed class AiRecommendedItem
{
    public string Path { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Result of a Groq batch analysis.</summary>
public sealed class AiCleanPlan
{
    public List<AiRecommendedItem> Recommended { get; init; } = [];
    public int FilesAnalyzed { get; init; }
    public string Model { get; init; } = string.Empty;
    public string RawError { get; init; } = string.Empty;
    public bool Success => string.IsNullOrEmpty(RawError);
}

/// <summary>Result of a single-file AI safety check (Clean tab "Check with AI").</summary>
public sealed class AiFileVerdict
{
    public bool IsSafe { get; init; }
    public string Consequence { get; init; } = string.Empty;
    public string SafeDeletionSteps { get; init; } = string.Empty;
    public string RawError { get; init; } = string.Empty;
    public bool Success => string.IsNullOrEmpty(RawError);
}

/// <summary>
/// Groq AI batch advisor. Sends a capped text manifest of large/old files
/// to Groq and gets back a list of paths it recommends quarantining, with reasons.
/// The user always reviews and confirms — AI never deletes anything.
///
/// Only files at or above 500 MB are considered.
/// Limit: only the top N files by size are sent (default 400) to control cost and time.
///
/// The class/file name intentionally remains MistralCleanAdvisor.cs
/// so no other parts of the application need to be changed.
/// </summary>
public static class MistralCleanAdvisor
{
    /// <summary>
    /// Replaces the Windows account name segment of a path (C:\Users\&lt;name&gt;\...) with a
    /// generic placeholder before the path is sent to a third-party AI provider. Falls back to
    /// returning the path unchanged if it doesn't match the expected \Users\&lt;name&gt; shape.
    /// </summary>
    private static string RedactUserName(string path)
    {
        const string marker = @"\Users\";
        int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return path;
        int nameStart = start + marker.Length;
        int nameEnd = path.IndexOf('\\', nameStart);
        if (nameEnd < 0) return path;
        return string.Concat(path.AsSpan(0, nameStart), "user", path.AsSpan(nameEnd));
    }

    private const string Endpoint =
        "https://api.groq.com/openai/v1/chat/completions";

    // Groq model.
    // Change this later if you want to test another Groq model.
    private const string DefaultModel = "openai/gpt-oss-20b";

    private const long DefaultMinFileSizeBytes =
        500L * 1024 * 1024; // 500 MB

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public static async Task<AiCleanPlan> AnalyzeAsync(
        List<FileItem> files,
        string apiKey,
        int maxFilesPerScan = 400,
        string model = DefaultModel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiCleanPlan
            {
                RawError = "No Groq API key set. Add it in the AI tab."
            };
        }

        // Only send non-system files that are 500 MB or larger.
        // Then prioritize the largest files and, for equal sizes, the oldest.
        var subset = files
            .Where(f => !f.IsSystem)
            .Where(f => f.Size >= DefaultMinFileSizeBytes)
            .OrderByDescending(f => f.Size)
            .ThenBy(f => f.LastModified)
            .Take(maxFilesPerScan)
            .ToList();

        // Privacy: the Windows username almost always appears in the path (C:\Users\<name>\...)
        // and would otherwise leave the device inside a third-party AI request. Replace it with
        // a placeholder before sending, and map recommended paths back to the real path afterward
        // so quarantine actions still work against the real file.
        var redactedToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new StringBuilder();
        manifest.AppendLine("path\tsize_bytes\text\tlast_modified");

        foreach (FileItem f in subset)
        {
            string redactedPath = RedactUserName(f.Path);
            redactedToReal[redactedPath] = f.Path;
            manifest.AppendLine(
                $"{redactedPath}\t{f.Size}\t{f.Extension}\t{f.LastModified:yyyy-MM-dd}");
        }

        string systemPrompt =
            "You are a conservative Windows disk-cleanup safety advisor. " +
            "Given a TSV manifest of files, return ONLY valid JSON with a single key \"quarantine\" " +
            "which is an array of objects {\"path\": string, \"reason\": string}. " +
            "Include ONLY files that are very likely safe to remove (temp, cache, logs, installers, old archives, " +
            "package caches, browser cache). NEVER include documents, photos, source code, .git, Program Files, " +
            "Windows system files, or anything personal. If unsure, omit it. No markdown, no commentary.";

        var payload = new
        {
            model,
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt
                },
                new
                {
                    role = "user",
                    content = manifest.ToString()
                }
            }
        };

        const int maxRetries = 4;

        HttpResponseMessage? response = null;
        string body = string.Empty;

        try
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    Endpoint);

                request.Headers.Add(
                    "Authorization",
                    $"Bearer {apiKey}");

                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                response = await Http.SendAsync(request, ct);

                body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    break;
                }

                if (attempt == maxRetries)
                {
                    return new AiCleanPlan
                    {
                        RawError =
                            $"Groq is rate-limiting this API key (429) after {maxRetries} retries. " +
                            "Try again in a minute, reduce the file batch size, or check your Groq rate limits."
                    };
                }

                // Respect Retry-After if Groq sends one;
                // otherwise use exponential backoff.
                TimeSpan delay =
                    response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

                await Task.Delay(delay, ct);
            }

            if (response is null || !response.IsSuccessStatusCode)
            {
                int status =
                    response is null
                        ? 0
                        : (int)response.StatusCode;

                return new AiCleanPlan
                {
                    RawError =
                        $"Groq API error {status}: {Truncate(body, 300)}"
                };
            }

            using JsonDocument doc =
                JsonDocument.Parse(body);

            string content =
                doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                ?? "{}";

            using JsonDocument result =
                JsonDocument.Parse(content);

            var recommended =
                new List<AiRecommendedItem>();

            if (result.RootElement.TryGetProperty(
                    "quarantine",
                    out JsonElement arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string? p =
                        el.TryGetProperty(
                            "path",
                            out var pp)
                            ? pp.GetString()
                            : null;

                    string? r =
                        el.TryGetProperty(
                            "reason",
                            out var rr)
                            ? rr.GetString()
                            : "AI-recommended";

                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        // Map the (possibly redacted) path the AI echoed back to the real,
                        // on-disk path so the Clean tab can actually find and quarantine it.
                        p = redactedToReal.TryGetValue(p, out string? real) ? real : p;
                        recommended.Add(
                            new AiRecommendedItem
                            {
                                Path = p,
                                Reason =
                                    r ?? "AI-recommended"
                            });
                    }
                }
            }

            return new AiCleanPlan
            {
                Recommended = recommended,
                FilesAnalyzed = subset.Count,
                Model = model
            };
        }
        catch (TaskCanceledException)
        {
            return new AiCleanPlan
            {
                RawError = "AI request timed out."
            };
        }
        catch (Exception ex)
        {
            return new AiCleanPlan
            {
                RawError = ex.Message
            };
        }
    }

    /// <summary>
    /// Single-file AI safety check, used by the "Check with AI" button on the Clean tab.
    /// Asks Groq whether this specific file/folder is safe to delete, what happens if it's
    /// deleted, and how to remove it safely. Same retry/error handling shape as AnalyzeAsync.
    /// </summary>
    public static async Task<AiFileVerdict> AnalyzeSingleFileAsync(
        FileItem file,
        string apiKey,
        string model = DefaultModel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiFileVerdict
            {
                RawError = "No Groq API key set. Add it in the AI tab."
            };
        }

        string details =
            $"path: {RedactUserName(file.Path)}\n" +
            $"size_bytes: {file.Size}\n" +
            $"extension: {file.Extension}\n" +
            $"last_modified: {file.LastModified:yyyy-MM-dd}\n" +
            $"is_hidden: {file.IsHidden}\n" +
            $"is_system: {file.IsSystem}";

        string systemPrompt =
            "You are a conservative Windows disk-cleanup safety advisor. " +
            "Given details about ONE specific file or folder, return ONLY valid JSON with exactly these keys: " +
            "\"safe\" (boolean — true only if you are confident it can be deleted with no meaningful risk), " +
            "\"consequence\" (one or two plain-English sentences describing what happens if the user deletes this — " +
            "what breaks, what regenerates automatically, or what data is lost), " +
            "\"steps\" (one or two plain-English sentences on the safest way to remove it, e.g. quarantine first, " +
            "close the related app first, or back it up first). " +
            "Be conservative: if you are not confident, set safe to false and explain why in consequence. " +
            "No markdown, no commentary, JSON only.";

        var payload = new
        {
            model,
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt
                },
                new
                {
                    role = "user",
                    content = details
                }
            }
        };

        const int maxRetries = 4;

        HttpResponseMessage? response = null;
        string body = string.Empty;

        try
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    Endpoint);

                request.Headers.Add(
                    "Authorization",
                    $"Bearer {apiKey}");

                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                response = await Http.SendAsync(request, ct);

                body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    break;
                }

                if (attempt == maxRetries)
                {
                    return new AiFileVerdict
                    {
                        RawError =
                            $"Groq is rate-limiting this API key (429) after {maxRetries} retries. " +
                            "Try again in a minute."
                    };
                }

                TimeSpan delay =
                    response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

                await Task.Delay(delay, ct);
            }

            if (response is null || !response.IsSuccessStatusCode)
            {
                int status =
                    response is null
                        ? 0
                        : (int)response.StatusCode;

                return new AiFileVerdict
                {
                    RawError =
                        $"Groq API error {status}: {Truncate(body, 300)}"
                };
            }

            using JsonDocument doc =
                JsonDocument.Parse(body);

            string content =
                doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                ?? "{}";

            using JsonDocument result =
                JsonDocument.Parse(content);

            bool safe = result.RootElement.TryGetProperty("safe", out var safeEl)
                && safeEl.ValueKind == JsonValueKind.True;

            string consequence = result.RootElement.TryGetProperty("consequence", out var cEl)
                ? (cEl.GetString() ?? string.Empty)
                : string.Empty;

            string steps = result.RootElement.TryGetProperty("steps", out var sEl)
                ? (sEl.GetString() ?? string.Empty)
                : string.Empty;

            return new AiFileVerdict
            {
                IsSafe = safe,
                Consequence = string.IsNullOrWhiteSpace(consequence) ? "No details returned." : consequence,
                SafeDeletionSteps = string.IsNullOrWhiteSpace(steps) ? "Use Quarantine so it's fully restorable." : steps
            };
        }
        catch (TaskCanceledException)
        {
            return new AiFileVerdict
            {
                RawError = "AI request timed out."
            };
        }
        catch (Exception ex)
        {
            return new AiFileVerdict
            {
                RawError = ex.Message
            };
        }
    }

    private static string Truncate(
        string s,
        int max) =>
        s.Length <= max
            ? s
            : s[..max] + "...";
}