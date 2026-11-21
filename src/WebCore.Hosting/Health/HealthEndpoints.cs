/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebCore.Accounts;
using WebCore.Health;
using WebCore.Storage;

namespace WebCore.Hosting.Health;

/// <summary>
/// The write door and the wiring of the health dashboard. Tools publish through the key-gated routes (the caller's gate, normally the import key); the app's own pages read the store and the report store, behind its
/// admin check. Everything a tool sends is validated and clamped here, because a signal ends up on a page and in a push notification.
/// </summary>
public static class HealthEndpoints
{
    private const int MaxSignals = 100;

    // A signal id is a dictionary key stored with every publication, so it is capped like a document name should be.
    private const int MaxSignalIdLength = 100;

    /// <summary>
    /// PUT /api/health/signals                      a <see cref="HealthPublication"/>: stores the signals, raises alerts for anything that turned red or recovered.
    /// PUT /api/health/reports/{environment}/{id}   an HTML report (at most <see cref="HealthReportStore.MaxBytes"/>) for a signal to link to.
    /// </summary>
    public static RouteGroupBuilder MapHealthSignals(this RouteGroupBuilder keyed)
    {
        keyed.MapPut("/api/health/signals", PutSignalsAsync);
        keyed.MapPut("/api/health/reports/{environment}/{id}", PutReportAsync);
        return keyed;
    }

    /// <summary>Registers the store, the report store, the alert dispatcher and the watchdog for this environment; phone push is added when <c>Health:Ntfy:Url</c> is configured.</summary>
    public static IServiceCollection AddHealthMonitoring(this IServiceCollection services, IConfiguration configuration, string environment, Func<IServiceProvider, IKeyedDocumentStore> signalDocuments, Func<IServiceProvider, IBinaryStore> reportFiles)
    {
        services.AddSingleton(new HealthEnvironment(HealthSignalStore.NormalizeEnvironment(environment)));
        services.AddSingleton(sp => new HealthSignalStore(signalDocuments(sp)));
        services.AddSingleton(sp => new HealthReportStore(reportFiles(sp)));
        services.AddSingleton<HealthAlertDispatcher>();
        services.AddHostedService<HealthWatchdog>();
        services.Configure<NtfyOptions>(configuration.GetSection("Health:Ntfy"));
        if (!string.IsNullOrWhiteSpace(configuration["Health:Ntfy:Url"]))
        {
            services.AddHttpClient(NtfyNotifier.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthNotifier, NtfyNotifier>());
        }

        return services;
    }

    private static async Task<IResult> PutSignalsAsync(HealthPublication publication, HealthSignalStore store, HealthAlertDispatcher alerts, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!DocumentNames.IsValid(publication.Environment))
        {
            return Results.BadRequest("environment must be letters, digits, dashes or underscores.");
        }

        if (publication.Signals is not { Count: > 0 and <= MaxSignals })
        {
            return Results.BadRequest($"Send between 1 and {MaxSignals} signals.");
        }

        if (publication.Signals.Select(ProblemWith).FirstOrDefault(p => p is not null) is { } problem)
        {
            return Results.BadRequest(problem);
        }

        var now = time.GetUtcNow();
        var environment = HealthSignalStore.NormalizeEnvironment(publication.Environment);
        var transitions = await store.PublishAsync(environment, publication.Signals.Select(s => Clamp(s, now)), cancellationToken).ConfigureAwait(false);
        var raised = transitions.Select(t => HealthAlertPolicy.ForTransition(environment, t)).OfType<HealthAlert>().ToList();
        await alerts.DispatchAsync(raised, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { accepted = publication.Signals.Count, alerts = raised.Count, channels = alerts.ChannelCount });
    }

    private static async Task<IResult> PutReportAsync(string environment, string id, HttpRequest request, HealthReportStore reports, CancellationToken cancellationToken)
    {
        if (!DocumentNames.IsValid(environment) || !HealthReportStore.IsValidId(id))
        {
            return Results.BadRequest("environment and id must be letters, digits, dashes or underscores.");
        }

        if (request.ContentLength is > HealthReportStore.MaxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var reader = new StreamReader(request.Body);
        var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return Results.BadRequest("The report is empty.");
        }

        await reports.SaveAsync(environment, id, html, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    // What a signal must be before it is stored. The first problem found is the one reported. A value that is not a finite
    // number is refused rather than clamped: infinity cannot be written to JSON, so the store would fail after the request was accepted.
    private static string? ProblemWith(HealthSignal s)
    {
        if (!DocumentNames.IsValid(s.Id) || s.Id.Length > MaxSignalIdLength || string.IsNullOrWhiteSpace(s.Title))
        {
            return $"Signal '{s.Id}' needs an id of letters, digits and dashes, and a title.";
        }

        // An out-of-range status would be stored as a number that no page knows how to colour.
        if (!Enum.IsDefined(s.Status))
        {
            return $"Signal '{s.Id}' has a status that is not Unknown, Green, Amber or Red.";
        }

        if (s.ExpectedEveryHours is { } hours && !double.IsFinite(hours))
        {
            return $"Signal '{s.Id}' needs an expectedEveryHours that is a finite number.";
        }

        return s.Value is { } value && !double.IsFinite(value) ? $"Signal '{s.Id}' needs a value that is a finite number." : null;
    }

    // What goes on a page and into a push is cut to size, a link must stay on this site, and a time in the future is the time of publishing.
    private static HealthSignal Clamp(HealthSignal s, DateTimeOffset now) => s with
    {
        Area = Cut(s.Area, 40),
        Title = Cut(s.Title, 80),
        Headline = Cut(s.Headline, 200),
        Detail = s.Detail is null ? null : Cut(s.Detail, 600),
        Unit = s.Unit is null ? null : Cut(s.Unit, 12),
        Link = s.Link is { } l && IsLocalLink(l) ? Cut(l, 200) : null,
        ReportId = HealthReportStore.IsValidId(s.ReportId) ? s.ReportId : null,
        AsOf = s.AsOf > now ? now : s.AsOf,
    };

    // The same rule as a return URL: a browser reads "/\host" as another host, so a link survives only when it is a local path
    // that sanitising leaves unchanged. Sanitize answers "/" for anything unsafe, so a link of exactly "/" still passes.
    private static bool IsLocalLink(string? link) =>
        link is not null && link.StartsWith('/') && LocalReturnUrl.Sanitize(link) == link;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
