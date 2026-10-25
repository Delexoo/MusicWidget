using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MusicWidget.Models;

namespace MusicWidget.Services;

public sealed class BackgroundDownloadProgressEventArgs : EventArgs
{
    public required string PlaylistName { get; init; }
    public required DownloadProgressUpdate Update { get; init; }
    public int QueueRemaining { get; init; }
}

public sealed class BackgroundDownloadCompletedEventArgs : EventArgs
{
    public required string PlaylistName { get; init; }
    public required bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
    public int DownloadedCount { get; init; }
    /// <summary>True when another queued job will start next.</summary>
    public bool HasMoreQueued { get; init; }
    public int QueueRemaining { get; init; }
}

public sealed class BackgroundDownloadQueuedEventArgs : EventArgs
{
    public required string PlaylistName { get; init; }
    public required string Url { get; init; }
    public int QueuePosition { get; init; }
    public int QueueRemaining { get; init; }
}

/// <summary>
/// Runs paste-audio-link downloads outside the dialog so they continue after it closes.
/// Multiple URLs are processed FIFO while earlier jobs are still running.
/// </summary>
public sealed class BackgroundDownloadService
{
    private readonly object _sync = new();
    private readonly Queue<QueuedJob> _queue = new();
    private CancellationTokenSource? _cts;
    private double _peakPercent;
    private bool _workerRunning;

    private sealed class QueuedJob
    {
        public required Playlist Playlist { get; init; }
        public required string Url { get; init; }
    }

