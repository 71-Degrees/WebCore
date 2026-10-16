/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net;
using System.Text;
using System.Text.Json;
using WebCore.Operations;

namespace WebCore.Deploy;

/// <summary>What the "being updated" page says, from the site's own downtime notice.</summary>
internal sealed record OfflineContent(string Heading, string Message, string? BackAt, string? ImageDataUri, bool ShowReleaseNotes, Release? Release);

/// <summary>
/// The page visitors get while a deploy restarts the site (app_offline.htm), drawn from the downtime notice the admin wrote on the site (heading, message, when it is expected back, a picture, and a button that opens what
/// is new in the release being deployed). One self-contained file: the picture is inside it, since the site is down. The page checks the site every few seconds and reloads itself only once it answers, so a visitor
/// reading the release notes is not interrupted. The site's own preview of it (the admin's downtime page) looks the same; keep the two alike.
/// </summary>
internal static class OfflinePage
{
    private const string DefaultHeading = "Better Trivia is being updated";
    private const string DefaultMessage = "We'll be back in about a minute. This page refreshes on its own - no need to do anything.";

    /// <summary>Reads the site's <c>GET /api/admin/maintenance</c> answer (<c>{ "notice": { ... } }</c>); null when it holds no notice.</summary>
    internal static OfflineContent? FromNotice(string json, string? imageDataUri, ReleaseNotes? notes)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("notice", out var notice) || notice.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string Text(string name) => notice.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        var ends = notice.TryGetProperty("endsAt", out var end) && end.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(end.GetString(), out var endsAt) ? endsAt : (DateTimeOffset?)null;
        var starts = notice.TryGetProperty("startsAt", out var start) && start.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(start.GetString(), out var startsAt) ? startsAt : (DateTimeOffset?)null;
        var back = ends ?? starts?.AddMinutes(30);
        var showNotes = !notice.TryGetProperty("showReleaseNotes", out var flag) || flag.ValueKind != JsonValueKind.False;
        return new OfflineContent(Text("heading"), Text("message"), back?.ToLocalTime().ToString("dddd h:mm tt"), imageDataUri, showNotes, notes?.Releases is { Count: > 0 } releases ? releases[0] : null);
    }

    internal static string Render(OfflineContent content)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
            <noscript><meta http-equiv="refresh" content="10" /></noscript>
            <title>Better Trivia - back in a minute</title>
            <style>
              body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; background: #0f1226; color: #e8eaf6; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; text-align: center; padding: 1rem; }
              .card { max-width: 32rem; width: 100%; background: #1a1f3d; border-radius: 16px; padding: 1.75rem 1.5rem; }
              img { width: 9rem; height: auto; max-height: 11rem; object-fit: contain; }
              h1 { margin: 0.4rem 0; font-size: 1.6rem; }
              p { margin: 0.5rem 0; color: #b8bde0; line-height: 1.5; }
              details { margin-top: 0.9rem; text-align: left; }
              summary { cursor: pointer; display: inline-block; padding: 0.55rem 1rem; border: 1px solid #3a4275; border-radius: 10px; color: #e8eaf6; list-style: none; }
              .notes { max-height: 13rem; overflow-y: auto; margin-top: 0.7rem; font-size: 0.9rem; color: #cfd3f0; }
              .notes h4 { margin: 0.6rem 0 0.15rem; font-size: 0.75rem; letter-spacing: 0.05em; text-transform: uppercase; color: #9aa0c9; }
              .notes ul { margin: 0; padding-left: 1.1rem; }
            </style></head>
            <body><div class="card">
            """);
        if (content.ImageDataUri is not null)
        {
            html.Append($"<img src=\"{content.ImageDataUri}\" alt=\"\" />");
        }

        html.Append($"<h1>{Encode(string.IsNullOrWhiteSpace(content.Heading) ? DefaultHeading : content.Heading)}</h1>");
        html.Append($"<p>{Encode(string.IsNullOrWhiteSpace(content.Message) ? DefaultMessage : content.Message)}</p>");
        if (!string.IsNullOrWhiteSpace(content.BackAt))
        {
            html.Append($"<p>Expected back around <strong>{Encode(content.BackAt)}</strong></p>");
        }

        if (content.ShowReleaseNotes && content.Release is { Commits.Count: > 0 } release)
        {
            html.Append("<details><summary>See what's new in this update</summary><div class=\"notes\">");
            foreach (var category in Enum.GetValues<ChangeCategory>())
            {
                var group = release.Commits.Where(c => c.Category == category).ToList();
                if (group.Count == 0)
                {
                    continue;
                }

                html.Append($"<h4>{Encode(CommitClassifier.Heading(category))}</h4><ul>");
                foreach (var commit in group)
                {
                    html.Append($"<li>{Encode(commit.Subject)}</li>");
                }

                html.Append("</ul>");
            }

            html.Append("</div></details>");
        }

        // Padding so browsers show this page instead of their own generic error page for a 503 (some need more than 512 bytes), then the check that reloads it once the site answers.
        html.Append("<!-- " + new string('.', 700) + " -->");
        html.Append("""
            </div>
            <script>
            setInterval(function () { fetch('/', { cache: 'no-store' }).then(function (r) { if (r.status < 500) { location.reload(); } }).catch(function () { }); }, 5000);
            </script>
            </body></html>
            """);
        return html.ToString();
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
