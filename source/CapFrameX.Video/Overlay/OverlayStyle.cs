using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;

namespace CapFrameX.Video.Overlay;

/// <summary>
/// HUD panel anchor inside the frame (fps-analyzer's 9 position codes).
/// </summary>
public enum HudPosition
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>
/// The 9 position codes shared with fps-analyzer's overlay style dict.
/// </summary>
public static class HudPositionCode
{
    /// <summary>
    /// Parse a position code. Case-sensitive, like the reference
    /// (<c>pos in POSITIONS</c>): anything that is not one of the 9 lowercase
    /// codes — including non-strings — falls back to "br".
    /// </summary>
    public static HudPosition Parse(object? value)
    {
        return (value as string) switch
        {
            "tl" => HudPosition.TopLeft,
            "t" => HudPosition.Top,
            "tr" => HudPosition.TopRight,
            "l" => HudPosition.Left,
            "c" => HudPosition.Center,
            "r" => HudPosition.Right,
            "bl" => HudPosition.BottomLeft,
            "b" => HudPosition.Bottom,
            "br" => HudPosition.BottomRight,
            _ => HudPosition.BottomRight,
        };
    }

    public static string Code(HudPosition position)
    {
        return position switch
        {
            HudPosition.TopLeft => "tl",
            HudPosition.Top => "t",
            HudPosition.TopRight => "tr",
            HudPosition.Left => "l",
            HudPosition.Center => "c",
            HudPosition.Right => "r",
            HudPosition.BottomLeft => "bl",
            HudPosition.Bottom => "b",
            HudPosition.BottomRight => "br",
            _ => "br",
        };
    }
}

/// <summary>
/// Render settings for the FPS HUD overlay. Line-for-line port of
/// fps-analyzer's <c>OverlayStyle</c> (fps_analyzer/overlay.py): same field
/// names and defaults, 6-hex colors without alpha (alpha is applied at
/// render time), the same clamping/normalization in <see cref="FromDictionary"/>,
/// and a lossless JSON round-trip (<c>ToDictionary</c> is <c>asdict</c>).
/// </summary>
public sealed class OverlayStyle
{
    public HudPosition Position { get; set; } = HudPosition.BottomRight;

    /// <summary>Panel-to-frame-edge margin in px at base scale (clamped 0..200).</summary>
    public double Margin { get; set; } = 16.0;

    /// <summary>Scales panel size AND fonts (clamped 0.5..3.0).</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>"" = built-in face (Aileron subset); else a TrueType family name.</summary>
    public string FontFamily { get; set; } = string.Empty;

    public bool Graph { get; set; } = true;
    public bool Label { get; set; } = true;
    public bool LiveGraph { get; set; } = false;

    // Colors: normalized "#rrggbb" (6 hex, no alpha).
    public string PanelBg { get; set; } = "#0a0c10";
    public string PanelBorder { get; set; } = "#ffffff";
    public string GraphBg { get; set; } = "#181a1e";
    public string FpsText { get; set; } = "#ffffff";
    public string Text { get; set; } = "#c8d0dc";
    public string LabelText { get; set; } = "#aab2be";
    public string CaptionText { get; set; } = "#969eaa";
    public string Line { get; set; } = "#f0f5fa";
    public string Stutter { get; set; } = "#ff4646";
    public string Ref1x { get; set; } = "#00dcff";
    public string Ref2x { get; set; } = "#ffd200";
    public string Ref3x { get; set; } = "#ff5050";

