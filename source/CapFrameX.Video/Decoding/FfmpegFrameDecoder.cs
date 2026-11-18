using System;
using System.IO;
using FFmpeg.AutoGen;

namespace CapFrameX.Video.Decoding;

/// <summary>
/// In-process streaming video frame decoder: decodes the primary video
/// stream of any container/codec FFmpeg supports and converts every frame
/// to packed RGB24, for pixel-accurate overlay compositing (the burn-in
/// pipeline). Mirrors the decode pattern of
/// <see cref="FfmpegVideoSource"/> (same library load, same send/receive
/// loop) and adds an swscale conversion to RGB24.
/// </summary>
/// <remarks>
/// The instance is NOT thread-safe; one decoder per burn. RGB24 buffer lines
/// are packed (stride = width * 3).
/// </remarks>
/// <remarks>
/// C# 15 unsafe-code rules: the <c>unsafe</c> type modifier establishes the
/// unsafe context for the pointer fields and method bodies below
/// (project-wide /unsafe+ no longer suffices).
/// </remarks>
public unsafe sealed class FfmpegFrameDecoder : IDisposable
{
    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _ictx;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private SwsContext* _sws;
    private int _videoStream = -1;
    private double _secondsPerTick;
    private bool _flushing;

    /// <summary>Decoded frame width (px); valid after construction.</summary>
    public int Width { get; private set; }

    /// <summary>Decoded frame height (px); valid after construction.</summary>
    public int Height { get; private set; }

