using System;
using System.IO;
using FFmpeg.AutoGen;

namespace CapFrameX.Video.Decoding;

/// <summary>
/// In-process video encoder mirroring fps-analyzer's overlay encoder side:
/// consumes packed RGB24 frames (plus optional audio packets for a straight
/// copy) and muxes them to either
/// <list type="bullet">
/// <item>MP4 / H.264: <c>-c:v libx264 -preset veryfast -crf 17 -pix_fmt yuv420p</c></item>
/// <item>MKV / FFV1: <c>-c:v ffv1 -level 3</c> with <c>bgr0</c> (32-bit
/// BGRX). The reference CLI passes <c>-pix_fmt rgb24</c>, which the FFmpeg 8
/// FFV1 encoder does not accept; the CLI then silently falls back to the
/// closest supported format, bgr0 — which is what the reference output
/// actually contains, so we match it directly.</item>
/// </list>
/// Frame timestamps are sequential (pts = frame index) at the supplied
/// output rate, matching the reference pipeline (rawvideo pipe + <c>-r</c> +
/// <c>-fps_mode passthrough</c>), so VFR input content is re-timed to a
/// constant rate exactly like the Python burn.
/// </summary>
/// <remarks>
/// The instance is NOT thread-safe; one encoder per burn. C# 15 unsafe-code
/// rules: the <c>unsafe</c> type modifier establishes the unsafe context for
/// the pointer fields and method bodies (project-wide /unsafe+ no longer
/// suffices).
/// </remarks>
public unsafe sealed class FfmpegVideoEncoder : IDisposable
{
    private AVFormatContext* _ofmt;
    private AVStream* _vstream;
    private AVStream* _astream;
    private AVCodecContext* _ictx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _lossless;
    private bool _headerWritten;
    private bool _finished;
    private readonly int _ptsTicksPerFrame;

