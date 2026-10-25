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

    public int Volume { get; set; } = 80;

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
    /// Pinned playlists float above the Liked/Saves divider in the dashboard.
    /// </summary>
    public List<string> PinnedPlaylists { get; set; } = new();

    /// <summary>Song row scale in the dashboard tracks list (Ctrl+scroll). 1.0 = default.</summary>
    public double TracksListZoom { get; set; } = 1.0;

    /// <summary>Boost low frequencies during playback.</summary>
    public bool BassBoost { get; set; }

    /// <summary>Spatial reverb (LibVLC spatializer) during playback.</summary>
    public bool Reverb { get; set; }

    /// <summary>
    /// Real-time vocal remover (center-channel cancel + mid scoop).
    /// Inspired by VLC karaoke / voiceout-style phase cancellation used by
    /// online tools such as vocalremover.org for the non-AI path.
    /// </summary>
    public bool VocalRemover { get; set; }
}