    /// <summary>
    /// Forwards progress on the caller thread — unlike <see cref="Progress{T}"/>, does not
    /// marshal to the UI sync context (which would block the UI on disk I/O).
    /// </summary>
    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public DirectProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _workerRunning || _queue.Count > 0;
            }
        }
    }

    public string? ActivePlaylistName { get; private set; }

    public int QueueCount
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public event EventHandler<BackgroundDownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<BackgroundDownloadCompletedEventArgs>? Completed;
    public event EventHandler<BackgroundDownloadQueuedEventArgs>? Queued;

    public void Cancel()
    {
        lock (_sync)
        {
            _queue.Clear();
        }

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Starts a download immediately, or enqueues it if one is already running.
    /// <paramref name="wasQueued"/> is true when another job is already in progress.
    /// <paramref name="queueDepth"/> is how many jobs are waiting behind the active one
    /// (including this one when queued).
    /// </summary>
    public bool TryStart(
        Playlist playlist,
        string url,
        out string? errorMessage,
        out bool wasQueued,
        out int queueDepth)
    {
        errorMessage = null;
        wasQueued = false;
        queueDepth = 0;
        var trimmed = url?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            errorMessage = "Paste a link first.";
            return false;
        }

        bool startWorker;
        int queuePosition;
        int depth;
        lock (_sync)
        {
            wasQueued = _workerRunning;
            _queue.Enqueue(new QueuedJob { Playlist = playlist, Url = trimmed });
            queuePosition = _queue.Count;
            depth = wasQueued ? _queue.Count : 0;
            queueDepth = depth;
            startWorker = !_workerRunning;
            if (startWorker)
            {
                _workerRunning = true;
            }
        }

        var queuedPlaylist = playlist.Name;
        var queuedUrl = trimmed;
        UiDispatcher.BeginInvokeSafe(() =>
        {
            Queued?.Invoke(this, new BackgroundDownloadQueuedEventArgs
            {
                PlaylistName = queuedPlaylist,
                Url = queuedUrl,
                QueuePosition = queuePosition,
                QueueRemaining = depth,
            });
        });

        if (startWorker)
        {
            _ = ProcessQueueAsync();
        }

        return true;
    }

    /// <summary>Convenience overload when queue metadata is not needed.</summary>
    public bool TryStart(Playlist playlist, string url, out string? errorMessage) =>
        TryStart(playlist, url, out errorMessage, out _, out _);

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                QueuedJob? job;
                lock (_sync)
                {
                    if (_queue.Count == 0)
                    {
                        _workerRunning = false;
                        ActivePlaylistName = null;
                        return;
                    }

                    job = _queue.Dequeue();
                }

                await RunJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "BackgroundDownloadService.ProcessQueueAsync");
            lock (_sync)
            {
                _workerRunning = false;
                ActivePlaylistName = null;
            }
        }
    }

    private async Task RunJobAsync(QueuedJob job)
    {
        var playlist = job.Playlist;
        var url = job.Url;
        ActivePlaylistName = playlist.Name;
        _peakPercent = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var dest = App.Playlists.EnsurePlaylistFolder(playlist.Name);
        var existingFiles = Directory.Exists(dest)
            ? new HashSet<string>(Directory.EnumerateFiles(dest), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownAudioFiles = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        using var folderPollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var folderPollTask = PollDestinationFolderAsync(dest, playlist.Name, knownAudioFiles, folderPollCts.Token);

        var progress = new DirectProgress<DownloadProgressUpdate>(p =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p.CompletedFilePath))
                {
                    App.PlaylistOrders.AppendToOrder(playlist.Name, [p.CompletedFilePath]);
                }

                if (p.RefreshPlaylistTracks || !string.IsNullOrWhiteSpace(p.CompletedFilePath))
                {
                    App.Playlists.RequestTracksRefresh(playlist.Name);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BackgroundDownloadService.Progress");
            }

            RaiseProgress(playlist.Name, p);
        });

        var success = false;
        var cancelled = false;
        string? error = null;
        var downloadedCount = 0;

        try
        {
            Report(playlist.Name, new DownloadProgressUpdate(0, "Preparing..."));
            await App.Tools.EnsureToolsAsync(progress, ct).ConfigureAwait(false);
            _peakPercent = 0;
            Report(playlist.Name, DownloadProgressUpdate.Status("Downloading..."));

            var result = await App.Downloader.DownloadAsync(url, dest, progress, ct).ConfigureAwait(false);
            if (result.Success)
            {
                success = true;
                var downloadedOrder = result.DownloadedPathsInOrder?.ToList();
                if (downloadedOrder is null || downloadedOrder.Count == 0)
                {
                    downloadedOrder = Directory.EnumerateFiles(dest)
                        .Where(PlaylistManager.IsAudioFile)
                        .Where(f => !existingFiles.Contains(f))
                        .OrderBy(f => File.GetLastWriteTimeUtc(f))
                        .ToList();
                }

                downloadedCount = downloadedOrder.Count;
                if (downloadedOrder.Count > 0)
                {
                    App.PlaylistOrders.MergeDownloadOrder(playlist.Name, downloadedOrder);
                }

                App.Playlists.RequestTracksRefresh(playlist.Name);

                App.Settings.Current.LastDownloadPlaylist = playlist.Name;
                App.Settings.Save();
                Report(playlist.Name, new DownloadProgressUpdate(100, "Done."));
            }
            else
            {
                error = string.IsNullOrWhiteSpace(result.Error) ? "Download failed." : result.Error;
                Report(playlist.Name, new DownloadProgressUpdate(-1, error));
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            error = "Download cancelled.";
            Report(playlist.Name, new DownloadProgressUpdate(-1, error));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Report(playlist.Name, new DownloadProgressUpdate(-1, error));
        }
        finally
        {
            folderPollCts.Cancel();
            try
            {
                await folderPollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            int remaining;
            lock (_sync)
            {
                remaining = _queue.Count;
            }

            UiDispatcher.BeginInvokeSafe(() =>
            {
                Completed?.Invoke(this, new BackgroundDownloadCompletedEventArgs
                {
                    PlaylistName = playlist.Name,
                    Success = success,
                    Cancelled = cancelled,
                    Error = error,
                    DownloadedCount = downloadedCount,
                    HasMoreQueued = remaining > 0,
                    QueueRemaining = remaining,
                });
            });

            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Report(string playlistName, DownloadProgressUpdate update) =>
        RaiseProgress(playlistName, update);

    private void RaiseProgress(string playlistName, DownloadProgressUpdate update)
    {
        if (update.Percent >= 0)
        {
            if (update.Percent >= 100)
            {
                _peakPercent = 100;
            }
            else if (update.Percent < _peakPercent)
            {
                update = update with { Percent = _peakPercent };
            }
            else
            {
                _peakPercent = update.Percent;
            }
        }

        var remaining = QueueCount;
        UiDispatcher.BeginInvokeSafe(() =>
        {
            ProgressChanged?.Invoke(this, new BackgroundDownloadProgressEventArgs
            {
                PlaylistName = playlistName,
                Update = update,
                QueueRemaining = remaining,
            });
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static async Task PollDestinationFolderAsync(
        string destFolder,
        string playlistName,
        HashSet<string> knownFiles,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.25));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!Directory.Exists(destFolder))
                {
                    continue;
                }

                var newFiles = Directory.EnumerateFiles(destFolder)
                    .Where(f => !f.Contains(".part", StringComparison.OrdinalIgnoreCase))
                    .Where(PlaylistManager.IsAudioFile)
                    .Where(f => knownFiles.Add(f))
                    .OrderBy(f => File.GetLastWriteTimeUtc(f))
                    .ToList();

                if (newFiles.Count == 0)
                {
                    continue;
                }

                try
                {
                    App.PlaylistOrders.AppendToOrder(playlistName, newFiles);
                    App.Playlists.RequestTracksRefresh(playlistName);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "BackgroundDownloadService.PollDestinationFolder");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
