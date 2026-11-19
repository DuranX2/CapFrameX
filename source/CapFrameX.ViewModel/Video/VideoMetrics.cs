using System;
using System.Collections.Generic;
using System.Linq;
using CapFrameX.Video;
using CapFrameX.Video.Contracts;

namespace CapFrameX.ViewModel.Video
{
    /// <summary>
    /// One bar of the fps-over-time chart: a whole-second bin with its frame
    /// count and a normalized height (0..1) for the view.
    /// </summary>
    public sealed record BarDatum(int Second, int Frames, double Height)
    {
        /// <summary>Label shown under the bar ("1", "2", ...).</summary>
        public string Label => Second.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Height in pixels for a 100 px tall chart area.</summary>
        public double HeightPx => Height * 100.0;
    }

    /// <summary>
    /// DF-style frame-pacing metrics computed from decoded frame timestamps:
    /// nominal vs. measured fps, the dense-cluster base interval, CFR/VFR
    /// classification, jank (intervals off every multiple of the base) and
    /// per-second frame counts.
    /// </summary>
    public sealed record VideoMetrics
    {
        /// <summary>Frames that carried a valid presentation timestamp.</summary>
        public required int FrameCount { get; init; }

        /// <summary>Time span of the decoded frames in seconds (last - first).</summary>
        public required double DurationSeconds { get; init; }

        /// <summary>Container-declared rate (avg_frame_rate); 0 when unknown.</summary>
        public double NominalFps { get; init; }

        /// <summary>Measured rate: frame count over the decoded span.</summary>
        public required double MeasuredFps { get; init; }

        /// <summary>Estimated base frame interval in milliseconds (dense-cluster median).</summary>
        public required double BaseIntervalMs { get; init; }

        /// <summary>True when the intervals look constant-frame-rate (CoV &lt; 0.02).</summary>
        public required bool IsCfr { get; init; }

        /// <summary>Intervals that are not within tolerance of any multiple of the base.</summary>
        public int JankCount { get; init; }

        public double MinIntervalMs { get; init; }
        public double AvgIntervalMs { get; init; }
        public double MaxIntervalMs { get; init; }

        /// <summary>Fps-over-time bins (one per full second, plus a trailing partial).</summary>
        public IReadOnlyList<int> PerSecondFrameCounts { get; init; } = Array.Empty<int>();

        /// <summary>Pre-shaped bins for the view (heights normalized to the tallest bin).</summary>
        public IReadOnlyList<BarDatum> FpsOverTime { get; init; } = Array.Empty<BarDatum>();
    }

    /// <summary>
    /// Pure computation from (probed info, decoded timestamps) to metrics.
    /// Stays allocation-light and unit-testable without a decoder.
    /// </summary>
    public static class VideoMetricsBuilder
    {
        public static VideoMetrics Build(VideoInfo info, IReadOnlyList<FrameTimestamp> frames)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(frames);

            var seconds = new double[frames.Count];
            for (var i = 0; i < frames.Count; i++)
                seconds[i] = frames[i].PresentationSeconds;

            var intervalsMs = FpsAnalysis.FrameIntervals(seconds)
                .Select(v => v * 1000.0)
                .ToList();

            var positive = new List<double>();
            foreach (var v in intervalsMs)
                if (double.IsFinite(v) && v > 0)
                    positive.Add(v);

            double baseMs = positive.Count > 0 ? FpsAnalysis.EstimateBaseIntervalMs(positive) : 0.0;

            int jank = 0;
            if (baseMs > 0)
            {
                var (_, inTolerance) = FpsAnalysis.ClassifyIntervals(positive, baseMs);
                foreach (var ok in inTolerance)
                    if (!ok)
                        jank++;
            }

            var duration = frames.Count > 0 ? seconds[^1] - seconds[0] : 0.0;
            if (duration < 0)
                duration = 0.0;

            var perSecond = FpsAnalysis.PerSecondFrameCounts(seconds);
            int max = 1;
            foreach (var c in perSecond)
                if (c > max)
                    max = c;

            var bars = new List<BarDatum>(perSecond.Length);
            for (var i = 0; i < perSecond.Length; i++)
                bars.Add(new BarDatum(i, perSecond[i], perSecond[i] / (double)max));

            return new VideoMetrics
            {
                FrameCount = frames.Count,
                DurationSeconds = duration,
                NominalFps = info.AverageFps,
                MeasuredFps = frames.Count > 1 && duration > 0
                    ? FpsAnalysis.FpsFromFrameCount(frames.Count, duration)
                    : 0.0,
                BaseIntervalMs = baseMs,
                IsCfr = baseMs > 0 && FpsAnalysis.IsCfr(positive),
                JankCount = jank,
                MinIntervalMs = positive.Count > 0 ? positive.Min() : 0.0,
                AvgIntervalMs = positive.Count > 0 ? positive.Average() : 0.0,
                MaxIntervalMs = positive.Count > 0 ? positive.Max() : 0.0,
                PerSecondFrameCounts = perSecond,
                FpsOverTime = bars,
            };
        }
    }
}
