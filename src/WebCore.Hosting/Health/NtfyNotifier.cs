/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using WebCore.Health;

namespace WebCore.Hosting.Health;

/// <summary>Where to send phone pushes: the full topic address ("https://ntfy.sh/a-long-private-topic"), which is the secret, so it lives in configuration and never in code.</summary>
public sealed class NtfyOptions
{
    public string? Url { get; set; }

    /// <summary>Opened when the notification is tapped: the dashboard.</summary>
    public string? ClickUrl { get; set; }
}

/// <summary>The phone push channel, over ntfy: one POST to the topic. Red is urgent, a recovery is quiet, a signal gone quiet is a normal push.</summary>
public sealed class NtfyNotifier(IHttpClientFactory httpFactory, IOptions<NtfyOptions> options) : IHealthNotifier
{
    public const string ClientName = "health-ntfy";

    public string Channel => "ntfy";

    public async Task NotifyAsync(HealthAlert alert, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = new StringContent(alert.Message, Encoding.UTF8, new MediaTypeHeaderValue("text/plain")) };
        request.Headers.TryAddWithoutValidation("Title", Ascii(alert.Title));
        request.Headers.TryAddWithoutValidation("Priority", PriorityFor(alert.Kind));
        request.Headers.TryAddWithoutValidation("Tags", TagFor(alert.Kind));
        if (!string.IsNullOrWhiteSpace(settings.ClickUrl))
        {
            request.Headers.TryAddWithoutValidation("Click", settings.ClickUrl);
        }

        using var client = httpFactory.CreateClient(ClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string PriorityFor(HealthAlertKind kind) => kind switch
    {
        HealthAlertKind.Red => "urgent",
        HealthAlertKind.Stale => "default",
        _ => "low",
    };

    private static string TagFor(HealthAlertKind kind) => kind switch
    {
        HealthAlertKind.Red => "rotating_light",
        HealthAlertKind.Recovered => "white_check_mark",
        _ => "zzz",
    };

    // HTTP header values must be plain ASCII.
    private static string Ascii(string text) => new(text.Select(c => c is >= ' ' and <= '~' ? c : '-').ToArray());
}
