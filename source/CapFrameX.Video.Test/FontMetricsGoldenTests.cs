using System;
using System.IO;
using System.Text.Json;
using CapFrameX.Video.Overlay;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace CapFrameX.Video.Test;

/// <summary>
/// Golden check that the bundled Aileron Regular subset (embedded in
/// CapFrameX.Video, copied to TestAssets/fonts for tests) measures the same
/// advance widths / metrics as Pillow's FreeType rasterizer at the HUD sizes
/// used by fps-analyzer.
///
/// Length tolerance is 2.5 px: Pillow measures through FreeType with
/// autohinting, which snaps per-glyph advances to whole pixels (the golden
/// lengths are integers), while Skia measures the raw unhinted advances —
/// at 46 px that accounts for up to ~2 px over a four-glyph string. A wrong
/// font file or a broken size scale would miss this tolerance by far. The
/// compositing E2E (pixel diff with tolerance) is the final arbiter.
/// </summary>
[TestClass]
public sealed class FontMetricsGoldenTests
{
    private static readonly double LengthTolerance = 2.5;
    private static readonly double MetricTolerance = 1.0;

    private static string GoldenDir => Path.Combine("TestAssets", "goldens");

    private static string FontPath =>
        Path.Combine("TestAssets", "fonts", "Aileron-Regular-Subset.ttf");

    [TestMethod]
    public void EmbeddedFontMatchesGoldenSubset()
    {
        // The embedded resource must be byte-identical to the golden font, so
        // whatever the tests measure is exactly what ships in the product.
        var assembly = typeof(OverlayStyle).Assembly;
        using var golden = File.OpenRead(FontPath);
        using var embedded = assembly.GetManifestResourceStream("overlay-font.ttf");
        Assert.IsNotNull(embedded, "overlay-font.ttf embedded resource not found");
        Assert.AreEqual(golden.Length, embedded.Length, "embedded font size mismatch");
        var a = new byte[golden.Length];
        var b = new byte[golden.Length];
        golden.ReadExactly(a);
        embedded.ReadExactly(b);
        CollectionAssert.AreEqual(a, b, "embedded font differs from golden subset");
    }

    [TestMethod]
    public void MetricsMatchPillowAtHudSizes()
    {
        var json = File.ReadAllText(Path.Combine(GoldenDir, "font_metrics.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        using var typeface = SKTypeface.FromFile(FontPath);
        Assert.IsFalse(typeface.IsEmpty, "font loaded as an empty/fake typeface");

        Check(typeface, root, "size46", 46, "60");
        Check(typeface, root, "size46", 46, "60.5");
        Check(typeface, root, "size46", 46, "59.9");
        Check(typeface, root, "size17", 17, "10");
        Check(typeface, root, "size18", 18, "30");
    }

    private static void Check(SKTypeface typeface, JsonElement root, string key,
        int size, string text)
    {
        using var font = new SKFont(typeface, (float)size);
        var sizeEl = root.GetProperty(key);
        double len = font.MeasureText(text, null);
        double expectedLength = sizeEl.GetProperty($"length_{text}").GetDouble();
        Assert.IsTrue(Math.Abs(len - expectedLength) <= LengthTolerance,
            $"size {size} \"{text}\": Skia {len:0.###} vs Pillow {expectedLength:0.###} (tol {LengthTolerance})");

        var m = font.Metrics;
        var expectedMetrics = sizeEl.GetProperty("getmetrics");
        int pAscent = expectedMetrics[0].GetInt32();
        int pDescent = expectedMetrics[1].GetInt32();
        // Pillow getmetrics() reports (ascent, descent) as positive distances
        // above/below the baseline; Skia's FontMetrics.Ascent is negative.
        Assert.IsTrue(Math.Abs(-m.Ascent - pAscent) <= MetricTolerance,
            $"size {size}: ascent Skia {-m.Ascent:0.###} vs Pillow {pAscent} (tol {MetricTolerance})");
        Assert.IsTrue(Math.Abs(m.Descent - pDescent) <= MetricTolerance,
            $"size {size}: descent Skia {m.Descent:0.###} vs Pillow {pDescent} (tol {MetricTolerance})");
    }
}
