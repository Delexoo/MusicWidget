using System.IO;
using MusicWidget;

namespace MusicWidget.Models;
public sealed class AppSettings
{
    public List<string> MusicFolders { get; set; } = new();

    public string PlaylistsRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        AppBranding.DefaultPlaylistsFolderName);

    public string? CurrentPlaylist { get; set; }

    /// <summary>
    /// Session volume (0–200). Always reset to 100 on launch; not restored from disk.
    /// </summary>
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Current-session playback rate (0.5–2.0). Always reset to 1.0 on launch;
    /// not restored from disk.
    /// </summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    public bool LoopCurrent { get; set; } = false;

    public bool Shuffle { get; set; } = false;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>
    /// Legacy persisted position from older builds. Cleared on launch; widget always starts centered.
    /// </summary>
    public bool WindowPositionPinned { get; set; }

    public double? DashboardWidth { get; set; }
    public double? DashboardHeight { get; set; }

    public string? LastDownloadPlaylist { get; set; }

    /// <summary>
    /// Optional Netscape-format cookies file for yt-dlp and spotDL (YouTube, Instagram, and other sites).
    /// Export with a browser extension per yt-dlp wiki.
    /// </summary>
    public string? YoutubeCookiesFilePath { get; set; }

    /// <summary>
    /// Names of Normal playlists the user has pinned via the right-click menu.
    /// Pinned playlists float above the Liked Songs divider in the dashboard.
    /// </summary>
    public List<string> PinnedPlaylists { get; set; } = new();

    /// <summary>Song row scale in the dashboard tracks list (Ctrl+scroll). 1.0 = default.</summary>
    public double TracksListZoom { get; set; } = 1.0;

    /// <summary>Bass boost intensity: 0 = off, 1–3 (3 = extreme).</summary>
    public int BassBoostLevel { get; set; }

    /// <summary>
    /// Hall reverb amount (0–1) via LibVLC spatializer on the media graph.
    /// Older builds stored a bool <c>Reverb</c>; migrated on load.
    /// </summary>
    public double ReverbAmount { get; set; }

    /// <summary>
    /// Default vocal-remover preference for tracks without a per-path entry.
    /// </summary>
    public bool VocalRemover { get; set; }

    /// <summary>
    /// Per-track instrumental preference (true = play MDX instrumental when available).
    /// </summary>
    public Dictionary<string, bool> VocalsOffByPath { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, clicking outside the dashboard does not collapse it — only the
    /// minimize button (or hotkey) closes/hides the widget.
    /// </summary>
    public bool KeepDashboardOpen { get; set; }
}
