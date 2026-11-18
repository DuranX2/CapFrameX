using System;
using System.Collections.Generic;
using System.IO;
using CapFrameX.Video.Contracts;
using CapFrameX.Video.Decoding;
using FFmpeg.AutoGen;

namespace CapFrameX.Video.Overlay;

/// <summary>Outcome of a burn.</summary>
public sealed record BurnResult(string OutputPath, int Frames);

/// <summary>
/// Burn the FPS HUD into a video in-process (the C# port of fps-analyzer's
/// <c>render_overlay</c>): timestamps + metrics first, then one pass of
/// decode → composite → encode.
/// </summary>
/// <remarks>
/// Pipeline parity with the reference: the output video is re-timed to a
/// constant rate (<c>declared or base fps</c>), lossless burns use
/// FFV1/MKV (rgb24) with the output forced to <c>.mkv</c>, lossy burns use
/// x264 crf 17 / veryfast in MP4, and audio (when present) is copied
/// straight through.
/// </remarks>
public static class OverlayBurn
{
    /// <summary>
    /// Burn the HUD into <paramref name="videoPath"/> and write the result.
    /// </summary>
    /// <param name="videoPath">Input video.</param>
    /// <param name="outputPath">
    /// Output path. Lossless burns are forced to a <c>.mkv</c> suffix.
    /// </param>
    /// <param name="declaredFps">
    /// Container-declared frame rate, or 0 to probe.
    /// </param>
    /// <param name="lossless">FFV1/MKV when true, x264/MP4 when false.</param>
    /// <param name="style">HUD appearance (default style when null).</param>
    /// <param name="ffmpegRoot">
    /// Optional explicit FFmpeg shared-library directory.
    /// </param>
    public static unsafe BurnResult Burn(string videoPath, string outputPath, double declaredFps,
        bool lossless, OverlayStyle? style = null, string? ffmpegRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var source = new FfmpegVideoSource(ffmpegRoot);
        VideoInfo info = source.GetVideoInfo(videoPath);
        var frames = source.ReadFrameTimestampsAsync(videoPath).GetAwaiter().GetResult();
        if (frames.Count < 2)
            throw new ArgumentException("video has fewer than 2 frames", nameof(videoPath));

        int w = info.Width;
        int h = info.Height;
        int n = frames.Count;
        var ptsMs = new double[n];
        for (int i = 0; i < n; i++)
            ptsMs[i] = frames[i].PresentationSeconds * 1000.0;

        double declared = declaredFps > 0 ? declaredFps : info.AverageFps;
        var m = HudMetrics.Compute(ptsMs, declared);

        var hud = new HudRenderer(w, h, m.BaseMs, m.BaseFps, m.Window,
            m.StutterSet, m.FpsDisplay, m.DtDisplay,
            block: m.R, liveGraph: style?.LiveGraph ?? false,
            stutterBlock: m.StutterBlockFlags, style: style);

        string outPath = outputPath;
        if (lossless &&
            !outPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            outPath = Path.ChangeExtension(outPath, ".mkv");

        double rFrame = declared > 0 ? declared : m.BaseFps;

        var decoder = new FfmpegFrameDecoder(videoPath, ffmpegRoot);
        var audio = new FfmpegAudioSource(videoPath, ffmpegRoot);
        var enc = new FfmpegVideoEncoder(outPath, w, h, rFrame, lossless,
            audio.HasAudio ? audio.CodecParameters : null, audio.TimeBase, ffmpegRoot);
        int done = 0;
        try
        {
            var frame = new byte[w * h * 3];
            AVPacket* apkt = ffmpeg.av_packet_alloc();
            try
            {
                    for (int i = 0; i < n; i++)
                    {
                        if (!decoder.TryReadNext(frame, out _, out _))
                            throw new InvalidOperationException(
                                $"decoder ended early after {i} frames (expected {n})");

                        byte[] panel = hud.Render(i, m.DtAll);
                        BlendPanel(frame, panel, w, h, hud.X0, hud.Y0,
                            hud.PanelWidth, hud.PanelHeight);

                        // Drain audio; the muxer's interleaver orders output.
                        while (audio.TryReadNext(apkt))
                        {
                            enc.WriteAudioPacket(apkt);
                            ffmpeg.av_packet_unref(apkt);
                        }

                        enc.WriteFrame(frame, i);
                        done++;
                    }
                    enc.Finish();
                }
            finally
            {
                ffmpeg.av_packet_free(&apkt);
            }
        }
        finally
        {
            enc.Dispose();
            audio.Dispose();
            decoder.Dispose();
            hud.Dispose();
        }

        if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            throw new InvalidOperationException($"output missing after encode: {outPath}");
        return new BurnResult(outPath, done);
    }

    /// <summary>
    /// Blend the premultiplied HUD panel onto an RGB24 frame, mirroring the
    /// reference's float32 formula <c>out = frame*(1-a) + panel*a</c>
    /// (premultiplied channel ≡ straight channel * a). Opaque panel pixels
    /// are copied verbatim; only the few rounded-corner pixels take the
    /// float path, with truncation to byte exactly like numpy astype(uint8).
    /// </summary>
    internal static void BlendPanel(byte[] frame, byte[] panel, int width, int height,
        int x0, int y0, int pw, int ph)
    {
        if (x0 < 0 || y0 < 0 || x0 + pw > width || y0 + ph > height)
            throw new ArgumentException("panel lies outside the frame");

        for (int y = 0; y < ph; y++)
        {
            int fRow = (y0 + y) * width * 3;
            int pRow = y * pw * 4;
            for (int x = 0; x < pw; x++)
            {
                int pi = pRow + x * 4;
                int fi = fRow + (x0 + x) * 3;
                byte a = panel[pi + 3];
                if (a == 255)
                {
                    frame[fi] = panel[pi];
                    frame[fi + 1] = panel[pi + 1];
                    frame[fi + 2] = panel[pi + 2];
                }
                else if (a != 0)
                {
                    float af = a / 255f;
                    frame[fi] = (byte)(frame[fi] * (1f - af) + panel[pi]);
                    frame[fi + 1] = (byte)(frame[fi + 1] * (1f - af) + panel[pi + 1]);
                    frame[fi + 2] = (byte)(frame[fi + 2] * (1f - af) + panel[pi + 2]);
                }
            }
        }
    }
}
