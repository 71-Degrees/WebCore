/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WebCore.Health;

/// <summary>What a tool sends: the environment it measured and its signals.</summary>
public sealed record HealthPublication(string Environment, IReadOnlyList<HealthSignal> Signals);

/// <summary>
/// The client every tool uses to put its signals (and, optionally, a full HTML report) on a site's dashboard, so no tool carries its own copy of the HTTP details. The caller owns the <see cref="HttpClient"/> (and its
/// base address and import key header); this only knows the routes.
/// </summary>
public sealed class HealthPublisher(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(string environment, IReadOnlyList<HealthSignal> signals, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync("api/health/signals", new HealthPublication(environment, signals), Json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadReportAsync(string environment, string reportId, string html, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(html, Encoding.UTF8, "text/html");
        using var response = await http.PutAsync($"api/health/reports/{Uri.EscapeDataString(environment)}/{Uri.EscapeDataString(reportId)}", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
