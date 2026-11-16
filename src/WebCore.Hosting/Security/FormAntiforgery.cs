/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.AspNetCore.Antiforgery;

namespace WebCore.Hosting.Security;

/// <summary>Marks an endpoint whose antiforgery token is checked by <see cref="FormAntiforgery"/>. A test fails on any POST
/// endpoint without it.</summary>
public sealed class FormAntiforgeryMetadata;

/// <summary>
/// Every plain form endpoint (sign-in, feedback, admin actions) is mapped on a group from <see cref="MapFormGroup"/>, which
/// refuses a request that isn't a form post with a valid antiforgery token (400) before the handler runs - one place,
/// instead of a hand-copied check in each handler that a new endpoint could forget.
/// </summary>
public static class FormAntiforgery
{
    public static RouteGroupBuilder MapFormGroup(this IEndpointRouteBuilder app, string prefix = "")
    {
        var group = app.MapGroup(prefix);
        group.WithMetadata(new FormAntiforgeryMetadata());
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!http.Request.HasFormContentType)
            {
                Refused(http, "not a form post");
                return Results.BadRequest("Invalid request.");
            }

            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException ex)
            {
                Refused(http, "antiforgery token invalid: " + ex.Message);
                return Results.BadRequest("Invalid request.");
            }

            return await next(context);
        });
        return group;
    }

    // The visitor only sees "Invalid request."; the log says why, because a refused phone upload is otherwise invisible (a 400 with no reason in telemetry).
    private static void Refused(HttpContext http, string reason) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebCore.Forms").LogWarning(
            "Form post refused on {Path}: {Reason} (content type {ContentType}, length {Length}, agent {Agent})",
            http.Request.Path, reason, http.Request.ContentType, http.Request.ContentLength, http.Request.Headers.UserAgent.ToString());
}
