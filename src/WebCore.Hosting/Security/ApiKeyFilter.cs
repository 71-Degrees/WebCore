/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Hosting.Security;

/// <summary>Runs the <see cref="ApiKeyGate"/> in front of every endpoint of a route group: a request without the right key is answered here and the handler never runs. Arguments are bound before filters run, exactly as before when each handler made the check itself.</summary>
public sealed class ApiKeyFilter(ApiKeyGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        gate.Check(context.HttpContext) ?? await next(context);
}
