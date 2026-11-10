namespace MusicWidget.Models;

/// <summary>
/// Sentinel item rendered between pinned playlists (Liked Songs) and the user's
/// normal playlists. Bound to a separate <c>DataTemplate</c> so it looks like a divider.
/// </summary>
public sealed class PlaylistDivider
{
    public static PlaylistDivider Instance { get; } = new();

    private PlaylistDivider() { }
}
