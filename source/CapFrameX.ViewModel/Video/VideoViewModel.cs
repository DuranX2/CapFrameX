using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CapFrameX.Contracts.Localization;
using CapFrameX.Video.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Mvvm;

namespace CapFrameX.ViewModel.Video
{
    /// <summary>UI state of the Video Analysis tab.</summary>
    public enum EVideoState
    {
        /// <summary>No file loaded.</summary>
        Idle,
        /// <summary>Probing container metadata.</summary>
        Probing,
        /// <summary>Decoding frames for the pacing analysis.</summary>
        Analyzing,
        /// <summary>Analysis complete; the burn controls are live.</summary>
        Ready,
        /// <summary>Burning the overlay.</summary>
        Burning,
        /// <summary>The last operation failed.</summary>
        Failed,
    }

    /// <summary>
    /// Backs the Video Analysis tab: open a video file, measure its frame
    /// pacing (nominal vs. measured fps, base interval, CFR/VFR, jank,
    /// fps-over-time) and burn the FPS HUD into a new file (lossless FFV1/MKV
    /// or lossy x264/MP4). The heavy lifting lives in CapFrameX.Video; this
    /// view model owns the state machine, threading and file dialogs.
    /// </summary>
    public class VideoViewModel : BindableBase
    {
        private readonly IFpsAnalysisProvider _videoSource;
        private readonly IVideoBurnService _burnService;
        private readonly ILogger<VideoViewModel> _logger;
        private CancellationTokenSource? _cts;

        private EVideoState _state = EVideoState.Idle;
        private string? _videoPath;
        private string _videoFileLeaf = string.Empty;
        private VideoInfo? _videoInfo;
        private VideoMetrics? _metrics;
        private string _statusText = string.Empty;
        private string? _errorText;
        private bool _isLossless = true;
        private string _outputPath = string.Empty;
        private int _burnedFrames;
        private string? _burnedOutputPath;

        public VideoViewModel(
            IFpsAnalysisProvider videoSource,
            IVideoBurnService burnService,
            ILogger<VideoViewModel> logger)
        {
            _videoSource = videoSource ?? throw new ArgumentNullException(nameof(videoSource));
            _burnService = burnService ?? throw new ArgumentNullException(nameof(burnService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            OpenVideoCommand = new DelegateCommand(OpenVideoDialog);
            BrowseOutputCommand = new DelegateCommand(BrowseOutputDialog, () => State == EVideoState.Ready);
            BurnCommand = new DelegateCommand(() => _ = BurnAsync(), () => State == EVideoState.Ready);
            CancelCommand = new DelegateCommand(Cancel, () =>
                State is EVideoState.Probing or EVideoState.Analyzing);
            OpenOutputFolderCommand = new DelegateCommand(OpenOutputFolder,
                () => !string.IsNullOrEmpty(BurnedOutputPath) && File.Exists(BurnedOutputPath));
        }

        public DelegateCommand OpenVideoCommand { get; }
        public DelegateCommand BrowseOutputCommand { get; }
        public DelegateCommand BurnCommand { get; }
        public DelegateCommand CancelCommand { get; }
        public DelegateCommand OpenOutputFolderCommand { get; }

        public EVideoState State
        {
            get => _state;
            private set
            {
                if (_state == value)
                    return;
                _state = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsBusy));
                RaisePropertyChanged(nameof(CanCancel));
                RaisePropertyChanged(nameof(IsIdle));
                RaisePropertyChanged(nameof(HasMetrics));
                RaisePropertyChanged(nameof(HasError));
                BurnCommand.RaiseCanExecuteChanged();
                BrowseOutputCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                OpenOutputFolderCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>True while probing/analyzing/burning.</summary>
        public bool IsBusy => State is EVideoState.Probing or EVideoState.Analyzing or EVideoState.Burning;

        /// <summary>True while a probe/analysis is still running and can be cancelled.</summary>
        public bool CanCancel => State is EVideoState.Probing or EVideoState.Analyzing;

        /// <summary>True before any file has been loaded.</summary>
        public bool IsIdle => State == EVideoState.Idle;

        public bool HasMetrics => Metrics != null;
        public bool HasError => !string.IsNullOrEmpty(ErrorText);

        /// <summary>Absolute path of the selected file.</summary>
        public string? VideoPath
        {
            get => _videoPath;
            private set { _videoPath = value; RaisePropertyChanged(); }
        }

        /// <summary>Leaf name for display.</summary>
        public string VideoFileLeaf
        {
            get => _videoFileLeaf;
            private set { _videoFileLeaf = value; RaisePropertyChanged(); }
        }

        public VideoInfo? VideoInfo
        {
            get => _videoInfo;
            private set
            {
                if (_videoInfo == value)
                    return;
                _videoInfo = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ResolutionText));
                RaisePropertyChanged(nameof(CodecText));
                RaisePropertyChanged(nameof(DurationText));
                RaisePropertyChanged(nameof(NominalFpsText));
            }
        }

