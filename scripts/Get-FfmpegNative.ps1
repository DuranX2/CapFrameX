<#
.SYNOPSIS
    Installs the FFmpeg shared libraries that CapFrameX.Video loads at runtime.

.DESCRIPTION
    CapFrameX.Video P/Invokes FFmpeg through FFmpeg.AutoGen, which requires
    FFmpeg's *shared* libraries (avutil-*.dll, avcodec-*.dll, ...), not the
    ffmpeg.exe from a static build. The loaded DLLs must match both the
    *major versions* the FFmpeg.AutoGen package expects (it loads
    avutil-61 / avcodec-63 / ... by name) and the *CPU architecture* of the
    machine running the app (a x64 build silently fails to load on an ARM64
    machine, and vice versa).

    This script detects the machine architecture and installs:

      * ARM64 (e.g. Windows on Snapdragon): the pinned stable FFmpeg 9.0.2
        "essentials-shared" build for win-arm64 from
        https://github.com/tordona/ffmpeg-win-arm64 (a .7z archive; 7-Zip's
        7z.exe is required, e.g. "C:\Program Files\7-Zip\7z.exe").
      * x64: the latest "shared" 64-bit build from
        https://github.com/BtbN/FFmpeg-Builds (releases), a .zip archive.

    The libraries are copied into:

        source/CapFrameX.Video/native/ffmpeg/

    That directory is gitignored; the test project copies the DLLs into its
    output on build when present, and the analyzer resolves them at runtime
    (see CapFrameX.Video.Decoding.FfmpegNative).

    After copying, the script smoke-tests the libraries by loading avutil
    with LoadLibraryEx and resolving av_version_info - this catches
    architecture mismatches (ERROR_BAD_EXE_FORMAT) and broken builds before
    anyone runs the test suite.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/Get-FfmpegNative.ps1

.NOTES
    Requires an internet connection. Overwrites any existing native/ffmpeg
    contents. FFmpeg.AutoGen is pinned in Directory.Packages.props; if that
    package moves to a new FFmpeg major line, update $ExpectedDlls below to
    the matching DLL major versions.
#>
[CmdletBinding()]
param(
    # Override the repo root if the script is run from a checkout elsewhere.
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

# DLL names (without extension) the pinned FFmpeg.AutoGen package loads by
# name. FFmpeg.AutoGen 9.0.1.1 <-> FFmpeg 9.0 shared libraries. If you bump
# the FFmpeg.AutoGen package version in Directory.Packages.props, update this
# list to the matching major versions.
$ExpectedDlls = @("avutil-61", "avcodec-63", "avformat-63", "avfilter-12", "avdevice-63", "swscale-10", "swresample-7")

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
Write-Host "Machine architecture: $arch"

$assetName = $null
$downloadUrl = $null
$archiveKind = $null   # "zip" or "7z"
$sevenZip = $null

if ($arch -eq "Arm64") {
    # BtbN's FFmpeg-Builds repo has no ARM64 builds. tordona/ffmpeg-win-arm64
    # does; pin the stable 9.0.2 essentials-shared release so the DLL major
    # versions stay in lockstep with FFmpeg.AutoGen 9.x (avutil-61/avcodec-63).
    $assetName = "ffmpeg-9.0.2-essentials-shared-win-arm64.7z"
    $downloadUrl = "https://github.com/tordona/ffmpeg-win-arm64/releases/download/9.0.2/$assetName"
    $archiveKind = "7z"
    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue)
    if (-not $sevenZip) { $sevenZip = Join-Path ${env:ProgramFiles} "7-Zip\7z.exe" }
    if (-not (Test-Path $sevenZip)) {
        throw "ARM64 FFmpeg ships as a .7z archive; install 7-Zip (7z.exe) and re-run, or add it to PATH."
    }
    Write-Host "Selected: tordona ffmpeg-win-arm64 $assetName (pinned 9.0.2 stable, shared)"
}
elseif ($arch -eq "X64") {
    Write-Host "Querying latest BtbN FFmpeg-Builds release..."
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest"
    # The SHARED builds are the only ones that expose DLLs; static builds ship
    # only an ffmpeg.exe and would fail to load at runtime.
    $x64Asset = $release.assets |
        Where-Object { $_.name -match '^ffmpeg-.*-shared-64bit\.zip$' } |
        Select-Object -First 1
    if (-not $x64Asset) {
        throw "No shared 64-bit asset in release '$($release.tag_name)'; assets: $($release.assets.name -join ', ')"
    }
    $assetName = $x64Asset.name
    $downloadUrl = $x64Asset.browser_download_url
    $archiveKind = "zip"
    Write-Host "Selected: BtbN $($release.tag_name) $assetName"
}
else {
    throw "Unsupported architecture '$arch' - no Windows FFmpeg shared build is known for it."
}

