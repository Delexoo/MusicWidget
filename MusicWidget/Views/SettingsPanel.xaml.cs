using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicWidget.Models;
using MusicWidget.Services;
using Track = MusicWidget.Models.Track;

namespace MusicWidget.Views;

public partial class SettingsPanel : UserControl
{
    private const string DonationUrl = "https://buy.stripe.com/9B63cu3RE2ouaKgcHLcjS00";

    private const double TracksZoomMin = 0.65;
    private const double TracksZoomMax = 1.35;
    private const double TracksZoomStep = 0.05;

    public static readonly DependencyProperty TracksZoomProperty =
        DependencyProperty.Register(
            nameof(TracksZoom),
            typeof(double),
            typeof(SettingsPanel),
            new PropertyMetadata(1.0, OnTracksZoomChanged));

    public double TracksZoom
    {
        get => (double)GetValue(TracksZoomProperty);
        set => SetValue(TracksZoomProperty, value);
    }

    private static void OnTracksZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Item sizing is handled by bindings in the Track DataTemplate.
    }

    private Point _trackDragStart;
    private bool _trackDragPending;
    private bool _handlingPlaylistSelection;
    private bool _isPanelActive;

    private enum View { Home, Help, QuickStart, Shortcuts, YoutubeCookies }

    private const string MoreProgramsUrl = "https://delexo.store";
    private const string HelpPageUrl = AppBranding.HelpPageUrl;

    private readonly Playlist _likedPlaylist = new(
        "Liked Songs",
        folderPath: string.Empty,
        kind: PlaylistKind.Liked,
        iconKey: "Icon.Heart");

    private readonly Playlist _savesPlaylist = new(
        "Saves",
        folderPath: string.Empty,
        kind: PlaylistKind.Saves,
        iconKey: "Icon.Download");

    private List<Playlist> _playlistsCache = new();
    private readonly List<object> _playlistItems = new();

    // --- Now Playing footer state ---
    private Track? _nowPlayingTrack;
    private bool _suppressVolumeChanged;
    private bool _suppressProgressChanged;
    private bool _suppressSpeedChanged;
    private bool _scrubbingProgress;
    private bool _adjustingVolume;
    private bool _adjustingSpeed;
    private bool _updateInstallInFlight;
    private bool _suppressEffectToggle;
    private bool _suppressKeepOpenToggle;
    private int _lastNonZeroVolume = 80;
    private DispatcherTimer? _progressTimer;
    private DispatcherTimer? _backgroundDownloadHideTimer;
    private EventHandler? _backgroundDownloadHideTick;
    private DispatcherTimer? _downloadRefreshDebounceTimer;
    private DispatcherTimer? _refreshAllDebounceTimer;
    private string? _pendingDownloadRefreshPlaylist;
    private bool _isLoaded;
    private string? _backgroundDownloadPlaylistName;
    private UpdateCheckResult? _pendingUpdate;
    private bool _updateCheckInFlight;
    private int _refreshAllGeneration;
    private bool _refreshAllRunning;
    private Playlist? _lastActivePlaylist;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _isPanelActive = true;
        ShowView(View.Home);
        ApplyDashboardChrome();
        InitializeTracksListZoom();
        UpdateShuffleButton();
        InitializeFooter();
        StartProgressTimer();
        App.Playlists.PlaylistsChanged += OnPlaylistsChanged;
        App.Playlists.PlaylistTracksChanged += OnPlaylistTracksChanged;
        App.Player.PlayStateChanged += OnPlayerStateChanged;
        App.Player.CurrentTrackChanged += OnPlayerStateChanged;
        App.Player.ShuffleChanged += OnShuffleChanged;
        App.Player.LoopCurrentChanged += OnLoopChanged;
        App.Player.EffectsChanged += OnEffectsChanged;
        App.Player.VocalSeparationChanged += OnVocalSeparationChanged;
        App.LikedSongs.Changed += OnLikedSongsChanged;
        App.SavedSongs.Changed += OnSavedSongsChanged;
        App.BackgroundDownloads.ProgressChanged += OnBackgroundDownloadProgress;
        App.BackgroundDownloads.Completed += OnBackgroundDownloadCompleted;
        _ = RunRefreshAllAsync();
        _ = SyncUpdateButtonFromServiceAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        _isLoaded = false;
        _isPanelActive = false;
        App.Playlists.PlaylistsChanged -= OnPlaylistsChanged;
        App.Playlists.PlaylistTracksChanged -= OnPlaylistTracksChanged;
        App.Player.PlayStateChanged -= OnPlayerStateChanged;
        App.Player.CurrentTrackChanged -= OnPlayerStateChanged;
        App.Player.ShuffleChanged -= OnShuffleChanged;
        _progressTimer?.Stop();
        _progressTimer = null;
        _downloadRefreshDebounceTimer?.Stop();
        if (_downloadRefreshDebounceTimer is not null)
        {
            _downloadRefreshDebounceTimer.Tick -= OnDownloadRefreshDebounceTick;
        }
        _downloadRefreshDebounceTimer = null;
        _pendingDownloadRefreshPlaylist = null;
        _refreshAllDebounceTimer?.Stop();
        if (_refreshAllDebounceTimer is not null)
        {
            _refreshAllDebounceTimer.Tick -= OnRefreshAllDebounceTick;
        }
        _refreshAllDebounceTimer = null;
        App.Player.LoopCurrentChanged -= OnLoopChanged;
        App.Player.EffectsChanged -= OnEffectsChanged;
        App.Player.VocalSeparationChanged -= OnVocalSeparationChanged;
        App.LikedSongs.Changed -= OnLikedSongsChanged;
        App.SavedSongs.Changed -= OnSavedSongsChanged;
        App.BackgroundDownloads.ProgressChanged -= OnBackgroundDownloadProgress;
        App.BackgroundDownloads.Completed -= OnBackgroundDownloadCompleted;
        if (_backgroundDownloadHideTick is not null && _backgroundDownloadHideTimer is not null)
        {
            _backgroundDownloadHideTimer.Tick -= _backgroundDownloadHideTick;
        }
        _backgroundDownloadHideTimer?.Stop();
        _backgroundDownloadHideTimer = null;
        _backgroundDownloadHideTick = null;
        if (_nowPlayingTrack is not null)
        {
            _nowPlayingTrack.PropertyChanged -= NowPlayingTrack_PropertyChanged;
            _nowPlayingTrack = null;
        }
    }

    private void OnShuffleChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(UpdateShuffleButton);
    }

    private void UpdateShuffleButton()
    {
        var on = App.Player.Shuffle;
        ShuffleIcon.Fill = (Brush)Application.Current.Resources[on ? "Brush.Blue" : "Brush.TextDim"];
        ShuffleButton.ToolTip = on ? "Shuffle: ON" : "Shuffle";
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        App.Player.SetShuffle(!App.Player.Shuffle);
    }

    private void OnPlayerStateChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(() =>
        {
            if (!_isPanelActive) return;
            UpdatePlaylistPlayingStates();
            UpdateNowPlayingFooter();
            UpdateFooterProgress();
        });
    }

    private void StartProgressTimer()
    {
        _progressTimer?.Stop();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressTimer.Tick += (_, _) => UpdateFooterProgress();
        _progressTimer.Start();
    }

    private void OnLoopChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(UpdateFooterLoopIcon);
    }

    private void UpdatePlaylistPlayingStates()
    {
        var isPlaying = App.Player.IsPlaying;
        var current = App.Player.CurrentTrack;
        var active = current is null ? null : ResolvePlaybackSourcePlaylist(current);

        foreach (var pl in EnumerateKnownPlaylists())
        {
            var isActive = ReferenceEquals(pl, active);
            pl.IsActivePlaylist = isActive;
            pl.IsPlayingNow = isActive && isPlaying;
        }

        _lastActivePlaylist = active;
    }

    private IEnumerable<Playlist> EnumerateKnownPlaylists()
    {
        yield return _likedPlaylist;
        yield return _savesPlaylist;
        foreach (var pl in _playlistsCache)
        {
            yield return pl;
        }
    }

    /// <summary>
    /// Playlist whose row should show play/pause for the current track.
    /// Must own the file — not merely be the last selected dashboard playlist
    /// (selecting an empty playlist used to falsely show Pause while another
    /// playlist was actually playing).
    /// </summary>
    private Playlist? ResolvePlaybackSourcePlaylist(Track current)
    {
        var currentName = App.Settings.Current.CurrentPlaylist;

        // Prefer the settings "current" playlist only when it actually contains the track.
        if (!string.IsNullOrEmpty(currentName))
        {
            var preferred = _playlistsCache.FirstOrDefault(p =>
                string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null && PlaylistContainsTrack(preferred, current))
            {
                return preferred;
            }
        }

        // Otherwise any folder playlist that owns the file.
        foreach (var pl in _playlistsCache)
        {
            if (PlaylistContainsTrack(pl, current))
            {
                return pl;
            }
        }

        // Virtual queues only when no folder playlist owns the track.
        if (App.LikedSongs.Contains(current.FilePath))
        {
            return _likedPlaylist;
        }

        if (App.SavedSongs.Contains(current.FilePath))
        {
            return _savesPlaylist;
        }

        return null;
    }

    private bool IsPlaylistActive(Playlist playlist)
    {
        var current = App.Player.CurrentTrack;
        if (current is null)
        {
            return false;
        }

        return ReferenceEquals(playlist, ResolvePlaybackSourcePlaylist(current));
    }

    private static bool PlaylistContainsTrack(Playlist playlist, Track track) =>
        playlist.Tracks.Any(t =>
            string.Equals(t.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase));

    // ===== Now Playing footer =====

    private void InitializeFooter()
    {
        var v = Math.Clamp(App.Player.Volume, 0, AudioPlayer.MaxVolume);
        if (v > 0) _lastNonZeroVolume = v;

        _suppressVolumeChanged = true;
        FooterVolumeSlider.Value = v;
        _suppressVolumeChanged = false;
        UpdateFooterVolumeLabel(v);

        // Always show/apply normal speed on launch (speed is not restored across sessions).
        App.Player.SetRate(1f);
        App.Settings.Current.PlaybackSpeed = 1.0;
        _suppressSpeedChanged = true;
        FooterSpeedSlider.Value = 1.0;
        _suppressSpeedChanged = false;
        UpdateFooterSpeedLabel(1.0);

        UpdateFooterPlayPauseIcon();
        UpdateFooterLoopIcon();
        UpdateFooterVolumeIcon();
        UpdateFooterLikeIcon();
        UpdateFooterSaveIcon();
        UpdateFooterProgress();
        UpdateNowPlayingFooter();
        SyncEffectToggles();
    }

    private void OnEffectsChanged(object? sender, EventArgs e) =>
        UiDispatcher.BeginInvokeSafe(SyncEffectToggles);

    private void SyncEffectToggles()
    {
        if (EffectBassBoostButton is null || EffectReverbButton is null || EffectVocalRemoverButton is null)
        {
            return;
        }

        _suppressEffectToggle = true;
        try
        {
            var bass = App.Player.BassBoostLevel;
            EffectBassBoostButton.Tag = bass > 0 ? "True" : "False";
            EffectBassBoostButton.Content = bass switch
            {
                1 => "Bass 1",
                2 => "Bass 2",
                3 => "Bass 3",
                _ => "Bass off",
            };
            EffectReverbButton.IsChecked = App.Player.Reverb;
            EffectVocalRemoverButton.IsChecked = App.Player.VocalRemover;
            EffectVocalRemoverButton.IsEnabled = !App.Player.IsVocalSeparationBusy;
        }
        finally
        {
            _suppressEffectToggle = false;
        }
    }

    private void EffectBassBoost_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEffectToggle || !_isLoaded) return;
        e.Handled = true;
        App.Player.CycleBassBoostLevel();
        SyncEffectToggles();
    }

    private void EffectReverb_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEffectToggle || !_isLoaded) return;
        App.Player.SetReverb(EffectReverbButton.IsChecked == true);
    }

    private void EffectVocalRemover_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEffectToggle || !_isLoaded) return;
        App.Player.SetVocalRemover(EffectVocalRemoverButton.IsChecked == true);
    }

    private void OnVocalSeparationChanged(object? sender, VocalSeparationEventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(() =>
        {
            if (!_isPanelActive || VocalRemoverStatusText is null || EffectVocalRemoverButton is null)
            {
                return;
            }

            EffectVocalRemoverButton.IsEnabled = !e.IsBusy;
            if (e.IsBusy)
            {
                VocalRemoverStatusText.Visibility = Visibility.Visible;
                VocalRemoverStatusText.Text = e.Status ?? "Removing vocals…";
                VocalRemoverStatusText.Foreground = (Brush)FindResource("Brush.TextMuted");
            }
            else if (e.Failed)
            {
                VocalRemoverStatusText.Visibility = Visibility.Visible;
                VocalRemoverStatusText.Text = string.IsNullOrWhiteSpace(e.Status)
                    ? "Vocal remover failed — playing original."
                    : e.Status;
                VocalRemoverStatusText.Foreground = (Brush)FindResource("Brush.TextMuted");
            }
            else
            {
                VocalRemoverStatusText.Visibility = Visibility.Collapsed;
                VocalRemoverStatusText.Text = string.Empty;
            }
        });
    }

    private void UpdateNowPlayingFooter()
    {
        var t = App.Player.CurrentTrack;
        BindNowPlayingTrack(t);
        RenderNowPlayingMeta();
        UpdateFooterPlayPauseIcon();
        UpdateFooterLikeIcon();
        UpdateFooterSaveIcon();
    }

    private void HighlightCurrentlyPlayingTrack()
    {
        // Footer and playlist row badges reflect playback; do not move the song list scroll.
    }

    private void BindNowPlayingTrack(Track? track)
    {
        if (ReferenceEquals(_nowPlayingTrack, track))
        {
            return;
        }

        if (_nowPlayingTrack is not null)
        {
            _nowPlayingTrack.PropertyChanged -= NowPlayingTrack_PropertyChanged;
        }

        _nowPlayingTrack = track;

        if (_nowPlayingTrack is not null)
        {
            _nowPlayingTrack.PropertyChanged += NowPlayingTrack_PropertyChanged;
            // Kick off artwork/tag load if it hasn't already happened for this track.
            // Cached results return synchronously inside ArtworkService.
            if (_nowPlayingTrack.ArtworkSource is null)
            {
                var capture = _nowPlayingTrack;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var art = await App.Artwork.GetArtworkAsync(capture).ConfigureAwait(false);
                        if (art is null) return;
                        UiDispatcher.BeginInvokeSafe(() =>
                        {
                            if (!ReferenceEquals(_nowPlayingTrack, capture)) return;
                            capture.ArtworkSource = art;
                        }, DispatcherPriority.Background);
                    }
                    catch
                    {
                        // Best-effort: missing artwork falls back to initials.
                    }
                });
            }
        }
    }

    private void NowPlayingTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Track is INotifyPropertyChanged — refresh the meta when title/artist/art arrive.
        UiDispatcher.BeginInvokeSafe(RenderNowPlayingMeta);
    }

    private void RenderNowPlayingMeta()
    {
        var t = _nowPlayingTrack;
        if (t is null)
        {
            NowPlayingTitle.Text = "Nothing playing";
            NowPlayingArtist.Text = string.Empty;
            NowPlayingInitials.Text = string.Empty;
            NowPlayingInitials.Visibility = Visibility.Collapsed;
            NowPlayingPlaceholderIcon.Visibility = Visibility.Visible;
            NowPlayingArtBrush.ImageSource = null;
            NowPlayingArt.Visibility = Visibility.Collapsed;
            return;
        }

        var title = !string.IsNullOrWhiteSpace(t.Title) ? t.Title : t.FriendlyDisplayName;
        NowPlayingTitle.Text = title ?? string.Empty;
        NowPlayingArtist.Text = !string.IsNullOrWhiteSpace(t.Artist) ? t.Artist : "Unknown artist";
        NowPlayingInitials.Text = t.Initials;

        if (t.ArtworkSource is not null)
        {
            NowPlayingArtBrush.ImageSource = t.ArtworkSource;
            NowPlayingArt.Visibility = Visibility.Visible;
            NowPlayingPlaceholderIcon.Visibility = Visibility.Collapsed;
            NowPlayingInitials.Visibility = Visibility.Collapsed;
        }
        else
        {
            NowPlayingArtBrush.ImageSource = null;
            NowPlayingArt.Visibility = Visibility.Collapsed;
            NowPlayingPlaceholderIcon.Visibility = Visibility.Visible;
            NowPlayingInitials.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateFooterPlayPauseIcon()
    {
        var playing = App.Player.IsPlaying;
        var key = playing ? "Icon.Pause" : "Icon.Play";
        FooterPlayPauseIcon.Data = (Geometry)Application.Current.Resources[key];
        FooterPlayPauseTooltip.Text = playing ? "Pause" : "Play";
    }

    private void UpdateFooterLoopIcon()
    {
        var on = App.Player.LoopCurrent;
        var accent = (Brush)Application.Current.Resources["Brush.Blue"];
        var dim = (Brush)Application.Current.Resources["Brush.TextDim"];

        FooterLoopIconRect.Fill = on ? accent : dim;
        FooterLoopTooltip.Text = on ? "Loop: ON (current song)" : "Loop: OFF";
    }

    private void UpdateFooterVolumeIcon()
    {
        var v = App.Player.Volume;
        var key = v == 0 ? "Icon.VolumeMute" : "Icon.Volume";
        FooterVolumeIcon.Data = (Geometry)Application.Current.Resources[key];
    }

    private void UpdateFooterLikeIcon()
    {
        var path = App.Player.CurrentTrack?.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            // No track playing — show the inert outline-style heart and disable the button.
            FooterLikeButton.IsEnabled = false;
            FooterLikeIcon.Fill = (Brush)Application.Current.Resources["Brush.TextMuted"];
            FooterLikeTooltip.Text = "Like (nothing playing)";
            return;
        }

        FooterLikeButton.IsEnabled = true;
        var liked = App.LikedSongs.Contains(path);
        // Use the danger red for "liked" so the heart pops; dim grey when unliked.
        FooterLikeIcon.Fill = liked
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44))
            : (Brush)Application.Current.Resources["Brush.TextDim"];
        FooterLikeTooltip.Text = liked ? "Remove from Liked Songs" : "Add to Liked Songs";
    }

    private void UpdateFooterSaveIcon()
    {
        var path = App.Player.CurrentTrack?.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            FooterSaveButton.IsEnabled = false;
            FooterSaveIcon.Fill = (Brush)Application.Current.Resources["Brush.TextMuted"];
            FooterSaveTooltip.Text = "Save (nothing playing)";
            return;
        }

        FooterSaveButton.IsEnabled = true;
        var saved = App.SavedSongs.Contains(path);
        FooterSaveIcon.Fill = saved
            ? (Brush)Application.Current.Resources["Brush.Blue"]
            : (Brush)Application.Current.Resources["Brush.TextDim"];
        FooterSaveTooltip.Text = saved ? "Remove from Saves" : "Add to Saves";
    }

    private void FooterLike_Click(object sender, RoutedEventArgs e)
    {
        var path = App.Player.CurrentTrack?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        App.LikedSongs.Toggle(path);
        // OnLikedSongsChanged refreshes the icon on the UI thread.
    }

    private void FooterSave_Click(object sender, RoutedEventArgs e)
    {
        var path = App.Player.CurrentTrack?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        App.SavedSongs.Toggle(path);
        // OnSavedSongsChanged refreshes the icon on the UI thread.
    }

    private void FooterPrev_Click(object sender, RoutedEventArgs e)
    {
        App.Player.Previous();
    }

    private void FooterNext_Click(object sender, RoutedEventArgs e)
    {
        App.Player.Next();
    }

    private void FooterPlayPause_Click(object sender, RoutedEventArgs e)
    {
        App.Player.TogglePlayPause();
    }

    private void FooterLoop_Click(object sender, RoutedEventArgs e)
    {
        App.Player.SetLoopCurrent(!App.Player.LoopCurrent);
    }

    private void FooterMute_Click(object sender, RoutedEventArgs e)
    {
        var current = App.Player.Volume;
        if (current > 0)
        {
            _lastNonZeroVolume = current;
            FooterVolumeSlider.Value = 0;
        }
        else
        {
            FooterVolumeSlider.Value = _lastNonZeroVolume > 0 ? _lastNonZeroVolume : 50;
        }
    }

    private static string FormatPlaybackTime(long totalMs)
    {
        if (totalMs < 0)
        {
            totalMs = 0;
        }

        var totalSeconds = (int)(totalMs / 1000);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}"
            : $"{minutes}:{seconds:D2}";
    }

    private void UpdateFooterProgress()
    {
        // Never fight the user while they scrub — that caused the seek bar to jump.
        if (_scrubbingProgress
            || FooterProgressSlider.IsMouseCaptureWithin
            || FooterProgressSlider.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        var track = App.Player.CurrentTrack;
        var durationMs = App.Player.DurationMs;
        var positionMs = App.Player.PositionMs;

        FooterPositionLabel.Text = FormatPlaybackTime(positionMs);
        FooterDurationLabel.Text = durationMs > 0 ? FormatPlaybackTime(durationMs) : "0:00";
        FooterProgressSlider.IsEnabled = track is not null && durationMs > 0;

        _suppressProgressChanged = true;
        try
        {
            if (durationMs > 0)
            {
                if (Math.Abs(FooterProgressSlider.Maximum - durationMs) > 0.5)
                {
                    FooterProgressSlider.Maximum = durationMs;
                }

                // Skip tiny timer updates so the thumb doesn't stutter.
                if (Math.Abs(FooterProgressSlider.Value - positionMs) >= 250)
                {
                    FooterProgressSlider.Value = Math.Clamp(positionMs, 0, durationMs);
                }
            }
            else
            {
                FooterProgressSlider.Maximum = 1;
                FooterProgressSlider.Value = 0;
            }
        }
        finally
        {
            _suppressProgressChanged = false;
        }
    }

    private void FooterProgress_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _scrubbingProgress = true;
        }
    }

    private void FooterProgress_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            FinishProgressScrub();
        }
    }

    private void FooterProgress_LostMouseCapture(object sender, MouseEventArgs e) => FinishProgressScrub();

    private void FinishProgressScrub()
    {
        if (!_scrubbingProgress || FooterProgressSlider is null)
        {
            return;
        }

        _scrubbingProgress = false;
        var durationMs = App.Player.DurationMs;
        if (durationMs <= 0)
        {
            return;
        }

        var positionMs = (long)Math.Clamp(FooterProgressSlider.Value, 0, durationMs);
        FooterPositionLabel.Text = FormatPlaybackTime(positionMs);
        App.Player.SeekToMilliseconds(positionMs);
    }

    private void FooterProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressProgressChanged || !_isLoaded || FooterProgressSlider is null)
        {
            return;
        }

        var durationMs = App.Player.DurationMs;
        if (durationMs <= 0)
        {
            return;
        }

        var positionMs = (long)Math.Clamp(e.NewValue, 0, durationMs);
        FooterPositionLabel.Text = FormatPlaybackTime(positionMs);

        // Seek once when scrubbing ends. Mid-drag seeks stutter LibVLC.
        if (!_scrubbingProgress && !FooterProgressSlider.IsMouseCaptureWithin)
        {
            App.Player.SeekToMilliseconds(positionMs);
        }
    }

    private void FooterVolume_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _adjustingVolume = true;
        }
    }

    private void FooterVolume_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            FinishVolumeAdjust();
        }
    }

    private void FooterVolume_LostMouseCapture(object sender, MouseEventArgs e) => FinishVolumeAdjust();

    private void FinishVolumeAdjust()
    {
        if (!_adjustingVolume)
        {
            return;
        }

        _adjustingVolume = false;
        App.Settings.Current.Volume = App.Player.Volume;
        App.Settings.ScheduleSave();
    }

    private void FooterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Same XAML-parse race as the speed slider — don't touch player/settings mid-load.
        if (_suppressVolumeChanged || !_isLoaded || FooterVolumeSlider is null)
        {
            return;
        }

        var v = (int)Math.Round(e.NewValue);
        v = Math.Clamp(v, 0, AudioPlayer.MaxVolume);
        if (v > 0) _lastNonZeroVolume = v;

        App.Player.Volume = v;
        App.Settings.Current.Volume = v;
        if (!_adjustingVolume)
        {
            App.Settings.ScheduleSave();
        }

        UpdateFooterVolumeLabel(v);
        UpdateFooterVolumeIcon();
    }

    private void UpdateFooterVolumeLabel(int volumePercent)
    {
        if (FooterVolumeLabel is null) return;
        FooterVolumeLabel.Text = $"{volumePercent}%";
    }

    private void FooterSpeed_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _adjustingSpeed = true;
        }
    }

    private void FooterSpeed_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            FinishSpeedAdjust();
        }
    }

    private void FooterSpeed_LostMouseCapture(object sender, MouseEventArgs e) => FinishSpeedAdjust();

    private void FinishSpeedAdjust()
    {
        if (!_adjustingSpeed || FooterSpeedSlider is null)
        {
            return;
        }

        _adjustingSpeed = false;
        var speed = Math.Clamp(FooterSpeedSlider.Value, 0.5, 2.0);
        App.Player.SetRate((float)speed, snapNearNormal: true);
        var applied = App.Player.Rate;
        App.Settings.Current.PlaybackSpeed = applied;
        UpdateFooterSpeedLabel(applied);

        if (Math.Abs(FooterSpeedSlider.Value - applied) > 0.001)
        {
            _suppressSpeedChanged = true;
            try
            {
                FooterSpeedSlider.Value = applied;
            }
            finally
            {
                _suppressSpeedChanged = false;
            }
        }
    }

    private void FooterSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider fires ValueChanged while XAML is still parsing (e.g. Minimum set).
        // Ignore until the panel is fully loaded and named fields exist.
        if (_suppressSpeedChanged || !_isLoaded || FooterSpeedLabel is null || FooterSpeedSlider is null)
        {
            return;
        }

        var speed = Math.Clamp(e.NewValue, 0.5, 2.0);
        // Live rate while dragging (no snap). Snap-to-1.0 happens on mouse release.
        App.Player.SetRate((float)speed, snapNearNormal: false);
        UpdateFooterSpeedLabel(speed);
        App.Settings.Current.PlaybackSpeed = App.Player.Rate;
    }

    private void UpdateFooterSpeedLabel(double speed)
    {
        if (FooterSpeedLabel is null) return;
        FooterSpeedLabel.Text = $"{speed:0.0}x";
    }

    // ===== Reset layout =====

    /// <summary>
    /// Restores the playlist &lt;-&gt; songs splitter to a 50/50 split. The GridSplitter
    /// writes absolute pixel widths when dragged; reinstating "1*" / "1*" makes the
    /// two panes share the remaining space evenly again. Public so the widget window
    /// can include the splitter when handling Ctrl+Shift+\ from anywhere.
    /// </summary>
    public void ResetSplitter()
    {
        // Split view removed (accordion layout).
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = ModernMessageBox.ConfirmYesNo(
            "Reset the widget's position and the dashboard's size to their defaults?",
            title: "Reset layout");
        if (!confirmed) return;

        // Walk up to the owning WidgetWindow and delegate the window-level reset
        // (position + dashboard dimensions + splitter).
        var owner = Window.GetWindow(this) as WidgetWindow;
        owner?.ResetLayoutToDefaults();
    }

    private void ApplyDashboardChrome()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        DashboardVersionText.Text = version is null
            ? string.Empty
            : $"v{version.Major}.{version.Minor}.{version.Build}";

        _suppressKeepOpenToggle = true;
        KeepOpenToggle.IsChecked = App.Settings.Current.KeepDashboardOpen;
        _suppressKeepOpenToggle = false;
        ApplyKeepOpenToggleVisual();
    }

    private void KeepOpenToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressKeepOpenToggle)
        {
            return;
        }

        var keepOpen = KeepOpenToggle.IsChecked == true;
        App.Settings.Current.KeepDashboardOpen = keepOpen;
        App.Settings.Save();
        ApplyKeepOpenToggleVisual();
    }

    private void ApplyKeepOpenToggleVisual()
    {
        var on = KeepOpenToggle.IsChecked == true;
        KeepOpenToggle.ToolTip = on
            ? "Keep open — on (click Minimize to close)"
            : "Keep open — dashboard stays up when you click outside";
        if (KeepOpenToggleIcon is not null)
        {
            KeepOpenToggleIcon.Fill = on
                ? (Brush)FindResource("Brush.Blue")
                : (Brush)FindResource("Brush.TextDim");
        }
    }

    // ----- Navigation -----

    private View _currentView = View.Home;

    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is WidgetWindow widget)
        {
            widget.MinimizeWidget();
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = HelpPageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not open browser: " + ex.Message);
        }
    }
    private void OpenQuickStart_Click(object sender, RoutedEventArgs e) => ShowView(View.QuickStart);
    private void OpenShortcuts_Click(object sender, RoutedEventArgs e) => ShowView(View.Shortcuts);
    private void OpenYoutubeCookies_Click(object sender, RoutedEventArgs e) => ShowView(View.YoutubeCookies);

    private void OpenMorePrograms_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = MoreProgramsUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not open browser: " + ex.Message);
        }
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        var target = _currentView switch
        {
            View.QuickStart => View.Home,
            View.Shortcuts => View.Home,
            View.YoutubeCookies => View.Home,
            View.Help => View.Home,
            _ => View.Home,
        };
        ShowView(target);
    }

    private void ShowView(View view)
    {
        _currentView = view;
        HomeView.Visibility = view == View.Home ? Visibility.Visible : Visibility.Collapsed;
        HelpView.Visibility = view == View.Help ? Visibility.Visible : Visibility.Collapsed;
        QuickStartView.Visibility = view == View.QuickStart ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsView.Visibility = view == View.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
        YoutubeCookiesView.Visibility = view == View.YoutubeCookies ? Visibility.Visible : Visibility.Collapsed;
        if (view == View.YoutubeCookies)
        {
            RefreshYoutubeCookiesPathUi();
        }
    }

    private void RefreshYoutubeCookiesPathUi()
    {
        YoutubeCookiesPathBox.Text = string.IsNullOrWhiteSpace(App.Settings.Current.YoutubeCookiesFilePath)
            ? "(none - use Browse to pick cookies.txt)"
            : App.Settings.Current.YoutubeCookiesFilePath;
    }

    private void YoutubeCookiesBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Netscape cookies file for yt-dlp",
            Filter = "Cookies (*.txt)|cookies.txt;*.txt|All files|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            App.Settings.Current.YoutubeCookiesFilePath = dlg.FileName;
            App.Settings.Save();
            RefreshYoutubeCookiesPathUi();
        }
    }

    private void YoutubeCookiesClear_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.YoutubeCookiesFilePath = null;
        App.Settings.Save();
        RefreshYoutubeCookiesPathUi();
    }

    private void WikiLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not open browser: " + ex.Message);
        }

        e.Handled = true;
    }

    // ----- Data refresh -----

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        ScheduleRefreshAll();
    }

    private void OnPlaylistTracksChanged(object? sender, string playlistName)
    {
        UiDispatcher.BeginInvokeSafe(() =>
        {
            if (!_isPanelActive) return;
            _ = ApplyPlaylistTracksChangedAsync(playlistName);
        }, DispatcherPriority.Background);
    }

    private async Task ApplyPlaylistTracksChangedAsync(string playlistName)
    {
        var cached = _playlistsCache.FirstOrDefault(p =>
            string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));
        if (cached is not null)
        {
            try
            {
                var files = await Task.Run(() => App.Playlists.EnumerateTrackFilesOnDisk(cached))
                    .ConfigureAwait(true);
                if (!_isPanelActive)
                {
                    return;
                }

                App.Playlists.ApplyTrackFiles(cached, files);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SettingsPanel.ApplyPlaylistTracksChangedAsync");
            }
        }

        if (PlaylistsList.SelectedItem is not Playlist selected)
        {
            return;
        }

        if (selected.Kind == PlaylistKind.Liked)
        {
            RebuildLikedTracks();
            QueueArtworkLoad(_likedPlaylist);
            return;
        }

        if (selected.Kind == PlaylistKind.Saves)
        {
            RebuildSavedTracks();
            QueueArtworkLoad(_savesPlaylist);
            return;
        }

        if (selected.Kind != PlaylistKind.Normal
            || !string.Equals(selected.Name, playlistName, StringComparison.OrdinalIgnoreCase)
            || cached is null)
        {
            return;
        }

        QueueArtworkLoad(cached);

        if (!string.IsNullOrEmpty(App.Settings.Current.CurrentPlaylist)
            && string.Equals(App.Settings.Current.CurrentPlaylist, playlistName, StringComparison.OrdinalIgnoreCase))
        {
            App.Player.UpdateQueueOrder(cached.Tracks);
        }
    }

    private void RefreshAll()
    {
        _ = RunRefreshAllAsync();
    }

    private async Task RunRefreshAllAsync()
    {
        if (!_isPanelActive)
        {
            return;
        }

        if (_refreshAllRunning)
        {
            _refreshAllGeneration++;
            return;
        }

        _refreshAllRunning = true;
        var generation = ++_refreshAllGeneration;

        try
        {
            List<Playlist> playlists;
            try
            {
                playlists = await Task.Run(() => App.Playlists.GetPlaylists().ToList()).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SettingsPanel.RunRefreshAllAsync");
                return;
            }

            if (!_isPanelActive || generation != _refreshAllGeneration)
            {
                return;
            }

            ApplyPlaylistsFromDisk(playlists);
        }
        finally
        {
            _refreshAllRunning = false;
            if (generation != _refreshAllGeneration && _isPanelActive)
            {
                _ = RunRefreshAllAsync();
            }
        }
    }

    private void ApplyPlaylistsFromDisk(List<Playlist> playlists)
    {
        _playlistsCache = playlists;

        var pinnedNames = new HashSet<string>(
            App.Settings.Current.PinnedPlaylists ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var pl in _playlistsCache)
        {
            pl.IsUserPinned = pinnedNames.Contains(pl.Name);
            PlaylistIconService.Hydrate(pl);
        }

        PlaylistIconService.Hydrate(_likedPlaylist);
        PlaylistIconService.Hydrate(_savesPlaylist);

        var prevSelected = PlaylistsList.SelectedItem;
        RebuildPlaylistItemsList();

        var curName = App.Settings.Current.CurrentPlaylist;
        object? toSelect = null;
        if (prevSelected is Playlist prevPl)
        {
            if (ReferenceEquals(prevPl, _likedPlaylist)) toSelect = _likedPlaylist;
            else if (ReferenceEquals(prevPl, _savesPlaylist)) toSelect = _savesPlaylist;
            else
            {
                toSelect = _playlistsCache.FirstOrDefault(p =>
                    string.Equals(p.Name, prevPl.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (toSelect is null && !string.IsNullOrEmpty(curName))
        {
            toSelect = _playlistsCache.FirstOrDefault(p =>
                string.Equals(p.Name, curName, StringComparison.OrdinalIgnoreCase));
        }

        toSelect ??= _playlistsCache.Count > 0 ? (object)_playlistsCache[0] : _likedPlaylist;

        try
        {
            _handlingPlaylistSelection = true;
            PlaylistsList.SelectedItem = toSelect;
        }
        finally
        {
            _handlingPlaylistSelection = false;
        }

        RebuildLikedTracks();
        RebuildSavedTracks();
        UpdatePlaylistPlayingStates();

        if (PlaylistsList.SelectedItem is Playlist selected)
        {
            QueueArtworkLoad(selected);
        }
    }

    private void RebuildPlaylistItemsList()
    {
        _playlistItems.Clear();
        _playlistItems.Add(_likedPlaylist);
        _playlistItems.Add(_savesPlaylist);

        var pinnedNormals = _playlistsCache.Where(p => p.IsUserPinned).ToList();
        var unpinnedNormals = _playlistsCache.Where(p => !p.IsUserPinned).ToList();
        foreach (var pl in pinnedNormals)
        {
            _playlistItems.Add(pl);
        }

        _playlistItems.Add(PlaylistDivider.Instance);
        foreach (var pl in unpinnedNormals)
        {
            _playlistItems.Add(pl);
        }

        if (!ReferenceEquals(PlaylistsList.ItemsSource, _playlistItems))
        {
            PlaylistsList.ItemsSource = _playlistItems;
        }
        else
        {
            PlaylistsList.ItemsSource = null;
            PlaylistsList.ItemsSource = _playlistItems;
        }
    }

    private void ScheduleRefreshAll()
    {
        if (!_isPanelActive)
        {
            return;
        }

        _refreshAllDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshAllDebounceTimer.Stop();
        _refreshAllDebounceTimer.Tick -= OnRefreshAllDebounceTick;
        _refreshAllDebounceTimer.Tick += OnRefreshAllDebounceTick;
        _refreshAllDebounceTimer.Start();
    }

    private void OnRefreshAllDebounceTick(object? sender, EventArgs e)
    {
        _refreshAllDebounceTimer?.Stop();
        _ = RunRefreshAllAsync();
    }

    private void RefreshTracks(Playlist pl, bool reloadVirtual = true)
    {
        if (reloadVirtual)
        {
            switch (pl.Kind)
            {
                case PlaylistKind.Liked:
                    RebuildLikedTracks();
                    break;
                case PlaylistKind.Saves:
                    RebuildSavedTracks();
                    break;
            }
        }

        SyncTrackLikedStates(pl);
        QueueArtworkLoad(pl);
    }

    private void SyncTrackLikedStates(Playlist? playlist = null)
    {
        var liked = App.LikedSongs;
        void Sync(IEnumerable<Track> tracks)
        {
            foreach (var track in tracks)
            {
                track.IsLiked = liked.Contains(track.FilePath);
            }
        }

        if (playlist is null)
        {
            Sync(_likedPlaylist.Tracks);
            Sync(_savesPlaylist.Tracks);
            foreach (var pl in _playlistsCache)
            {
                Sync(pl.Tracks);
            }

            return;
        }

        Sync(playlist.Tracks);
    }

    /// <summary>
    /// Repopulates the given pinned playlist's Tracks collection from the store's
    /// ordered list of paths, reusing existing Track instances from the loaded
    /// playlists where possible so artwork/title/artist already fetched survive
    /// across rebuilds.
    /// </summary>
    private void RebuildPinnedTracks(TrackListStore store, Playlist pinned)
    {
        var paths = store.GetAll();
        var index = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in _playlistsCache)
        {
            foreach (var tr in pl.Tracks)
            {
                index[tr.FilePath] = tr;
            }
        }

        pinned.Tracks.Clear();
        foreach (var p in paths)
        {
            pinned.Tracks.Add(
                index.TryGetValue(p, out var existing) ? existing : new Track(p));
        }
    }

    private void RebuildLikedTracks() => RebuildPinnedTracks(App.LikedSongs, _likedPlaylist);
    private void RebuildSavedTracks() => RebuildPinnedTracks(App.SavedSongs, _savesPlaylist);

    private static void HydrateTracksArtwork(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks)
        {
            App.Artwork.TryHydrateTrackArtwork(track);
        }
    }

    private static void QueueArtworkLoad(Playlist pl)
    {
        var tracks = pl.Tracks.ToList();
        if (tracks.Count == 0)
        {
            return;
        }

        App.Artwork.ClearMissCache();
        UiDispatcher.BeginInvokeSafe(() => HydrateTracksArtwork(tracks), DispatcherPriority.Loaded);

        _ = Task.Run(async () =>
        {
            const int maxTracks = 400;
            var attempted = 0;
            // Load a few in parallel so the list fills covers quickly.
            var gate = new SemaphoreSlim(4, 4);
            var tasks = new List<Task>();
            foreach (var track in tracks)
            {
                if (attempted++ >= maxTracks)
                {
                    break;
                }

                var capture = track;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (capture.ArtworkSource is not null)
                        {
                            return;
                        }

                        var art = await App.Artwork.GetArtworkAsync(capture).ConfigureAwait(false);
                        if (art is null)
                        {
                            return;
                        }

                        UiDispatcher.BeginInvokeSafe(() =>
                        {
                            capture.ArtworkSource = art;
                        }, DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "SettingsPanel.QueueArtworkLoad");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // Individual failures already logged.
            }
        });
    }

    // ----- Playlists: row icon buttons -----

    private void PlaylistPlayButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Playlist playlist })
        {
            return;
        }

        // Virtual playlists keep their track list dynamic; refresh before checking count.
        switch (playlist.Kind)
        {
            case PlaylistKind.Liked:
                RebuildLikedTracks();
                break;
            case PlaylistKind.Saves:
                RebuildSavedTracks();
                break;
        }

        if (playlist.Tracks.Count == 0)
        {
            return;
        }

        PlaylistsList.SelectedItem = playlist;

        if (IsPlaylistActive(playlist) && App.Player.CurrentTrack is not null)
        {
            App.Player.TogglePlayPause();
            return;
        }

        // Only persist as "current" for real, on-disk playlists.
        if (playlist.Kind == PlaylistKind.Normal)
        {
            App.Playlists.SetCurrentPlaylist(playlist.Name);
        }

        App.Player.SetQueue(playlist.Tracks, 0);
    }

    private void PlaylistAddButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Playlist playlist })
        {
            return;
        }
        ShowAddMenuFor(playlist, (UIElement)sender);
    }

    private void ShowAddMenuFor(Playlist playlist, UIElement placementTarget)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var addFiles = new MenuItem { Header = "Add files…" };
        addFiles.Click += (_, _) =>
        {
            menu.IsOpen = false;
            AddFilesToPlaylist(playlist);
        };

        var addFolder = new MenuItem { Header = "Add folder…" };
        addFolder.Click += (_, _) =>
        {
            menu.IsOpen = false;
            AddFolderToPlaylist(playlist);
        };

        var download = new MenuItem { Header = "Paste audio link…" };
        download.Click += (_, _) =>
        {
            menu.IsOpen = false;
            DownloadUrlIntoPlaylist(playlist);
        };

        menu.Items.Add(addFiles);
        menu.Items.Add(addFolder);
        menu.Items.Add(new Separator());
        menu.Items.Add(download);
        menu.IsOpen = true;
    }

    private void AddFilesToPlaylist(Playlist playlist)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"Add music to \"{playlist.Name}\"",
            Filter = "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.aif;*.aiff;*.alac|All files|*.*",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        if (dlg.FileNames.Length == 0)
        {
            return;
        }

        App.Playlists.AddTracksFromFiles(playlist.Name, dlg.FileNames);
    }

    private void AddFolderToPlaylist(Playlist playlist)
    {
        var dlg = new OpenFolderDialog
        {
            Title = $"Pick a folder to import audio into \"{playlist.Name}\"",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        App.Playlists.AddTracksFromFolder(
            playlist.Name,
            dlg.FolderName,
            SearchOption.AllDirectories);
    }

    private void DownloadUrlIntoPlaylist(Playlist playlist)
    {
        var owner = Window.GetWindow(this)
                    ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var window = new DownloadUrlWindow(playlist);
        DismissibleWindow.Show(window, owner);
        if (window.OpenYoutubeCookiesRequested)
        {
            ShowView(View.YoutubeCookies);
        }
    }

    private void RefreshAfterDownload(string playlistName)
    {
        _ = RefreshAfterDownloadAsync(playlistName);
    }

    private async Task RefreshAfterDownloadAsync(string playlistName)
    {
        var cached = FindPlaylistByName(playlistName);
        if (cached is null)
        {
            await RunRefreshAllAsync().ConfigureAwait(true);
            cached = FindPlaylistByName(playlistName);
        }

        if (cached is null || !_isPanelActive)
        {
            return;
        }

        try
        {
            var files = await Task.Run(() => App.Playlists.EnumerateTrackFilesOnDisk(cached))
                .ConfigureAwait(true);
            if (!_isPanelActive)
            {
                return;
            }

            App.Playlists.ApplyTrackFiles(cached, files);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.RefreshAfterDownloadAsync");
            return;
        }

        if (PlaylistsList.SelectedItem is Playlist selected
            && string.Equals(selected.Name, playlistName, StringComparison.OrdinalIgnoreCase))
        {
            SyncTrackLikedStates(cached);
            QueueArtworkLoad(cached);
        }
    }

    private void ScheduleRefreshAfterDownload(string playlistName)
    {
        _pendingDownloadRefreshPlaylist = playlistName;
        _downloadRefreshDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _downloadRefreshDebounceTimer.Stop();
        _downloadRefreshDebounceTimer.Tick -= OnDownloadRefreshDebounceTick;
        _downloadRefreshDebounceTimer.Tick += OnDownloadRefreshDebounceTick;
        _downloadRefreshDebounceTimer.Start();
    }

    private void OnDownloadRefreshDebounceTick(object? sender, EventArgs e)
    {
        _downloadRefreshDebounceTimer?.Stop();
        var playlistName = _pendingDownloadRefreshPlaylist;
        _pendingDownloadRefreshPlaylist = null;
        if (string.IsNullOrWhiteSpace(playlistName) || !_isPanelActive)
        {
            return;
        }

        try
        {
            RefreshAfterDownload(playlistName);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.OnDownloadRefreshDebounceTick");
        }
    }

    private void SelectPlaylistByName(string playlistName)
    {
        _ = SelectPlaylistByNameAsync(playlistName);
    }

    private async Task SelectPlaylistByNameAsync(string playlistName)
    {
        var cached = FindPlaylistByName(playlistName);
        if (cached is null)
        {
            await RunRefreshAllAsync().ConfigureAwait(true);
            cached = FindPlaylistByName(playlistName);
        }

        if (cached is null || !_isPanelActive)
        {
            return;
        }

        try
        {
            _handlingPlaylistSelection = true;
            PlaylistsList.SelectedItem = cached;
        }
        finally
        {
            _handlingPlaylistSelection = false;
        }

        RefreshTracks(cached);
        App.Playlists.SetCurrentPlaylist(cached.Name);
    }

    private void BackgroundDownloadCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!App.BackgroundDownloads.IsRunning)
        {
            return;
        }

        BackgroundDownloadCancelButton.IsEnabled = false;
        BackgroundDownloadDetail.Text = "Cancelling...";
        App.BackgroundDownloads.Cancel();
    }

    private void OnBackgroundDownloadProgress(object? sender, BackgroundDownloadProgressEventArgs e)
    {
        if (!_isPanelActive)
        {
            return;
        }

        try
        {
            if (!string.Equals(_backgroundDownloadPlaylistName, e.PlaylistName, StringComparison.OrdinalIgnoreCase))
            {
                _backgroundDownloadPlaylistName = e.PlaylistName;
                SelectPlaylistByName(e.PlaylistName);
            }

            var update = e.Update;
            if (update.RefreshPlaylistTracks || !string.IsNullOrWhiteSpace(update.CompletedFilePath))
            {
                ScheduleRefreshAfterDownload(e.PlaylistName);
            }

            BackgroundDownloadBanner.Visibility = Visibility.Visible;
            var queueNote = e.QueueRemaining > 0 ? $"  ·  {e.QueueRemaining} queued" : string.Empty;
            BackgroundDownloadTitle.Text = $"Downloading to \"{e.PlaylistName}\"{queueNote}";
            BackgroundDownloadCancelButton.IsEnabled = App.BackgroundDownloads.IsRunning;
            BackgroundDownloadCancelButton.Visibility = App.BackgroundDownloads.IsRunning
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (update.Percent >= 0)
            {
                BackgroundDownloadProgress.Value = update.Percent;
                BackgroundDownloadPercent.Text = $"{update.Percent:0}%";
            }

            if (!string.IsNullOrWhiteSpace(update.CurrentSong))
            {
                BackgroundDownloadDetail.Text = update.CurrentSong;
            }
            else if (!string.IsNullOrWhiteSpace(update.Message))
            {
                BackgroundDownloadDetail.Text = update.Message;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.OnBackgroundDownloadProgress");
        }
    }

    private void OnBackgroundDownloadCompleted(object? sender, BackgroundDownloadCompletedEventArgs e)
    {
        if (!_isPanelActive)
        {
            return;
        }

        if (e.Success)
        {
            RefreshAfterDownload(e.PlaylistName);
        }

        // More jobs waiting — keep the banner and let progress events take over.
        if (e.HasMoreQueued && !e.Cancelled)
        {
            BackgroundDownloadBanner.Visibility = Visibility.Visible;
            BackgroundDownloadTitle.Text = e.Success
                ? $"Download complete · {e.QueueRemaining} queued"
                : $"Download issue · continuing queue ({e.QueueRemaining} left)";
            BackgroundDownloadDetail.Text = e.Success
                ? (e.DownloadedCount == 1
                    ? "1 song added. Starting next download..."
                    : $"{e.DownloadedCount} songs added. Starting next download...")
                : (e.Error ?? "Something went wrong. Starting next download...");
            BackgroundDownloadProgress.Value = 0;
            BackgroundDownloadPercent.Text = string.Empty;
            BackgroundDownloadCancelButton.IsEnabled = true;
            BackgroundDownloadCancelButton.Visibility = Visibility.Visible;
            return;
        }

        _backgroundDownloadPlaylistName = null;

        if (e.Success)
        {
            BackgroundDownloadTitle.Text = "Download complete";
            BackgroundDownloadDetail.Text = e.DownloadedCount == 1
                ? "1 song added to your playlist."
                : $"{e.DownloadedCount} songs added to \"{e.PlaylistName}\".";
            BackgroundDownloadProgress.Value = 100;
            BackgroundDownloadPercent.Text = "100%";
        }
        else if (e.Cancelled)
        {
            BackgroundDownloadTitle.Text = "Download cancelled";
            BackgroundDownloadDetail.Text = "The download was stopped.";
            BackgroundDownloadPercent.Text = string.Empty;
        }
        else
        {
            BackgroundDownloadTitle.Text = "Download failed";
            BackgroundDownloadDetail.Text = e.Error ?? "Something went wrong.";
            BackgroundDownloadPercent.Text = string.Empty;
        }

        BackgroundDownloadCancelButton.Visibility = Visibility.Collapsed;

        _backgroundDownloadHideTimer?.Stop();
        if (_backgroundDownloadHideTick is not null)
        {
            _backgroundDownloadHideTimer!.Tick -= _backgroundDownloadHideTick;
        }

        _backgroundDownloadHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _backgroundDownloadHideTick = (_, _) =>
        {
            _backgroundDownloadHideTimer?.Stop();
            BackgroundDownloadBanner.Visibility = Visibility.Collapsed;
        };
        _backgroundDownloadHideTimer.Tick += _backgroundDownloadHideTick;
        _backgroundDownloadHideTimer.Start();
    }

    private Playlist? FindPlaylistByName(string name) =>
        _playlistsCache.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_handlingPlaylistSelection)
        {
            return;
        }

        // The divider sentinel isn't selectable: revert to the previous selection.
        if (PlaylistsList.SelectedItem is PlaylistDivider)
        {
            var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            UiDispatcher.BeginInvokeSafe(() => PlaylistsList.SelectedItem = previous);
            return;
        }

        if (PlaylistsList.SelectedItem is not Playlist pl)
        {
            return;
        }

        try
        {
            _handlingPlaylistSelection = true;
            CollapseAllPlaylistsExcept(pl);
            pl.IsExpanded = true;

            var needsReload = pl.Kind is PlaylistKind.Liked or PlaylistKind.Saves
                              || pl.Tracks.Count == 0;
            if (needsReload)
            {
                RefreshTracks(pl);
            }
            else
            {
                SyncTrackLikedStates(pl);
                QueueArtworkLoad(pl);
            }

            if (pl.Kind == PlaylistKind.Normal)
            {
                App.Playlists.SetCurrentPlaylist(pl.Name);
            }
        }
        finally
        {
            _handlingPlaylistSelection = false;
        }
    }

    private void PlaylistListItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: Playlist pl })
        {
            return;
        }

        if (IsInsideNestedTracksList(sender as DependencyObject, e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (TryHandlePlaylistRowClick(pl, e))
        {
            e.Handled = true;
        }
    }

    private void PlaylistRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Playlist pl })
        {
            return;
        }

        if (TryHandlePlaylistRowClick(pl, e))
        {
            e.Handled = true;
        }
    }

    private bool TryHandlePlaylistRowClick(Playlist pl, MouseButtonEventArgs e)
    {
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return false;
        }

        HandlePlaylistRowClick(pl);
        return true;
    }

    private void HandlePlaylistRowClick(Playlist pl)
    {
        if (!ReferenceEquals(PlaylistsList.SelectedItem, pl))
        {
            PlaylistsList.SelectedItem = pl;
            return;
        }

        if (pl.IsExpanded)
        {
            pl.IsExpanded = false;
            return;
        }

        CollapseAllPlaylistsExcept(pl);
        pl.IsExpanded = true;
        if (pl.Kind is PlaylistKind.Liked or PlaylistKind.Saves || pl.Tracks.Count == 0)
        {
            RefreshTracks(pl);
        }
        else
        {
            QueueArtworkLoad(pl);
        }
    }

    private static bool IsInsideNestedTracksList(DependencyObject? listBoxItem, DependencyObject? source)
    {
        if (listBoxItem is null || source is null)
        {
            return false;
        }

        var tracksList = FindDescendant<ListBox>(listBoxItem);
        if (tracksList is null)
        {
            return false;
        }

        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, tracksList))
            {
                return true;
            }
        }

        return false;
    }

    private void PlaylistAddMusicMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Playlist context is resolved from PlacementTarget via PlaylistFromMenuItem.
    }

    private void PlaylistExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not ToggleButton tb || tb.DataContext is not Playlist pl)
        {
            return;
        }

        PlaylistsList.SelectedItem = pl;
        if (tb.IsChecked == true)
        {
            CollapseAllPlaylistsExcept(pl);
            pl.IsExpanded = true;
            RefreshTracks(pl);
        }
        else
        {
            pl.IsExpanded = false;
        }
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("New playlist", "Name for the new playlist:", "My Playlist");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var created = App.Playlists.CreatePlaylist(name);
            SelectPlaylistByName(created.Name);
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning(ex.Message);
        }
    }

    private void PlaylistRenameButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Playlist pl })
        {
            return;
        }

        var name = PromptForName("Rename playlist", "New name:", pl.Name);
        if (string.IsNullOrWhiteSpace(name) || name == pl.Name) return;
        try
        {
            var oldFolder = Path.Combine(App.Playlists.Root, pl.Name) + Path.DirectorySeparatorChar;
            App.Playlists.RenamePlaylist(pl.Name, name);
            var newFolder = Path.Combine(App.Playlists.Root, name) + Path.DirectorySeparatorChar;
            // Any liked tracks that lived in the old folder now live in the new folder.
            App.LikedSongs.ReplacePathPrefix(oldFolder, newFolder);
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning(ex.Message);
        }
    }

    private void PlaylistDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Playlist pl })
        {
            return;
        }

        var ok = ModernMessageBox.ConfirmYesNo(
            $"Delete playlist \"{pl.Name}\" and all its files?",
            "Delete playlist",
            ModernMessageBox.Severity.Warning);
        if (!ok) return;
        try
        {
            var folder = Path.Combine(App.Playlists.Root, pl.Name) + Path.DirectorySeparatorChar;
            App.Playlists.DeletePlaylist(pl.Name);
            // The files are gone; prune any matching liked entries so we don't end up with
            // broken pointers in the Liked Songs list.
            App.LikedSongs.RemovePathsUnder(folder);
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning(ex.Message);
        }
    }

    // ----- Playlist right-click context menu -----

    /// <summary>
    /// Walks from a context-menu item up to its ContextMenu, then over to the
    /// PlacementTarget (the playlist row) and returns its bound <see cref="Playlist"/>.
    /// ContextMenus live in their own popup visual tree, so the usual
    /// <c>((FrameworkElement)sender).DataContext</c> trick doesn't reliably reach
    /// the row's DataContext on every WPF version.
    /// </summary>
    private static Playlist? PlaylistFromMenuItem(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var cm = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                 ?? mi.Parent as ContextMenu;
        if (cm?.PlacementTarget is FrameworkElement target)
        {
            return target.DataContext as Playlist;
        }
        return mi.DataContext as Playlist;
    }

    private void PlaylistContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var pl = cm.PlacementTarget is FrameworkElement target
            ? target.DataContext as Playlist
            : null;

        bool isNormal = pl is not null && pl.Kind == PlaylistKind.Normal;

        foreach (var obj in cm.Items)
        {
            if (obj is not MenuItem mi) continue;
            var header = mi.Header as string ?? string.Empty;

            // Update the Pin item's label + icon to reflect the current state.
            if (header.StartsWith("Pin", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("Unpin", StringComparison.OrdinalIgnoreCase))
            {
                bool pinned = pl?.IsUserPinned == true;
                mi.Header = pinned ? "Unpin playlist" : "Pin playlist";
                // Note: System.IO.Path is also `using`'d in this file, so we have
                // to spell out the WPF shape to disambiguate.
                if (mi.Icon is System.Windows.Shapes.Path p)
                {
                    p.Data = (Geometry)Application.Current.Resources[
                        pinned ? "Icon.PinOff" : "Icon.Pin"];
                }
                mi.IsEnabled = isNormal;
                continue;
            }

            // Items that only make sense for real (Normal) playlists. Play is always
            // enabled regardless of kind.
            switch (header)
            {
                case "Add files…":
                case "Add folder…":
                case "Paste audio link…":
                case "Open folder in File Explorer":
                case "Rename…":
                case "Delete playlist":
                    mi.IsEnabled = isNormal;
                    break;
                case "Remove icon":
                    mi.IsEnabled = pl?.HasCustomIcon == true;
                    break;
            }
        }
    }

    private void PlaylistIconButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Playlist pl })
        {
            return;
        }

        ChangePlaylistIcon(pl);
    }

    private void PlaylistContextChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null) return;
        ChangePlaylistIcon(pl);
    }

    private void PlaylistContextRemoveIcon_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || !pl.HasCustomIcon) return;
        PlaylistIconService.ClearIcon(pl);
    }

    private void ChangePlaylistIcon(Playlist pl)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose playlist icon",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        var owner = Window.GetWindow(this);
        if (dlg.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            var icon = PlaylistIconService.SetIconFromFile(pl, dlg.FileName);
            if (icon is null)
            {
                ModernMessageBox.ShowWarning("Could not load the selected image.");
            }
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning($"Could not set playlist icon: {ex.Message}");
        }
    }

    private void PlaylistContextPlay_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null) return;

        PlaylistsList.SelectedItem = pl;
        PlaylistPlayButton_Click(new Button { Tag = pl }, e);
    }

    private void PlaylistContextPin_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;

        var pinned = App.Settings.Current.PinnedPlaylists ??= new List<string>();
        bool nowPinned = !pl.IsUserPinned;

        // Mirror the change into both the persisted list and the live model so
        // the row's badge updates immediately without waiting for RefreshAll.
        if (nowPinned)
        {
            if (!pinned.Contains(pl.Name, StringComparer.OrdinalIgnoreCase))
                pinned.Add(pl.Name);
        }
        else
        {
            pinned.RemoveAll(n => string.Equals(n, pl.Name, StringComparison.OrdinalIgnoreCase));
        }
        pl.IsUserPinned = nowPinned;
        foreach (var playlist in _playlistsCache)
        {
            playlist.IsUserPinned = pinned.Contains(playlist.Name, StringComparer.OrdinalIgnoreCase);
        }

        App.Settings.ScheduleSave();
        RebuildPlaylistItemsList();
    }

    private void PlaylistContextAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;
        AddFilesToPlaylist(pl);
    }

    private void PlaylistContextAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;
        AddFolderToPlaylist(pl);
    }

    private void PlaylistContextDownload_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;
        DownloadUrlIntoPlaylist(pl);
    }

    private void PlaylistContextOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;

        var folder = Path.Combine(App.Playlists.Root, pl.Name);
        if (!Directory.Exists(folder))
        {
            ModernMessageBox.ShowWarning($"Folder \"{folder}\" doesn't exist.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning(ex.Message);
        }
    }

    private void PlaylistContextRename_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;

        // Reuse the same flow as the inline rename button by faking the Tag.
        PlaylistRenameButton_Click(new Button { Tag = pl }, e);
    }

    private void PlaylistContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var pl = PlaylistFromMenuItem(sender);
        if (pl is null || pl.Kind != PlaylistKind.Normal) return;

        PlaylistDeleteButton_Click(new Button { Tag = pl }, e);
    }

    // ----- Tracks -----

    private void InitializeTracksListZoom()
    {
        var saved = App.Settings.Current.TracksListZoom;
        TracksZoom = Math.Clamp(saved > 0 ? saved : 1.0, TracksZoomMin, TracksZoomMax);
    }

    private void ApplyTracksListItemStyle()
    {
        // Item sizing is handled by bindings in the Track DataTemplate.
    }

    // ----- Accordion helpers -----

    private void CollapseAllPlaylistsExcept(Playlist? expanded)
    {
        _likedPlaylist.IsExpanded = expanded is not null && ReferenceEquals(expanded, _likedPlaylist);
        _savesPlaylist.IsExpanded = expanded is not null && ReferenceEquals(expanded, _savesPlaylist);
        foreach (var pl in _playlistsCache)
        {
            pl.IsExpanded = expanded is not null && ReferenceEquals(pl, expanded);
        }
    }

    private static T? FindDescendant<T>(DependencyObject? node) where T : DependencyObject
    {
        if (node is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private ListBox? FindTracksListForPlaylist(Playlist playlist)
    {
        var container = PlaylistsList.ItemContainerGenerator.ContainerFromItem(playlist) as DependencyObject;
        if (container is null) return null;
        // The nested tracks ListBox is the only ListBox inside the Playlist template.
        return FindDescendant<ListBox>(container);
    }

    private void TracksList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _trackDragStart = e.GetPosition(null);
        _trackDragPending = true;
    }

    private void TracksList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_trackDragPending)
        {
            return;
        }

        _trackDragPending = false;

        if (sender is not ListBox list || list.DataContext is not Playlist pl)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not Track track)
        {
            return;
        }

        PlayTrackFromList(pl, list, track);
    }

    private void PlayTrackFromList(Playlist pl, ListBox list, Track track)
    {
        SetTrackListSelectionPreservingScroll(list, track);

        if (pl.Kind == PlaylistKind.Normal)
        {
            App.Playlists.SetCurrentPlaylist(pl.Name);
        }

        var idx = pl.Tracks.IndexOf(track);
        if (idx < 0)
        {
            idx = 0;
        }

        App.Player.SetQueue(pl.Tracks, idx);
    }

    private static void SetTrackListSelectionPreservingScroll(ListBox list, object item)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(list);
        var offset = scrollViewer?.VerticalOffset ?? 0;

        list.SelectedItem = item;

        if (scrollViewer is not null)
        {
            scrollViewer.ScrollToVerticalOffset(offset);
        }
    }

    private void TracksList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_trackDragPending || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (sender is not ListBox list || list.DataContext is not Playlist pl || pl.Kind != PlaylistKind.Normal)
        {
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _trackDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _trackDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _trackDragPending = false;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not Track track)
        {
            return;
        }

        try
        {
            DragDrop.DoDragDrop(list, track, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.TracksList drag");
        }
    }

    private void TracksList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Track)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TracksList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (sender is not ListBox list) return;
            if (!e.Data.GetDataPresent(typeof(Track))) return;
            if (e.Data.GetData(typeof(Track)) is not Track dragged) return;
            if (list.DataContext is not Playlist pl || pl.Kind != PlaylistKind.Normal) return;

            var tracks = pl.Tracks;
            var oldIndex = tracks.IndexOf(dragged);
            if (oldIndex < 0)
            {
                oldIndex = tracks.ToList().FindIndex(t =>
                    string.Equals(t.FilePath, dragged.FilePath, StringComparison.OrdinalIgnoreCase));
            }
            if (oldIndex < 0) return;

            var newIndex = GetTracksDropIndex(e.GetPosition(list), list);
            newIndex = Math.Clamp(newIndex, 0, tracks.Count);
            if (newIndex > oldIndex) newIndex--;
            newIndex = Math.Clamp(newIndex, 0, Math.Max(0, tracks.Count - 1));
            if (oldIndex == newIndex) return;

            MoveTrackInPlaylist(pl, oldIndex, newIndex);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.TracksList drop");
        }
    }

    private int GetTracksDropIndex(Point position, ListBox list)
    {
        try
        {
            var hit = list.InputHitTest(position) as DependencyObject;
            var item = FindAncestor<ListBoxItem>(hit);
            if (item is null)
            {
                return list.Items.Count;
            }

            if (list.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                return list.Items.Count;
            }

            var index = list.ItemContainerGenerator.IndexFromContainer(item);
            if (index < 0)
            {
                return list.Items.Count;
            }

            var itemTop = item.TranslatePoint(new Point(0, 0), list).Y;
            var relativeY = position.Y - itemTop;
            var height = ((FrameworkElement)item).ActualHeight;
            if (height <= 0)
            {
                return index;
            }

            return relativeY > height / 2 ? index + 1 : index;
        }
        catch
        {
            return list.Items.Count;
        }
    }

    private void MoveTrackInPlaylist(Playlist pl, int fromIndex, int toIndex)
    {
        var tracks = pl.Tracks;
        if (fromIndex < 0 || fromIndex >= tracks.Count) return;
        toIndex = Math.Clamp(toIndex, 0, tracks.Count - 1);

        var track = tracks[fromIndex];
        tracks.RemoveAt(fromIndex);
        tracks.Insert(toIndex, track);

        switch (pl.Kind)
        {
            case PlaylistKind.Normal:
                App.PlaylistOrders.SetOrder(pl.Name, tracks.Select(t => t.FilePath));
                break;
            case PlaylistKind.Liked:
                App.LikedSongs.Move(fromIndex, toIndex);
                break;
            case PlaylistKind.Saves:
                App.SavedSongs.Move(fromIndex, toIndex);
                break;
        }

        var list = FindTracksListForPlaylist(pl);
        if (list is not null)
        {
            list.SelectedItem = track;
            list.ScrollIntoView(track);
        }

        if (IsPlaylistActive(pl))
        {
            App.Player.UpdateQueueOrder(tracks);
        }
    }

    private void TracksList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            var step = e.Delta > 0 ? TracksZoomStep : -TracksZoomStep;
            TracksZoom = Math.Clamp(TracksZoom + step, TracksZoomMin, TracksZoomMax);
            App.Settings.Current.TracksListZoom = TracksZoom;
            App.Settings.Save();
            return;
        }

        // Scroll the list — never change the selected song on wheel.
        if (sender is not ListBox list)
        {
            return;
        }

        var nested = FindDescendantScrollViewer(list);
        if (nested is not null)
        {
            var scrollingUp = e.Delta > 0;
            var canScrollNested = scrollingUp
                ? nested.VerticalOffset > 0.5
                : nested.VerticalOffset < nested.ScrollableHeight - 0.5;

            if (canScrollNested)
            {
                // Let the nested track list scroll normally.
                return;
            }
        }

        // At the nested edge (or no overflow): scroll the outer playlists panel.
        if (PlaylistsScrollViewer is null)
        {
            return;
        }

        e.Handled = true;
        PlaylistsScrollViewer.ScrollToVerticalOffset(
            PlaylistsScrollViewer.VerticalOffset - e.Delta);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindDescendantScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void TracksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.DataContext is not Playlist pl) return;
        if (list.SelectedItem is not Track t) return;

        PlayTrackFromList(pl, list, t);
    }

    private void TracksList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select whichever row was right-clicked so the context menu acts on it.
        if (sender is ListBox list)
        {
            _activeTrackListForContextMenu = list;
        }

        if (e.OriginalSource is DependencyObject src)
        {
            var item = FindAncestor<ListBoxItem>(src);
            if (item is not null)
            {
                item.IsSelected = true;
                item.Focus();
            }
            else
            {
                if (sender is ListBox lb) lb.SelectedItem = null;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private ListBox? _activeTrackListForContextMenu;

    private Track? GetContextTrack() =>
        _activeTrackListForContextMenu?.SelectedItem as Track;

    private Playlist? GetActivePlaylist()
    {
        // Prefer the playlist that owns the track list that opened the context menu.
        if (_activeTrackListForContextMenu?.DataContext is Playlist plFromMenu)
        {
            return plFromMenu;
        }
        return PlaylistsList.SelectedItem as Playlist;
    }

    private void TrackContextPlay_Click(object sender, RoutedEventArgs e)
    {
        var t = GetContextTrack();
        if (t is null || GetActivePlaylist() is not Playlist pl) return;

        App.Playlists.SetCurrentPlaylist(pl.Name);
        var idx = pl.Tracks.IndexOf(t);
        if (idx < 0) idx = 0;
        App.Player.SetQueue(pl.Tracks, idx);
    }

    private void TrackContextShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var t = GetContextTrack();
        if (t is null) return;

        try
        {
            if (!File.Exists(t.FilePath))
            {
                ModernMessageBox.ShowInfo("This file no longer exists on disk.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{t.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not open File Explorer: " + ex.Message);
        }
    }

    private void TrackContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var t = GetContextTrack();
        if (t is null) return;

        try
        {
            Clipboard.SetText(t.FilePath);
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not access clipboard: " + ex.Message);
        }
    }

    private void TrackContextEdit_Click(object sender, RoutedEventArgs e)
    {
        var t = GetContextTrack();
        var pl = GetActivePlaylist();
        if (t is null || pl is null) return;

        var edit = PromptForEditTrack(t);
        if (edit is null) return;

        var wasCurrent = string.Equals(App.Player.CurrentTrack?.FilePath, t.FilePath,
            StringComparison.OrdinalIgnoreCase);

        if (wasCurrent)
        {
            // Releases the file handle so tag writes / file moves can succeed.
            App.Player.Stop();
        }

        try
        {
            var oldPath = t.FilePath;
            string newPath;
            if (pl.Kind == PlaylistKind.Normal)
            {
                newPath = App.Playlists.EditTrack(
                    pl.Name,
                    Path.GetFileName(oldPath),
                    edit.Artist,
                    edit.Title,
                    edit.CoverImagePath);
            }
            else
            {
                newPath = App.Playlists.EditTrackAtPath(
                    oldPath,
                    edit.Artist,
                    edit.Title,
                    edit.CoverImagePath);
            }

            App.LikedSongs.ReplacePath(oldPath, newPath);
            App.Artwork.Invalidate(oldPath);
            App.Artwork.Invalidate(newPath);
            t.ArtworkSource = null;

            if (pl.Kind == PlaylistKind.Normal)
            {
                RefreshAfterDownload(pl.Name);
            }
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning(ex.Message);
        }
    }

    private void TrackContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var cm = sender as ContextMenu;
        if (cm is null) return;
        _activeTrackListForContextMenu = cm.PlacementTarget as ListBox;

        var t = GetContextTrack();
        if (t is null) return;

        bool liked = App.LikedSongs.Contains(t.FilePath);
        MenuItem? likeItem = null;
        System.Windows.Shapes.Path? likeIcon = null;
        Separator? beforeEdit = null;
        MenuItem? editItem = null;
        MenuItem? deleteItem = null;
        System.Windows.Shapes.Path? deleteIcon = null;

        foreach (var obj in cm.Items)
        {
            if (obj is MenuItem mi)
            {
                if (Equals(mi.Tag, "like")) { likeItem = mi; likeIcon = mi.Icon as System.Windows.Shapes.Path; }
                else if (Equals(mi.Tag, "edit")) editItem = mi;
                else if (Equals(mi.Tag, "delete")) { deleteItem = mi; deleteIcon = mi.Icon as System.Windows.Shapes.Path; }
            }
            else if (obj is Separator sep && Equals(sep.Tag, "before-edit"))
            {
                beforeEdit = sep;
            }
        }

        if (likeItem is not null)
        {
            likeItem.Header = liked ? "Remove from Liked Songs" : "Add to Liked Songs";
        }
        if (likeIcon is not null)
        {
            likeIcon.Fill = liked
                ? (Brush)Application.Current.Resources["Brush.TextDim"]
                : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }

        // Tailor the rename/delete row to whichever playlist owns this menu invocation.
        var active = GetActivePlaylist();
        bool isVirtual = active is not null && active.Kind != PlaylistKind.Normal;
        bool isLiked = active is not null && active.Kind == PlaylistKind.Liked;

        if (beforeEdit is not null) beforeEdit.Visibility = isVirtual ? Visibility.Collapsed : Visibility.Visible;
        if (editItem is not null) editItem.Visibility = isVirtual ? Visibility.Collapsed : Visibility.Visible;

        if (isLiked)
        {
            if (deleteItem is not null) deleteItem.Header = "Remove from Liked Songs";
            if (deleteIcon is not null)
            {
                deleteIcon.Data = (Geometry)Application.Current.Resources["Icon.Heart"];
                deleteIcon.Fill = (Brush)Application.Current.Resources["Brush.TextDim"];
            }
            if (deleteItem is not null) deleteItem.Visibility = Visibility.Visible;
        }
        else if (isVirtual)
        {
            if (deleteItem is not null) deleteItem.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (deleteItem is not null) deleteItem.Header = "Delete from playlist";
            if (deleteIcon is not null)
            {
                deleteIcon.Data = (Geometry)Application.Current.Resources["Icon.Trash"];
                deleteIcon.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            }
            if (deleteItem is not null) deleteItem.Visibility = Visibility.Visible;
        }
    }

    private void TrackContextLike_Click(object sender, RoutedEventArgs e)
    {
        var t = GetContextTrack();
        if (t is null) return;
        App.LikedSongs.Toggle(t.FilePath);
    }

    private void TrackContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var t = GetContextTrack();
        var pl = GetActivePlaylist();
        if (t is null || pl is null) return;

        if (pl.Kind == PlaylistKind.Liked)
        {
            // From the Liked virtual playlist, "delete" just unlikes — leave the file alone.
            App.LikedSongs.Remove(t.FilePath);
            return;
        }

        if (pl.Kind != PlaylistKind.Normal)
        {
            // Other virtual playlists don't support delete.
            return;
        }

        var confirm = ModernMessageBox.Confirm(
            $"Delete \"{t.FriendlyDisplayName}\" from \"{pl.Name}\"?\n\nThe file will be removed from disk.",
            "Delete song",
            ModernMessageBox.Severity.Warning);
        if (!confirm) return;

        var wasCurrent = string.Equals(App.Player.CurrentTrack?.FilePath, t.FilePath,
            StringComparison.OrdinalIgnoreCase);

        if (wasCurrent)
        {
            App.Player.Stop();
        }

        try
        {
            App.Playlists.RemoveTrack(pl.Name, Path.GetFileName(t.FilePath));
            // Also drop the file from Liked so we don't leave dangling pointers.
            App.LikedSongs.Remove(t.FilePath);
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not delete: " + ex.Message);
        }
    }

    private void PlayTrack_Click(object sender, RoutedEventArgs e)
    {
        // Removed with the split Songs panel. Playback is driven by playlist row play,
        // track double-click, or context menu.
    }

    // ----- Minimize / Exit -----

    private void MinimizeWidget_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is WidgetWindow widget)
        {
            widget.MinimizeWidget();
        }
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        // Close every window first so each one's Closed handler runs cleanly,
        // then Shutdown() fires OnExit (Player.Dispose, settings save, mutex release).
        try
        {
            foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
            {
                try { w.Close(); } catch { /* best-effort */ }
            }
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    // ----- Donate -----

    private async Task SyncUpdateButtonFromServiceAsync()
    {
        if (!_isPanelActive)
        {
            return;
        }

        if (App.Updates.IsAutoUpdateRunning)
        {
            SetUpdateButtonCheckingState();
            for (var i = 0; i < 120 && App.Updates.IsAutoUpdateRunning && _isPanelActive; i++)
            {
                await Task.Delay(100).ConfigureAwait(true);
            }
        }

        if (!_isPanelActive)
        {
            return;
        }

        if (App.Updates.LastCheckResult is not null || App.Updates.StartupCheckCompleted)
        {
            _pendingUpdate = App.Updates.LastCheckResult;
            ApplyUpdateButtonState(_pendingUpdate);
            return;
        }

        await CheckForUpdatesAsync(showUpToDateMessage: false);
    }

    private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
    {
        if (_updateCheckInFlight || !_isPanelActive)
        {
            return;
        }

        _updateCheckInFlight = true;
        SetUpdateButtonCheckingState();

        try
        {
            // Wait out the startup background check instead of ignoring the click.
            for (var i = 0; i < 150 && App.Updates.IsAutoUpdateRunning && _isPanelActive; i++)
            {
                await Task.Delay(100).ConfigureAwait(true);
            }

            if (!_isPanelActive)
            {
                return;
            }

            UpdateCheckResult? result;
            if (App.Updates.IsAutoUpdateRunning)
            {
                result = App.Updates.LastCheckResult;
            }
            else if (showUpToDateMessage
                     && App.Updates.LastCheckResult is not null
                     && App.Updates.StartupCheckCompleted
                     && !App.Updates.LastCheckResult.IsUpdateAvailable)
            {
                // Fresh user-initiated check.
                result = await App.Updates.CheckForUpdateAsync().ConfigureAwait(true);
            }
            else if (App.Updates.LastCheckResult is not null && !showUpToDateMessage)
            {
                result = App.Updates.LastCheckResult;
            }
            else
            {
                result = await App.Updates.CheckForUpdateAsync().ConfigureAwait(true);
            }

            if (!_isPanelActive)
            {
                return;
            }

            _pendingUpdate = result;
            ApplyUpdateButtonState(result);
            ShowUpdateCheckMessage(result, showUpToDateMessage);
        }
        catch (Exception ex)
        {
            if (_isPanelActive)
            {
                ApplyUpdateButtonState(null);
                if (showUpToDateMessage)
                {
                    ModernMessageBox.ShowWarning("Could not check for updates: " + ex.Message);
                }
            }
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private void ShowUpdateCheckMessage(UpdateCheckResult? result, bool showUpToDateMessage)
    {
        if (!showUpToDateMessage)
        {
            return;
        }

        if (result is null)
        {
            ModernMessageBox.ShowWarning(
                "Could not check for updates. Check your internet connection and try again.");
        }
        else if (result.IsUpdateAvailable)
        {
            ModernMessageBox.ShowInfo(
                $"Version {result.LatestVersion} is available. Click Update again to download and install.");
        }
        else
        {
            ModernMessageBox.ShowInfo(
                $"You are on the latest version (v{App.Updates.CurrentVersion}).");
        }
    }

    private void SetUpdateButtonCheckingState()
    {
        if (UpdateButton is null)
        {
            return;
        }

        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = false;
        UpdateButton.Padding = new Thickness(0);
        UpdateButton.Style = (Style)FindResource("DashboardToolbarButton");
        UpdateButton.ToolTip = "Checking for updates...";

        if (UpdateButtonIcon is not null)
        {
            UpdateButtonIcon.Margin = new Thickness(0);
            UpdateButtonIcon.Fill = (Brush)FindResource("Brush.TextDim");
        }
    }

    private void ApplyUpdateButtonState(UpdateCheckResult? result)
    {
        if (UpdateButton is null)
        {
            return;
        }

        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = true;
        UpdateButton.Padding = new Thickness(0);
        UpdateButton.Style = (Style)FindResource("DashboardToolbarButton");

        if (result is null)
        {
            UpdateButton.ToolTip = "Could not check for updates. Click to try again.";

            if (UpdateButtonIcon is not null)
            {
                UpdateButtonIcon.Margin = new Thickness(0);
                UpdateButtonIcon.Fill = (Brush)FindResource("Brush.TextDim");
            }

            return;
        }

        if (result.IsUpdateAvailable)
        {
            UpdateButton.ToolTip = $"Version {result.LatestVersion} is available. Click to download and install.";

            if (UpdateButtonIcon is not null)
            {
                UpdateButtonIcon.Margin = new Thickness(0);
                UpdateButtonIcon.Fill = (Brush)FindResource("Brush.Blue");
            }

            return;
        }

        UpdateButton.ToolTip = $"You are on the latest version (v{App.Updates.CurrentVersion}). Click to check again.";

        if (UpdateButtonIcon is not null)
        {
            UpdateButtonIcon.Margin = new Thickness(0);
            UpdateButtonIcon.Fill = (Brush)FindResource("Brush.TextDim");
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInstallInFlight || _updateCheckInFlight)
        {
            return;
        }

        try
        {
            if (_pendingUpdate?.IsUpdateAvailable == true)
            {
                await InstallPendingUpdateAsync().ConfigureAwait(true);
                return;
            }

            await CheckForUpdatesAsync(showUpToDateMessage: true).ConfigureAwait(true);

            if (_pendingUpdate?.IsUpdateAvailable == true)
            {
                await InstallPendingUpdateAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.UpdateButton_Click");
            ModernMessageBox.ShowWarning("Update check failed: " + ex.Message);
            ApplyUpdateButtonState(null);
        }
    }

    private async Task InstallPendingUpdateAsync()
    {
        var update = _pendingUpdate;
        if (update is null || !update.IsUpdateAvailable || _updateInstallInFlight)
        {
            return;
        }

        // Always resolve to a GitHub Releases download URL (asset or /latest/download).
        var downloadUrl = string.IsNullOrWhiteSpace(update.DownloadUrl)
            ? UpdateService.LatestInstallerDownloadUrl
            : update.DownloadUrl!;
        update = update with { DownloadUrl = downloadUrl };
        _pendingUpdate = update;

        var confirmed = ModernMessageBox.ConfirmYesNo(
            $"Download and install MusicWidget {update.LatestVersion} from GitHub?\n\n" +
            "MusicWidget will download the official installer, then close and reopen after setup.",
            "Install update",
            ModernMessageBox.Severity.Question);
        if (!confirmed)
        {
            return;
        }

        _updateInstallInFlight = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = "Downloading update from GitHub...";
        if (UpdateButtonIcon is not null)
        {
            UpdateButtonIcon.Fill = (Brush)FindResource("Brush.Blue");
        }

        try
        {
            var progress = new Progress<double>(p =>
            {
                if (UpdateButton is null)
                {
                    return;
                }

                UpdateButton.ToolTip = p < 0
                    ? "Downloading update from GitHub..."
                    : $"Downloading update… {(int)Math.Round(Math.Clamp(p, 0, 1) * 100)}%";
            });

            // Download the official GitHub Releases installer in-process (with %),
            // then hand off to a silent install + relaunch script.
            string installerPath;
            try
            {
                installerPath = await App.Updates
                    .DownloadInstallerAsync(downloadUrl, progress)
                    .ConfigureAwait(true);
            }
            catch (Exception downloadEx)
            {
                CrashLog.Write(downloadEx, "SettingsPanel.InstallPendingUpdateAsync.Download");
                // Fallback: PowerShell downloads the same GitHub URL after we close.
                UpdateButton.ToolTip = "Starting update download...";
                if (!App.Updates.BeginDetachedUpdateAndShutdown(update))
                {
                    throw;
                }

                return;
            }

            UpdateButton.ToolTip = "Installing update...";
            if (!App.Updates.BeginSilentInstallFromFileAndShutdown(installerPath))
            {
                throw new InvalidOperationException("Could not start the downloaded installer.");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SettingsPanel.InstallPendingUpdateAsync");
            _updateInstallInFlight = false;
            UpdateButton.IsEnabled = true;
            ApplyUpdateButtonState(update);
            ModernMessageBox.ShowWarning(
                "Could not download the update from GitHub: " + ex.Message +
                "\n\nYou can also grab MusicWidget-Setup-x64.exe from the Releases page.");
        }
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is WidgetWindow widget)
        {
            widget.MinimizeWidget();
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DonationUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowWarning("Could not open browser: " + ex.Message);
        }
    }

    // ----- Liked / Saved Songs (virtual playlists) -----

    private void OnLikedSongsChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(() =>
        {
            if (!_isPanelActive) return;
            try
            {
                RebuildLikedTracks();
                SyncTrackLikedStates();
                UpdateFooterLikeIcon();
                if (PlaylistsList.SelectedItem is Playlist sel && sel.Kind == PlaylistKind.Liked)
                {
                    QueueArtworkLoad(_likedPlaylist);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SettingsPanel.OnLikedSongsChanged");
            }
        });
    }

    private void OnSavedSongsChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(() =>
        {
            if (!_isPanelActive) return;
            try
            {
                RebuildSavedTracks();
                UpdateFooterSaveIcon();
                if (PlaylistsList.SelectedItem is Playlist sel && sel.Kind == PlaylistKind.Saves)
                {
                    QueueArtworkLoad(_savesPlaylist);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "SettingsPanel.OnSavedSongsChanged");
            }
        });
    }

    // ----- Helpers -----

    private static EditTrackResult? PromptForEditTrack(Track track)
    {
        var owner = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive);
        var window = new EditTrackWindow(track);
        DismissibleWindow.Show(window, owner, () => window.Accepted);
        return window.Accepted ? window.Result : null;
    }

    private static string? PromptForName(string title, string prompt, string defaultValue)
    {
        var owner = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive);
        var window = new NameInputWindow(title, prompt, defaultValue);
        DismissibleWindow.Show(window, owner, () => window.Accepted);
        return window.Accepted ? window.Value : null;
    }
}
