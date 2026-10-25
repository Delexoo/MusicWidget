using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using MusicWidget.Models;

namespace MusicWidget.Services;

public sealed class AudioPlayer : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly object _playbackLock = new();

    private List<Track> _queue = new();
    private int _index = -1;
    private Track? _currentTrack;
    private Media? _loadedMedia;
    private bool _loopCurrent;
    private bool _shuffle;
    private float _rate = 1f;
    private bool _disposed;
    private long _resumeMsAfterUserPause = -1;
    private int _trackEndAdvanceScheduled;
    private long _lastPositionNotifyTicks;
    private EventHandler<EventArgs>? _onPlayingHandler;
    private EventHandler<EventArgs>? _onPausedHandler;
    private static readonly Random _random = new();

    public event EventHandler? PlayStateChanged;
    public event EventHandler? CurrentTrackChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? LoopCurrentChanged;
    public event EventHandler? ShuffleChanged;

    public bool IsPlaying => _player.IsPlaying;
    public long PositionMs => Math.Max(0, _player.Time);
    public long DurationMs => Math.Max(0, _player.Length);
    public Track? CurrentTrack => _currentTrack;
    public bool LoopCurrent => _loopCurrent;
    public bool Shuffle => _shuffle;

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    /// <summary>Playback speed multiplier (0.5–2.0). 1.0 is normal.</summary>
    public float Rate
    {
        get => _rate;
        set => SetRate(value);
    }

    public AudioPlayer()
    {
        // LibVLC must load from disk under libvlc\win-x64 next to the apphost.
        // Use GetFullPath so spaces / relative bases resolve the same as Debug.
        var libvlcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        if (File.Exists(Path.Combine(libvlcDir, "libvlc.dll")))
        {
            Core.Initialize(libvlcDir);
        }
        else
        {
            Core.Initialize();
        }

        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
        _player.EndReached += OnEndReached;
        _onPlayingHandler = (_, _) => PlayStateChanged?.Invoke(this, EventArgs.Empty);
        _onPausedHandler = (_, _) => PlayStateChanged?.Invoke(this, EventArgs.Empty);
        _player.Playing += _onPlayingHandler;
        _player.Paused += _onPausedHandler;
        _player.Stopped += OnStopped;
        _player.TimeChanged += OnTimeChanged;

        _loopCurrent = App.Settings.Current.LoopCurrent;
        _shuffle = App.Settings.Current.Shuffle;
        _player.Volume = App.Settings.Current.Volume;
        // Speed is session-only — always launch at normal (1.0x).
        SetRate(1f);
        App.Settings.Current.PlaybackSpeed = 1.0;
    }

    public void SetRate(float rate)
    {
        var clamped = Math.Clamp(rate, 0.5f, 2.0f);
        // Snap near 1.0 so "normal" is easy to hit on the slider.
        if (Math.Abs(clamped - 1f) < 0.03f)
        {
            clamped = 1f;
        }

        _rate = clamped;
        try
        {
            if (!_disposed)
            {
                _player.SetRate(clamped);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.SetRate");
        }
    }

    public void SetQueue(IEnumerable<Track> tracks, int startIndex = 0)
    {
        lock (_playbackLock)
        {
            _queue = new List<Track>(tracks);
            if (_queue.Count == 0)
            {
                _index = -1;
                _currentTrack = null;
                CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _index = Math.Clamp(startIndex, 0, _queue.Count - 1);
            PlayUnlocked(_queue[_index]);
        }
    }

    /// <summary>
    /// Reorders the active queue to match a playlist without restarting playback.
    /// </summary>
    public void UpdateQueueOrder(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        lock (_playbackLock)
        {
            var currentPath = _currentTrack?.FilePath;
            _queue = tracks.ToList();
            if (string.IsNullOrEmpty(currentPath))
            {
                return;
            }

            _index = _queue.FindIndex(t =>
                string.Equals(t.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
            if (_index < 0)
            {
                _index = 0;
            }
        }
    }

    public void Play(Track track, long resumePositionMs = -1)
    {
        lock (_playbackLock)
        {
            PlayUnlocked(track, resumePositionMs);
        }
    }

    private void PlayUnlocked(Track track, long resumePositionMs = -1)
    {
        if (_disposed || !File.Exists(track.FilePath))
        {
            return;
        }

        try
        {
            if (resumePositionMs < 0)
            {
                _resumeMsAfterUserPause = -1;
            }

            var idx = _queue.FindIndex(t => string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _index = idx;
            }
            else
            {
                _queue.Add(track);
                _index = _queue.Count - 1;
            }

            _currentTrack = track;
            Interlocked.Exchange(ref _trackEndAdvanceScheduled, 0);

            if (resumePositionMs < 0)
            {
                _player.Stop();
                _loadedMedia?.Dispose();
                _loadedMedia = null;
            }

            _loadedMedia = new Media(_libVlc, new Uri(track.FilePath));
            if (!_player.Play(_loadedMedia))
            {
                _loadedMedia.Dispose();
                _loadedMedia = null;
                return;
            }

            // LibVLC can reset rate when switching media — re-apply after Play.
            try
            {
                _player.SetRate(_rate);
            }
            catch
            {
                /* rate re-apply is best-effort */
            }

            if (resumePositionMs >= 0)
            {
                var targetMs = resumePositionMs;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(140);
                    try
                    {
                        if (_disposed || !_player.IsPlaying)
                        {
                            return;
                        }

                        long pos = Math.Max(0, targetMs);
                        var len = _player.Length;
                        if (len > 0)
                        {
                            pos = Math.Min(pos, Math.Max(0, len - 250));
                        }

                        _player.Time = pos;
                    }
                    catch
                    {
                        /* seek is best-effort */
                    }
                });
            }

            CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.Play");
        }
    }

    public void TogglePlayPause()
    {
        lock (_playbackLock)
        {
            if (_currentTrack is null)
            {
                if (TryAutoSelectFirstTrackUnlocked(out var first))
                {
                    PlayUnlocked(first);
                }

                return;
            }

            if (_player.IsPlaying)
            {
                var t = _player.Time;
                _resumeMsAfterUserPause = Math.Max(0, t);
                _player.Stop();
            }
            else
            {
                var resume = _resumeMsAfterUserPause;
                _resumeMsAfterUserPause = -1;
                PlayUnlocked(_currentTrack!, resume);
            }
        }
    }

    public void Next()
    {
        lock (_playbackLock)
        {
            if (_queue.Count == 0)
            {
                if (TryAutoSelectFirstTrackUnlocked(out var first))
                {
                    PlayUnlocked(first);
                }

                return;
            }

            if (_shuffle && _queue.Count > 1)
            {
                var next = _random.Next(_queue.Count - 1);
                if (next >= _index) next++;
                _index = next;
            }
            else
            {
                _index = (_index + 1) % _queue.Count;
            }

            PlayUnlocked(_queue[_index]);
        }
    }

    public void Previous()
    {
        lock (_playbackLock)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            _index = (_index - 1 + _queue.Count) % _queue.Count;
            PlayUnlocked(_queue[_index]);
        }
    }

    public void Stop()
    {
        lock (_playbackLock)
        {
            _player.Stop();
        }
    }

    public void SeekToMilliseconds(long positionMs)
    {
        if (_disposed || _currentTrack is null)
        {
            return;
        }

        try
        {
            var len = _player.Length;
            var pos = Math.Max(0, positionMs);
            if (len > 0)
            {
                pos = Math.Min(pos, Math.Max(0, len - 250));
            }

            _player.Time = pos;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.SeekToMilliseconds");
        }
    }

    public void SetLoopCurrent(bool loop)
    {
        if (_loopCurrent == loop) return;
        _loopCurrent = loop;
        App.Settings.Current.LoopCurrent = loop;
        App.Settings.Save();
        LoopCurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetShuffle(bool shuffle)
    {
        if (_shuffle == shuffle) return;
        _shuffle = shuffle;
        App.Settings.Current.Shuffle = shuffle;
        App.Settings.Save();
        ShuffleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        var now = Environment.TickCount64;
        if (now - _lastPositionNotifyTicks < 250)
        {
            return;
        }

        _lastPositionNotifyTicks = now;
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        PlayStateChanged?.Invoke(this, EventArgs.Empty);

        // Fallback when EndReached does not fire (some formats/codecs on Windows).
        if (_resumeMsAfterUserPause >= 0 || _currentTrack is null)
        {
            return;
        }

        var length = _player.Length;
        var time = _player.Time;
        if (length > 0 && time >= length - 500)
        {
            ScheduleTrackEndAdvance();
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // LibVLC requires changing media on a separate thread from EndReached.
        ScheduleTrackEndAdvance();
    }

    private void ScheduleTrackEndAdvance()
    {
        if (Interlocked.CompareExchange(ref _trackEndAdvanceScheduled, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed)
            {
                Interlocked.Exchange(ref _trackEndAdvanceScheduled, 0);
                return;
            }

            try
            {
                lock (_playbackLock)
                {
                    _player.Stop();

                    if (_loopCurrent && _currentTrack is not null)
                    {
                        PlayUnlocked(_currentTrack);
                    }
                    else
                    {
                        AdvanceQueueAfterTrackEndUnlocked();
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AudioPlayer.ScheduleTrackEndAdvance");
            }
            finally
            {
                Interlocked.Exchange(ref _trackEndAdvanceScheduled, 0);
            }
        });
    }

    private void AdvanceQueueAfterTrackEndUnlocked()
    {
        if (_queue.Count == 0)
        {
            if (TryAutoSelectFirstTrackUnlocked(out var first))
            {
                PlayUnlocked(first);
            }

            return;
        }

        if (_shuffle && _queue.Count > 1)
        {
            var next = _random.Next(_queue.Count - 1);
            if (next >= _index)
            {
                next++;
            }

            _index = next;
        }
        else
        {
            _index = (_index + 1) % _queue.Count;
        }

        PlayUnlocked(_queue[_index]);
    }

    private bool TryAutoSelectFirstTrackUnlocked(out Track track)
    {
        track = null!;
        if (_queue.Count > 0)
        {
            _index = 0;
            track = _queue[0];
            return true;
        }

        // Playlist scans touch disk — only run on the UI thread to avoid background freezes.
        if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() != true)
        {
            return false;
        }

        try
        {
            var current = App.Playlists.GetCurrentPlaylist();
            if (current is null || current.Tracks.Count == 0)
            {
                return false;
            }

            _queue = new List<Track>(current.Tracks);
            _index = 0;
            track = _queue[0];
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.TryAutoSelectFirstTrack");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            lock (_playbackLock)
            {
                _player.Stop();
            }

            _player.EndReached -= OnEndReached;
            _player.TimeChanged -= OnTimeChanged;
            _player.Stopped -= OnStopped;
            if (_onPlayingHandler is not null)
            {
                _player.Playing -= _onPlayingHandler;
            }

            if (_onPausedHandler is not null)
            {
                _player.Paused -= _onPausedHandler;
            }

            _loadedMedia?.Dispose();
            _loadedMedia = null;
            _player.Dispose();
            _libVlc.Dispose();
        }
        catch { /* shutdown best-effort */ }
    }
}
