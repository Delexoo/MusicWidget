using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using MusicWidget;

namespace MusicWidget.Services;

public sealed class ToolBootstrapper
{
    // Nightly builds track YouTube changes faster than stable releases; bot checks and
    // PO-token behavior are updated here first (see yt-dlp wiki / nightly-builds repo).
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe";

    // spotDL v4 — Spotify tracks / albums / playlists with metadata (uses yt-dlp + ffmpeg).
    private const string SpotDlUrl =
        "https://github.com/spotDL/spotify-downloader/releases/download/v4.5.2/spotdl-4.5.2-win32.exe";
    private const string SpotDlVersionMarkerFile = ".spotdl-version";
    private const string SpotDlVersionMarkerValue = "4.5.2";

    // gyan.dev's static essentials build is widely used and stable.
    private const string FfmpegZipUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>Sidecar so Deno bootstrap runs only once after spotDL install.</summary>
    private const string DenoMarkerFile = ".deno-installed";
    /// <summary>Sidecar so existing installs pick up the nightly URL once without waiting for stale refresh.</summary>
    private const string YtDlpChannelMarkerFile = ".yt-dlp-channel";
    private const string YtDlpChannelMarkerValue = "nightly-v1";

    // Embeddable CPython + audio-separator (UVR-MDX HQ) for AI vocal remover (lazy).
    private const string PythonEmbedUrl =
        "https://www.python.org/ftp/python/3.12.8/python-3.12.8-embed-amd64.zip";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string VocalSepVersionMarkerFile = ".vocal-sep-version";
    private const string VocalSepVersionMarkerValue = "mdx-hq-1";
    private const string VocalModelFile = "UVR-MDX-NET-Inst_HQ_3.onnx";
    private const string VocalModelUrl =
        "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/UVR-MDX-NET-Inst_HQ_3.onnx";

    private readonly string _toolsDir;
    private readonly string _vocalSepDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _vocalSepGate = new(1, 1);
    private bool _ensured;

    public string YtDlpPath { get; }
    public string FfmpegPath { get; }
    public string SpotDlPath { get; }
    public string ToolsDirectory => _toolsDir;
    public string VocalSepDirectory => _vocalSepDir;
    public string VocalSeparatorPython { get; private set; }
    public string VocalModelDirectory { get; }
    public string VocalModelFileName => VocalModelFile;
    public string VocalSeparateScriptPath { get; }

    public ToolBootstrapper(string toolsDir)
    {
        _toolsDir = toolsDir;
        Directory.CreateDirectory(_toolsDir);
        YtDlpPath = Path.Combine(_toolsDir, "yt-dlp.exe");
        FfmpegPath = Path.Combine(_toolsDir, "ffmpeg.exe");
        SpotDlPath = Path.Combine(_toolsDir, "spotdl.exe");
        _vocalSepDir = Path.Combine(_toolsDir, "vocal-sep");
        Directory.CreateDirectory(_vocalSepDir);
        VocalModelDirectory = Path.Combine(_vocalSepDir, "models");
        Directory.CreateDirectory(VocalModelDirectory);
        VocalSeparateScriptPath = Path.Combine(_vocalSepDir, "separate_instrumental.py");
        // Prefer venv when present (MDX); fall back to embeddable python path.
        var venvPy = Path.Combine(_vocalSepDir, "venv", "Scripts", "python.exe");
        VocalSeparatorPython = File.Exists(venvPy)
            ? venvPy
            : Path.Combine(_vocalSepDir, "python", "python.exe");
    }

    public bool AreToolsReady => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    public bool IsSpotDlReady => File.Exists(SpotDlPath);

    public bool IsVocalSeparatorReady =>
        File.Exists(VocalSeparatorPython)
        && File.Exists(Path.Combine(VocalModelDirectory, VocalModelFile))
        && File.Exists(VocalSeparateScriptPath)
        && File.Exists(Path.Combine(_vocalSepDir, VocalSepVersionMarkerFile))
        && string.Equals(
            SafeReadText(Path.Combine(_vocalSepDir, VocalSepVersionMarkerFile)).Trim(),
            VocalSepVersionMarkerValue,
            StringComparison.Ordinal);

