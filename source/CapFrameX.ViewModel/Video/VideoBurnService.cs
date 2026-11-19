using System;
using System.Threading;
using System.Threading.Tasks;
using CapFrameX.Video.Overlay;

namespace CapFrameX.ViewModel.Video
{
    /// <summary>
    /// Burns the FPS HUD into a video off the UI thread. Wraps the library's
    /// synchronous <see cref="OverlayBurn.Burn"/> so the view model stays
    /// testable with a fake.
    /// </summary>
    public interface IVideoBurnService
    {
        /// <param name="cancellationToken">
        /// Accepted for API symmetry; a burn in progress cannot be interrupted
        /// in the current library (it runs to completion).
        /// </param>
        Task<BurnResult> BurnAsync(
            string videoPath,
            string outputPath,
            double declaredFps,
            bool lossless,
            CancellationToken cancellationToken = default);
    }

    /// <inheritdoc/>
    public sealed class VideoBurnService : IVideoBurnService
    {
        public Task<BurnResult> BurnAsync(
            string videoPath,
            string outputPath,
            double declaredFps,
            bool lossless,
            CancellationToken cancellationToken = default)
            => Task.Run(
                () => OverlayBurn.Burn(videoPath, outputPath, declaredFps, lossless),
                cancellationToken);
    }
}
