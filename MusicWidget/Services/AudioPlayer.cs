using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using MusicWidget.Models;

namespace MusicWidget.Services;

/// <summary>
/// Dual-slot LibVLC player with live EFX (always-on spatializer via media options),
/// continuous reverb, lowshelf-style bass, and crossfade stem swaps.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const int CrossfadeMs = 70;
    private static readonly float[] BassShelfDb = { 0f, 7f, 12f, 16f };

    private LibVLC _libVlc = null!;
    private readonly MediaPlayer[] _players = new MediaPlayer[2];
    private readonly Media?[] _loadedMedia = new Media?[2];
    private int _activeIndex;
    private readonly object _playbackLock = new();

    private List<Track> _queue = new();
    private int _index = -1;
    private Track? _currentTrack;
    private string? _activeMediaPath;
    private bool _loopCurrent;
    private bool _shuffle;
    private float _rate = 1f;
    private bool _disposed;
    private long _resumeMsAfterUserPause = -1;
    private int _trackEndAdvanceScheduled;
    private long _lastPositionNotifyTicks;
    private Equalizer? _equalizer;
    private int _bassBoostLevel;
    private float _reverbAmount;
    private bool _vocalRemoverDefault;
    private bool _playingInstrumental;
    private int _volume = 100;
    private CancellationTokenSource? _vocalSepCts;
    private CancellationTokenSource? _crossfadeCts;
    private int _vocalSepGeneration;
    private int _playbackGeneration;
    private int _crossfadeBusy;
    private static readonly Random _random = new();

    private EventHandler<EventArgs>?[] _onPlayingHandlers = new EventHandler<EventArgs>?[2];
    private EventHandler<EventArgs>?[] _onPausedHandlers = new EventHandler<EventArgs>?[2];

    public event EventHandler? PlayStateChanged;
    public event EventHandler? CurrentTrackChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? LoopCurrentChanged;
    public event EventHandler? ShuffleChanged;
    public event EventHandler? EffectsChanged;
    public event EventHandler<VocalSeparationEventArgs>? VocalSeparationChanged;

    private MediaPlayer ActivePlayer => _players[_activeIndex];

    public bool IsPlaying => ActivePlayer.IsPlaying;
    public long PositionMs => Math.Max(0, ActivePlayer.Time);
    public long DurationMs => Math.Max(0, ActivePlayer.Length);
    public Track? CurrentTrack => _currentTrack;
    public bool LoopCurrent => _loopCurrent;
    public bool Shuffle => _shuffle;
    public int BassBoostLevel => _bassBoostLevel;
    public float ReverbAmount => _reverbAmount;
    /// <summary>True when reverb wet is audible.</summary>
    public bool Reverb => _reverbAmount > 0.001f;
    public bool VocalRemover => _vocalRemoverDefault;
    public bool IsPlayingInstrumental => _playingInstrumental;
    public bool IsVocalSeparationBusy { get; private set; }

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
                if (!_disposed && Volatile.Read(ref _crossfadeBusy) == 0)
                {
                    ActivePlayer.Volume = _volume;
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

    public float Rate
    {
        get => _rate;
        set => SetRate(value);
    }

    public AudioPlayer()
    {
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
        _reverbAmount = (float)Math.Clamp(App.Settings.Current.ReverbAmount, 0, 1);
        _vocalRemoverDefault = App.Settings.Current.VocalRemover;
        _volume = 100;
        App.Settings.Current.Volume = 100;

        CreateEngine();

        _loopCurrent = App.Settings.Current.LoopCurrent;
        _shuffle = App.Settings.Current.Shuffle;
        SetRate(1f);
        App.Settings.Current.PlaybackSpeed = _rate;
        ApplyEqualizer();
    }

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

    /// <summary>Cycles reverb: Off → 25% → 50% → 75% → 100% → Off.</summary>
    public void CycleReverbAmount()
    {
        var next = _reverbAmount switch
        {
            < 0.01f => 0.25f,
            < 0.30f => 0.50f,
            < 0.55f => 0.75f,
            < 0.90f => 1.00f,
            _ => 0f,
        };
        SetReverbAmount(next);
    }

    /// <summary>Legacy bool API — maps to 0 or 0.55.</summary>
    public void SetReverb(bool enabled) =>
        SetReverbAmount(enabled ? Math.Max(_reverbAmount, 0.55f) : 0f);

    public void SetReverbAmount(float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        if (Math.Abs(_reverbAmount - amount) < 0.0005f) return;

        Track? track;
        string? mediaPath;
        long resume;
        bool wasPlaying;
        lock (_playbackLock)
        {
            _reverbAmount = amount;
            App.Settings.Current.ReverbAmount = amount;
            App.Settings.ScheduleSave();
            track = _currentTrack;
            mediaPath = _activeMediaPath;
            resume = Math.Max(0, ActivePlayer.IsPlaying || _resumeMsAfterUserPause < 0
                ? ActivePlayer.Time
                : _resumeMsAfterUserPause);
            wasPlaying = track is not null && (ActivePlayer.IsPlaying || _resumeMsAfterUserPause >= 0);
            ApplyEqualizer();
        }

        EffectsChanged?.Invoke(this, EventArgs.Empty);

        // Spatializer is applied as media options — crossfade same path to hot-swap wet/dry.
        if (wasPlaying && track is not null && !string.IsNullOrEmpty(mediaPath) && File.Exists(mediaPath))
        {
            _ = CrossfadeToPathAsync(track, mediaPath, resume, keepInstrumental: _playingInstrumental);
        }
    }

    /// <summary>
    /// Toggles AI instrumental for the current track (and updates the global default).
    /// </summary>
    public void SetVocalRemover(bool enabled)
    {
        Track? track;
        long resume;
        bool resumePlayback;
        lock (_playbackLock)
        {
            _vocalRemoverDefault = enabled;
            App.Settings.Current.VocalRemover = enabled;
            track = _currentTrack;
            if (track is not null)
            {
                SetVocalsOffForPath(track.FilePath, enabled);
            }
            else
            {
                App.Settings.ScheduleSave();
            }

            resume = Math.Max(0, ActivePlayer.IsPlaying || _resumeMsAfterUserPause < 0
                ? ActivePlayer.Time
                : _resumeMsAfterUserPause);
            resumePlayback = track is not null && (ActivePlayer.IsPlaying || _resumeMsAfterUserPause >= 0);
        }

        EffectsChanged?.Invoke(this, EventArgs.Empty);

        App.StemPrefetch.SetEnabled(enabled || HasAnyVocalsOff());
        if (enabled)
        {
            _ = App.StemPrefetch.WarmToolsAsync();
            if (track is not null)
            {
                App.StemPrefetch.Enqueue(track.FilePath, highPriority: true);
            }
        }

        if (track is null)
        {
            CancelVocalSeparation();
            return;
        }

        CancelVocalSeparation();
        if (resumePlayback)
        {
            _ = SwapVocalSourceAsync(track, resume, enabled);
        }
        else
        {
            lock (_playbackLock)
            {
                PlayUnlocked(track, -1);
            }
        }
    }

    public bool IsVocalsOffForTrack(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return _vocalRemoverDefault;
        }

        var map = App.Settings.Current.VocalsOffByPath;
        if (map is not null && map.TryGetValue(filePath, out var off))
        {
            return off;
        }

        return _vocalRemoverDefault;
    }

    private void SetVocalsOffForPath(string filePath, bool off)
    {
        var map = App.Settings.Current.VocalsOffByPath
                  ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        App.Settings.Current.VocalsOffByPath = map;
        map[filePath] = off;
        App.Settings.ScheduleSave();
    }

    private static bool HasAnyVocalsOff()
    {
        var map = App.Settings.Current.VocalsOffByPath;
        if (map is null || map.Count == 0) return false;
        foreach (var kv in map)
        {
            if (kv.Value) return true;
        }

        return false;
    }

    private void CancelVocalSeparation()
    {
        try { _vocalSepCts?.Cancel(); } catch { /* ignore */ }
        _vocalSepCts = null;
        if (IsVocalSeparationBusy)
        {
            IsVocalSeparationBusy = false;
            VocalSeparationChanged?.Invoke(this, new VocalSeparationEventArgs(false, null, _currentTrack));
        }
    }

    private void CreateEngine()
    {
        TearDownEngine(unsubscribe: true);

        // One LibVLC for both slots. Spatializer is attached per-media so wet can change
        // without tearing down the shared engine.
        _libVlc = new LibVLC();
        for (var i = 0; i < 2; i++)
        {
            _players[i] = new MediaPlayer(_libVlc);
            WirePlayerEvents(i);
            try
            {
                _players[i].Volume = i == _activeIndex ? _volume : 0;
                _players[i].SetRate(_rate);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    private void WirePlayerEvents(int slot)
    {
        var player = _players[slot];
        _onPlayingHandlers[slot] = (_, _) =>
        {
            if (slot != _activeIndex) return;
            ReapplyPlaybackState();
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
        };
        _onPausedHandlers[slot] = (_, _) =>
        {
            if (slot != _activeIndex) return;
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
        };
        player.EndReached += OnEndReached;
        player.Playing += _onPlayingHandlers[slot]!;
        player.Paused += _onPausedHandlers[slot]!;
        player.Stopped += OnStopped;
        player.TimeChanged += OnTimeChanged;
    }

    private void ReapplyPlaybackState()
    {
        if (_disposed) return;
        try
        {
            var active = ActivePlayer;
            if (Volatile.Read(ref _crossfadeBusy) == 0)
            {
                active.Volume = _volume;
            }

            active.SetRate(_rate);
            ApplyEqualizer();
        }
        catch
        {
            /* best-effort */
        }
    }

    private void TearDownEngine(bool unsubscribe)
    {
        try
        {
            try { _crossfadeCts?.Cancel(); } catch { /* ignore */ }
            _crossfadeCts = null;

            for (var i = 0; i < 2; i++)
            {
                var player = _players[i];
                if (player is null) continue;
                if (unsubscribe)
                {
                    try
                    {
                        player.EndReached -= OnEndReached;
                        player.TimeChanged -= OnTimeChanged;
                        player.Stopped -= OnStopped;
                        if (_onPlayingHandlers[i] is not null) player.Playing -= _onPlayingHandlers[i];
                        if (_onPausedHandlers[i] is not null) player.Paused -= _onPausedHandlers[i];
                    }
                    catch { /* unsubscribe best-effort */ }
                }

                try { player.Stop(); } catch { /* ignore */ }
                _loadedMedia[i]?.Dispose();
                _loadedMedia[i] = null;
                player.Dispose();
                _players[i] = null!;
            }

            _equalizer?.Dispose();
            _equalizer = null;
            _libVlc?.Dispose();
            _libVlc = null!;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.TearDownEngine");
        }
    }

    private void ApplyEqualizer()
    {
        if (_disposed || ActivePlayer is null) return;

        try
        {
            var eq = new Equalizer();
            var bandCount = (int)eq.BandCount;
            for (uint i = 0; i < bandCount; i++)
            {
                eq.SetAmp(0f, i);
            }

            eq.SetPreamp(0f);

            var volumeHeadroom = _volume > 100
                ? -((_volume - 100) / 100f) * 6f
                : 0f;

            // Mochi-style lowshelf (~180 Hz) approximated on VLC's first bands.
            var shelfDb = BassShelfDb[Math.Clamp(_bassBoostLevel, 0, 3)];
            if (shelfDb > 0.01f)
            {
                var pre = -(shelfDb * 0.45f);
                if (bandCount > 0) eq.SetAmp(shelfDb, 0);
                if (bandCount > 1) eq.SetAmp(shelfDb * 0.72f, 1);
                if (bandCount > 2) eq.SetAmp(shelfDb * 0.35f, 2);
                if (bandCount > 3) eq.SetAmp(shelfDb * 0.12f, 3);
                eq.SetPreamp(pre + volumeHeadroom);
            }
            else if (volumeHeadroom < 0f)
            {
                eq.SetPreamp(volumeHeadroom);
            }

            if (_reverbAmount > 0.001f)
            {
                var air = 1.2f + _reverbAmount * 1.2f;
                if (bandCount > 7) eq.SetAmp(Math.Max(eq.Amp(7), air), 7);
                if (bandCount > 8) eq.SetAmp(Math.Max(eq.Amp(8), air * 0.75f), 8);
            }

            ActivePlayer.SetEqualizer(eq);
            // Keep inactive slot EQ in sync for seamless crossfade.
            var other = _players[1 - _activeIndex];
            try { other?.SetEqualizer(eq); } catch { /* best-effort */ }

            _equalizer?.Dispose();
            _equalizer = eq;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.ApplyEqualizer");
        }
    }

    private void ApplySpatializerOptions(Media media)
    {
        var amount = Math.Clamp(_reverbAmount, 0f, 1f);
        if (amount <= 0.001f)
        {
            return;
        }

        // Continuous wet/dry mapped like Mochi's delay-feedback balance.
        var wet = amount * 0.55f;
        var dry = 1f - amount * 0.28f;
        media.AddOption(":audio-filter=spatializer");
        media.AddOption(":spatializer-roomsize=0.95");
        media.AddOption(":spatializer-width=0.90");
        media.AddOption($":spatializer-wet={wet:0.###}");
        media.AddOption($":spatializer-dry={dry:0.###}");
        media.AddOption(":spatializer-damp=0.30");
    }

    public void SetRate(float rate, bool snapNearNormal = false)
    {
        var clamped = Math.Clamp(rate, 0.5f, 2.0f);
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
                foreach (var p in _players)
                {
                    try { p?.SetRate(clamped); } catch { /* ignore */ }
                }
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

    public void UpdateQueueOrder(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;

        lock (_playbackLock)
        {
            var currentPath = _currentTrack?.FilePath;
            _queue = tracks.ToList();
            if (string.IsNullOrEmpty(currentPath)) return;

            _index = _queue.FindIndex(t =>
                string.Equals(t.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
            if (_index < 0) _index = 0;
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
            Interlocked.Increment(ref _playbackGeneration);

            if (resumePositionMs < 0)
            {
                _resumeMsAfterUserPause = -1;
            }

            var idx = _queue.FindIndex(t =>
                string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase));
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

            var vocalsOff = IsVocalsOffForTrack(track.FilePath);
            var mediaPath = track.FilePath;
            _playingInstrumental = false;

            if (vocalsOff)
            {
                App.StemPrefetch.SetEnabled(true);
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
                    try { ActivePlayer.Stop(); } catch { /* ignore */ }
                    CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
                    PositionChanged?.Invoke(this, EventArgs.Empty);
                    BeginVocalSeparationThenPlay(track, resumePositionMs);
                    return;
                }
            }

            PlayMediaPathUnlocked(track, mediaPath, resumePositionMs, hardCut: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.Play");
        }
    }

    private void PlayMediaPathUnlocked(Track track, string mediaPath, long resumePositionMs, bool hardCut)
    {
        if (_disposed || !File.Exists(mediaPath))
        {
            return;
        }

        if (hardCut)
        {
            var generation = Volatile.Read(ref _playbackGeneration);
            var slot = _activeIndex;
            try { _players[1 - slot]?.Stop(); } catch { /* ignore */ }

            _loadedMedia[slot]?.Dispose();
            _loadedMedia[slot] = null;

            var media = new Media(_libVlc, new Uri(mediaPath));
            ApplySpatializerOptions(media);
            _loadedMedia[slot] = media;
            _activeMediaPath = mediaPath;

            var player = _players[slot];
            player.Volume = _volume;
            if (!player.Play(media))
            {
                media.Dispose();
                _loadedMedia[slot] = null;
                return;
            }

            ReapplyPlaybackState();
            ScheduleSeekAfterPlay(player, resumePositionMs, generation);

            CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
            PositionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = CrossfadeToPathAsync(track, mediaPath, resumePositionMs, keepInstrumental: _playingInstrumental);
    }

    private void ScheduleSeekAfterPlay(MediaPlayer player, long resumePositionMs, int generation)
    {
        if (resumePositionMs < 0) return;

        var targetMs = resumePositionMs;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 25; i++)
                {
                    if (_disposed || generation != Volatile.Read(ref _playbackGeneration))
                    {
                        return;
                    }

                    await Task.Delay(40).ConfigureAwait(false);
                    if (!player.IsPlaying) continue;

                    long pos = Math.Max(0, targetMs);
                    var len = player.Length;
                    if (len > 0)
                    {
                        pos = Math.Min(pos, Math.Max(0, len - 250));
                    }

                    player.Time = pos;
                    ReapplyPlaybackState();
                    return;
                }
            }
            catch
            {
                /* seek is best-effort */
            }
        });
    }

    private async Task CrossfadeToPathAsync(
        Track track,
        string mediaPath,
        long resumePositionMs,
        bool keepInstrumental)
    {
        var generation = Interlocked.Increment(ref _playbackGeneration);
        try { _crossfadeCts?.Cancel(); } catch { /* ignore */ }
        var cts = new CancellationTokenSource();
        _crossfadeCts = cts;

        Interlocked.Exchange(ref _crossfadeBusy, 1);
        try
        {
            int from;
            int to;
            MediaPlayer outgoing;
            MediaPlayer incoming;
            bool wasPlaying;
            long atMs;

            lock (_playbackLock)
            {
                if (_disposed || generation != Volatile.Read(ref _playbackGeneration))
                {
                    return;
                }

                from = _activeIndex;
                to = 1 - from;
                outgoing = _players[from];
                incoming = _players[to];
                wasPlaying = outgoing.IsPlaying || _resumeMsAfterUserPause >= 0;
                atMs = resumePositionMs >= 0
                    ? resumePositionMs
                    : Math.Max(0, outgoing.IsPlaying || _resumeMsAfterUserPause < 0
                        ? outgoing.Time
                        : _resumeMsAfterUserPause);

                _loadedMedia[to]?.Dispose();
                _loadedMedia[to] = null;

                var media = new Media(_libVlc, new Uri(mediaPath));
                ApplySpatializerOptions(media);
                _loadedMedia[to] = media;
                incoming.Volume = 0;
                incoming.SetRate(_rate);
                if (!incoming.Play(media))
                {
                    media.Dispose();
                    _loadedMedia[to] = null;
                    return;
                }
            }

            // Wait until the incoming slot can seek, then align playheads.
            for (var i = 0; i < 40; i++)
            {
                if (cts.IsCancellationRequested || generation != Volatile.Read(ref _playbackGeneration))
                {
                    return;
                }

                await Task.Delay(25, cts.Token).ConfigureAwait(false);
                if (incoming.IsPlaying || incoming.Length > 0)
                {
                    break;
                }
            }

            try
            {
                var len = incoming.Length;
                var pos = Math.Max(0, atMs);
                if (len > 0)
                {
                    pos = Math.Min(pos, Math.Max(0, len - 250));
                }

                incoming.Time = pos;
            }
            catch
            {
                /* seek best-effort */
            }

            if (!wasPlaying)
            {
                lock (_playbackLock)
                {
                    if (generation != Volatile.Read(ref _playbackGeneration)) return;
                    _activeIndex = to;
                    _activeMediaPath = mediaPath;
                    _playingInstrumental = keepInstrumental;
                    _currentTrack = track;
                    incoming.Volume = _volume;
                    try { outgoing.Stop(); } catch { /* ignore */ }
                    ApplyEqualizer();
                }

                CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
                PositionChanged?.Invoke(this, EventArgs.Empty);
                PlayStateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var steps = Math.Max(4, CrossfadeMs / 16);
            for (var s = 1; s <= steps; s++)
            {
                if (cts.IsCancellationRequested || generation != Volatile.Read(ref _playbackGeneration))
                {
                    return;
                }

                var t = (float)s / steps;
                try
                {
                    incoming.Volume = (int)Math.Round(_volume * t);
                    outgoing.Volume = (int)Math.Round(_volume * (1f - t));
                }
                catch
                {
                    /* ignore */
                }

                await Task.Delay(Math.Max(1, CrossfadeMs / steps), cts.Token).ConfigureAwait(false);
            }

            lock (_playbackLock)
            {
                if (generation != Volatile.Read(ref _playbackGeneration)) return;
                _activeIndex = to;
                _activeMediaPath = mediaPath;
                _playingInstrumental = keepInstrumental;
                _currentTrack = track;
                try { incoming.Volume = _volume; } catch { /* ignore */ }
                try { outgoing.Volume = 0; } catch { /* ignore */ }
                try { outgoing.Stop(); } catch { /* ignore */ }
                ApplyEqualizer();
            }

            CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
            PositionChanged?.Invoke(this, EventArgs.Empty);
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            /* superseded */
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AudioPlayer.CrossfadeToPath");
        }
        finally
        {
            Interlocked.Exchange(ref _crossfadeBusy, 0);
        }
    }

    private async Task SwapVocalSourceAsync(Track track, long resumeMs, bool vocalsOff)
    {
        var generation = Interlocked.Increment(ref _playbackGeneration);
        if (!vocalsOff)
        {
            _playingInstrumental = false;
            await CrossfadeToPathAsync(track, track.FilePath, resumeMs, keepInstrumental: false)
                .ConfigureAwait(false);
            return;
        }

        var cached = App.Stems.TryGetCachedInstrumental(track.FilePath);
        if (cached is not null)
        {
            _playingInstrumental = true;
            await CrossfadeToPathAsync(track, cached, resumeMs, keepInstrumental: true)
                .ConfigureAwait(false);
            return;
        }

        if (generation != Volatile.Read(ref _playbackGeneration))
        {
            return;
        }

        BeginVocalSeparationThenPlay(track, resumeMs);
    }

    private void BeginVocalSeparationThenPlay(Track track, long resumePositionMs)
    {
        CancelVocalSeparation();
        var cts = new CancellationTokenSource();
        _vocalSepCts = cts;
        var generation = Interlocked.Increment(ref _vocalSepGeneration);
        var playGen = Volatile.Read(ref _playbackGeneration);
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

                if (cts.IsCancellationRequested
                    || generation != Volatile.Read(ref _vocalSepGeneration)
                    || playGen != Volatile.Read(ref _playbackGeneration))
                {
                    return;
                }

                if (!IsVocalsOffForTrack(track.FilePath))
                {
                    return;
                }

                _playingInstrumental = true;
                await CrossfadeToPathAsync(track, instrumental, resumePositionMs, keepInstrumental: true)
                    .ConfigureAwait(false);

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

                if (!IsVocalsOffForTrack(track.FilePath)) return;
                if (_currentTrack is null
                    || !string.Equals(_currentTrack.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _playingInstrumental = false;
                await CrossfadeToPathAsync(track, track.FilePath, resumePositionMs, keepInstrumental: false)
                    .ConfigureAwait(false);
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

            if (ActivePlayer.IsPlaying)
            {
                var t = ActivePlayer.Time;
                _resumeMsAfterUserPause = Math.Max(0, t);
                ActivePlayer.Stop();
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
            if (_queue.Count == 0) return;
            _index = (_index - 1 + _queue.Count) % _queue.Count;
            PlayUnlocked(_queue[_index]);
        }
    }

    public void Stop()
    {
        lock (_playbackLock)
        {
            try { ActivePlayer.Stop(); } catch { /* ignore */ }
        }
    }

    public void SeekToMilliseconds(long positionMs)
    {
        if (_disposed || _currentTrack is null) return;

        try
        {
            var player = ActivePlayer;
            var len = player.Length;
            var pos = Math.Max(0, positionMs);
            if (len > 0)
            {
                pos = Math.Min(pos, Math.Max(0, len - 250));
            }

            player.Time = pos;
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
        if (!ReferenceEquals(sender, ActivePlayer)) return;

        var now = Environment.TickCount64;
        if (now - _lastPositionNotifyTicks < 250) return;
        _lastPositionNotifyTicks = now;
        PositionChanged?.Invoke(this, EventArgs.Empty);

        // Harden end-of-track when EndReached is skipped under filter graphs.
        try
        {
            var len = ActivePlayer.Length;
            var time = ActivePlayer.Time;
            if (len > 1500 && time >= len - 350 && ActivePlayer.IsPlaying)
            {
                ScheduleTrackEndAdvance();
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ActivePlayer)) return;
        PlayStateChanged?.Invoke(this, EventArgs.Empty);

        if (_resumeMsAfterUserPause >= 0 || _currentTrack is null) return;

        var length = ActivePlayer.Length;
        var time = ActivePlayer.Time;
        if (length > 0 && time >= length - 500)
        {
            ScheduleTrackEndAdvance();
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ActivePlayer)) return;
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
                    try { ActivePlayer.Stop(); } catch { /* ignore */ }

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
            if (next >= _index) next++;
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
