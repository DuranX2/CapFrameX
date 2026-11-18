using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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

    /// <summary>
    /// Estimates the base frame interval (milliseconds) from per-frame intervals:
    /// the median of the dense cluster within 15% of the 10th percentile (port of
    /// fps-analyzer's estimate_base). Returns the plain median when fewer than
    /// eight positive intervals are given.
    /// </summary>
    public static double EstimateBaseIntervalMs(IReadOnlyList<double> intervalsMs)
    {
        ArgumentNullException.ThrowIfNull(intervalsMs);
        var values = new List<double>();
        foreach (var v in intervalsMs)
            if (double.IsFinite(v) && v > 0)
                values.Add(v);
        if (values.Count == 0)
            throw new ArgumentException("No positive intervals.", nameof(intervalsMs));

        if (values.Count < 8)
            return Percentile(values, 0.5);

        var seed = Percentile(values, 0.10);
        var band = new List<double>();
        foreach (var v in values)
            if (Math.Abs(v - seed) <= 0.15 * seed)
                band.Add(v);
        return band.Count > 0 ? Percentile(band, 0.5) : seed;
    }

    /// <summary>
    /// Snaps each interval to a multiple of <paramref name="baseMs"/> (clamped to
    /// 1..16, port of fps-analyzer's classify) and reports which intervals fit
    /// within 12% of an exact multiple.
    /// </summary>
    public static (int[] Multiples, bool[] InTolerance) ClassifyIntervals(
        IReadOnlyList<double> intervalsMs, double baseMs)
    {
        ArgumentNullException.ThrowIfNull(intervalsMs);
        if (baseMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(baseMs));

        var multiples = new int[intervalsMs.Count];
        var inTolerance = new bool[intervalsMs.Count];
        for (var i = 0; i < intervalsMs.Count; i++)
        {
            var d = intervalsMs[i];
            if (!double.IsFinite(d) || d <= 0)
                continue;
            var m = (int)Math.Round(d / baseMs, MidpointRounding.ToEven);
            if (m < 1) m = 1;
            if (m > 16) m = 16;
            multiples[i] = m;
            inTolerance[i] = Math.Abs(d - m * baseMs) <= 0.12 * baseMs;
        }
        return (multiples, inTolerance);
    }

    /// <summary>
    /// True when the intervals look constant-frame-rate: their coefficient of
    /// variation is below 0.02 (port of fps-analyzer's is_cfr check).
    /// </summary>
    public static bool IsCfr(IReadOnlyList<double> intervalsMs)
        => IntervalCoefficientOfVariation(intervalsMs) < 0.02;

    /// <summary>
    /// Coefficient of variation (population standard deviation divided by the
    /// mean) of the finite intervals; 1.0 when the mean is not positive.
    /// </summary>
    public static double IntervalCoefficientOfVariation(IReadOnlyList<double> intervalsMs)
    {
        ArgumentNullException.ThrowIfNull(intervalsMs);
        var finite = new List<double>();
        foreach (var v in intervalsMs)
            if (double.IsFinite(v))
                finite.Add(v);
        if (finite.Count == 0)
            return 1.0;

        var mean = finite.Average();
        if (mean <= 0)
            return 1.0;

        var sq = 0.0;
        foreach (var v in finite)
            sq += (v - mean) * (v - mean);
        return Math.Sqrt(sq / finite.Count) / mean;
    }

    /// <summary>
    /// Per-1-second frame counts (DF-style fps-over-time, port of fps-analyzer's
    /// per_second_unique): full-second bins from t=0 plus one trailing bin for a
    /// partial final second, so no frame is silently dropped.
    /// </summary>
    public static int[] PerSecondFrameCounts(IReadOnlyList<double> timesSeconds)
    {
        ArgumentNullException.ThrowIfNull(timesSeconds);
        if (timesSeconds.Count < 2)
            return Array.Empty<int>();

        var tmax = 0.0;
        foreach (var t in timesSeconds)
            if (t > tmax)
                tmax = t;

        var nb = Math.Max(1, (int)Math.Floor(tmax + 1e-9));
        var binCount = nb + (tmax > nb + 1e-6 ? 1 : 0);
        var counts = new int[binCount];
        foreach (var t in timesSeconds)
        {
            var b = (int)Math.Floor(t + 1e-9);
            if (b >= binCount) b = binCount - 1;
            if (b < 0) b = 0;
            counts[b]++;
        }
        return counts;
    }

    /// <summary>
    /// Linearly interpolated percentile (numpy's default) of an unsorted value
    /// list; <paramref name="q"/> in [0, 1].
    /// </summary>
    private static double Percentile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.ToList();
        sorted.Sort();
        var index = (sorted.Count - 1) * q;
        var lo = (int)Math.Floor(index);
        var hi = (int)Math.Ceiling(index);
        if (lo == hi || hi >= sorted.Count)
            return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
    }
}
