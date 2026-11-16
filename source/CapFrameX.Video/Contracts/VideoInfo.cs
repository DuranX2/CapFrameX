namespace CapFrameX.Video.Contracts;

/// <summary>
/// Static metadata about a video's primary video stream, obtained by probing
/// the container without decoding any frames.
/// </summary>
public sealed record VideoInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>
    /// Declared frame rate in frames per second (container avg_frame_rate,
    /// falling back to the codec base rate). 0 when unknown (e.g. some VFR files).
    /// </summary>
    public required double AverageFps { get; init; }

    /// <summary>Stream duration in seconds. 0 when the container does not report one.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Index of the video stream within the container.</summary>
    public required int StreamIndex { get; init; }

    public string CodecName { get; init; }
    public string PixelFormat { get; init; }
}