    /// <summary>
    /// Parse a style from a fps-analyzer-style dictionary (analysis JSON).
    /// Mirrors <c>OverlayStyle.from_dict</c>: unknown keys ignored, wrong
    /// types ignored (defaults kept), invalid position → "br", margin clamped
    /// to [0, 200], font_scale to [0.5, 3.0], font_family truncated to 64
    /// chars, colors normalized via <see cref="NormalizeHex"/>.
    /// </summary>
    public static OverlayStyle FromDictionary(IReadOnlyDictionary<string, object> dict)
    {
        if (dict is null) throw new ArgumentNullException(nameof(dict));
        var st = new OverlayStyle();

        st.Position = HudPositionCode.Parse(dict.GetValueOrDefault("position"));

        if (TryGetDouble(dict, "margin", out var margin))
            st.Margin = Math.Clamp(margin, 0.0, 200.0);
        if (TryGetDouble(dict, "font_scale", out var scale))
            st.FontScale = Math.Clamp(scale, 0.5, 3.0);

        if (dict.TryGetValue("font_family", out var ff))
            st.FontFamily = ff is string s ? s[..Math.Min(64, s.Length)] : string.Empty;

        foreach (var (key, set) in new (string, Action<bool>)[]
        {
            ("graph", v => st.Graph = v),
            ("label", v => st.Label = v),
            ("live_graph", v => st.LiveGraph = v),
        })
        {
            if (dict.TryGetValue(key, out var v) && v is bool b) set(b);
        }

        foreach (var (key, get, set) in new (string, Func<string>, Action<string>)[]
        {
            ("panel_bg", () => st.PanelBg, v => st.PanelBg = v),
            ("panel_border", () => st.PanelBorder, v => st.PanelBorder = v),
            ("graph_bg", () => st.GraphBg, v => st.GraphBg = v),
            ("fps_text", () => st.FpsText, v => st.FpsText = v),
            ("text", () => st.Text, v => st.Text = v),
            ("label_text", () => st.LabelText, v => st.LabelText = v),
            ("caption_text", () => st.CaptionText, v => st.CaptionText = v),
            ("line", () => st.Line, v => st.Line = v),
            ("stutter", () => st.Stutter, v => st.Stutter = v),
            ("ref_1x", () => st.Ref1x, v => st.Ref1x = v),
            ("ref_2x", () => st.Ref2x, v => st.Ref2x = v),
            ("ref_3x", () => st.Ref3x, v => st.Ref3x = v),
        })
        {
            if (dict.TryGetValue(key, out var v))
                set(NormalizeHex(v, get()));
        }
        return st;
    }

    /// <summary>Serialize to a fps-analyzer-compatible dictionary (<c>asdict</c>).</summary>
    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            ["position"] = HudPositionCode.Code(Position),
            ["margin"] = Margin,
            ["font_scale"] = FontScale,
            ["font_family"] = FontFamily,
            ["graph"] = Graph,
            ["label"] = Label,
            ["live_graph"] = LiveGraph,
            ["panel_bg"] = PanelBg,
            ["panel_border"] = PanelBorder,
            ["graph_bg"] = GraphBg,
            ["fps_text"] = FpsText,
            ["text"] = Text,
            ["label_text"] = LabelText,
            ["caption_text"] = CaptionText,
            ["line"] = Line,
            ["stutter"] = Stutter,
            ["ref_1x"] = Ref1x,
            ["ref_2x"] = Ref2x,
            ["ref_3x"] = Ref3x,
        };
    }

    /// <summary>
    /// Port of <c>OverlayStyle._hex</c>: normalize a <c>#rrggbb</c> /
    /// <c>rgb</c> string (case-insensitive, '#' optional); return
    /// <paramref name="fallback"/> when invalid.
    /// </summary>
    public static string NormalizeHex(object value, string fallback)
    {
        if (value is not string raw) return fallback;
        var v = raw.Trim().TrimStart('#').ToLowerInvariant();
        if (v.Length == 3)
            v = string.Concat(v.Select(ch => new string(ch, 2)));
        if (v.Length != 6 || v.Any(ch => !Uri.IsHexDigit(ch)))
            return fallback;
        return "#" + v;
    }

    /// <summary>
    /// Port of <c>_Hud._a</c>: expand a normalized "#rrggbb" plus an explicit
    /// alpha into an <see cref="SKColor"/>.
    /// </summary>
    public static SKColor WithAlpha(string hexRgb, int alpha)
    {
        var v = hexRgb.TrimStart('#');
        return new SKColor(
            byte.Parse(v.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(v.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(v.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            (byte)alpha);
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, object> dict,
        string key, out double value)
    {
        value = 0;
        return dict.TryGetValue(key, out var v) && v switch
        {
            int i => Assign(ref value, i),
            long l => Assign(ref value, l),
            float f => Assign(ref value, f),
            double d => Assign(ref value, d),
            _ => false,
        };
    }

    private static bool Assign(ref double value, double d)
    {
        value = d;
        return true;
    }
}
