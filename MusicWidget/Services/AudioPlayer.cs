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
    private LibVLC _libVlc = null!;
    private MediaPlayer _player = null!;
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
    private Equalizer? _equalizer;
    private int _bassBoostLevel; // 0–3
    private bool _reverb;
    private bool _vocalRemover;
    private bool _playingInstrumental;
    private string _engineFilterKey = "";
    private int _volume = 80;
    private CancellationTokenSource? _vocalSepCts;
    private int _vocalSepGeneration;
    private static readonly Random _random = new();

    public event EventHandler? PlayStateChanged;
    public event EventHandler? CurrentTrackChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? LoopCurrentChanged;
    public event EventHandler? ShuffleChanged;
    public event EventHandler? EffectsChanged;
    public event EventHandler<VocalSeparationEventArgs>? VocalSeparationChanged;

    public bool IsPlaying => _player.IsPlaying;
    public long PositionMs => Math.Max(0, _player.Time);
    public long DurationMs => Math.Max(0, _player.Length);
    public Track? CurrentTrack => _currentTrack;
    public bool LoopCurrent => _loopCurrent;
    public bool Shuffle => _shuffle;
    public int BassBoostLevel => _bassBoostLevel;
    public bool Reverb => _reverb;
    public bool VocalRemover => _vocalRemover;
    public bool IsPlayingInstrumental => _playingInstrumental;
    public bool IsVocalSeparationBusy { get; private set; }

    /// <summary>LibVLC software volume percent. 100 = nominal; up to <see cref="MaxVolume"/> for boost.</summary>
    public const int MaxVolume = 200;

    public int Volume
    {
        get => _volume;
        set
        {
            var next = Math.Clamp(value, 0, MaxVolume);
            var headroomChanged = (_volume > 100) != (next > 100)
                || Math.Abs(_volume - next) >= 5;
            _volume = next;
            try
            {
                if (!_disposed)
                {
                    _player.Volume = _volume;
                    // Refresh EQ headroom when boosting above 100% so volume swings don't clip.
                    if (headroomChanged)
                    {
                        ApplyEqualizer();
                    }
                }
            }
            catch
            {
                /* volume apply is best-effort */
            }
        }
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

        _bassBoostLevel = Math.Clamp(App.Settings.Current.BassBoostLevel, 0, 3);
        _reverb = App.Settings.Current.Reverb;
        _vocalRemover = App.Settings.Current.VocalRemover;
        _volume = Math.Clamp(App.Settings.Current.Volume, 0, MaxVolume);

        CreateEngine(BuildFilterKey(_reverb), wireEvents: true);

        _loopCurrent = App.Settings.Current.LoopCurrent;
        _shuffle = App.Settings.Current.Shuffle;
        // Speed is session-only — always launch at normal (1.0x).
        SetRate(1f);

        App.Settings.Current.PlaybackSpeed = _rate;
        ApplyEqualizer();
    }

    /// <summary>Cycles bass boost: Off → 1 → 2 → 3 → Off.</summary>
    public void CycleBassBoostLevel()
    {
        lock (_playbackLock)
        {
            _bassBoostLevel = (_bassBoostLevel + 1) % 4;
            App.Settings.Current.BassBoostLevel = _bassBoostLevel;
            App.Settings.ScheduleSave();
            ApplyEqualizer();
        }

        EffectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetBassBoostLevel(int level)
    {
        level = Math.Clamp(level, 0, 3);
        if (_bassBoostLevel == level) return;
        lock (_playbackLock)
        {
            _bassBoostLevel = level;
            App.Settings.Current.BassBoostLevel = level;
            App.Settings.ScheduleSave();
            ApplyEqualizer();
        }

        EffectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetReverb(bool enabled)
    {
        if (_reverb == enabled) return;
        lock (_playbackLock)
        {
            _reverb = enabled;
            App.Settings.Current.Reverb = enabled;
            App.Settings.ScheduleSave();
            EnsureEngineForEffectsUnlocked();
            ApplyEqualizer();
        }

        EffectsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Toggles AI instrumental playback (cached demucs-onnx karaoke). Does not change playlist files.
    /// </summary>
    public void SetVocalRemover(bool enabled)
    {
        if (_vocalRemover == enabled) return;

        Track? track;
        long resume;
        bool resumePlayback;
        lock (_playbackLock)
        {
            _vocalRemover = enabled;
            App.Settings.Current.VocalRemover = enabled;
            App.Settings.ScheduleSave();
            track = _currentTrack;
            resume = Math.Max(0, _player.IsPlaying || _resumeMsAfterUserPause < 0
                ? _player.Time
                : _resumeMsAfterUserPause);
            resumePlayback = track is not null && (_player.IsPlaying || _resumeMsAfterUserPause >= 0);
        }

        EffectsChanged?.Invoke(this, EventArgs.Empty);

        App.StemPrefetch.SetEnabled(enabled);
        if (enabled)
        {
            _ = App.StemPrefetch.WarmToolsAsync();
        }

        if (track is null)
        {
            CancelVocalSeparation();
            return;
        }

        CancelVocalSeparation();
        lock (_playbackLock)
        {
            if (enabled)
            {
                App.StemPrefetch.EnqueueAround(_queue, Math.Max(0, _index));
            }

            PlayUnlocked(track, resumePlayback ? resume : -1);
        }
    }

    private void CancelVocalSeparation()
    {
        try
        {
            _vocalSepCts?.Cancel();
        }
        catch { /* ignore */ }

        _vocalSepCts = null;
        if (IsVocalSeparationBusy)
        {
            IsVocalSeparationBusy = false;
            VocalSeparationChanged?.Invoke(this, new VocalSeparationEventArgs(false, null, _currentTrack));
        }
    }

    private static string BuildFilterKey(bool reverb) =>
        reverb ? "spatializer" : "";

    private void EnsureEngineForEffectsUnlocked()
    {
        var key = BuildFilterKey(_reverb);
        if (string.Equals(key, _engineFilterKey, StringComparison.Ordinal))
        {
            return;
        }

        var track = _currentTrack;
        var resume = track is null ? -1L : Math.Max(0, _player.IsPlaying || _resumeMsAfterUserPause < 0
            ? _player.Time
            : _resumeMsAfterUserPause);
        var wasPlaying = track is not null && (_player.IsPlaying || _resumeMsAfterUserPause >= 0);

        CreateEngine(key, wireEvents: true);

        if (wasPlaying && track is not null)
        {
            PlayUnlocked(track, resume);
        }
    }

    private void CreateEngine(string filterKey, bool wireEvents)
    {
        TearDownEngine(unsubscribe: true);

        var args = new List<string>();
        if (!string.IsNullOrEmpty(filterKey))
        {
            args.Add("--audio-filter=" + filterKey);
        }

        if (filterKey.Contains("spatializer", StringComparison.Ordinal))
        {
            args.Add("--spatializer-roomsize=0.95");
            args.Add("--spatializer-width=0.90");
            args.Add("--spatializer-wet=0.55");
            args.Add("--spatializer-dry=0.55");
            args.Add("--spatializer-damp=0.30");
        }

        _libVlc = args.Count == 0 ? new LibVLC() : new LibVLC(args.ToArray());
        _player = new MediaPlayer(_libVlc);
        _engineFilterKey = filterKey;

        if (wireEvents)
        {
            WirePlayerEvents();
        }

        try
        {
            _player.Volume = _volume;
            _player.SetRate(_rate);
        }
        catch
        {
            /* apply after create is best-effort */
        }
    }

    private void WirePlayerEvents()
    {
        _onPlayingHandler = (_, _) => PlayStateChanged?.Invoke(this, EventArgs.Empty);
        _onPausedHandler = (_, _) => PlayStateChanged?.Invoke(this, EventArgs.Empty);
        _player.EndReached += OnEndReached;
        _player.Playing += _onPlayingHandler;
        _player.Paused += _onPausedHandler;
        _player.Stopped += OnStopped;
        _player.TimeChanged += OnTimeChanged;
    }

    private void TearDownEngine(bool unsubscribe)
    {
        try
        {
            if (_player is not null)
            {
                if (unsubscribe)
                {
                    try
                    {
                        _player.EndReached -= OnEndReached;
                        _player.TimeChanged -= OnTimeChanged;
                        _player.Stopped -= OnStopped;
                        if (_onPlayingHandler is not null) _player.Playing -= _onPlayingHandler;
                        if (_onPausedHandler is not null) _player.Paused -= _onPausedHandler;
                    }
                    catch
                    {
                        /* unsubscribe best-effort */
                    }
                }

                try { _player.Stop(); } catch { /* ignore */ }
                _loadedMedia?.Dispose();
                _loadedMedia = null;
                _player.Dispose();
            }

            _equalizer?.Dispose();
            _equalizer = null;
            _libVlc?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.TearDownEngine");
        }
    }

    /// <summary>Builds the live equalizer curve (3-level bass boost + light reverb air).</summary>
    private void ApplyEqualizer()
    {
        if (_disposed || _player is null)
        {
            return;
        }

        try
        {
            var eq = new Equalizer();
            var bandCount = (int)eq.BandCount;

            // Start flat. Typical 10-band: 60, 170, 310, 600, 1k, 3k, 6k, 12k, 14k, 16k
            for (uint i = 0; i < bandCount; i++)
            {
                eq.SetAmp(0f, i);
            }

            eq.SetPreamp(0f);

            // Extra headroom when software volume is boosted above 100% (prevents digital clip/static).
            var volumeHeadroom = _volume > 100
                ? -((_volume - 100) / 100f) * 6f
                : 0f;

            if (_bassBoostLevel > 0)
            {
                // Level 1 mild → 2 strong → 3 extreme. Negative preamp = headroom so boosts don't clip.
                var (b0, b1, b2, b3, b4, pre) = _bassBoostLevel switch
                {
                    1 => (8f, 6f, 3f, 1f, 0f, -2.5f),
                    2 => (14f, 11f, 7f, 3f, 0f, -5f),
                    // Level 3: heavy low end, deep preamp cut so it stays loud without static.
                    _ => (20f, 18f, 13f, 7f, 2f, -9f),
                };
                if (bandCount > 0) eq.SetAmp(b0, 0);
                if (bandCount > 1) eq.SetAmp(b1, 1);
                if (bandCount > 2) eq.SetAmp(b2, 2);
                if (bandCount > 3) eq.SetAmp(b3, 3);
                if (bandCount > 4) eq.SetAmp(b4, 4);
                eq.SetPreamp(pre + volumeHeadroom);
            }
            else if (volumeHeadroom < 0f)
            {
                eq.SetPreamp(volumeHeadroom);
            }

            if (_reverb)
            {
                // Slight treble air so the spatializer reads as a clear hall.
                if (bandCount > 7) eq.SetAmp(Math.Max(eq.Amp(7), 2f), 7);
                if (bandCount > 8) eq.SetAmp(Math.Max(eq.Amp(8), 1.5f), 8);
            }

            _player.SetEqualizer(eq);
            _equalizer?.Dispose();
            _equalizer = eq;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.ApplyEqualizer");
        }
    }

    public void SetRate(float rate, bool snapNearNormal = false)
    {
        var clamped = Math.Clamp(rate, 0.5f, 2.0f);
        // Snap near 1.0 only when the user finishes dragging — snapping mid-drag
        // made the speed slider jump and fight the thumb.
        if (snapNearNormal && Math.Abs(clamped - 1f) < 0.05f)
        {
            clamped = 1f;
        }

        if (Math.Abs(_rate - clamped) < 0.0005f)
        {
            return;
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

            var mediaPath = track.FilePath;
            _playingInstrumental = false;

            if (_vocalRemover)
            {
                // Prefetch this track + next few so upcoming plays are already cached.
                App.StemPrefetch.EnqueueAround(_queue, Math.Max(0, _index));

                var cached = App.Stems.TryGetCachedInstrumental(track.FilePath);
                if (cached is not null)
                {
                    mediaPath = cached;
                    _playingInstrumental = true;
                }
                else
                {
                    App.StemPrefetch.YieldForUrgent(track.FilePath);
                    try { _player.Stop(); } catch { /* ignore */ }
                    CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
                    PositionChanged?.Invoke(this, EventArgs.Empty);
                    BeginVocalSeparationThenPlay(track, resumePositionMs);
                    return;
                }
            }

            PlayMediaPathUnlocked(track, mediaPath, resumePositionMs);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.Play");
        }
    }

    private void PlayMediaPathUnlocked(Track track, string mediaPath, long resumePositionMs)
    {
        if (_disposed || !File.Exists(mediaPath))
        {
            return;
        }

        if (resumePositionMs < 0)
        {
            _player.Stop();
            _loadedMedia?.Dispose();
            _loadedMedia = null;
        }

        _loadedMedia = new Media(_libVlc, new Uri(mediaPath));
        if (!_player.Play(_loadedMedia))
        {
            _loadedMedia.Dispose();
            _loadedMedia = null;
            return;
        }

        try
        {
            _player.SetRate(_rate);
            _player.Volume = _volume;
            ApplyEqualizer();
        }
        catch
        {
            /* rate / FX re-apply is best-effort */
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

    private void BeginVocalSeparationThenPlay(Track track, long resumePositionMs)
    {
        CancelVocalSeparation();
        var cts = new CancellationTokenSource();
        _vocalSepCts = cts;
        var generation = Interlocked.Increment(ref _vocalSepGeneration);
        IsVocalSeparationBusy = true;
        VocalSeparationChanged?.Invoke(this,
            new VocalSeparationEventArgs(true, "Removing vocals (AI)…", track));

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<DownloadProgressUpdate>(u =>
                {
                    if (generation != Volatile.Read(ref _vocalSepGeneration)) return;
                    var msg = string.IsNullOrWhiteSpace(u.Message) ? "Removing vocals…" : u.Message;
                    VocalSeparationChanged?.Invoke(this,
                        new VocalSeparationEventArgs(true, msg, track));
                });

                var instrumental = await App.Stems
                    .GetOrCreateInstrumentalAsync(track.FilePath, progress, cts.Token)
                    .ConfigureAwait(false);

                if (cts.IsCancellationRequested || generation != Volatile.Read(ref _vocalSepGeneration))
                {
                    return;
                }

                lock (_playbackLock)
                {
                    if (_disposed || !_vocalRemover)
                    {
                        return;
                    }

                    if (_currentTrack is null
                        || !string.Equals(_currentTrack.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _playingInstrumental = true;
                    PlayMediaPathUnlocked(track, instrumental, resumePositionMs);
                }

                IsVocalSeparationBusy = false;
                VocalSeparationChanged?.Invoke(this,
                    new VocalSeparationEventArgs(false, null, track));
            }
            catch (OperationCanceledException)
            {
                IsVocalSeparationBusy = false;
                VocalSeparationChanged?.Invoke(this,
                    new VocalSeparationEventArgs(false, null, track));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "AudioPlayer.VocalSeparation");
                IsVocalSeparationBusy = false;
                VocalSeparationChanged?.Invoke(this,
                    new VocalSeparationEventArgs(false, ex.Message, track, failed: true));

                // Fall back to original audio so the user is not stuck silent.
                lock (_playbackLock)
                {
                    if (_disposed || !_vocalRemover) return;
                    if (_currentTrack is null
                        || !string.Equals(_currentTrack.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _playingInstrumental = false;
                    PlayMediaPathUnlocked(track, track.FilePath, resumePositionMs);
                }
            }
        });
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
        CancelVocalSeparation();
        try
        {
            lock (_playbackLock)
            {
                TearDownEngine(unsubscribe: true);
            }
        }
        catch { /* shutdown best-effort */ }
    }
}
