using System;
using System.Windows;
using System.Windows.Input;
using MusicWidget.Models;
using MusicWidget.Services;

namespace MusicWidget.Views;

public partial class DownloadUrlWindow : Window
{
    private readonly Playlist _playlist;
    private bool _trackingDownload;
    public bool AnyDownloaded { get; private set; }
    public bool OpenYoutubeCookiesRequested { get; private set; }

    public DownloadUrlWindow(Playlist playlist)
    {
        InitializeComponent();
        _playlist = playlist;
        TargetPlaylistText.Text = $"Saving to \"{playlist.Name}\"";
        Loaded += (_, _) => UrlBox.Focus();
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachDownloadHandlers();
    }

    private void DetachDownloadHandlers()
    {
        if (!_trackingDownload)
        {
            return;
        }

        App.BackgroundDownloads.ProgressChanged -= OnBackgroundDownloadProgress;
        App.BackgroundDownloads.Completed -= OnBackgroundDownloadCompleted;
        App.BackgroundDownloads.Queued -= OnBackgroundDownloadQueued;
        _trackingDownload = false;
    }

    private void EnsureTracking()
    {
        if (_trackingDownload)
        {
            return;
        }

        _trackingDownload = true;
        App.BackgroundDownloads.ProgressChanged += OnBackgroundDownloadProgress;
        App.BackgroundDownloads.Completed += OnBackgroundDownloadCompleted;
        App.BackgroundDownloads.Queued += OnBackgroundDownloadQueued;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            Download_Click(sender, new RoutedEventArgs());
        }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (!App.BackgroundDownloads.TryStart(
                _playlist,
                url ?? string.Empty,
                out var error,
                out var wasQueued,
                out var queueDepth))
        {
            DownloadStatus.Text = error ?? "Could not start download.";
            return;
        }

        EnsureTracking();
        AnyDownloaded = true;

        BackgroundHint.Visibility = Visibility.Visible;
        BackgroundHint.Text = "You can paste another link anytime. Close this window; downloads continue in the dashboard.";

        if (wasQueued)
        {
            DownloadStatus.Text = queueDepth <= 1
                ? "Queued — will start after the current download."
                : $"Queued (#{queueDepth}) — downloads run one after another.";
        }
        else
        {
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
            ProgressDetailRow.Visibility = Visibility.Collapsed;
            DownloadCountText.Text = string.Empty;
            DownloadSongText.Text = string.Empty;
            DownloadStatus.Text = "Preparing...";
        }

        // Keep the box ready for the next paste / queue entry.
        UrlBox.Text = string.Empty;
        UrlBox.IsEnabled = true;
        DownloadButton.IsEnabled = true;
        UrlBox.Focus();
    }

    private void OnBackgroundDownloadQueued(object? sender, BackgroundDownloadQueuedEventArgs e)
    {
        if (!string.Equals(e.PlaylistName, _playlist.Name, StringComparison.OrdinalIgnoreCase) || !IsLoaded)
        {
            return;
        }

        if (e.QueuePosition > 1)
        {
            DownloadStatus.Text = $"Queued (#{e.QueuePosition - 1} ahead) — will download next.";
        }
    }

    private void OnBackgroundDownloadProgress(object? sender, BackgroundDownloadProgressEventArgs e)
    {
        if (!string.Equals(e.PlaylistName, _playlist.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        DownloadProgress.Visibility = Visibility.Visible;
        BackgroundHint.Visibility = Visibility.Visible;

        var p = e.Update;
        if (p.Percent >= 0)
        {
            DownloadProgress.Value = p.Percent;
        }

        if (!string.IsNullOrWhiteSpace(p.CurrentSong))
        {
            DownloadSongText.Text = p.CurrentSong;
        }

        var queueSuffix = e.QueueRemaining > 0
            ? $"  ·  {e.QueueRemaining} queued"
            : string.Empty;

        if (string.IsNullOrWhiteSpace(p.Message))
        {
            return;
        }

        if (IsPlaylistProgressMessage(p.Message))
        {
            ProgressDetailRow.Visibility = Visibility.Visible;
            DownloadCountText.Text = p.Message + queueSuffix;
            DownloadStatus.Text = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(p.CurrentSong)
                 && string.Equals(p.Message, "Downloading...", StringComparison.OrdinalIgnoreCase))
        {
            ProgressDetailRow.Visibility = Visibility.Visible;
            DownloadCountText.Text = p.Message + queueSuffix;
            DownloadStatus.Text = string.Empty;
        }
        else if (string.Equals(p.Message, "Preparing...", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(p.Message, "Downloading...", StringComparison.OrdinalIgnoreCase))
        {
            DownloadStatus.Text = p.Message + queueSuffix;
        }
        else
        {
            DownloadStatus.Text = p.Message + queueSuffix;
        }
    }

    private void OnBackgroundDownloadCompleted(object? sender, BackgroundDownloadCompletedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        // Always refresh status when our playlist finished, or when the queue advances.
        if (string.Equals(e.PlaylistName, _playlist.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (e.Success)
            {
                AnyDownloaded = true;
            }

            if (e.HasMoreQueued)
            {
                DownloadStatus.Text = e.Success
                    ? $"Done. Starting next queued download ({e.QueueRemaining} left)..."
                    : $"{e.Error ?? "Failed."} Starting next queued download...";
                DownloadProgress.Value = 0;
            }
            else
            {
                HandleDownloadFinished(e.Success, e.Success ? "Done." : e.Error ?? "Download failed.");
            }
        }
        else if (e.HasMoreQueued && App.BackgroundDownloads.ActivePlaylistName is string next
                 && string.Equals(next, _playlist.Name, StringComparison.OrdinalIgnoreCase))
        {
            DownloadStatus.Text = "Starting queued download...";
            DownloadProgress.Value = 0;
        }
    }

    private void HandleDownloadFinished(bool success, string message)
    {
        if (success)
        {
            DownloadStatus.Text = "Done. Paste another link anytime.";
            DownloadCountText.Text = string.Empty;
            DownloadSongText.Text = string.Empty;
            ProgressDetailRow.Visibility = Visibility.Collapsed;
            DownloadProgress.Value = 100;
        }
        else
        {
            DownloadStatus.Text = message;
        }

        BackgroundHint.Text = "You can paste another link or close this window.";
        UrlBox.IsEnabled = true;
        DownloadButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void YoutubeCookiesSetup_Click(object sender, RoutedEventArgs e)
    {
        OpenYoutubeCookiesRequested = true;
        Close();
    }

    private static bool IsPlaylistProgressMessage(string message) =>
        message.Contains(" of ", StringComparison.OrdinalIgnoreCase)
        && message.Contains("songs", StringComparison.OrdinalIgnoreCase);
}
