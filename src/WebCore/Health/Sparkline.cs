/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Net;

namespace WebCore.Health;

/// <summary>A tiny line chart as an inline SVG string, the same for the dashboard and for the standalone reports, so a trend looks the same wherever it is read.</summary>
public static class Sparkline
{
    public static string Svg(IReadOnlyList<double> values, int width = 200, int height = 32, string stroke = "currentColor")
    {
        // NaN and infinity have no place on the chart; leaving them out keeps the markup free of "NaN" and "Infinity".
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count < 2)
        {
            return string.Empty;
        }

        double lo = finite.Min(), hi = finite.Max();
        var points = string.Join(' ', finite.Select((v, i) => string.Create(CultureInfo.InvariantCulture, $"{i * (double)width / (finite.Count - 1):0.#},{height - 4 - VerticalFraction(v, lo, hi) * (height - 8):0.#}")));

        // The stroke is written into an attribute, so it is encoded; "currentColor" passes through unchanged.
        return $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"trend\"><polyline fill=\"none\" stroke=\"{WebUtility.HtmlEncode(stroke)}\" stroke-width=\"2\" stroke-linejoin=\"round\" points=\"{points}\"/></svg>";
    }

    // Halves are used because hi - lo overflows to infinity for values near double.MaxValue. Dividing each end first
    // gives the same fraction as (v - lo) / max(hi - lo, 0.0001) for ordinary numbers, without the overflow.
    private static double VerticalFraction(double v, double lo, double hi) =>
        (v / 2 - lo / 2) / Math.Max(hi / 2 - lo / 2, 0.00005);
}
