using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicWidget.Services;

/// <summary>
/// AI vocal→instrumental separation via demucs-onnx (karaoke mix), with on-disk cache.
/// Never modifies playlist source files.
/// </summary>
public sealed class StemSeparationService
{
    private readonly ToolBootstrapper _tools;
    private readonly string _stemsDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.OrdinalIgnoreCase);

    public StemSeparationService(ToolBootstrapper tools, string stemsDir)
    {
        _tools = tools;
        _stemsDir = stemsDir;
        Directory.CreateDirectory(_stemsDir);
    }

    public string StemsDirectory => _stemsDir;

    public static string ComputeCacheKey(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var payload = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string GetCachePath(string sourcePath) =>
        Path.Combine(_stemsDir, ComputeCacheKey(sourcePath) + ".mp3");

    public string? TryGetCachedInstrumental(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            var cached = GetCachePath(sourcePath);
            return File.Exists(cached) && new FileInfo(cached).Length > 1024 ? cached : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a cached instrumental path, running demucs-onnx when needed.
    /// </summary>
    public async Task<string> GetOrCreateInstrumentalAsync(
        string sourcePath,
        IProgress<DownloadProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Track file not found for vocal removal.", sourcePath);
        }

        var cached = TryGetCachedInstrumental(sourcePath);
        if (cached is not null)
        {
            return cached;
        }

        var key = ComputeCacheKey(sourcePath);
        var gate = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            cached = TryGetCachedInstrumental(sourcePath);
            if (cached is not null)
            {
                return cached;
            }

            progress?.Report(new DownloadProgressUpdate(1, "Preparing vocal remover..."));
            await _tools.EnsureVocalSeparatorAsync(progress, ct);

            progress?.Report(new DownloadProgressUpdate(5,
                "Removing vocals (AI)… first run downloads the model."));
            return await RunDemucsKaraokeAsync(sourcePath, key, progress, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> RunDemucsKaraokeAsync(
        string sourcePath,
        string key,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(_stemsDir, "tmp-" + key);
        var cachePath = Path.Combine(_stemsDir, key + ".mp3");

        try
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }

            Directory.CreateDirectory(workDir);

            var cli = _tools.VocalSeparatorCli;
            var useModule = !File.Exists(cli);
            var psi = new ProcessStartInfo
            {
                FileName = useModule ? _tools.VocalSeparatorPython : cli,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };

            psi.Environment["PATH"] = _tools.ToolsDirectory + Path.PathSeparator
                + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

            if (useModule)
            {
                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add("demucs_onnx");
            }

            psi.ArgumentList.Add("separate");
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add(workDir);
            // Single-file htdemucs + fp16weights (~166MB) is much lighter than the FT bag.
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add("htdemucs");
            psi.ArgumentList.Add("--karaoke");
            psi.ArgumentList.Add("--mp3");
            psi.ArgumentList.Add("--small");
            psi.ArgumentList.Add("--providers");
            psi.ArgumentList.Add("auto");

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>();
            proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

            var log = new StringBuilder();
            var logLines = 0;

            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                if (logLines < 200)
                {
                    log.AppendLine(line);
                    logLines++;
                }

                if (line.Contains('%', StringComparison.Ordinal))
                {
                    progress?.Report(new DownloadProgressUpdate(-1, "Removing vocals… " + TrimStatus(line)));
                }
                else if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("Fetching", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new DownloadProgressUpdate(-1, TrimStatus(line)));
                }
            }

            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

            if (!proc.Start())
            {
                throw new InvalidOperationException("Failed to start demucs-onnx.");
            }

            // Keep the UI snappy while demucs runs in the background.
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* priority is best-effort */ }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (ct.Register(() =>
                   {
                       try
                       {
                           if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                       }
                       catch { /* best-effort */ }
                   }))
            {
                var exit = await tcs.Task.ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                if (exit != 0)
                {
                    var detail = log.ToString();
                    if (detail.Length > 600) detail = detail[^600..];
                    throw new InvalidOperationException(
                        "Vocal remover failed." + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail));
                }
            }

            var karaoke = FindKaraokeOutput(workDir);
            if (karaoke is null)
            {
                throw new InvalidOperationException(
                    "Vocal remover finished but no instrumental file was produced.");
            }

            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            File.Move(karaoke, cachePath);
            progress?.Report(new DownloadProgressUpdate(100, "Vocals removed."));
            return cachePath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch { /* temp cleanup best-effort */ }
        }
    }

    private static string? FindKaraokeOutput(string workDir)
    {
        try
        {
            var files = Directory.EnumerateFiles(workDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var karaoke = files.FirstOrDefault(f =>
                Path.GetFileName(f).Contains("karaoke", StringComparison.OrdinalIgnoreCase));
            if (karaoke is not null) return karaoke;

            return files
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Length > 1024)
                .OrderByDescending(fi => fi.Length)
                .Select(fi => fi.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string TrimStatus(string line)
    {
        line = line.Trim();
        return line.Length <= 120 ? line : line[..117] + "...";
    }
}