    /// <summary>
    /// Open the output container and encoder.
    /// </summary>
    /// <param name="outputPath">File to write (created/truncated).</param>
    /// <param name="width">Frame width (px).</param>
    /// <param name="height">Frame height (px).</param>
    /// <param name="frameRate">Output frame rate (the reference's r_frame).</param>
    /// <param name="lossless">FFV1/MKV (rgb24) when true, else x264/MP4.</param>
    /// <param name="audioCodecpar">
    /// Input audio stream's codec parameters for a straight copy, or null.
    /// Must outlive this encoder (owned by the input demuxer).
    /// </param>
    /// <param name="audioTimeBase">
    /// Input audio stream's time base; copied to the output stream so
    /// packets pass through unrescaled.
    /// </param>
    /// <param name="ffmpegRoot">
    /// Optional explicit FFmpeg shared-library directory.
    /// </param>
    public FfmpegVideoEncoder(string outputPath, int width, int height, double frameRate,
        bool lossless, AVCodecParameters* audioCodecpar, AVRational audioTimeBase,
        string? ffmpegRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (width <= 0 || height <= 0)
            throw new ArgumentException("frame size must be positive", nameof(width));
        if (frameRate <= 0)
            throw new ArgumentException("frame rate must be positive", nameof(frameRate));
        _width = width;
        _height = height;
        _lossless = lossless;

        FfmpegNative.EnsureLoaded(ffmpegRoot);

        try
        {
            int ret;
            fixed (AVFormatContext** ofmtPtr = &_ofmt)
            {
                ret = ffmpeg.avformat_alloc_output_context2(
                    ofmtPtr, null, lossless ? "matroska" : "mp4", outputPath);
                if (ret < 0 || _ofmt == null)
                    throw new InvalidOperationException(
                        $"avformat_alloc_output_context2 failed with error code {ret}.");
            }

            _vstream = ffmpeg.avformat_new_stream(_ofmt, null);
            if (_vstream == null)
                throw new InvalidOperationException("avformat_new_stream (video) failed.");

            AVCodec* codec = ffmpeg.avcodec_find_encoder(
                lossless ? AVCodecID.AV_CODEC_ID_FFV1 : AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                throw new InvalidOperationException(
                    lossless ? "FFV1 encoder not found in FFmpeg build."
                             : "H.264 encoder (libx264) not found in FFmpeg build.");
            _ictx = ffmpeg.avcodec_alloc_context3(codec);
            if (_ictx == null)
                throw new OutOfMemoryException("avcodec_alloc_context3 failed.");

            // State codec_type explicitly. avcodec_alloc_context3 leaves it
            // for the caller in some paths; the muxer's stream init switches
            // on it, and UNKNOWN (-1) would be a different failure mode than
            // the missing-pb crash (see the avio_open2 comment below).
            _ictx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;

            var (num, den) = RationalFromRate(frameRate);
            // RationalFromRate returns (num, den) ≈ 1/rate (the time base),
            // e.g. 44.262 → (42, 1859). The mov muxer stores packet
            // timestamps UNRESCALED and sets the track timescale to
            // time_base.den, doubling until >= 10000 (movenc.c 7.1). A base
            // like 42/1859 therefore lands in a file whose timescale (14872)
            // is 8x the base's denominator: playback runs 8x fast, and the
            // elst/mdhd durations (computed from pkt->duration in the same
            // units) come out wrong, so the mp4 demuxer drops tail samples.
            // Use a 1/T base instead, with T = den scaled by a power of two
            // to >= 10000 (matching the muxer's doubling, so it stays T);
            // one frame is then exactly num*(T/den) ticks.
            int timeScale = den;
            while (timeScale < 10000)
                timeScale *= 2;
            _ptsTicksPerFrame = num * (timeScale / den);
            var tb = new AVRational { num = 1, den = timeScale };
            _ictx->width = _width;
            _ictx->height = _height;
            _ictx->time_base = tb;
            _ictx->framerate = new AVRational { num = den, den = num };
            _vstream->time_base = tb;
            if (lossless)
            {
                // bgr0 (BGRX): the FFV1 encoder's closest supported format to
                // the reference CLI's requested rgb24 (see class remarks).
                _ictx->pix_fmt = AVPixelFormat.AV_PIX_FMT_BGR0;
                _ictx->level = 3; // -level 3 like the reference
            }
            else
            {
                _ictx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            }

            AVDictionary* dict = null;
            if (!lossless)
            {
                ffmpeg.av_dict_set(&dict, "preset", "veryfast", 0);
                ffmpeg.av_dict_set(&dict, "crf", "17", 0);
            }
            ret = ffmpeg.avcodec_open2(_ictx, codec, &dict);
            if (dict != null)
                ffmpeg.av_dict_free(&dict);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"avcodec_open2 failed with error code {ret}.");

            ret = ffmpeg.avcodec_parameters_from_context(_vstream->codecpar, _ictx);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"avcodec_parameters_from_context failed with error code {ret}.");

            // Defensive: guarantee the muxer never sees an UNKNOWN codec type
            // (see the codec_type comment above).
            _vstream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;

            if (audioCodecpar != null)
            {
                _astream = ffmpeg.avformat_new_stream(_ofmt, null);
                if (_astream == null)
                    throw new InvalidOperationException("avformat_new_stream (audio) failed.");
                ret = ffmpeg.avcodec_parameters_copy(_astream->codecpar, audioCodecpar);
                if (ret < 0)
                    throw new InvalidOperationException(
                        $"avcodec_parameters_copy failed with error code {ret}.");
                _astream->time_base = audioTimeBase; // packets pass through unrescaled
            }

            AVDictionary* opt = null;
            // avformat_alloc_output_context2 only records the URL; every
            // non-AVFMT_NOFILE muxer writes the container header into s->pb,
            // which must therefore be opened by the caller first. With a NULL
            // pb, avformat_write_header segfaults (0xC0000005) inside the
            // muxer's init — verified on the pinned ARM64 build with a pure
            // FFI probe (Node/koffi, no .NET in the loop).
            if ((_ofmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open2(&_ofmt->pb, outputPath, (int)ffmpeg.AVIO_FLAG_WRITE, null, null);
                if (ret < 0)
                    throw new InvalidOperationException(
                        $"avio_open2 failed with error code {ret}.");
            }

            ret = ffmpeg.avformat_write_header(_ofmt, &opt);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"avformat_write_header failed with error code {ret}.");
            _headerWritten = true;

            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
            if (_frame == null || _pkt == null)
                throw new OutOfMemoryException("FFmpeg frame/packet allocation failed.");
            _frame->format = (int)(lossless
                ? AVPixelFormat.AV_PIX_FMT_BGR0
                : AVPixelFormat.AV_PIX_FMT_YUV420P);
            _frame->width = _width;
            _frame->height = _height;
            ret = ffmpeg.av_frame_get_buffer(_frame, 0);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"av_frame_get_buffer failed with error code {ret}.");

