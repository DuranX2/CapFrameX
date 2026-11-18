using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CapFrameX.Video.Contracts;

/// <summary>
/// Decodes a video file and reports per-frame presentation timestamps.
/// The Phase 1 implementation will back this with FFmpeg (libav) demuxing.
/// </summary>
public interface IFpsAnalysisProvider
{
    /// <summary>
    /// Reads the presentation timestamps of every frame in the given video file.
    /// </summary>
    Task<IReadOnlyList<FrameTimestamp>> ReadFrameTimestampsAsync(
        string videoPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the file and returns static stream metadata (resolution, declared
    /// frame rate, duration) without decoding any frames.
    /// </summary>
    VideoInfo GetVideoInfo(string videoPath);
}
