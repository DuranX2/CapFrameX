using System.Collections.Generic;
using System.Text.Json;
using CapFrameX.Video.Overlay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CapFrameX.Video.Test;

/// <summary>
/// Golden check of the <see cref="OverlayStyle"/> port against
/// fps-analyzer's OverlayStyle semantics (defaults, from_dict clamping,
/// hex normalization, asdict round-trip).
/// </summary>
[TestClass]
public sealed class OverlayStyleTests
{
    [TestMethod]
    public void DefaultsMatchFpsAnalyzer()
    {
        var s = new OverlayStyle();
        Assert.AreEqual(HudPosition.BottomRight, s.Position);
        Assert.AreEqual(16.0, s.Margin);
        Assert.AreEqual(1.0, s.FontScale);
        Assert.AreEqual(string.Empty, s.FontFamily);
        Assert.IsTrue(s.Graph);
        Assert.IsTrue(s.Label);
        Assert.IsFalse(s.LiveGraph);
        Assert.AreEqual("#0a0c10", s.PanelBg);
        Assert.AreEqual("#ffffff", s.PanelBorder);
        Assert.AreEqual("#181a1e", s.GraphBg);
        Assert.AreEqual("#ffffff", s.FpsText);
        Assert.AreEqual("#c8d0dc", s.Text);
        Assert.AreEqual("#aab2be", s.LabelText);
        Assert.AreEqual("#969eaa", s.CaptionText);
        Assert.AreEqual("#f0f5fa", s.Line);
        Assert.AreEqual("#ff4646", s.Stutter);
        Assert.AreEqual("#00dcff", s.Ref1x);
        Assert.AreEqual("#ffd200", s.Ref2x);
        Assert.AreEqual("#ff5050", s.Ref3x);
    }

    [TestMethod]
    public void ToDictionaryRoundTripIsLossless()
    {
        var s = new OverlayStyle
        {
            Position = HudPosition.TopLeft,
            Margin = 24.5,
            FontScale = 1.25,
            FontFamily = "Consolas",
            Graph = false,
            Label = false,
            LiveGraph = true,
            PanelBg = "#101010",
            FpsText = "#00ff00",
            Line = "#808080",
            Stutter = "#ff0000",
            Ref1x = "#0000ff",
            Ref2x = "#000000",
            Ref3x = "#ffffff",
        };
        var roundTripped = OverlayStyle.FromDictionary(s.ToDictionary());
        Assert.AreEqual(
            JsonSerializer.Serialize(s.ToDictionary()),
            JsonSerializer.Serialize(roundTripped.ToDictionary()),
            "style dict must round-trip losslessly");
    }

    [TestMethod]
    public void FromDictionaryKeepsDefaultsForUnknownKeysAndWrongTypes()
    {
        var s = OverlayStyle.FromDictionary(new Dictionary<string, object>
        {
            ["position"] = 42,           // not a string → "br"
            ["margin"] = "big",          // not a number → default
            ["font_scale"] = null,
            ["font_family"] = 7,         // not a string → ""
            ["graph"] = "yes",           // not a bool → default
            ["label"] = 1,
            ["live_graph"] = "nope",
            ["panel_bg"] = 123,          // not a string → default
            ["unknown_key"] = "whatever",
        });
        var d = new OverlayStyle();
        Assert.AreEqual(d.Position, s.Position);
        Assert.AreEqual(d.Margin, s.Margin);
        Assert.AreEqual(d.FontScale, s.FontScale);
        Assert.AreEqual(d.FontFamily, s.FontFamily);
        Assert.AreEqual(d.Graph, s.Graph);
        Assert.AreEqual(d.Label, s.Label);
        Assert.AreEqual(d.LiveGraph, s.LiveGraph);
        Assert.AreEqual(d.PanelBg, s.PanelBg);
    }

    [TestMethod]
    public void FromDictionaryClampsAndNormalizes()
    {
        var s = OverlayStyle.FromDictionary(new Dictionary<string, object>
        {
            ["position"] = "TR",          // case-sensitive like `pos in POSITIONS` → "br"
            ["margin"] = -5.0,            // → 0.0
            ["font_scale"] = 9.0,         // → 3.0
            ["font_family"] = new string('a', 80),  // → 64 chars
            ["graph"] = false,
            ["label"] = false,
            ["live_graph"] = true,
            ["panel_bg"] = "ABC",         // 3-hex → "#aabbcc"
            ["fps_text"] = "ff8000",      // no '#' → "#ff8000"
            ["line"] = "zzzzzz",          // invalid → default kept
            ["ref_1x"] = "#ABCDEF",       // → "#abcdef"
        });
        Assert.AreEqual(HudPosition.BottomRight, s.Position);
        Assert.AreEqual(0.0, s.Margin);
        Assert.AreEqual(3.0, s.FontScale);
        Assert.AreEqual(64, s.FontFamily.Length);
        Assert.IsFalse(s.Graph);
        Assert.IsFalse(s.Label);
        Assert.IsTrue(s.LiveGraph);
        Assert.AreEqual("#aabbcc", s.PanelBg);
        Assert.AreEqual("#ff8000", s.FpsText);
        Assert.AreEqual("#f0f5fa", s.Line);
        Assert.AreEqual("#abcdef", s.Ref1x);
    }

    [TestMethod]
    public void NormalizeHexMatchesReference()
    {
        Assert.AreEqual("#aabbcc", OverlayStyle.NormalizeHex("abc", "#000000"));
        Assert.AreEqual("#aabbcc", OverlayStyle.NormalizeHex("ABC", "#000000"));
        Assert.AreEqual("#aabbcc", OverlayStyle.NormalizeHex("#ABC", "#000000"));
        Assert.AreEqual("#ff8000", OverlayStyle.NormalizeHex("ff8000", "#000000"));
        Assert.AreEqual("#ff8000", OverlayStyle.NormalizeHex("#FF8000", "#000000"));
        Assert.AreEqual("#010203", OverlayStyle.NormalizeHex("  #010203  ", "#000000"));

        // Invalid inputs fall back, like the reference _hex.
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex("zz", "#000000"));
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex("#12345", "#000000"));
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex("#1234567", "#000000"));
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex(null, "#000000"));
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex(5, "#000000"));
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex("", "#000000"));
        Assert.AreEqual("#000000", OverlayStyle.NormalizeHex("  ", "#000000"));
    }

    [TestMethod]
    public void WithAlphaBuildsSkColor()
    {
        var c = OverlayStyle.WithAlpha("#ff8000", 128);
        Assert.AreEqual(255, c.Red);
        Assert.AreEqual(128, c.Green);
        Assert.AreEqual(0, c.Blue);
        Assert.AreEqual(128, c.Alpha);
    }
}