    public async Task EnsureToolsAsync(
        IProgress<DownloadProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            TryMigrateYtDlpDistributionChannel();

            // YouTube changes often; refresh yt-dlp periodically so users aren't stuck on
            // an old exe that no longer passes bot checks (first install still works offline).
            if (File.Exists(YtDlpPath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(YtDlpPath);
                if (age > TimeSpan.FromDays(1))
                {
                    progress?.Report(new DownloadProgressUpdate(0,
                        "Refreshing yt-dlp nightly (latest YouTube fixes)..."));
                    try
                    {
                        File.Delete(YtDlpPath);
                        _ensured = false;
                    }
                    catch { /* locked; keep existing binary */ }
                }
            }

            if (_ensured && AreToolsReady)
            {
                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
            http.Timeout = TimeSpan.FromMinutes(5);

            if (!File.Exists(YtDlpPath))
            {
                progress?.Report(new DownloadProgressUpdate(0, "Downloading yt-dlp nightly (~10MB)..."));
                await DownloadFileAsync(http, YtDlpUrl, YtDlpPath, progress,
                    "Downloading yt-dlp nightly", 0, 40, ct);
            }

            if (!File.Exists(FfmpegPath))
            {
                progress?.Report(new DownloadProgressUpdate(40, "Downloading ffmpeg (~80MB)..."));
                var zipPath = Path.Combine(_toolsDir, "ffmpeg.zip");
                await DownloadFileAsync(http, FfmpegZipUrl, zipPath, progress,
                    "Downloading ffmpeg", 40, 90, ct);

                progress?.Report(new DownloadProgressUpdate(90, "Extracting ffmpeg..."));
                ExtractFfmpegFromZip(zipPath, FfmpegPath);
                try { File.Delete(zipPath); } catch { }
            }

            _ensured = AreToolsReady;
            if (_ensured)
            {
                WriteYtDlpChannelMarker();
                progress?.Report(new DownloadProgressUpdate(100, "Tools ready."));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads spotDL on demand (Spotify URLs only). Requires ffmpeg from <see cref="EnsureToolsAsync"/>.
    /// </summary>
    public async Task EnsureSpotDlAsync(
        IProgress<DownloadProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureToolsAsync(progress, ct);
        TryMigrateSpotDlVersion(progress);

        if (IsSpotDlReady)
        {
            StartDenoBootstrapInBackground();
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            TryMigrateSpotDlVersion(progress);
            if (IsSpotDlReady)
            {
                StartDenoBootstrapInBackground();
                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
            http.Timeout = TimeSpan.FromMinutes(10);

            progress?.Report(new DownloadProgressUpdate(2,
                "Downloading spotDL for Spotify links (~45MB, one-time)..."));
            await DownloadFileAsync(http, SpotDlUrl, SpotDlPath, progress,
                "Downloading spotDL", 2, 90, ct);
            WriteSpotDlVersionMarker();

            // Deno is optional and used to hang Spotify downloads (stdout deadlock + long fetch).
            // Kick it off in the background so the actual track download can start immediately.
            StartDenoBootstrapInBackground();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces older bundled spotDL builds that used a brittle yt-dlp format
    /// selector ("bestaudio" with no fallback → "Requested format is not available").
    /// </summary>
    private void TryMigrateSpotDlVersion(IProgress<DownloadProgressUpdate>? progress)
    {
        try
        {
            var marker = Path.Combine(_toolsDir, SpotDlVersionMarkerFile);
            var current = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (string.Equals(current, SpotDlVersionMarkerValue, StringComparison.Ordinal)
                && File.Exists(SpotDlPath))
            {
                return;
            }

            if (File.Exists(SpotDlPath))
            {
                progress?.Report(new DownloadProgressUpdate(1,
                    "Updating spotDL (Spotify download fixes)..."));
                File.Delete(SpotDlPath);
            }

            try { File.Delete(marker); } catch { /* best-effort */ }
        }
        catch
        {
            // Locked binary — keep existing; next launch may refresh.
        }
    }

    private void WriteSpotDlVersionMarker()
    {
        try
        {
            File.WriteAllText(
                Path.Combine(_toolsDir, SpotDlVersionMarkerFile),
                SpotDlVersionMarkerValue + Environment.NewLine);
        }
        catch
        {
            // Best-effort; migration may run again next launch.
        }
    }

    /// <summary>
    /// Ensures Python + audio-separator + UVR-MDX HQ model for AI vocal removal.
    /// Large one-time download; progress is reported like other tool bootstraps.
    /// </summary>
    public async Task EnsureVocalSeparatorAsync(
        IProgress<DownloadProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        if (IsVocalSeparatorReady)
        {
            return;
        }

        await EnsureToolsAsync(progress, ct);

        await _vocalSepGate.WaitAsync(ct);
        try
        {
            if (IsVocalSeparatorReady)
            {
                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
            http.Timeout = TimeSpan.FromMinutes(45);

            WriteVocalSeparateScript();

            progress?.Report(new DownloadProgressUpdate(2, "Preparing Python for vocal remover..."));
            await EnsureVocalPythonAsync(http, progress, ct);

            progress?.Report(new DownloadProgressUpdate(40,
                "Installing audio-separator (MDX HQ)..."));
            await PipInstallVocalSeparatorAsync(progress, ct);

            var modelPath = Path.Combine(VocalModelDirectory, VocalModelFile);
            if (!File.Exists(modelPath))
            {
                progress?.Report(new DownloadProgressUpdate(70,
                    "Downloading UVR-MDX Inst HQ model..."));
                await DownloadFileAsync(http, VocalModelUrl, modelPath, progress,
                    "Downloading MDX model", 70, 96, ct);
            }

            File.WriteAllText(
                Path.Combine(_vocalSepDir, VocalSepVersionMarkerFile),
                VocalSepVersionMarkerValue + Environment.NewLine);

            progress?.Report(new DownloadProgressUpdate(100, "Vocal remover ready."));
        }
        finally
        {
            _vocalSepGate.Release();
        }
    }

    private async Task EnsureVocalPythonAsync(
        HttpClient http,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        var venvPy = Path.Combine(_vocalSepDir, "venv", "Scripts", "python.exe");
        if (File.Exists(venvPy))
        {
            VocalSeparatorPython = venvPy;
            return;
        }

        // Prefer system Python to create a venv (torch/audio-separator need full site-packages).
        var systemPy = await FindSystemPythonAsync(ct);
        if (systemPy is not null)
        {
            progress?.Report(new DownloadProgressUpdate(8, "Creating vocal-remover venv..."));
            var venvDir = Path.Combine(_vocalSepDir, "venv");
            var (code, err) = await RunProcessAsync(systemPy, ["-m", "venv", venvDir], ct);
            if (code == 0 && File.Exists(venvPy))
            {
                VocalSeparatorPython = venvPy;
                return;
            }

            CrashLog.Write(
                new InvalidOperationException(err),
                "ToolBootstrapper.EnsureVocalPython.venv");
        }

        // Fallback: embeddable CPython (may lack wheels for some deps).
        var pythonDir = Path.Combine(_vocalSepDir, "python");
        var embedPy = Path.Combine(pythonDir, "python.exe");
        if (!File.Exists(embedPy))
        {
            progress?.Report(new DownloadProgressUpdate(5,
                "Downloading Python for vocal remover (~15MB)..."));
            var zipPath = Path.Combine(_vocalSepDir, "python-embed.zip");
            await DownloadFileAsync(http, PythonEmbedUrl, zipPath, progress,
                "Downloading Python", 5, 22, ct);

            progress?.Report(new DownloadProgressUpdate(23, "Extracting Python..."));
            if (Directory.Exists(pythonDir))
            {
                try { Directory.Delete(pythonDir, recursive: true); } catch { /* retry extract */ }
            }

            Directory.CreateDirectory(pythonDir);
            ZipFile.ExtractToDirectory(zipPath, pythonDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }
        }

        EnableEmbeddableSitePackages(pythonDir);
        VocalSeparatorPython = embedPy;

        progress?.Report(new DownloadProgressUpdate(28, "Bootstrapping pip..."));
        await EnsurePipAsync(http, progress, ct);
    }

    private static async Task<string?> FindSystemPythonAsync(CancellationToken ct)
    {
        foreach (var (file, args) in new (string, string[])[]
                 {
                     ("py", ["-3", "-c", "import sys; print(sys.executable)"]),
                     ("python", ["-c", "import sys; print(sys.executable)"]),
                     ("python3", ["-c", "import sys; print(sys.executable)"]),
                 })
        {
            try
            {
                var (code, stdout) = await RunProcessCaptureAsync(file, args, ct);
                if (code == 0)
                {
                    var path = stdout.Trim();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch
            {
                /* try next */
            }
        }

        return null;
    }

    private void WriteVocalSeparateScript()
    {
        const string script = """
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from audio_separator.separator import Separator

def main() -> None:
    input_path, output_dir, model_dir, model_name, target_path = sys.argv[1:6]
    output_dir = str(Path(output_dir))
    target = Path(target_path)
    separator = Separator(
        model_file_dir=model_dir,
        output_dir=output_dir,
        output_format="WAV",
        output_single_stem="Instrumental",
        sample_rate=44100,
        mdx_params={
            "hop_length": 1024,
            "segment_size": 256,
            "overlap": 0.25,
            "batch_size": 1,
            "enable_denoise": False,
        },
    )
    separator.load_model(model_filename=model_name)
    files = separator.separate(
        input_path,
        custom_output_names={"Instrumental": "mw_instrumental"},
    )
    produced = None
    for name in files or []:
        path = Path(name)
        if not path.is_file():
            path = Path(output_dir) / name
        if path.is_file():
            produced = path
    if produced is None:
        raise SystemExit("Vocal model finished without an instrumental file")
    if produced.resolve() != target.resolve():
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            target.unlink()
        produced.replace(target)
    data, _sr = sf.read(str(target), always_2d=True, frames=48000)
    if data.ndim != 2 or data.shape[1] < 2 or data.shape[0] < 1000:
        raise SystemExit("Instrumental file was empty")
    left = data[:, 0]
    right = data[:, 1]
    if float(np.std(left)) > 1e-4 and float(np.std(right)) > 1e-4:
        corr = float(np.corrcoef(left, right)[0, 1])
        if np.isfinite(corr) and corr < -0.98:
            raise SystemExit("Vocal removal collapsed to a center-cancel mix")
    print("MUSICWIDGET_OK", flush=True)

if __name__ == "__main__":
    main()
""";
        File.WriteAllText(VocalSeparateScriptPath, script);
    }

    private static string SafeReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    private static void EnableEmbeddableSitePackages(string pythonDir)
    {
        Directory.CreateDirectory(Path.Combine(pythonDir, "Lib", "site-packages"));

        foreach (var pth in Directory.EnumerateFiles(pythonDir, "python*._pth"))
        {
            var text = File.ReadAllText(pth);
            text = text.Replace("#import site", "import site", StringComparison.Ordinal);
            if (!text.Contains("import site", StringComparison.Ordinal))
            {
                text = text.TrimEnd() + Environment.NewLine + "import site" + Environment.NewLine;
            }

            if (!text.Contains("Lib\\site-packages", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("Lib/site-packages", StringComparison.OrdinalIgnoreCase))
            {
                text = text.TrimEnd() + Environment.NewLine + "Lib\\site-packages" + Environment.NewLine;
            }

            File.WriteAllText(pth, text);
        }
    }

    private async Task EnsurePipAsync(
        HttpClient http,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        var getPip = Path.Combine(_vocalSepDir, "get-pip.py");
        if (!File.Exists(getPip))
        {
            await DownloadFileAsync(http, GetPipUrl, getPip, progress, "Downloading get-pip", 30, 40, ct);
        }

        // Already have pip?
        if ((await RunPythonAsync(["-m", "pip", "--version"], ct)).ExitCode == 0)
        {
            return;
        }

        var (code, stderr) = await RunPythonAsync(
            [getPip, "--no-warn-script-location"],
            ct);
        if (code != 0)
        {
            // Retry once with --force-reinstall style fresh get-pip download.
            try { File.Delete(getPip); } catch { /* ignore */ }
            await DownloadFileAsync(http, GetPipUrl, getPip, progress, "Re-downloading get-pip", 32, 40, ct);
            (code, stderr) = await RunPythonAsync([getPip, "--no-warn-script-location"], ct);
        }

        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? "" : " " + TrimError(stderr);
            throw new InvalidOperationException(
                "Could not install pip for the vocal remover Python runtime." + detail);
        }
    }

    private async Task PipInstallVocalSeparatorAsync(
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        await RunPythonAsync(
            ["-m", "pip", "install", "--upgrade", "pip", "setuptools", "wheel", "--no-warn-script-location"],
            ct);

        // Primary path: audio-separator[cpu] (MDX ONNX).
        var (code, stderr) = await RunPythonAsync(
            ["-m", "pip", "install", "--upgrade", "--no-warn-script-location", "audio-separator[cpu]"],
            ct);

        if (code != 0 || !await SeparatorImportOkAsync(ct))
        {
            progress?.Report(new DownloadProgressUpdate(50,
                "Retrying MDX packages without diffq (Windows)..."));
            // Skip broken Windows native diffq; MDX Inst HQ only needs ONNX + torch.
            await RunPythonAsync(
                ["-m", "pip", "install", "--upgrade", "--no-warn-script-location",
                    "audio-separator", "--no-deps"],
                ct);
            (code, stderr) = await RunPythonAsync(
                [
                    "-m", "pip", "install", "--upgrade", "--no-warn-script-location",
                    "onnxruntime>=1.17", "torch>=2.3", "numpy>=2", "librosa>=0.10",
                    "soundfile>=0.12", "scipy>=1.13", "pydub>=0.25", "tqdm", "requests>=2",
                    "resampy>=0.4", "beartype>=0.18.5,<0.19.0", "pyyaml", "ml_collections",
                    "einops>=0.7", "six>=1.16", "packaging", "samplerate==0.1.0",
                    "julius>=0.2", "rotary-embedding-torch>=0.6.1,<0.7.0",
                    "audioop-lts>=0.2.1", "onnx-weekly", "onnx2torch-py313>=1.6", "audioread"
                ],
                ct);

            await WriteDiffqStubAsync(ct);
        }

        if (code != 0 || !await SeparatorImportOkAsync(ct))
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? "" : " " + TrimError(stderr);
            throw new InvalidOperationException(
                "Could not install audio-separator (MDX). Install Python 3.12 from python.org (Add to PATH), then retry."
                + detail);
        }
    }

    private async Task<bool> SeparatorImportOkAsync(CancellationToken ct)
    {
        var (code, _) = await RunPythonAsync(
            ["-c", "import audio_separator.separator"],
            ct);
        return code == 0;
    }

    private async Task WriteDiffqStubAsync(CancellationToken ct)
    {
        // Provide importable diffq so Demucs helpers can import; MDX does not call it.
        const string stub =
            "import site, pathlib\n"
            + "pkgs=[pathlib.Path(p) for p in site.getsitepackages() if p.replace(chr(92),'/').endswith('site-packages')]\n"
            + "root=(pkgs[0] if pkgs else pathlib.Path(site.getsitepackages()[0]))/'diffq'\n"
            + "root.mkdir(parents=True, exist_ok=True)\n"
            + "(root/'__init__.py').write_text('class DiffQuantizer: pass\\nclass UniformQuantizer: pass\\n"
            + "def restore_quantized_state(*a,**k):\\n    raise RuntimeError(\"diffq unavailable\")\\n', encoding='utf-8')\n";
        await RunPythonAsync(["-c", stub], ct);
    }

    private static async Task<(int ExitCode, string Stdout)> RunProcessCaptureAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, string.Empty);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        _ = await stderrTask;
        return (proc.ExitCode, await stdoutTask);
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "failed to start");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        _ = await stdoutTask;
        return (proc.ExitCode, await stderrTask);
    }

    /// <summary>
    /// Runs embeddable python.exe. Always drains redirected stdout/stderr so verbose
    /// tools like get-pip / pip install cannot deadlock on a full pipe buffer.
    /// </summary>
    private async Task<(int ExitCode, string Stderr)> RunPythonAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (!File.Exists(VocalSeparatorPython))
        {
            return (-1, "python.exe missing");
        }

        var psi = new ProcessStartInfo
        {
            FileName = VocalSeparatorPython,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.Combine(_vocalSepDir, "python"),
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            return (-1, "failed to start python");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using (ct.Register(() =>
               {
                   try
                   {
                       if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                   }
                   catch { /* ignore */ }
               }))
        {
            await proc.WaitForExitAsync(ct);
        }

        var stderr = await stderrTask;
        _ = await stdoutTask;
        return (proc.ExitCode, stderr ?? string.Empty);
    }

    private static string TrimError(string text)
    {
        text = text.Trim();
        if (text.Length <= 280) return text;
        return text[^280..];
    }

    /// <summary>
    /// spotDL recommends Deno for some YouTube Music tracks. Never blocks downloads —
    /// a prior await deadlocked on redirected stdout and stuck the UI at 92%.
    /// </summary>
    private void StartDenoBootstrapInBackground()
    {
        var marker = Path.Combine(_toolsDir, DenoMarkerFile);
        if (File.Exists(marker) || !File.Exists(SpotDlPath))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await TryBootstrapDenoAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "ToolBootstrapper.DenoBackground");
            }
        });
    }

    private async Task TryBootstrapDenoAsync(CancellationToken ct)
    {
        var marker = Path.Combine(_toolsDir, DenoMarkerFile);
        if (File.Exists(marker))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SpotDlPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _toolsDir,
            };
            psi.Environment["PATH"] = _toolsDir + Path.PathSeparator
                + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            psi.ArgumentList.Add("--download-deno");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                WriteDenoMarker();
                return;
            }

            // Must drain redirected pipes or spotDL/Deno can hang forever (full buffers).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    /* best-effort */
                }
            }
        }
        catch
        {
            // Non-fatal — spotDL still works for most tracks without Deno.
        }
        finally
        {
            // Always mark attempted so we never block / retry-hang on every Spotify download.
            WriteDenoMarker();
        }
    }

    private void WriteDenoMarker()
    {
        try
        {
            File.WriteAllText(
                Path.Combine(_toolsDir, DenoMarkerFile),
                DateTime.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch
        {
            /* best-effort */
        }
    }

    private void TryMigrateYtDlpDistributionChannel()
    {
        try
        {
            if (!File.Exists(YtDlpPath))
            {
                return;
            }

            var marker = Path.Combine(_toolsDir, YtDlpChannelMarkerFile);
            if (File.Exists(marker)
                && string.Equals(File.ReadAllText(marker).Trim(), YtDlpChannelMarkerValue, StringComparison.Ordinal))
            {
                return;
            }

            File.Delete(YtDlpPath);
            _ensured = false;
        }
        catch
        {
            // Locked binary or permission issue — age-based refresh may still apply later.
        }
    }

    private void WriteYtDlpChannelMarker()
    {
        try
        {
            File.WriteAllText(
                Path.Combine(_toolsDir, YtDlpChannelMarkerFile),
                YtDlpChannelMarkerValue + Environment.NewLine);
        }
        catch
        {
            // Best-effort; migration may run again next launch.
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient http,
        string url,
        string destPath,
        IProgress<DownloadProgressUpdate>? progress,
        string label,
        double progressFrom,
        double progressTo,
        CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = destPath + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            long lastReportTicks = 0;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && progress is not null)
                {
                    var now = Environment.TickCount64;
                    var isDone = read >= total;
                    // Cap UI chatter while pulling large tool zips (ffmpeg ~80MB).
                    if (isDone || now - lastReportTicks >= 250)
                    {
                        lastReportTicks = now;
                        var frac = (double)read / total;
                        var pct = progressFrom + (progressTo - progressFrom) * frac;
                        progress.Report(new DownloadProgressUpdate(pct,
                            $"{label}: {read / (1024 * 1024):N1} MB / {total / (1024 * 1024):N1} MB"));
                    }
                }
            }
        }
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
    }

    private static void ExtractFfmpegFromZip(string zipPath, string destFfmpegPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(destFfmpegPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(destFfmpegPath, overwrite: true);
                return;
            }
        }
        throw new InvalidOperationException("ffmpeg.exe was not found in the downloaded archive.");
    }
}
