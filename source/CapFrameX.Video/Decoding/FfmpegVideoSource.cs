using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CapFrameX.Video.Contracts;
using FFmpeg.AutoGen;

namespace CapFrameX.Video.Decoding;

/// <summary>
/// Frame timestamp provider backed by FFmpeg's libav* shared libraries
/// (FFmpeg.AutoGen). Decodes the primary video stream of any container/codec
/// FFmpeg supports and reports each frame's presentation time, so frame
/// pacing can be measured on real capture files.
/// </summary>
public sealed class FfmpegVideoSource : IFpsAnalysisProvider
{
    private readonly string? _ffmpegRoot;

    /// <param name="ffmpegRoot">
    /// Optional explicit directory containing the FFmpeg shared libraries.
    /// When null, <see cref="FfmpegNative.ResolveRoot"/> is used.
    /// </param>
    public FfmpegVideoSource(string? ffmpegRoot = null) => _ffmpegRoot = ffmpegRoot;

    /// <inheritdoc/>
    /// <remarks>
    /// Decoding happens on a thread pool thread so the caller's sync context is
    /// never blocked. Cancellation is checked between frames; an in-flight frame
    /// decode runs to completion.
    /// </remarks>
    public Task<IReadOnlyList<FrameTimestamp>> ReadFrameTimestampsAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: {videoPath}", videoPath);

        FfmpegNative.EnsureLoaded(_ffmpegRoot);
        return Task.Run(() => DecodeTimestamps(videoPath, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public VideoInfo GetVideoInfo(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: {videoPath}", videoPath);

        FfmpegNative.EnsureLoaded(_ffmpegRoot);
        return Probe(videoPath);
    }

    /// <summary>
    /// Decodes every frame of the primary video stream and collects
    /// (frame index, presentation time in seconds) pairs in decode order.
    /// Frames without a valid presentation timestamp are still counted in the
    /// frame index but produce no timestamp entry (rare; clean captures always
    /// carry PTS).
    /// </summary>
    private static unsafe IReadOnlyList<FrameTimestamp> DecodeTimestamps(
        string videoPath,
        CancellationToken cancellationToken)
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* ictx = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        var timestamps = new List<FrameTimestamp>();
        long frameIndex = 0;

        try
        {
            int ret = ffmpeg.avformat_open_input(&fmtCtx, videoPath, null, null);
            if (ret < 0)
                throw new InvalidOperationException($"avformat_open_input failed with error code {ret}.");

            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (ret < 0)
                throw new InvalidOperationException($"avformat_find_stream_info failed with error code {ret}.");

            AVCodec* decoder;
            int videoStream = ffmpeg.av_find_best_stream(
                fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (videoStream < 0)
                throw new InvalidOperationException("No video stream found in the file.");

            AVStream* stream = fmtCtx->streams[videoStream];
            ret = ffmpeg.avcodec_parameters_to_context(
                ictx = ffmpeg.avcodec_alloc_context3(decoder), stream->codecpar);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_parameters_to_context failed with error code {ret}.");

            ret = ffmpeg.avcodec_open2(ictx, decoder, null);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2 failed with error code {ret}.");

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null)
                throw new OutOfMemoryException("FFmpeg packet/frame allocation failed.");

            double secondsPerTick = stream->time_base.den > 0
                ? (double)stream->time_base.num / stream->time_base.den
                : 0;

            void ReceiveFrames(AVCodecContext* ctx, AVFrame* f)
            {
                while (true)
                {
                    int r = ffmpeg.avcodec_receive_frame(ctx, f);
                    if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF)
                        return;
                    if (r < 0)
                        throw new InvalidOperationException($"avcodec_receive_frame failed with error code {r}.");

                    if (f->pts != ffmpeg.AV_NOPTS_VALUE && secondsPerTick > 0)
                        timestamps.Add(new FrameTimestamp(frameIndex, f->pts * secondsPerTick));
                    frameIndex++;
                    ffmpeg.av_frame_unref(f);
                }
            }

            while (ffmpeg.av_read_frame(fmtCtx, pkt) >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pkt->stream_index != videoStream)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                int sendRet;
                while ((sendRet = ffmpeg.avcodec_send_packet(ictx, pkt)) == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    ReceiveFrames(ictx, frame);
                if (sendRet < 0)
                    throw new InvalidOperationException($"avcodec_send_packet failed with error code {sendRet}.");
                ReceiveFrames(ictx, frame);

                ffmpeg.av_packet_unref(pkt);
            }

            // Flush: signal EOF, then drain every delayed frame the decoder is
            // holding (h264 and friends typically queue a few).
            int flushRet;
            while (true)
            {
                flushRet = ffmpeg.avcodec_send_packet(ictx, null);
                if (flushRet < 0)
                    break;
                ReceiveFrames(ictx, frame);
            }
            ReceiveFrames(ictx, frame);

            return timestamps;
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (ictx != null) ffmpeg.avcodec_free_context(&ictx);
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
        }
    }

    /// <summary>Probes the container for stream metadata without decoding frames.</summary>
    private static unsafe VideoInfo Probe(string videoPath)
    {
        AVFormatContext* fmtCtx = null;
        try
        {
            int ret = ffmpeg.avformat_open_input(&fmtCtx, videoPath, null, null);
            if (ret < 0)
                throw new InvalidOperationException($"avformat_open_input failed with error code {ret}.");

            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (ret < 0)
                throw new InvalidOperationException($"avformat_find_stream_info failed with error code {ret}.");

            AVCodec* decoder;
            int index = ffmpeg.av_find_best_stream(
                fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (index < 0)
                throw new InvalidOperationException("No video stream found in the file.");

            AVStream* stream = fmtCtx->streams[index];
            AVCodecParameters* par = stream->codecpar;

            double avgFps = ToFps(stream->avg_frame_rate);
            double baseFps = ToFps(stream->r_frame_rate);

            double duration = 0;
            if (stream->duration > 0 && stream->time_base.den > 0)
                duration = stream->duration * (double)stream->time_base.num / stream->time_base.den;
            else if (fmtCtx->duration > 0)
                duration = fmtCtx->duration / 1_000_000.0; // AV_TIME_BASE units

            string? pixelFormat = par->format >= 0
                ? ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format)
                : null;
            string? codecName = par->codec_id == AVCodecID.AV_CODEC_ID_NONE
                ? null
                : ffmpeg.avcodec_get_name(par->codec_id);

            return new VideoInfo
            {
                Width = par->width,
                Height = par->height,
                AverageFps = avgFps > 0 ? avgFps : baseFps,
                DurationSeconds = duration,
                StreamIndex = index,
                CodecName = codecName,
                PixelFormat = pixelFormat,
            };
        }
        finally
        {
            if (fmtCtx != null)
                ffmpeg.avformat_close_input(&fmtCtx);
        }
    }

    private static double ToFps(AVRational rate) => rate.den > 0 ? (double)rate.num / rate.den : 0;
}
