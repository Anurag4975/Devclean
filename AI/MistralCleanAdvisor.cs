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

        var manifest = new StringBuilder();
        manifest.AppendLine("path\tsize_bytes\text\tlast_modified");

        foreach (FileItem f in subset)
        {
            manifest.AppendLine(
                $"{f.Path}\t{f.Size}\t{f.Extension}\t{f.LastModified:yyyy-MM-dd}");
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

    private static string Truncate(
        string s,
        int max) =>
        s.Length <= max
            ? s
            : s[..max] + "...";
}