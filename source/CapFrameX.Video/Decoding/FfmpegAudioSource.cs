using System;
using System.IO;
using FFmpeg.AutoGen;

namespace CapFrameX.Video.Decoding;

/// <summary>
/// In-process demuxer for a container's best audio stream, used for the
/// straight audio copy in the overlay burn pipeline. Video packets are read
/// and discarded; audio packets are handed out as owned copies.
/// </summary>
/// <remarks>
/// The instance is NOT thread-safe; one source per burn. C# 15 unsafe-code
/// rules: the <c>unsafe</c> type modifier establishes the unsafe context for
/// the pointer fields and method bodies (project-wide /unsafe+ no longer
/// suffices).
/// </remarks>
public unsafe sealed class FfmpegAudioSource : IDisposable
{
    private AVFormatContext* _fmt;
    private AVPacket* _pkt;
    private int _audioStream = -1;

    /// <summary>True when the container has an audio stream.</summary>
    public bool HasAudio => _audioStream >= 0 && _fmt != null;

    /// <summary>
    /// The audio stream's codec parameters (owned by the demuxer; valid while
    /// this source is alive). Null when there is no audio.
    /// </summary>
    public AVCodecParameters* CodecParameters { get; private set; }

    /// <summary>
    /// The audio stream's time base. The burn encoder copies it to the output
    /// stream so packets pass through unrescaled.
    /// </summary>
    public AVRational TimeBase { get; private set; }

    /// <param name="videoPath">Container to demux.</param>
    /// <param name="ffmpegRoot">
    /// Optional explicit FFmpeg shared-library directory; null uses
    /// <see cref="FfmpegNative.ResolveRoot" />.
    /// </param>
    public FfmpegAudioSource(string videoPath, string? ffmpegRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: {videoPath}", videoPath);

        FfmpegNative.EnsureLoaded(ffmpegRoot);

        try
        {
            int ret;
            fixed (AVFormatContext** fmtPtr = &_fmt)
            {
                ret = ffmpeg.avformat_open_input(fmtPtr, videoPath, null, null);
            }
            if (ret < 0)
                throw new InvalidOperationException(
                    $"avformat_open_input failed with error code {ret}.");

            ret = ffmpeg.avformat_find_stream_info(_fmt, null);
            if (ret < 0)
                throw new InvalidOperationException(
                    $"avformat_find_stream_info failed with error code {ret}.");

            int astream = ffmpeg.av_find_best_stream(
                _fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (astream >= 0)
            {
                _audioStream = astream;
                CodecParameters = _fmt->streams[astream]->codecpar;
                TimeBase = _fmt->streams[astream]->time_base;
            }

            _pkt = ffmpeg.av_packet_alloc();
            if (_pkt == null)
                throw new OutOfMemoryException("FFmpeg packet allocation failed.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read packets until the next audio packet; <paramref name="dst"/>
    /// receives an owned copy (the caller frees it with
    /// <c>av_packet_unref</c>/<c>av_packet_free</c>).
    /// </summary>
    /// <returns>False at end of container.</returns>
    public bool TryReadNext(AVPacket* dst)
    {
        if (!HasAudio || dst == null)
            return false;
        while (true)
        {
            int rd = ffmpeg.av_read_frame(_fmt, _pkt);
            if (rd < 0)
                return false;
            if (_pkt->stream_index == _audioStream)
            {
                ffmpeg.av_packet_ref(dst, _pkt);
                ffmpeg.av_packet_unref(_pkt);
                return true;
            }
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    public void Dispose()
    {
        if (_pkt != null)
        {
            fixed (AVPacket** p = &_pkt) ffmpeg.av_packet_free(p);
            _pkt = null;
        }
        if (_fmt != null)
        {
            fixed (AVFormatContext** s = &_fmt) ffmpeg.avformat_close_input(s);
            _fmt = null;
        }
        _audioStream = -1;
        CodecParameters = null;
    }
}
