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
/// AI vocal→instrumental separation via UVR-MDX-NET-Inst_HQ_3 (audio-separator),
/// writing 44.1 kHz WAV caches. Never modifies playlist source files.
/// </summary>
public sealed class StemSeparationService
{
    public const string CacheMarker = "mdx-instrumental";

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
        var payload = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{CacheMarker}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string GetCachePath(string sourcePath) =>
        Path.Combine(_stemsDir, ComputeCacheKey(sourcePath) + ".wav");

    public string? TryGetCachedInstrumental(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            var cached = GetCachePath(sourcePath);
            return File.Exists(cached) && new FileInfo(cached).Length > 4096 ? cached : null;
        }
        catch
        {
            return null;
        }
    }

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
                "Removing vocals (MDX HQ)… first run downloads the model."));
            return await RunMdxInstrumentalAsync(sourcePath, key, progress, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> RunMdxInstrumentalAsync(
        string sourcePath,
        string key,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(_stemsDir, "tmp-" + key);
        var cachePath = Path.Combine(_stemsDir, key + ".wav");
        var scriptPath = _tools.VocalSeparateScriptPath;

        try
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }

            Directory.CreateDirectory(workDir);

            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException("Vocal separator script is missing. Reinstall MusicWidget tools.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = _tools.VocalSeparatorPython,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };

            psi.Environment["PATH"] = _tools.ToolsDirectory + Path.PathSeparator
                + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add(workDir);
            psi.ArgumentList.Add(_tools.VocalModelDirectory);
            psi.ArgumentList.Add(_tools.VocalModelFileName);
            psi.ArgumentList.Add(cachePath);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>();
            proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

            var log = new StringBuilder();
            var logLines = 0;
            var sawOk = false;

            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                if (logLines < 200)
                {
                    log.AppendLine(line);
                    logLines++;
                }

                if (line.Contains("MUSICWIDGET_OK", StringComparison.Ordinal)
                    || line.Contains("MOCHI_OK", StringComparison.Ordinal))
                {
                    sawOk = true;
                    progress?.Report(new DownloadProgressUpdate(95, "Validating instrumental…"));
                }
                else if (line.Contains('%', StringComparison.Ordinal))
                {
                    progress?.Report(new DownloadProgressUpdate(-1, "Removing vocals… " + TrimStatus(line)));
                }
                else if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("Fetching", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("Loading", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new DownloadProgressUpdate(-1, TrimStatus(line)));
                }
            }

            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

            if (!proc.Start())
            {
                throw new InvalidOperationException("Failed to start MDX vocal remover.");
            }

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

                if (exit != 0 || !sawOk || !File.Exists(cachePath) || new FileInfo(cachePath).Length < 4096)
                {
                    var detail = log.ToString();
                    if (detail.Length > 600) detail = detail[^600..];
                    throw new InvalidOperationException(
                        "Vocal remover failed." + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail));
                }
            }

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

    private static string TrimStatus(string line)
    {
        line = line.Trim();
        return line.Length <= 120 ? line : line[..117] + "...";
    }
}
