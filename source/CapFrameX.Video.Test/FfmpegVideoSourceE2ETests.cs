using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CapFrameX.Video;
using CapFrameX.Video.Contracts;
using CapFrameX.Video.Decoding;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CapFrameX.Video.Test;

/// <summary>
/// End-to-end tests that drive <see cref="FfmpegVideoSource"/> against real
/// media files in <c>TestVideos</c> using the locally installed FFmpeg shared
/// libraries (scripts/Get-FfmpegNative.ps1).
/// </summary>
[TestClass]
public class FfmpegVideoSourceE2ETests
{
    private static readonly FfmpegVideoSource Source = new();

    private static string Video(string name) => Path.Combine("TestVideos", name);

    private static List<double> IntervalsMs(IReadOnlyList<FrameTimestamp> frames)
        => frames.Skip(1)
            .Zip(frames.Take(frames.Count - 1))
            .Select(p => (p.First.PresentationSeconds - p.Second.PresentationSeconds) * 1000.0)
            .ToList();

    [ClassInitialize]
    public static void VerifyPrerequisites(TestContext _)
    {
        // Fail with an actionable message instead of a DllNotFoundException.
        foreach (var name in new[] { "clip_30fps.mp4", "clip_vfr.mp4" })
            Assert.IsTrue(File.Exists(Video(name)),
                $"missing test media '{name}' in TestVideos");
        Assert.IsTrue(
            CapFrameX.Video.Decoding.FfmpegNative.ResolveRoot() != null,
            "FFmpeg shared libraries not found; run scripts/Get-FfmpegNative.ps1");
    }

    [TestMethod]
    public async Task CfrClip_TimestampsFollowNominalInterval()
    {
        var frames = await Source.ReadFrameTimestampsAsync(Video("clip_30fps.mp4"));
        Assert.IsTrue(frames.Count is >= 85 and <= 95,
            $"expected ~90 frames for a 3s/30fps clip, got {frames.Count}");

        var intervals = IntervalsMs(frames);
        var baseMs = FpsAnalysis.EstimateBaseIntervalMs(intervals);
        Assert.AreEqual(33.333, baseMs, 1.0,
            $"30fps clip should land near 33.33 ms, got {baseMs}");
        Assert.IsTrue(FpsAnalysis.IsCfr(intervals),
            $"CFR clip classified as VFR (cv={FpsAnalysis.IntervalCoefficientOfVariation(intervals)})");
    }

    [TestMethod]
    public async Task VfrClip_IsClassifiedAsVariableAndStuttersDetected()
    {
        var frames = await Source.ReadFrameTimestampsAsync(Video("clip_vfr.mp4"));
        Assert.IsTrue(frames.Count is >= 85 and <= 95,
            $"expected ~90 frames, got {frames.Count}");

        var intervals = IntervalsMs(frames);
        Assert.IsFalse(FpsAnalysis.IsCfr(intervals),
            $"VFR test clip must not classify as CFR (cv={FpsAnalysis.IntervalCoefficientOfVariation(intervals)})");

        var baseMs = FpsAnalysis.EstimateBaseIntervalMs(intervals);
        Assert.AreEqual(16.667, baseMs, 2.0,
            $"clip is dominated by 60fps frames; base should be near 16.67 ms, got {baseMs}");

        var (mult, ok) = FpsAnalysis.ClassifyIntervals(intervals, baseMs);
        var stutter = mult.Where((m, i) => m >= 2 && ok[i]).Count();
        Assert.IsTrue(stutter > 0, "VFR clip should contain >2x intervals (frame drops)");
    }

    [TestMethod]
    public void GetVideoInfo_ReportsStreamMetadata()
    {
        var info = Source.GetVideoInfo(Video("clip_30fps.mp4"));
        Assert.AreEqual(640, info.Width);
        Assert.AreEqual(360, info.Height);
        Assert.AreEqual(0, info.StreamIndex);
        Assert.IsTrue(info.DurationSeconds is > 2.5 and < 3.5,
            $"expected ~3s duration, got {info.DurationSeconds}");
        Assert.IsTrue(info.AverageFps is > 29.0 and < 31.0,
            $"expected ~30fps declared rate, got {info.AverageFps}");
        Assert.IsFalse(string.IsNullOrEmpty(info.CodecName));
        Assert.AreEqual("yuv420p", info.PixelFormat);
    }
}
