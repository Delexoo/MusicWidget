using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MusicWidget.Models;

namespace MusicWidget.Services;

/// <summary>
/// Background queue that builds instrumental caches (demucs) ahead of playback so
/// vocal remover is ready when the user hits play. Single-flight, below-play priority.
/// </summary>
public sealed class StemPrefetchService
{
    private readonly object _sync = new();
    private readonly LinkedList<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _jobCts;
    private string? _activePath;
    private bool _workerRunning;
    private bool _enabled;
    private int _maxQueue = 24;

    public bool IsEnabled
    {
        get { lock (_sync) return _enabled; }
    }

    /// <summary>Turns prefetch on/off. When enabling, warms the vocal-remover toolchain.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                _queue.Clear();
                _queued.Clear();
                try { _jobCts?.Cancel(); } catch { /* ignore */ }
                _jobCts = null;
                _activePath = null;
                return;
            }
        }

        _ = WarmToolsAsync();
    }

    /// <summary>Installs Python/demucs in the background so first separation is faster.</summary>
    public Task WarmToolsAsync()
    {
        if (App.Tools.IsVocalSeparatorReady)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                await App.Tools.EnsureVocalSeparatorAsync(null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "StemPrefetchService.WarmTools");
            }
        });
    }

    /// <summary>
    /// When playback needs an instrumental right now, cancel a different in-flight prefetch
    /// so demucs CPU goes to the track the user just clicked.
    /// </summary>
    public void YieldForUrgent(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        lock (_sync)
        {
            if (_activePath is not null
                && !string.Equals(_activePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                try { _jobCts?.Cancel(); } catch { /* ignore */ }
            }

            // Promote this path to the front for after the urgent run.
            PromoteUnlocked(sourcePath);
        }
    }

    public void Enqueue(string? sourcePath, bool highPriority = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        if (!_enabled && !App.Settings.Current.VocalRemover)
        {
            return;
        }

        // Auto-enable when VocalRemover is already on (toggle / play path — never downloads).
        if (!_enabled && App.Settings.Current.VocalRemover)
        {
            SetEnabled(true);
        }

        if (!_enabled)
        {
            return;
        }

        if (App.Stems.TryGetCachedInstrumental(sourcePath) is not null)
        {
            return;
        }

        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            if (highPriority)
            {
                PromoteUnlocked(sourcePath);
            }
            else if (_queued.Add(sourcePath))
            {
                _queue.AddLast(sourcePath);
                TrimQueueUnlocked();
            }
        }

        EnsureWorker();
    }

    /// <summary>Prefetch the current track first, then the next few in the play queue.</summary>
    public void EnqueueAround(IReadOnlyList<Track> queue, int index, int ahead = 3)
    {
        if (!_enabled && !App.Settings.Current.VocalRemover)
        {
            return;
        }

        if (queue is null || queue.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, queue.Count - 1);
        Enqueue(queue[index].FilePath, highPriority: true);

        var end = Math.Min(queue.Count - 1, index + Math.Max(0, ahead));
        for (var i = index + 1; i <= end; i++)
        {
            Enqueue(queue[i].FilePath, highPriority: false);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _queue.Clear();
            _queued.Clear();
            try { _jobCts?.Cancel(); } catch { /* ignore */ }
            _jobCts = null;
            _activePath = null;
        }
    }

    private void PromoteUnlocked(string sourcePath)
    {
        if (_queued.Contains(sourcePath))
        {
            var node = _queue.First;
            while (node is not null)
            {
                if (string.Equals(node.Value, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _queue.Remove(node);
                    break;
                }

                node = node.Next;
            }
        }
        else
        {
            _queued.Add(sourcePath);
        }

        _queue.AddFirst(sourcePath);
        TrimQueueUnlocked();
    }

    private void TrimQueueUnlocked()
    {
        while (_queue.Count > _maxQueue)
        {
            var last = _queue.Last;
            if (last is null) break;
            _queued.Remove(last.Value);
            _queue.RemoveLast();
        }
    }

    private void EnsureWorker()
    {
        lock (_sync)
        {
            if (_workerRunning || !_enabled || _queue.Count == 0)
            {
                return;
            }

            _workerRunning = true;
        }

        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                string? path;
                CancellationTokenSource cts;
                lock (_sync)
                {
                    if (!_enabled || _queue.Count == 0)
                    {
                        _workerRunning = false;
                        _activePath = null;
                        return;
                    }

                    path = _queue.First!.Value;
                    _queue.RemoveFirst();
                    _queued.Remove(path);
                    _activePath = path;
                    _jobCts = new CancellationTokenSource();
                    cts = _jobCts;
                }

                try
                {
                    if (App.Stems.TryGetCachedInstrumental(path) is null)
                    {
                        await App.Stems
                            .GetOrCreateInstrumentalAsync(path, progress: null, cts.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Yielded for urgent playback or disabled — requeue if still enabled.
                    lock (_sync)
                    {
                        if (_enabled && File.Exists(path) && App.Stems.TryGetCachedInstrumental(path) is null)
                        {
                            if (_queued.Add(path))
                            {
                                _queue.AddLast(path);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "StemPrefetchService.Process");
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_jobCts, cts))
                        {
                            _jobCts = null;
                            _activePath = null;
                        }
                    }

                    try { cts.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "StemPrefetchService.ProcessQueueAsync");
            lock (_sync)
            {
                _workerRunning = false;
                _activePath = null;
            }
        }
    }
}
