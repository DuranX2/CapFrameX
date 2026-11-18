using System;
using System.IO;

namespace CapFrameX.Video.Decoding;

/// <summary>
/// Locates the FFmpeg shared libraries (avutil/avcodec/... DLLs on Windows,
/// libav*.so on Linux, libav*.dylib on macOS) and points FFmpeg.AutoGen at
/// them. The binaries are not committed to the repo: run
/// <c>scripts/Get-FfmpegNative.ps1</c> to install a pinned FFmpeg-Builds
/// shared build into <c>source/CapFrameX.Video/native/ffmpeg/</c>.
/// </summary>
public static class FfmpegNative
{
    /// <summary>Environment variable holding a directory that contains the FFmpeg shared libraries.</summary>
    public const string EnvironmentVariable = "CAPFRAMX_FFMPEG_ROOT";

    private static readonly object SyncRoot = new();
    private static string? _root;

    /// <summary>
    /// Ensures FFmpeg.AutoGen is pointed at a directory containing the FFmpeg
    /// shared libraries. Safe to call repeatedly; the first resolution wins
    /// (FFmpeg's library handles are process-lifetime singletons).
    /// </summary>
    /// <param name="rootPath">Explicit directory override, e.g. from a settings property.</param>
    public static void EnsureLoaded(string? rootPath = null)
    {
        lock (SyncRoot)
        {
            if (_root != null && (rootPath == null || _root.Equals(rootPath, StringComparison.Ordinal)))
                return;
            _root = ResolveRoot(rootPath);
            FFmpeg.AutoGen.ffmpeg.RootPath = _root;
        }
    }

    /// <summary>
    /// Resolves the FFmpeg root directory: explicit override, then the
    /// <see cref="EnvironmentVariable"/> variable, then beside the executing
    /// assembly, then the repo layout <c>&lt;ancestor&gt;/source/CapFrameX.Video/native/ffmpeg</c>.
    /// </summary>
    public static string ResolveRoot(string? rootPath = null)
    {
        if (!string.IsNullOrWhiteSpace(rootPath) && Directory.Exists(rootPath) && HasLibraries(rootPath))
            return rootPath;

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env) && HasLibraries(env))
            return env;

        var baseDir = AppContext.BaseDirectory;
        if (HasLibraries(baseDir))
            return baseDir;

        // Dev layout: walk up from the build output looking for the repo's
        // source/CapFrameX.Video/native/ffmpeg directory.
        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "source", "CapFrameX.Video", "native", "ffmpeg");
            if (HasLibraries(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "FFmpeg shared libraries not found. Run scripts/Get-FfmpegNative.ps1 " +
            $"or set {EnvironmentVariable} to a directory containing the avutil/avcodec/... libraries.",
            "avutil");
    }

    /// <summary>True when the directory holds an avutil shared library (present in every FFmpeg build).</summary>
    private static bool HasLibraries(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("avutil", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("libavutil", StringComparison.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(name);
                if (ext is ".dll" or ".so" or ".dylib" or "")
                    return true;
            }
        }
        return false;
    }
}
