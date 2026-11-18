using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace CapFrameX.Video.Overlay;

/// <summary>
/// SkiaSharp port of fps-analyzer's <c>_Hud</c> (fps_analyzer/overlay.py):
/// precomputes the static panel (rounded-rect background, label, graph area,
/// 1x/2x/3x reference lines, caption) and renders the per-frame variable
/// parts (FPS number, frametime readout, frametime graph line).
/// </summary>
/// <remarks>
/// The reference draws with Pillow (unantialiased) into an RGBA array and
/// alpha-blends the panel onto the video frame; this port draws with Skia
/// (antialiased) into an Rgba8888/premul bitmap and returns the raw
/// premultiplied pixels. The two pipelines agree within a pixel in the
/// opaque panel interior; the E2E burn test compares full frames with a
/// small per-pixel tolerance.
/// Text placement mirrors Pillow's top-left anchor: baseline = y + ascent,
/// where ascent is the whole-font (FreeType) ascent, negative in Skia.
/// </remarks>
public sealed class HudRenderer : IDisposable
{
    private readonly OverlayStyle _style;
    private readonly double _s;
    private readonly double _baseMs;
    private readonly double _baseFps;
    private readonly int _win;
    private readonly int _block;
    private readonly bool _liveGraph;
    private readonly bool[] _stutterSet;
    private readonly bool[]? _stutterBlock;
    private readonly double[] _fpsDisp;
    private readonly double[] _dtDisp;
    private SKTypeface? _typeface;
    private readonly float _sizeBig;
    private readonly float _sizeMid;
    private readonly float _sizeSm;
    private readonly bool _ownsTypeface;
    private int _gx0, _gx1, _gy0, _gy1;
    private readonly double _yMax;
    private static SKTypeface? _builtinFont;
    private static readonly object BuiltinLock = new();

    /// <summary>Panel width in px (already scaled).</summary>
    public int PanelWidth { get; }

    /// <summary>Panel height in px (already scaled).</summary>
    public int PanelHeight { get; }

    /// <summary>Panel top-left x in frame coordinates.</summary>
    public int X0 { get; }

    /// <summary>Panel top-left y in frame coordinates.</summary>
    public int Y0 { get; }

