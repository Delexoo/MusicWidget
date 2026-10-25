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

    private readonly string _toolsDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ensured;

    public string YtDlpPath { get; }
    public string FfmpegPath { get; }
    public string SpotDlPath { get; }
    public string ToolsDirectory => _toolsDir;

    public ToolBootstrapper(string toolsDir)
    {
        _toolsDir = toolsDir;
        Directory.CreateDirectory(_toolsDir);
        YtDlpPath = Path.Combine(_toolsDir, "yt-dlp.exe");
        FfmpegPath = Path.Combine(_toolsDir, "ffmpeg.exe");
        SpotDlPath = Path.Combine(_toolsDir, "spotdl.exe");
    }

    public bool AreToolsReady => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    public bool IsSpotDlReady => File.Exists(SpotDlPath);

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
