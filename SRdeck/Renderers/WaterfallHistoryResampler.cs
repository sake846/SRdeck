using System;

namespace SRdeck.Renderers;

internal static class WaterfallHistoryResampler
{
    /// <summary>
    /// Reprojects top-to-bottom (newest-to-oldest) waterfall rows onto a new
    /// time scale. History outside the new time range is discarded, while
    /// newly exposed older time is filled with the supplied background color.
    /// </summary>
    public static uint[] ResampleForTimeScale(
        uint[] source,
        int width,
        int height,
        double oldRowDurationMs,
        double newRowDurationMs,
        uint backgroundColor)
    {
        int length = checked(width * height);
        var result = new uint[length];
        if (backgroundColor != 0) Array.Fill(result, backgroundColor);

        if (source.Length < length || width <= 0 || height <= 0 ||
            !double.IsFinite(oldRowDurationMs) || oldRowDurationMs <= 0.0 ||
            !double.IsFinite(newRowDurationMs) || newRowDurationMs <= 0.0)
        {
            return result;
        }

        double rowScale = newRowDurationMs / oldRowDurationMs;
        for (int y = 0; y < height; y++)
        {
            // Map row centers by their age so the newest data stays at the top.
            double sourceY = ((y + 0.5) * rowScale) - 0.5;
            int sourceRow = (int)Math.Round(sourceY, MidpointRounding.AwayFromZero);
            if ((uint)sourceRow >= (uint)height) continue;

            Array.Copy(source, sourceRow * width, result, y * width, width);
        }

        return result;
    }
}