    /// <param name="width">Frame width (px).</param>
    /// <param name="height">Frame height (px).</param>
    /// <param name="baseMs">Base frametime in ms.</param>
    /// <param name="baseFps">1000 / baseMs.</param>
    /// <param name="win">Graph window in frames.</param>
    /// <param name="stutterSet">Per-frame stutter flags.</param>
    /// <param name="fpsDisp">Per-frame displayed fps (block-quantized).</param>
    /// <param name="dtDisp">Per-frame displayed frametime ms (block-quantized).</param>
    /// <param name="block">Text/graph refresh period in frames (R).</param>
    /// <param name="liveGraph">Scroll the graph per frame instead of stepping.</param>
    /// <param name="stutterBlock">Block-quantized stutter colour flags.</param>
    /// <param name="style">HUD appearance.</param>
    public HudRenderer(int width, int height, double baseMs, double baseFps, int win,
        bool[] stutterSet, double[] fpsDisp, double[] dtDisp,
        int block = 1, bool liveGraph = false, bool[]? stutterBlock = null,
        OverlayStyle? style = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("frame size must be positive", nameof(width));
        ArgumentNullException.ThrowIfNull(stutterSet);
        ArgumentNullException.ThrowIfNull(fpsDisp);
        ArgumentNullException.ThrowIfNull(dtDisp);
        _style = style ?? new OverlayStyle();
        _s = (width <= 1440 ? 1.0 : Math.Min(1.6, width / 1280.0)) * _style.FontScale;
        PanelWidth = (int)(390 * _s);
        PanelHeight = (int)((_style.Graph ? 216.0 : 108.0) * _s);
        _win = win;
        _block = Math.Max(1, block);
        _liveGraph = liveGraph;
        _stutterSet = stutterSet;
        _stutterBlock = stutterBlock;
        _fpsDisp = fpsDisp;
        _dtDisp = dtDisp;
        _baseMs = baseMs;
        _baseFps = baseFps;

        // position: anchor from the style, margin in scaled px --------------
        // (Python checks position.endswith("l"/"r") / startswith("t"/"b") on
        // the 9 codes; the same partition as an enum match:)
        var pos = _style.Position;
        bool left = pos is HudPosition.TopLeft or HudPosition.Left or HudPosition.BottomLeft;
        bool right = pos is HudPosition.TopRight or HudPosition.Right or HudPosition.BottomRight;
        bool top = pos is HudPosition.TopLeft or HudPosition.Top or HudPosition.TopRight;
        bool bottom = pos is HudPosition.BottomLeft or HudPosition.Bottom or HudPosition.BottomRight;
        double m = _style.Margin * _s;
        double x0 = left ? m
            : right ? width - PanelWidth - m
            : (width - (double)PanelWidth) / 2.0;
        double y0 = top ? m
            : bottom ? height - PanelHeight - m
            : (height - (double)PanelHeight) / 2.0;
        X0 = (int)Math.Max(4, x0);
        Y0 = (int)Math.Max(4, y0);

        _sizeBig = (float)(int)(46 * _s);
        _sizeMid = (float)(int)(19 * _s);
        _sizeSm = (float)(int)(13 * _s);
        LoadFont(out _typeface, out _ownsTypeface);

        if (_style.Graph)
        {
            _gx0 = (int)(14 * _s);
            _gx1 = (int)(PanelWidth - 14 * _s);
            _gy0 = (int)(112 * _s);
            _gy1 = (int)(PanelHeight - 26 * _s);
            _yMax = 3.15 * baseMs; // headroom above the 3x line
        }
    }

    /// <summary>
    /// Port of <c>_Hud._font</c>: the style's family when it resolves, else
    /// the built-in Aileron subset embedded in this assembly.
    /// </summary>
    private void LoadFont(out SKTypeface typeface, out bool owns)
    {
        string fam = _style.FontFamily.Trim();
        if (fam.Length > 0)
        {
            var tf = SKTypeface.FromFamilyName(fam);
            if (tf is not null && !tf.IsEmpty)
            {
                typeface = tf;
                owns = true;
                return;
            }
            tf?.Dispose();
        }
        typeface = GetBuiltinFont();
        owns = false; // shared static; never dispose per instance
    }

    private static SKTypeface GetBuiltinFont()
    {
        lock (BuiltinLock)
        {
            if (_builtinFont is not null)
                return _builtinFont;
            var assembly = typeof(OverlayStyle).Assembly;
            using var stream = assembly.GetManifestResourceStream("overlay-font.ttf")
                ?? throw new InvalidOperationException(
                    "embedded overlay font 'overlay-font.ttf' not found");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            using var data = SKData.CreateCopy(bytes);
            _builtinFont = SKTypeface.FromData(data)
                ?? throw new InvalidOperationException("failed to load embedded overlay font");
            return _builtinFont;
        }
    }

    /// <summary>
    /// Port of <c>_Hud.render</c>: render the HUD for frame <paramref name="i"/>.
    /// </summary>
    /// <param name="i">Frame index (decode order).</param>
    /// <param name="dts">Per-frame frametimes in ms (dt_all).</param>
    /// <returns>Premultiplied RGBA bytes, PanelWidth * PanelHeight * 4.</returns>
    public byte[] Render(int i, IReadOnlyList<double> dts)
    {
        ArgumentNullException.ThrowIfNull(dts);
        if (i < 0 || i >= dts.Count)
            throw new ArgumentOutOfRangeException(nameof(i));

        using var bmp = new SKBitmap(PanelWidth, PanelHeight,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(0, 0, 0, 0));
        DrawTemplate(canvas);
        DrawFrame(canvas, i, dts);

        var info = bmp.Info;
        var pixels = new byte[info.RowBytes * PanelHeight];
        Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        if (info.RowBytes != PanelWidth * 4)
        {
            // Defensive: Rgba8888 is unpadded, but keep the contract explicit.
            var tight = new byte[PanelWidth * PanelHeight * 4];
            for (int y = 0; y < PanelHeight; y++)
                Buffer.BlockCopy(pixels, y * info.RowBytes, tight, y * PanelWidth * 4,
                    PanelWidth * 4);
            return tight;
        }
        return pixels;
    }

