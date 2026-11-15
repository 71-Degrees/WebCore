/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

// Moved here from TriviaNight.Art.Photos (AiVisibility slice 6) so TriviaNight and AiVisibility share one Gemini client.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebCore.Ai;

/// <summary>The one place that knows how to ask Gemini for text (the key goes in a header, never the address) and how to read the answer.</summary>
public static partial class GeminiApi
{
    public static async Task<string> GenerateAsync(HttpClient http, string apiKey, string model, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-goog-api-key", apiKey);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : throw new HttpRequestException($"Gemini answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    /// <summary>The tokens a reply says it used (prompt, answer); zeros when it does not say.</summary>
    public static (long In, long Out) Usage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("usageMetadata", out var u)
            ? (u.TryGetProperty("promptTokenCount", out var i) ? i.GetInt64() : 0, u.TryGetProperty("candidatesTokenCount", out var o) ? o.GetInt64() : 0)
            : (0, 0);
    }

    /// <summary>The text a reply holds (its parts joined), or empty when it holds none.</summary>
    public static string ReplyText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts)
            ? string.Concat(parts.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null))
            : string.Empty;
    }

    /// <summary>The JSON array written somewhere in a reply (a model may wrap it in prose), parsed; empty when there is none or it is not valid.</summary>
    public static IReadOnlyList<JsonElement> ArrayIn(string text)
    {
        var match = JsonArray().Match(text);
        if (!match.Success)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"\[.*\]", RegexOptions.Singleline)]
    private static partial Regex JsonArray();
}
