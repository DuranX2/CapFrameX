using System;
using System.Collections.Generic;

namespace CapFrameX.Video.Overlay;

/// <summary>
/// HUD metric stage results for one clip: the per-frame displayed fps /
/// frametime values plus stutter flags, exactly as fps-analyzer's
/// <c>render_overlay</c> computes them before drawing.
/// </summary>
public sealed record HudMetricsResult(
    double BaseMs,
    double BaseFps,
    int Window,
    int K,
    int R,
    bool[] StutterSet,
    bool[] StutterBlockFlags,
    double[] DtAll,
    double[] FpsDisplay,
    double[] DtDisplay);

/// <summary>
/// Port of the metric stage of fps-analyzer's <c>render_overlay</c>
/// (fps_analyzer/overlay.py). Operates on per-frame presentation times in
/// milliseconds (decode order, as ffprobe reports them) and reproduces the
/// Python float64 math operation-for-operation:
/// <list type="bullet">
/// <item>base interval = declared frame rate when known, else median(dt);</item>
/// <item>stutter frame = frametime &gt;= 1.9 * base;</item>
/// <item>displayed values = averages over a K-frame window anchored at each
/// R-frame block boundary (the "smoothed" HUD numbers).</item>
/// </list>
/// </summary>
public static class HudMetrics
{
    /// <summary>fps-analyzer overlay.py STUTTER_MULT.</summary>
    public const double StutterMult = 1.9;

    /// <summary>
    /// Compute the HUD metrics for a clip.
    /// </summary>
    /// <param name="ptsMs">
    /// Presentation time of every decoded frame, ms, decode order.
    /// </param>
    /// <param name="declaredFps">
    /// Container-declared frame rate (r_frame_rate, falling back to
    /// avg_frame_rate — fps-analyzer probe semantics), or 0 when unknown.
    /// </param>
    public static HudMetricsResult Compute(IReadOnlyList<double> ptsMs, double declaredFps)
    {
        ArgumentNullException.ThrowIfNull(ptsMs);
        int n = ptsMs.Count;
        if (n < 2)
            throw new ArgumentException("need at least two frames", nameof(ptsMs));

        // dt = diff(ts); dt_all[0] = dt[0], dt_all[1:] = dt
        var dtAll = new double[n];
        dtAll[0] = ptsMs[1] - ptsMs[0];
        for (int i = 1; i < n; i++) dtAll[i] = ptsMs[i] - ptsMs[i - 1];

        // base = declared ? 1000/declared : median(dt)
        // (np.median semantics: odd → middle, even → mean of the two middles)
        double baseMs;
        if (declaredFps > 0)
        {
            baseMs = 1000.0 / declaredFps;
        }
        else
        {
            var dt = new double[n - 1];
            for (int i = 1; i < n; i++) dt[i - 1] = ptsMs[i] - ptsMs[i - 1];
            Array.Sort(dt);
            int m = dt.Length / 2;
            baseMs = dt.Length % 2 == 1 ? dt[m] : (dt[m - 1] + dt[m]) / 2.0;
        }

        double baseFps = 1000.0 / baseMs;
        int win = Math.Min(300, Math.Max(60, (int)Math.Round(3.0 * baseFps)));

        // stutter_set = { i : dt_all[i] >= 1.9 * base }
        var stutterSet = new bool[n];
        double threshold = StutterMult * baseMs;
        for (int i = 0; i < n; i++) stutterSet[i] = dtAll[i] >= threshold;

        int k = Math.Max(4, (int)Math.Round(0.5 * baseFps));
        int r = Math.Max(2, (int)Math.Round(0.25 * baseFps));
        int nBlk = n / r + 1;
        var stutterBlk = new bool[nBlk];
        for (int i = 0; i < n; i++)
        {
            if (stutterSet[i]) stutterBlk[i / r] = true;
        }

        // cs = [0] + cumsum(dt_all)   (float64, left-to-right, like np.cumsum)
        var cs = new double[n + 1];
        for (int i = 0; i < n; i++) cs[i + 1] = cs[i] + dtAll[i];

        var fpsDisp = new double[n];
        var dtDisp = new double[n];
        for (int i = 0; i < n; i++)
        {
            int anchor = (i / r) * r;
            int lo = Math.Max(0, anchor - k + 1);
            double sums = cs[anchor + 1] - cs[lo];
            int cnts = anchor + 1 - lo;
            fpsDisp[i] = 1000.0 * cnts / sums;
            dtDisp[i] = sums / cnts;
        }

        return new HudMetricsResult(baseMs, baseFps, win, k, r, stutterSet, stutterBlk,
            dtAll, fpsDisp, dtDisp);
    }
}
