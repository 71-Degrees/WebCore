/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;

namespace WebCore.Health;

/// <summary>A tiny line chart as an inline SVG string, the same for the dashboard and for the standalone reports, so a trend looks the same wherever it is read.</summary>
public static class Sparkline
{
    public static string Svg(IReadOnlyList<double> values, int width = 200, int height = 32, string stroke = "currentColor")
    {
        if (values.Count < 2)
        {
            return string.Empty;
        }

        double lo = values.Min(), hi = values.Max(), span = Math.Max(hi - lo, 0.0001);
        var points = string.Join(' ', values.Select((v, i) => string.Create(CultureInfo.InvariantCulture, $"{i * (double)width / (values.Count - 1):0.#},{height - 4 - (v - lo) / span * (height - 8):0.#}")));
        return $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"trend\"><polyline fill=\"none\" stroke=\"{stroke}\" stroke-width=\"2\" stroke-linejoin=\"round\" points=\"{points}\"/></svg>";
    }
}