$dest = Join-Path $RepoRoot "source/CapFrameX.Video/native/ffmpeg"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-native-" + [guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tmp $assetName
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    Write-Host "Downloading $assetName..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing

    Write-Host "Extracting..."
    if ($archiveKind -eq "zip") {
        Expand-Archive -Path $archivePath -DestinationPath $tmp -Force
    }
    else {
        & $sevenZip x "-y" "-o$tmp" $archivePath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z extraction failed with exit code $LASTEXITCODE" }
    }

    # Both layouts nest the DLLs in a bin/ subdirectory; find it by content.
    $libDir = Get-ChildItem -Path $tmp -Directory -Recurse |
        Where-Object { Get-ChildItem -Path $_.FullName -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue } |
        Select-Object -First 1
    if (-not $libDir) { throw "Extracted archive does not contain an avcodec-*.dll" }

    # Copy only the shared libraries CapFrameX links against, then verify the
    # set matches exactly what FFmpeg.AutoGen loads by name.
    $dlls = Get-ChildItem -Path $libDir.FullName -Filter '*.dll' |
        Where-Object { $_.Name -match '^(avcodec|avdevice|avfilter|avformat|avutil|swscale|swresample)-\d+\.dll$' }
    $installed = @($dlls | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) } | Sort-Object)
    $missing = @($ExpectedDlls | Where-Object { $_ -notin $installed })
    $unexpected = @($installed | Where-Object { $_ -notin $ExpectedDlls })
    if ($missing.Count -gt 0) { throw "Build is missing DLLs FFmpeg.AutoGen needs: $($missing -join ', ')" }
    if ($unexpected.Count -gt 0) {
        throw "DLL major versions do not match the pinned FFmpeg.AutoGen package (unexpected: $($unexpected -join ', ')). " +
              "The FFmpeg build and FFmpeg.AutoGen package are out of lockstep - re-pin the build or update \$ExpectedDlls."
    }
    Copy-Item -Path $dlls.FullName -Destination $dest -Force
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Installed $($dlls.Count) FFmpeg shared libraries to:"
Write-Host "  $dest"

# Smoke test: load avutil and resolve a well-known export. A x64/ARM64
# mismatch surfaces here as ERROR_BAD_EXE_FORMAT instead of as a cryptic
# DllImport failure inside the app much later.
$probeSource = @'
using System;
using System.Runtime.InteropServices;
public static class DllProbe
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool FreeLibrary(IntPtr hModule);
}
'@
$probe = Add-Type -TypeDefinition $probeSource -PassThru
$avutil = Get-ChildItem -Path $dest -Filter 'avutil-*.dll' | Select-Object -First 1
$handle = $probe::LoadLibraryEx($avutil.FullName, [IntPtr]::Zero, 0x00000000)
if ($handle -eq [IntPtr]::Zero) {
    throw "Smoke test FAILED: LoadLibraryEx could not load $($avutil.Name) (Win32 error $( [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())). " +
          "On ARM64 machines this usually means the x64 DLLs were installed by an older version of this script."
}
$proc = $probe::GetProcAddress($handle, "av_version_info")
$probe::FreeLibrary($handle) | Out-Null
if ($proc -eq [IntPtr]::Zero) {
    throw "Smoke test FAILED: av_version_info not found in $($avutil.Name) - not a valid shared FFmpeg build."
}
Write-Host "Smoke test OK: $($avutil.Name) loads and exports av_version_info on $arch."
Write-Host "Rebuild the test project to have the DLLs copied into its output directory."
