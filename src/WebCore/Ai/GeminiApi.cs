/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

// Moved here from TriviaNight.Art.Photos (AiVisibility slice 6) so TriviaNight and AiVisibility share one Gemini client.

using System.Text;
using System.Text.Json;

namespace WebCore.Ai;

/// <summary>The one place that knows how to ask Gemini for text (the key goes in a header, never the address) and how to read the answer.</summary>
public static class GeminiApi
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

    /// <summary>The first JSON array written in a reply (a model may wrap it in prose), parsed; empty when there is none.</summary>
    public static IReadOnlyList<JsonElement> ArrayIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // '[' is one ASCII byte and never occurs inside a multi-byte UTF-8 character, so the search can run on the bytes.
        var utf8 = Encoding.UTF8.GetBytes(text);
        for (var start = Array.IndexOf(utf8, (byte)'['); start >= 0; start = Array.IndexOf(utf8, (byte)'[', start + 1))
        {
            if (ArrayAt(utf8, start) is { } array)
            {
                return array;
            }
        }

        return [];
    }

    // Reads one complete JSON value that starts at this '['. Prose brackets ("[note 1]") are not valid JSON, so they
    // return null and the search moves on; only a whole, valid value counts.
    private static IReadOnlyList<JsonElement>? ArrayAt(byte[] utf8, int start)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8.AsSpan(start));
            reader.Read();
            reader.Skip();
            using var doc = JsonDocument.Parse(utf8.AsMemory(start, (int)reader.BytesConsumed));
            return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