            if (!lossless)
            {
                _sws = ffmpeg.sws_getContext(
                    _width, _height, AVPixelFormat.AV_PIX_FMT_RGB24,
                    _width, _height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (_sws == null)
                    throw new InvalidOperationException(
                        "sws_getContext (rgb24 → yuv420p) failed.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encode and write one RGB24 frame with pts <paramref name="pts"/>.
    /// </summary>
    /// <param name="rgb24Buffer">Packed RGB24, at least Width * Height * 3 bytes.</param>
    /// <param name="pts">Frame presentation index at the output rate (0,1,2,...);
    /// internally scaled to stream time-base ticks (one frame per 1/rate).</param>
    public void WriteFrame(byte[] rgb24Buffer, long pts)
    {
        if (_ictx == null || _frame == null)
            throw new ObjectDisposedException(nameof(FfmpegVideoEncoder));
        if (rgb24Buffer is null || rgb24Buffer.Length < _width * _height * 3)
            throw new ArgumentException(
                $"rgb24Buffer must be at least {_width * _height * 3} bytes.",
                nameof(rgb24Buffer));

        if (_frame->data[0] == null)
        {
            // av_frame_unref resets format/width/height (to -1/0/0), so the
            // per-frame re-allocation must re-establish them first, or
            // av_frame_get_buffer fails with AVERROR(EINVAL).
            _frame->format = (int)(_lossless
                ? AVPixelFormat.AV_PIX_FMT_BGR0
                : AVPixelFormat.AV_PIX_FMT_YUV420P);
            _frame->width = _width;
            _frame->height = _height;
            int ret = ffmpeg.av_frame_get_buffer(_frame, 0);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"av_frame_get_buffer failed with error code {ret}.");
        }

        if (_lossless)
        {
            // RGB24 -> bgr0 (BGRX): swap channels, pad zero.
            fixed (byte* src = rgb24Buffer)
            {
                byte* dst = _frame->data[0];
                int n = _width * _height;
                for (int i = 0; i < n; i++)
                {
                    dst[i * 4 + 0] = src[i * 3 + 2];
                    dst[i * 4 + 1] = src[i * 3 + 1];
                    dst[i * 4 + 2] = src[i * 3 + 0];
                    dst[i * 4 + 3] = 0;
                }
            }
        }
        else
        {
            fixed (byte* rgbPtr = rgb24Buffer)
            {
                var dst = new[] { _frame->data[0], _frame->data[1], _frame->data[2] };
                var stride = new[]
                {
                    _frame->linesize[0], _frame->linesize[1], _frame->linesize[2],
                };
                var srcSlice = new[] { rgbPtr };
                var srcStride = new[] { _width * 3 };
                int r = ffmpeg.sws_scale(
                    _sws, srcSlice, srcStride, 0, _height, dst, stride);
                if (r < 0)
                    throw new InvalidOperationException(
                        $"sws_scale failed with error code {r}.");
            }
        }

        _frame->pts = pts * _ptsTicksPerFrame;

        // Send the frame, draining the encoder between retries: send_frame
        // returns EAGAIN while the encoder still has output to give, and the
        // frame must be retried after draining (it stays in _frame until
        // accepted — dropping it here would silently lose the frame).
        int send;
        while ((send = ffmpeg.avcodec_send_frame(_ictx, _frame)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            DrainAndWrite();
        if (send < 0)
            throw new InvalidOperationException(
                $"avcodec_send_frame failed with error code {send}.");

        DrainAndWrite();
        ffmpeg.av_frame_unref(_frame);
    }

    /// <summary>
    /// Receive all pending encoded packets and write them to the muxer.
    /// </summary>
    private void DrainAndWrite()
    {
        int recv;
        while ((recv = ffmpeg.avcodec_receive_packet(_ictx, _pkt)) == 0)
        {
            _pkt->stream_index = 0;
            // One frame = _ptsTicksPerFrame ticks (time base is 1/T, T >=
            // 10000, see the constructor). The mov muxer uses pkt->duration
            // (stored UNRESCALED) to compute the mdhd/tkhd duration and the
            // elst (edit list) duration; a zero duration makes the elst
            // shorter than the sample count by the codec delay, and the mp4
            // demuxer then DROPS the tail samples ("drop a frame at
            // curr_cts"). Encoders leave duration unset (fftools sets it in
            // the CLI), so set it here.
            _pkt->duration = _ptsTicksPerFrame;
            int w = ffmpeg.av_interleaved_write_frame(_ofmt, _pkt);
            ffmpeg.av_packet_unref(_pkt);
            if (w < 0)
                throw new InvalidOperationException(
                    $"av_interleaved_write_frame failed with error code {w}.");
        }
        if (recv != ffmpeg.AVERROR(ffmpeg.EAGAIN) && recv != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException(
                $"avcodec_receive_packet failed with error code {recv}.");
    }

    /// <summary>
    /// Queue an audio packet for the copy stream (output time base equals the
    /// input's, so the packet is used as-is). The caller owns <paramref name="packet"/>.
    /// </summary>
    public void WriteAudioPacket(AVPacket* packet)
    {
        if (_astream == null)
            throw new InvalidOperationException("encoder has no audio stream.");
        if (packet == null)
            throw new ArgumentNullException(nameof(packet));
        packet->stream_index = 1;
        int w = ffmpeg.av_interleaved_write_frame(_ofmt, packet);
        if (w < 0)
            throw new InvalidOperationException(
                $"av_interleaved_write_frame (audio) failed with error code {w}.");
    }

    /// <summary>Flush the encoder and write the container trailer. Idempotent.</summary>
    public void Finish()
    {
        if (_finished || _ofmt == null)
        {
            _finished = true;
            return;
        }

        // Send the null flush, retrying after draining: send_frame returns
        // EAGAIN while the encoder still has output (x264 B-frame tail).
        // The flush request is retried, never dropped.
        int send = 0;
        while ((send = ffmpeg.avcodec_send_frame(_ictx, null)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            int r;
            do
            {
                r = ffmpeg.avcodec_receive_packet(_ictx, _pkt);
                if (r == 0)
                {
                    _pkt->stream_index = 0;
                    _pkt->duration = _ptsTicksPerFrame; // same as DrainAndWrite
                    int w = ffmpeg.av_interleaved_write_frame(_ofmt, _pkt);
                    ffmpeg.av_packet_unref(_pkt);
                    if (w < 0)
                        throw new InvalidOperationException(
                            $"av_interleaved_write_frame failed with error code {w}.");
                }
            } while (r == 0);
            if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                throw new InvalidOperationException(
                    $"avcodec_receive_packet (flush drain) failed with error code {r}.");
        }
        if (send < 0)
            throw new InvalidOperationException(
                $"avcodec_send_frame (flush) failed with error code {send}.");

        // Final drain: only AVERROR_EOF ends the loop. A transient EAGAIN can
        // arrive while the encoder finalizes delayed frames (x264 B-frame
        // reordering); treating it as terminal silently drops the tail frames.
        int recv = 0;
        do
        {
            recv = ffmpeg.avcodec_receive_packet(_ictx, _pkt);
            if (recv == 0)
            {
                _pkt->stream_index = 0;
                _pkt->duration = _ptsTicksPerFrame; // same as DrainAndWrite
                int w = ffmpeg.av_interleaved_write_frame(_ofmt, _pkt);
                ffmpeg.av_packet_unref(_pkt);
                if (w < 0)
                    throw new InvalidOperationException(
                        $"av_interleaved_write_frame failed with error code {w}.");
            }
        } while (recv == 0 || recv == ffmpeg.AVERROR(ffmpeg.EAGAIN));
        if (recv != ffmpeg.AVERROR_EOF)
            throw new InvalidOperationException(
                $"avcodec_receive_packet (flush) failed with error code {recv}.");

        int t = ffmpeg.av_write_trailer(_ofmt);
        if (t < 0)
            throw new InvalidOperationException(
                $"av_write_trailer failed with error code {t}.");
        _finished = true;
    }

    public void Dispose()
    {
        if (_ofmt != null && _headerWritten && !_finished)
        {
            try { Finish(); }
            catch { /* abandoning a corrupt output; nothing salvageable */ }
        }
        if (_ofmt != null && _ofmt->pb != null)
        {
            ffmpeg.avio_closep(&_ofmt->pb);
            _ofmt->pb = null;
        }
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
        if (_ofmt != null)
        {
            fixed (AVFormatContext** s = &_ofmt) ffmpeg.avformat_close_input(s);
            _ofmt = null;
        }
    }

    /// <summary>
    /// Nearest simple rational (denominator ≤ 10000, ≤ 5 continued-fraction
    /// terms) for the TIME BASE, i.e. 1/rate: 30 → 1/30, 29.97 → 100/2997.
    /// The continued-fraction state below returns the reciprocal convergent
    /// (for 44.262 it yields 42/1859 ≈ 1/44.262) — use the result as-is as
    /// num/den; swap the pair to get the rate back (as in <c>framerate</c>).
    /// The encoder constructor rescales this base to a 1/T form (T ≥ 10000,
    /// a power-of-two multiple of den) so the mov muxer's timescale stays
    /// exact; see the constructor for why.
    /// </summary>
    internal static (int Num, int Den) RationalFromRate(double rate)
    {
        double x = rate;
        long p0 = 1, p1 = 0, q0 = 0, q1 = 1;
        for (int i = 0; i < 5; i++)
        {
            double a = Math.Floor(x);
            long p2 = (long)a * p1 + p0;
            long q2 = (long)a * q1 + q0;
            if (q2 <= 0 || q2 > 10000)
                break;
            (p0, p1, q0, q1) = (p1, p2, q1, q2);
            double frac = x - a;
            if (frac < 1e-9)
                break;
            x = 1.0 / frac;
        }
        return ((int)p1, (int)q1);
    }
}
