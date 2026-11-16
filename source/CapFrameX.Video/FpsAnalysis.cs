using System;
using System.Collections.Generic;
using System.Globalization;

namespace CapFrameX.Video;

/// <summary>
/// Pure frame-timing math. The analysis algorithms (nominal fps, intervals,
/// jank/rolling classification) are being ported from the fps-analyzer project;
/// everything here must stay allocation-light and unit-testable without a decoder.
/// </summary>
public static class FpsAnalysis
{
    /// <summary>
    /// Parses a nominal frame rate string (e.g. "29.97", "60", "119.88") using invariant culture.
    /// </summary>
    public static double ParseNominalFps(string fps)
    {
        ArgumentNullException.ThrowIfNull(fps);
        return double.Parse(fps, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Computes fps from a frame count over a duration. Guards against zero/negative duration.
    /// </summary>
    public static double FpsFromFrameCount(int frameCount, double durationSeconds)
    {
        if (frameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        return frameCount / durationSeconds;
    }

    /// <summary>
    /// Computes per-frame intervals (in seconds) from an ordered list of presentation timestamps.
    /// Returns an empty array for fewer than two frames.
    /// </summary>
    public static double[] FrameIntervals(IReadOnlyList<double> presentationSeconds)
    {
        ArgumentNullException.ThrowIfNull(presentationSeconds);
        var intervals = new double[Math.Max(0, presentationSeconds.Count - 1)];
        for (var i = 0; i < intervals.Length; i++)
        {
            intervals[i] = presentationSeconds[i + 1] - presentationSeconds[i];
        }

        return intervals;
    }
}
