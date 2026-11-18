using System;
using System.Collections.Generic;
using CapFrameX.Video;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CapFrameX.Video.Test;

[TestClass]
public class FpsAnalysisTests
{
    [DataTestMethod]
    [DataRow("29.97", 29.97)]
    [DataRow("60", 60.0)]
    [DataRow("119.88", 119.88)]
    [DataRow("0.5", 0.5)]
    public void ParseNominalFps_HandlesCommonRates(string input, double expected)
    {
        Assert.AreEqual(expected, FpsAnalysis.ParseNominalFps(input), 1e-9);
    }

    [TestMethod]
    public void ParseNominalFps_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => FpsAnalysis.ParseNominalFps(null!));
    }

    [TestMethod]
    public void FpsFromFrameCount_DividesCountByDuration()
    {
        Assert.AreEqual(30.0, FpsAnalysis.FpsFromFrameCount(3000, 100.0), 1e-9);
    }

    [TestMethod]
    public void FpsFromFrameCount_ThrowsOnZeroDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FpsAnalysis.FpsFromFrameCount(10, 0.0));
    }

    [TestMethod]
    public void FpsFromFrameCount_ThrowsOnNegativeCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FpsAnalysis.FpsFromFrameCount(-1, 1.0));
    }

    [TestMethod]
    public void FrameIntervals_ComputesDeltas()
    {
        var intervals = FpsAnalysis.FrameIntervals(new[] { 0.0, 0.05, 0.1 });
        Assert.AreEqual(2, intervals.Length);
        Assert.AreEqual(0.05, intervals[0], 1e-9);
        Assert.AreEqual(0.05, intervals[1], 1e-9);
    }

    [TestMethod]
    public void FrameIntervals_EmptyForSingleOrNoFrames()
    {
        Assert.AreEqual(0, FpsAnalysis.FrameIntervals(new double[0]).Length);
        Assert.AreEqual(0, FpsAnalysis.FrameIntervals(new[] { 1.0 }).Length);
    }

    [TestMethod]
    public void EstimateBaseIntervalMs_FindsDenseClusterAroundLowPercentile()
    {
        // ~10% of frames are 2x slow (frame drops); the base must stay at 16.67 ms.
        var intervals = new List<double>();
        for (var i = 0; i < 90; i++) intervals.Add(16.6667);
        for (var i = 0; i < 10; i++) intervals.Add(33.3333);
        Assert.AreEqual(16.6667, FpsAnalysis.EstimateBaseIntervalMs(intervals), 0.01);
    }

    [TestMethod]
    public void EstimateBaseIntervalMs_MedianForShortInput()
    {
        Assert.AreEqual(30.0, FpsAnalysis.EstimateBaseIntervalMs(new double[] { 20, 30, 40 }), 1e-9);
    }

    [TestMethod]
    public void ClassifyIntervals_SnapsToNearestMultiple()
    {
        var (mult, ok) = FpsAnalysis.ClassifyIntervals(
            new double[] { 16.6, 33.4, 66.9, 40.0 }, 16.6667);
        Assert.AreEqual(1, mult[0]); Assert.IsTrue(ok[0]);
        Assert.AreEqual(2, mult[1]); Assert.IsTrue(ok[1]);
        Assert.AreEqual(4, mult[2]); Assert.IsTrue(ok[2]);
        // 40 ms is not within 12% of any multiple of 16.6667 ms.
        Assert.IsFalse(ok[3]);
    }

    [TestMethod]
    public void IsCfr_TrueForUniform_FalseForScattered()
    {
        var cfr = new List<double>();
        for (var i = 0; i < 100; i++) cfr.Add(16.6667 * (1.0 + 0.001 * (i % 3 - 1)));
        Assert.IsTrue(FpsAnalysis.IsCfr(cfr));

        var vfr = new List<double>();
        for (var i = 0; i < 100; i++) vfr.Add(16.6667 * (0.5 + (i % 10) * 0.1));
        Assert.IsFalse(FpsAnalysis.IsCfr(vfr));
    }

    [TestMethod]
    public void PerSecondFrameCounts_FullSecondsPlusTrailingPartialBin()
    {
        var times = new List<double>();
        for (var i = 0; i < 36; i++) times.Add(i / 10.0); // 0.0 .. 3.5 s
        Assert.AreSequenceEqual(new[] { 10, 10, 10, 6 }, FpsAnalysis.PerSecondFrameCounts(times));
    }

    [TestMethod]
    public void PerSecondFrameCounts_ClosesLastBinAtExactSecondBoundary()
    {
        var times = new List<double>();
        for (var i = 0; i < 31; i++) times.Add(i / 10.0); // 0.0 .. 3.0 s
        Assert.AreSequenceEqual(new[] { 10, 10, 11 }, FpsAnalysis.PerSecondFrameCounts(times));
    }
}
