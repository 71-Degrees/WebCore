# Edge middleware

The first few steps of every 71 Degrees app's pipeline, lifted from Better Trivia and CrateDigger (2026-11-16), which each had them inline.

```csharp
app.UseHeadAsGet();                    // a HEAD for a page answers like its GET without the body (uptime monitors); before routing
app.UseRouting();                      // explicit, so UseHeadAsGet runs first

app.UseSecurityHeaders(o =>
{
    o.PermissionsPolicy = "geolocation=(self), camera=(self), microphone=()";   // why: the viewfinder and the shop map
    o.NoIndexEverywhere = siteInfo.IsStaging;
    o.NoIndexPaths.Add("/share");                                               // shared links are not for search engines
    o.Csp.Allow("img-src", "https://tile.openstreetmap.org");                  // why: map tiles; Leaflet itself is vendored
});

app.UseCanonicalHost(siteInfo.PublicUrl);   // 308 to the public address from any other host name; off when empty
```

- **`ContentSecurityPolicy`** starts strict (`'self'` for scripts, styles, fonts, images plus `data:`, connections; `frame-ancestors 'none'`; forms post here) and
  only grows by `Allow(directive, sources)`. Put the reason beside each call. `Set` replaces a directive.
- **`SecurityHeadersOptions.PerRequest`** is the last word per request (headers and a copy of the CSP), for a rule that depends on the page, such as a page the
  site frames itself.
- **`CanonicalHostRedirect`** exempts `/healthz`, `/version` and `/api` by default (pass your own list), never moves a POST or a live connection, and never redirects
  localhost.