    // ------------------------------------------------------------------
    // template (static panel parts)
    // ------------------------------------------------------------------

    private void DrawTemplate(SKCanvas canvas)
    {
        float radius = (float)(int)(10 * _s);
        var panelRect = new SKRect(0, 0, PanelWidth, PanelHeight);

        var fill = new SKPaint
        {
            Color = OverlayStyle.WithAlpha(_style.PanelBg, 255),
            IsAntialias = true,
        };
        using (fill) canvas.DrawRoundRect(panelRect, radius, radius, fill);

        // Pillow's rounded_rectangle outline sits inside the box; a Skia
        // stroke is centred on the edge, so inset by half the width.
        float bw = Math.Max(1f, (float)(int)(1 * _s));
        var stroke = new SKPaint
        {
            Color = OverlayStyle.WithAlpha(_style.PanelBorder, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = bw,
            IsAntialias = true,
        };
        using (stroke)
            canvas.DrawRoundRect(new SKRect(bw / 2, bw / 2,
                PanelWidth - bw / 2, PanelHeight - bw / 2), radius, radius, stroke);

        if (_style.Label)
            DrawTextTopLeft(canvas, 14f * (float)_s, 7f * (float)_s, "fps-analyzer",
                _sizeSm, OverlayStyle.WithAlpha(_style.LabelText, 255));

        if (_style.Graph)
        {
            var gbg = new SKPaint { Color = OverlayStyle.WithAlpha(_style.GraphBg, 255) };
            using (gbg)
                // Pillow rectangles include both endpoints: +1 on each side.
                canvas.DrawRect(_gx0, _gy0, _gx1 - _gx0 + 1, _gy1 - _gy0 + 1, gbg);

            float lw = Math.Max(1f, (float)(int)(1 * _s));
            using var line = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = lw,
                IsAntialias = true,
            };
            string[] refColors = { _style.Ref1x, _style.Ref2x, _style.Ref3x };
            for (int mult = 1; mult <= 3; mult++)
            {
                int y = (int)(_gy1 - (mult * _baseMs / _yMax) * (_gy1 - _gy0));
                line.Color = OverlayStyle.WithAlpha(refColors[mult - 1], 255);
                canvas.DrawLine(_gx0, y, _gx1, y, line);
                DrawTextTopLeft(canvas, _gx1 - 2f, y - 12f * (float)_s,
                    mult + "x", _sizeSm, line.Color);
            }

            string caption = string.Format(CultureInfo.InvariantCulture,
                "frametimes, last ~{0:0.0}s  (base {1:0.00} ms)",
                _win / Math.Max(_baseFps, 1e-6), _baseMs);
            DrawTextTopLeft(canvas, _gx0, PanelHeight - 20f * (float)_s,
                caption, _sizeSm, OverlayStyle.WithAlpha(_style.CaptionText, 255));
        }
    }

    // ------------------------------------------------------------------
    // per-frame variable parts
    // ------------------------------------------------------------------

