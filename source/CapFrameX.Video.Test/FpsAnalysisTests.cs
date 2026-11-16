using System;
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
}
