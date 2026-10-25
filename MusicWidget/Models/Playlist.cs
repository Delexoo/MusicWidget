using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MusicWidget.Models;

public enum PlaylistKind
{
    /// <summary>A user playlist backed by a real folder on disk.</summary>
    Normal,
    /// <summary>Virtual playlist of liked songs, kept in <c>liked-songs.json</c>.</summary>
    Liked,
    /// <summary>Virtual placeholder playlist for saves / downloads.</summary>
    Saves,
}

public sealed class Playlist : INotifyPropertyChanged
{
    private bool _isPlayingNow;
    private bool _isActivePlaylist;
    private bool _isUserPinned;
    private bool _isExpanded;
    private ImageSource? _iconSource;

    public string Name { get; }
    public string FolderPath { get; }
    public PlaylistKind Kind { get; }
    public string? IconKey { get; }

    /// <summary>Optional user-chosen playlist cover/icon image.</summary>
    public ImageSource? IconSource
    {
        get => _iconSource;
        set
        {
            if (ReferenceEquals(_iconSource, value)) return;
            _iconSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomIcon));
        }
    }

    public bool HasCustomIcon => _iconSource is not null;

    /// <summary>
    /// User-controlled pin flag for Normal playlists. Liked/Saves are always pinned
    /// regardless of this value (see <see cref="IsPinned"/>).
    /// </summary>
    public bool IsUserPinned
    {
        get => _isUserPinned;
        set
        {
            if (_isUserPinned == value) return;
            _isUserPinned = value;
            OnPropertyChanged();
            // IsPinned is derived from IsUserPinned + Kind, so notify it too so
            // the pinned-badge data triggers re-evaluate.
            OnPropertyChanged(nameof(IsPinned));
        }
    }

    /// <summary>
    /// True for virtual playlists (Liked/Saves) and for any Normal playlist the
    /// user has pinned. Pinned rows render with a leading badge and bubble up
    /// above the divider in the playlist list.
    /// </summary>
    public bool IsPinned => Kind != PlaylistKind.Normal || _isUserPinned;

    public ObservableCollection<Track> Tracks { get; } = new();

    /// <summary>
    /// UI-only state for the dashboard accordion.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlayingNow
    {
        get => _isPlayingNow;
        set
        {
            if (_isPlayingNow != value)
            {
                _isPlayingNow = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// True when this playlist is the current playback source (playing or paused).
    /// Drives play/pause toggle vs. play-from-start on the row play button.
    /// </summary>
    public bool IsActivePlaylist
    {
        get => _isActivePlaylist;
        set
        {
            if (_isActivePlaylist != value)
            {
                _isActivePlaylist = value;
                OnPropertyChanged();
            }
        }
    }

    public Playlist(string name, string folderPath, PlaylistKind kind = PlaylistKind.Normal, string? iconKey = null)
    {
        Name = name;
        FolderPath = folderPath;
        Kind = kind;
        IconKey = iconKey;
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