    private void DrawFrame(SKCanvas canvas, int i, IReadOnlyList<double> dts)
    {
        double dtNow = dts[i];
        double fps = _fpsDisp[i];
        double dt = _dtDisp[i];
        bool stutter = _stutterBlock is not null
            ? _stutterBlock[Math.Min(i / _block, _stutterBlock.Length - 1)]
            : _stutterSet[i] || dtNow >= HudMetrics.StutterMult * _baseMs;

        SKColor col = OverlayStyle.WithAlpha(
            stutter ? _style.Stutter : _style.FpsText, 255);
        SKColor colText = OverlayStyle.WithAlpha(stutter
            ? _style.Stutter : _style.Text, 235);

        string num = fps.ToString("0.0", CultureInfo.InvariantCulture);
        DrawTextTopLeft(canvas, 14f * (float)_s, 24f * (float)_s, num, _sizeBig, col);
        using (var font = new SKFont(_typeface, _sizeBig))
        {
            float nwid = font.MeasureText(num, null);
            DrawTextTopLeft(canvas,
                14f * (float)_s + nwid + 8f * (float)_s, 52f * (float)_s,
                "fps", _sizeMid, OverlayStyle.WithAlpha(_style.Text, 235));
        }
        string frametime = string.Format(CultureInfo.InvariantCulture,
            "frametime {0:0.00} ms", dt);
        DrawTextTopLeft(canvas, 14f * (float)_s, 78f * (float)_s,
            frametime, _sizeMid, colText);

        if (!_style.Graph)
            return;

        int end = _liveGraph
            ? i + 1
            : Math.Min(dts.Count, (i / _block) * _block + 1);
        int lo = Math.Max(0, end - _win);
        int n = end - lo;
        if (n == 0)
            return;

        double step = (_gx1 - _gx0 - 2) / Math.Max(1, _win - 1);
        var xs = new float[n];
        var ys = new float[n];
        for (int k = 0; k < n; k++)
        {
            xs[k] = (float)(_gx1 - 1 - (n - 1 - k) * step);
            ys[k] = (float)YFor(dts[lo + k]);
        }

        SKColor cLine = OverlayStyle.WithAlpha(_style.Line, 250);
        SKColor cStutter = OverlayStyle.WithAlpha(_style.Stutter, 255);
        float lw = Math.Max(1f, (float)(int)(1.2 * _s));
        using var line = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = lw,
            IsAntialias = true,
        };
        for (int k = 0; k < n - 1; k++)
        {
            bool a = dts[lo + k] >= HudMetrics.StutterMult * _baseMs;
            bool b = dts[lo + k + 1] >= HudMetrics.StutterMult * _baseMs;
            line.Color = a || b ? cStutter : cLine;
            canvas.DrawLine(xs[k], ys[k], xs[k + 1], ys[k + 1], line);
        }
        bool lastSt = dts[lo + n - 1] >= HudMetrics.StutterMult * _baseMs;
        line.Color = lastSt ? cStutter : OverlayStyle.WithAlpha(_style.FpsText, 255);
        line.Style = SKPaintStyle.Fill;
        // Pillow ellipse box [xs-2, ys-2, xs+2, ys+2] (inclusive) = r 2.5 circle.
        canvas.DrawCircle(xs[n - 1], ys[n - 1], 2.5f, line);
    }

    /// <summary>Port of <c>_Hud._y</c> (int truncation, like Python int()).</summary>
    private int YFor(double ms)
    {
        double v = Math.Max(0.0, Math.Min(ms, _yMax));
        return (int)(_gy1 - (v / _yMax) * (_gy1 - _gy0));
    }

    /// <summary>
    /// Draw text with Pillow's top-left anchor: the text's top (font ascent)
    /// is placed at y, so the baseline is y + ascent (Skia ascent is negative).
    /// </summary>
    private void DrawTextTopLeft(SKCanvas canvas, float x, float y, string text,
        float size, SKColor color)
    {
        using var font = new SKFont(_typeface, size);
        var paint = new SKPaint { Color = color, IsAntialias = true };
        using (paint)
            canvas.DrawText(text, x, y - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
    }

    public void Dispose()
    {
        if (_ownsTypeface)
            _typeface?.Dispose();
        _typeface = null;
    }
}
