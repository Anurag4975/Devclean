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

/// <summary>Result of a Mistral batch analysis.</summary>
public sealed class AiCleanPlan
{
    public List<AiRecommendedItem> Recommended { get; init; } = [];
    public int FilesAnalyzed { get; init; }
    public string Model { get; init; } = string.Empty;
    public string RawError { get; init; } = string.Empty;
    public bool Success => string.IsNullOrEmpty(RawError);
}

/// <summary>
/// Mistral AI batch advisor. Sends a capped text manifest of the largest/oldest files
/// to Mistral and gets back a list of paths it recommends quarantining, with reasons.
/// The user always reviews and confirms — AI never deletes anything.
/// Limit: only the top N files by size are sent (default 1500) to control cost and time.
/// </summary>
public static class MistralCleanAdvisor
{
    private const string Endpoint = "https://api.mistral.ai/v1/chat/completions";
    private const string DefaultModel = "mistral-small-latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<AiCleanPlan> AnalyzeAsync(
        List<FileItem> files,
        string apiKey,
        int maxFilesPerScan = 1500,
        string model = DefaultModel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiCleanPlan { RawError = "No Mistral API key set. Add it in the AI tab." };
        }

        // Send only the largest + oldest files (highest value, bounded cost).
        var subset = files
            .Where(f => !f.IsSystem)
            .OrderByDescending(f => f.Size)
            .ThenBy(f => f.LastModified)
            .Take(maxFilesPerScan)
            .ToList();

        var manifest = new StringBuilder();
        manifest.AppendLine("path\tsize_bytes\text\tlast_modified");
        foreach (FileItem f in subset)
        {
            manifest.AppendLine($"{f.Path}\t{f.Size}\t{f.Extension}\t{f.LastModified:yyyy-MM-dd}");
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
                new { role = "system", content = systemPrompt },
                new { role = "user", content = manifest.ToString() }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return new AiCleanPlan { RawError = $"Mistral API error {(int)response.StatusCode}: {Truncate(body, 300)}" };
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            string content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";

            using JsonDocument result = JsonDocument.Parse(content);
            var recommended = new List<AiRecommendedItem>();
            if (result.RootElement.TryGetProperty("quarantine", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string? p = el.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                    string? r = el.TryGetProperty("reason", out var rr) ? rr.GetString() : "AI-recommended";
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        recommended.Add(new AiRecommendedItem { Path = p, Reason = r ?? "AI-recommended" });
                    }
                }
            }

            return new AiCleanPlan { Recommended = recommended, FilesAnalyzed = subset.Count, Model = model };
        }
        catch (TaskCanceledException) { return new AiCleanPlan { RawError = "AI request timed out." }; }
        catch (Exception ex) { return new AiCleanPlan { RawError = ex.Message }; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
