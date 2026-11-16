/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebCore.Audit;
using WebCore.Caching;
using WebCore.Storage;

namespace WebCore.Hosting.Audit;

/// <summary>What an app tells the audit trail: how much to keep, and which top-level fields of its items no person needs to see in a change (bulky or bookkeeping).</summary>
public sealed record AuditTrailOptions
{
    public AuditLogOptions Log { get; init; } = new();

    public IReadOnlySet<string> IgnoredFields { get; init; } = AuditDiff.DefaultIgnored;
}

/// <summary>
/// The audit trail for an app in two calls: <c>services.AddAuditTrail(store)</c> registers the actor context, the log in the app's own store, the auditor and the Blazor
/// circuit handler; <c>app.UseAuditActors()</c>, after authentication, makes each request's signed-in person the actor. The app then wraps its own repositories with
/// <see cref="ChangeAuditor"/> (one subject per kind of item) and, if it wants one, shows the trail on an admin page from <see cref="IAuditLog"/>.
/// </summary>
public static class AuditTrailRegistration
{
    public static IServiceCollection AddAuditTrail(this IServiceCollection services, IKeyedDocumentStore store, AuditTrailOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var settings = options ?? new AuditTrailOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(settings);
        services.AddSingleton<IAuditActorContext, AuditActorContext>();
        // Uncached on purpose: written on every data change, read only by the trail's own pages.
        services.AddSingleton<IAuditLog>(sp => StorageCalls.Wrap<IAuditLog>(new AuditLog(store, sp.GetRequiredService<TimeProvider>(), settings.Log)));
        services.AddSingleton<ChangeAuditor>();
        services.AddScoped<CircuitHandler, AuditCircuitHandler>();
        return services;
    }

    /// <summary>Makes each request's signed-in person (with the request's trace id and path) the actor of what it changes. Call after UseAuthentication.</summary>
    public static IApplicationBuilder UseAuditActors(this IApplicationBuilder app) => app.UseMiddleware<AuditActorMiddleware>();
}

/// <summary>Who is behind a request or a circuit activity: the signed-in person; a request to <c>/api</c> with nobody signed in is the key-gated API; otherwise the system.</summary>
public static class AuditActors
{
    public static AuditActor From(ClaimsPrincipal? user, PathString path, string? traceId = null)
    {
        var trace = traceId ?? Activity.Current?.TraceId.ToString();
        var where = path.HasValue ? path.Value : null;
        if (user?.Identity?.IsAuthenticated == true && user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id)
        {
            var name = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity.Name ?? id;
            return new AuditActor(id, name, "web", trace, where);
        }

        return path.StartsWithSegments("/api") ? new AuditActor("api", "import API", "api", trace, where) : AuditActor.System with { TraceId = trace, Path = where };
    }
}

/// <summary>Holds the request's actor for the rest of the pipeline and releases it when the request leaves.</summary>
public sealed class AuditActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuditActorContext actors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actors);
        using var actor = actors.Use(AuditActors.From(context.User, context.Request.Path, Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier));
        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// An edit made from a page's buttons runs inside the Blazor circuit, not inside a request, so the request's actor does not reach it: each inbound activity of the
/// circuit (a click, an input) runs with the circuit's signed-in person as the actor. Harmless in an app without interactive circuits.
/// </summary>
public sealed class AuditCircuitHandler(IAuditActorContext actors, AuthenticationStateProvider authentication) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var user = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            using var scope = actors.Use(AuditActors.From(user, default));
            await next(context).ConfigureAwait(false);
        };
}