    /// <summary>
    /// Open the file and prepare the decoder.
    /// </summary>
    /// <param name="videoPath">Container to decode.</param>
    /// <param name="ffmpegRoot">
    /// Optional explicit FFmpeg shared-library directory; null uses
    /// <see cref="FfmpegNative.ResolveRoot"/>.
    /// </param>
    public FfmpegFrameDecoder(string videoPath, string? ffmpegRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: {videoPath}", videoPath);

        FfmpegNative.EnsureLoaded(ffmpegRoot);

        try
        {
            int ret;
            fixed (AVFormatContext** fmtPtr = &_fmtCtx)
                ret = ffmpeg.avformat_open_input(fmtPtr, videoPath, null, null);
            if (ret < 0)
                throw new InvalidOperationException($"avformat_open_input failed with error code {ret}.");

            ret = ffmpeg.avformat_find_stream_info(_fmtCtx, null);
            if (ret < 0)
                throw new InvalidOperationException($"avformat_find_stream_info failed with error code {ret}.");

            AVCodec* decoder;
            int videoStream = ffmpeg.av_find_best_stream(
                _fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (videoStream < 0)
                throw new InvalidOperationException("No video stream found in the file.");
            _videoStream = videoStream;

            AVStream* stream = _fmtCtx->streams[videoStream];
            _ictx = ffmpeg.avcodec_alloc_context3(decoder);
            ret = ffmpeg.avcodec_parameters_to_context(_ictx, stream->codecpar);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_parameters_to_context failed with error code {ret}.");

            ret = ffmpeg.avcodec_open2(_ictx, decoder, null);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2 failed with error code {ret}.");

            Width = _ictx->width;
            Height = _ictx->height;
            if (Width <= 0 || Height <= 0)
                throw new InvalidOperationException($"Decoded size {Width}x{Height} is invalid.");

            _sws = ffmpeg.sws_getContext(
                Width, Height, (AVPixelFormat)stream->codecpar->format,
                Width, Height, AVPixelFormat.AV_PIX_FMT_RGB24,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_sws == null)
                throw new InvalidOperationException(
                    $"sws_getContext failed for pixel format {(AVPixelFormat)stream->codecpar->format} (not convertible to rgb24).");

            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_pkt == null || _frame == null)
                throw new OutOfMemoryException("FFmpeg packet/frame allocation failed.");

            _secondsPerTick = stream->time_base.den > 0
                ? (double)stream->time_base.num / stream->time_base.den
                : 0;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decode and convert the next video frame.
    /// </summary>
    /// <param name="rgb24Buffer">
    /// Destination buffer; must be at least <c>Width * Height * 3</c> bytes.
    /// Packed RGB24, no row padding.
    /// </param>
    /// <param name="ptsSeconds">
    /// Presentation time in seconds (0 when the frame has no PTS).
    /// </param>
    /// <param name="hasPts">Whether a valid PTS was present.</param>
    /// <returns>False at end of stream (after the decoder is flushed).</returns>
    public bool TryReadNext(byte[] rgb24Buffer, out double ptsSeconds, out bool hasPts)
    {
        ptsSeconds = 0;
        hasPts = false;
        if (_fmtCtx == null) return false;
        if (rgb24Buffer is null || rgb24Buffer.Length < Width * Height * 3)
            throw new ArgumentException(
                $"rgb24Buffer must be at least {Width * Height * 3} bytes.", nameof(rgb24Buffer));

        // 1) A frame may already be buffered (decoder delay).
        int r = ffmpeg.avcodec_receive_frame(_ictx, _frame);
        if (r == 0)
        {
            if (TryConvert(rgb24Buffer, out ptsSeconds, out hasPts)) return true;
            ffmpeg.av_frame_unref(_frame);
        }

        // 2) Otherwise keep pulling packets until a frame comes out or the
        //    stream is truly exhausted. EAGAIN from receive means "more
        //    packets needed" — NOT end of stream; only AVERROR_EOF after the
        //    flush (null packet sent) ends the stream.
        while (true)
        {
            if (!_flushing)
            {
                int rd = ffmpeg.av_read_frame(_fmtCtx, _pkt);
                if (rd < 0)
                {
                    // Stream exhausted: signal EOF to the decoder and drain.
                    _flushing = true;
                    int flush = ffmpeg.avcodec_send_packet(_ictx, null);
                    if (flush < 0) return false;
                }
                else
                {
                    // Audio/other packets are not for this decoder; skip them.
                    if (_pkt->stream_index == _videoStream)
                    {
                        int send = ffmpeg.avcodec_send_packet(_ictx, _pkt);
                        if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            throw new InvalidOperationException(
                                $"avcodec_send_packet failed with error code {send}.");
                    }
                    ffmpeg.av_packet_unref(_pkt);
                }
            }

            r = ffmpeg.avcodec_receive_frame(_ictx, _frame);
            if (r == 0)
            {
                if (TryConvert(rgb24Buffer, out ptsSeconds, out hasPts)) return true;
                ffmpeg.av_frame_unref(_frame);
                continue;
            }
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue; // need more input
            if (r == ffmpeg.AVERROR_EOF)
                return false; // decoder fully drained after flush
            throw new InvalidOperationException(
                $"avcodec_receive_frame failed with error code {r}.");
        }
    }

    private unsafe bool TryConvert(byte[] buffer, out double ptsSeconds, out bool hasPts)
    {
        ptsSeconds = 0;
        hasPts = _frame->pts != ffmpeg.AV_NOPTS_VALUE && _secondsPerTick > 0;
        if (hasPts) ptsSeconds = _frame->pts * _secondsPerTick;

        fixed (byte* rgbPtr = buffer)
        {
            var dst = new[] { rgbPtr };
            var stride = new[] { Width * 3 };

            int r = ffmpeg.sws_scale(
                _sws, _frame->data, _frame->linesize, 0, Height, dst, stride);
            if (r < 0)
                throw new InvalidOperationException($"sws_scale failed with error code {r}.");
        }
        return true;
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_pkt != null)
        {
            fixed (AVPacket** p = &_pkt) ffmpeg.av_packet_free(p);
            _pkt = null;
        }
        if (_frame != null)
        {
            fixed (AVFrame** f = &_frame) ffmpeg.av_frame_free(f);
            _frame = null;
        }
        if (_ictx != null)
        {
            fixed (AVCodecContext** c = &_ictx) ffmpeg.avcodec_free_context(c);
            _ictx = null;
        }
        if (_fmtCtx != null)
        {
            fixed (AVFormatContext** s = &_fmtCtx) ffmpeg.avformat_close_input(s);
            _fmtCtx = null;
        }
    }
}
