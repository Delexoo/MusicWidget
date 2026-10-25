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

    // Embeddable CPython + demucs-onnx for AI vocal remover (lazy, first toggle).
    private const string PythonEmbedUrl =
        "https://www.python.org/ftp/python/3.12.8/python-3.12.8-embed-amd64.zip";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string VocalSepVersionMarkerFile = ".vocal-sep-version";
    private const string VocalSepVersionMarkerValue = "demucs-onnx-1";

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
    public string VocalSeparatorPython { get; }
    public string VocalSeparatorCli { get; }

    public ToolBootstrapper(string toolsDir)
    {
        _toolsDir = toolsDir;
        Directory.CreateDirectory(_toolsDir);
        YtDlpPath = Path.Combine(_toolsDir, "yt-dlp.exe");
        FfmpegPath = Path.Combine(_toolsDir, "ffmpeg.exe");
        SpotDlPath = Path.Combine(_toolsDir, "spotdl.exe");
        _vocalSepDir = Path.Combine(_toolsDir, "vocal-sep");
        Directory.CreateDirectory(_vocalSepDir);
        VocalSeparatorPython = Path.Combine(_vocalSepDir, "python", "python.exe");
        VocalSeparatorCli = Path.Combine(_vocalSepDir, "python", "Scripts", "demucs-onnx.exe");
    }

    public bool AreToolsReady => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    public bool IsSpotDlReady => File.Exists(SpotDlPath);

    public bool IsVocalSeparatorReady =>
        File.Exists(VocalSeparatorPython)
        && File.Exists(Path.Combine(_vocalSepDir, VocalSepVersionMarkerFile));

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
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            TryMigrateSpotDlVersion(progress);
            if (IsSpotDlReady)
            {
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

            await TryBootstrapDenoAsync(progress, ct);
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
    /// Ensures embeddable Python + demucs-onnx are installed for AI vocal removal.
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
            http.Timeout = TimeSpan.FromMinutes(30);

            var pythonDir = Path.Combine(_vocalSepDir, "python");
            if (!File.Exists(VocalSeparatorPython))
            {
                progress?.Report(new DownloadProgressUpdate(2,
                    "Downloading Python for vocal remover (~15MB)..."));
                var zipPath = Path.Combine(_vocalSepDir, "python-embed.zip");
                await DownloadFileAsync(http, PythonEmbedUrl, zipPath, progress,
                    "Downloading Python", 2, 25, ct);

                progress?.Report(new DownloadProgressUpdate(26, "Extracting Python..."));
                if (Directory.Exists(pythonDir))
                {
                    try { Directory.Delete(pythonDir, recursive: true); } catch { /* retry extract */ }
                }

                Directory.CreateDirectory(pythonDir);
                ZipFile.ExtractToDirectory(zipPath, pythonDir, overwriteFiles: true);
                try { File.Delete(zipPath); } catch { }

                EnableEmbeddableSitePackages(pythonDir);
            }

            progress?.Report(new DownloadProgressUpdate(30, "Bootstrapping pip..."));
            await EnsurePipAsync(http, progress, ct);

            progress?.Report(new DownloadProgressUpdate(45,
                "Installing demucs-onnx (AI vocal remover)..."));
            await PipInstallVocalSeparatorAsync(progress, ct);

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

    private static void EnableEmbeddableSitePackages(string pythonDir)
    {
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
        if (await RunPythonAsync(["-m", "pip", "--version"], ct) == 0)
        {
            return;
        }

        var code = await RunPythonAsync([getPip, "--no-warn-script-location"], ct);
        if (code != 0)
        {
            throw new InvalidOperationException("Could not install pip for the vocal remover Python runtime.");
        }
    }

    private async Task PipInstallVocalSeparatorAsync(
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        // Prefer DirectML on Windows; fall back to CPU onnxruntime.
        var packages = new[]
        {
            "demucs-onnx",
            "demucs-onnx[mp3]",
            "onnxruntime-directml",
        };

        var installArgs = new List<string>
        {
            "-m", "pip", "install", "--upgrade", "--no-warn-script-location",
        };
        installArgs.AddRange(packages);
        var code = await RunPythonAsync(installArgs, ct);
        if (code != 0)
        {
            progress?.Report(new DownloadProgressUpdate(60,
                "DirectML install failed; trying CPU onnxruntime..."));
            code = await RunPythonAsync(
                [
                    "-m", "pip", "install", "--upgrade", "--no-warn-script-location",
                    "demucs-onnx", "demucs-onnx[mp3]", "onnxruntime"
                ],
                ct);
        }

        if (code != 0)
        {
            throw new InvalidOperationException(
                "Could not install demucs-onnx. Check your network and try again.");
        }
    }

    private async Task<int> RunPythonAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!File.Exists(VocalSeparatorPython))
        {
            return -1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = VocalSeparatorPython,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _vocalSepDir,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return -1;
        }

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    /// <summary>
    /// spotDL recommends Deno for some YouTube Music tracks. Best-effort; failures are ignored.
    /// </summary>
    private async Task TryBootstrapDenoAsync(
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        var marker = Path.Combine(_toolsDir, DenoMarkerFile);
        if (File.Exists(marker))
        {
            return;
        }

        progress?.Report(new DownloadProgressUpdate(92,
            "Installing Deno for spotDL (one-time, optional)..."));

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
                return;
            }

            await proc.WaitForExitAsync(ct);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch
        {
            // Non-fatal — spotDL still works for most tracks without Deno.
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
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && progress is not null)
                {
                    var frac = (double)read / total;
                    var pct = progressFrom + (progressTo - progressFrom) * frac;
                    progress.Report(new DownloadProgressUpdate(pct,
                        $"{label}: {read / (1024 * 1024):N1} MB / {total / (1024 * 1024):N1} MB"));
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