        /// <summary>Pacing metrics; null until the analysis completes.</summary>
        public VideoMetrics? Metrics
        {
            get => _metrics;
            private set
            {
                if (_metrics == value)
                    return;
                _metrics = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasMetrics));
                RaisePropertyChanged(nameof(MeasuredFpsText));
                RaisePropertyChanged(nameof(BaseIntervalText));
                RaisePropertyChanged(nameof(ModeText));
                RaisePropertyChanged(nameof(JankText));
                RaisePropertyChanged(nameof(FrameCountText));
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; RaisePropertyChanged(); }
        }

        public string? ErrorText
        {
            get => _errorText;
            private set
            {
                _errorText = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasError));
            }
        }

        /// <summary>Lossless (FFV1/MKV) when true, lossy (x264/MP4) when false.</summary>
        public bool IsLossless
        {
            get => _isLossless;
            set
            {
                if (_isLossless == value)
                    return;
                _isLossless = value;
                RaisePropertyChanged();
                // Re-derive the default output extension when the user has not
                // picked a custom path.
                if (State == EVideoState.Ready && IsDefaultOutputPath)
                    OutputPath = DefaultOutputPath;
            }
        }

        /// <summary>True while the output path still carries the derived default.</summary>
        private bool IsDefaultOutputPath
            => VideoPath != null &&
               OutputPath == DefaultOutputPath;

        private string DefaultOutputPath
        {
            get
            {
                var dir = Path.GetDirectoryName(VideoPath) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(VideoPath);
                return Path.Combine(dir, stem + (IsLossless ? "_fps.mkv" : "_fps.mp4"));
            }
        }

        public string OutputPath
        {
            get => _outputPath;
            set { _outputPath = value ?? string.Empty; RaisePropertyChanged(); }
        }

        public int BurnedFrames
        {
            get => _burnedFrames;
            private set { _burnedFrames = value; RaisePropertyChanged(); }
        }

        public string? BurnedOutputPath
        {
            get => _burnedOutputPath;
            private set
            {
                _burnedOutputPath = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasBurnedOutput));
                OpenOutputFolderCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>True once a burn produced an output file.</summary>
        public bool HasBurnedOutput => !string.IsNullOrEmpty(BurnedOutputPath);

        public string ResolutionText => VideoInfo == null
            ? string.Empty
            : $"{VideoInfo.Width} × {VideoInfo.Height}";

        public string CodecText => VideoInfo?.CodecName ?? string.Empty;

        public string DurationText => VideoInfo == null || VideoInfo.DurationSeconds <= 0
            ? string.Empty
            : VideoInfo.DurationSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s";

        public string NominalFpsText => VideoInfo == null || VideoInfo.AverageFps <= 0
            ? "—"
            : VideoInfo.AverageFps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        public string MeasuredFpsText => Metrics == null || Metrics.MeasuredFps <= 0
            ? "—"
            : Metrics.MeasuredFps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        public string BaseIntervalText => Metrics == null || Metrics.BaseIntervalMs <= 0
            ? "—"
            : Metrics.BaseIntervalMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>CFR / VFR badge text (or "—" before analysis).</summary>
        public string ModeText => Metrics == null
            ? "—"
            : Metrics.IsCfr ? CxLang.T("VideoView_Cfr") : CxLang.T("VideoView_Vfr");

        public string JankText => Metrics == null
            ? "—"
            : Metrics.JankCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string FrameCountText => Metrics == null
            ? "—"
            : Metrics.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Open a file dialog and analyze the picked video.</summary>
        private void OpenVideoDialog()
        {
            var dialog = new OpenFileDialog
            {
                Filter = CxLang.T("VideoView_FileFilter"),
                Title = CxLang.T("VideoView_Browse"),
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() == true)
                _ = OpenVideoAsync(dialog.FileName);
        }

        private void BrowseOutputDialog()
        {
            var dialog = new SaveFileDialog
            {
                Filter = IsLossless
                    ? CxLang.T("VideoView_LosslessFileFilter")
                    : CxLang.T("VideoView_LossyFileFilter"),
                Title = CxLang.T("VideoView_OutputPath"),
                CheckFileExists = false,
                FileName = Path.GetFileName(OutputPath),
            };
            if (dialog.ShowDialog() == true)
                OutputPath = dialog.FileName;
        }

        /// <summary>
        /// Probe the file, decode its frames and compute the pacing metrics.
        /// Public (not command-only) so tests can drive it without a dialog.
        /// </summary>
        public async Task OpenVideoAsync(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                throw new FileNotFoundException(
                    CxLang.T("VideoView_FileNotFound"), videoPath);

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            ErrorText = null;
            BurnedOutputPath = null;
            BurnedFrames = 0;
            Metrics = null;
            VideoPath = videoPath;
            VideoFileLeaf = Path.GetFileName(videoPath);
            VideoInfo = null;
            OutputPath = string.Empty;
            StatusText = CxLang.T("VideoView_Probing");

            try
            {
                State = EVideoState.Probing;
                // The probe is a synchronous container read; keep it off the UI thread.
                var info = await Task.Run(() => _videoSource.GetVideoInfo(videoPath), token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;
                VideoInfo = info;

                State = EVideoState.Analyzing;
                StatusText = CxLang.T("VideoView_Analyzing");
                var frames = await _videoSource.ReadFrameTimestampsAsync(videoPath, token).ConfigureAwait(false);

                if (frames.Count < 2)
                {
                    State = EVideoState.Failed;
                    ErrorText = CxLang.T("VideoView_TooFewFrames");
                    return;
                }

                Metrics = VideoMetricsBuilder.Build(info, frames);
                State = EVideoState.Ready;
                OutputPath = DefaultOutputPath;
                StatusText = CxLang.Format("VideoView_Analyzed", frames.Count);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    State = EVideoState.Idle;
                    StatusText = string.Empty;
                    return;
                }
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video analysis failed for {VideoPath}.", videoPath);
                State = EVideoState.Failed;
                ErrorText = ex.Message;
            }
        }

        /// <summary>Burn the HUD into the derived (or picked) output path.</summary>
        public async Task BurnAsync()
        {
            if (State != EVideoState.Ready || VideoPath == null || VideoInfo == null)
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            ErrorText = null;
            BurnedOutputPath = null;
            State = EVideoState.Burning;
            StatusText = CxLang.T("VideoView_Burning");

            try
            {
                var result = await _burnService.BurnAsync(
                    VideoPath,
                    OutputPath,
                    VideoInfo.AverageFps,
                    IsLossless,
                    token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;

                BurnedOutputPath = result.OutputPath;
                BurnedFrames = result.Frames;
                State = EVideoState.Ready;
                StatusText = CxLang.Format("VideoView_Burned", result.Frames);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overlay burn failed for {VideoPath}.", VideoPath);
                State = EVideoState.Failed;
                ErrorText = ex.Message;
            }
        }

        /// <summary>Cancels the in-flight probe/analysis (burns run to completion).</summary>
        public void Cancel()
        {
            _cts?.Cancel();
        }

        /// <summary>Opens the output folder with the burned file selected.</summary>
        private void OpenOutputFolder()
        {
            if (string.IsNullOrEmpty(BurnedOutputPath) || !File.Exists(BurnedOutputPath))
                return;
            try
            {
                // /select reveals Explorer with the file highlighted (Windows).
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{BurnedOutputPath}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the output folder for {OutputPath}.", BurnedOutputPath);
            }
        }
    }
}
