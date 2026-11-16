namespace CapFrameX.Video.Contracts;

/// <summary>
/// A single decoded frame with its presentation timestamp.
/// </summary>
/// <param name="FrameIndex">Zero-based index of the frame in the decode order.</param>
/// <param name="PresentationSeconds">Presentation timestamp in seconds from the start of the stream.</param>
public sealed record FrameTimestamp(long FrameIndex, double PresentationSeconds);
