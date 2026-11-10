using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using MusicWidget.Models;

namespace MusicWidget.Services;

using MusicWidget;

public sealed class PlaylistManager
{
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".alac"
    };

    private readonly SettingsStore _settings;
    private readonly PlaylistOrderStore _orders;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Timers.Timer> _watchDebounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchDebounceLock = new();

    public event EventHandler? PlaylistsChanged;
    public event EventHandler<string>? PlaylistTracksChanged;

    public PlaylistManager(SettingsStore settings, PlaylistOrderStore orders)
    {
        _settings = settings;
        _orders = orders;
        EnsureRootExists();
    }

    public string Root => _settings.Current.PlaylistsRoot;

    private void EnsureRootExists()
    {
        try
        {
            Directory.CreateDirectory(Root);
        }
        catch { /* user can fix in settings */ }
    }

    public IReadOnlyList<Playlist> GetPlaylists()
    {
        EnsureRootExists();
        var list = new List<Playlist>();
        if (!Directory.Exists(Root))
        {
            return list;
        }

        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var pl = new Playlist(name, dir);
            LoadTracks(pl);
            list.Add(pl);
            EnsureWatcher(pl);
        }

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Playlist? GetCurrentPlaylist()
    {
        var name = _settings.Current.CurrentPlaylist;
        if (string.IsNullOrEmpty(name))
        {
            return GetPlaylists().FirstOrDefault();
        }

        var path = Path.Combine(Root, name);
        if (!Directory.Exists(path))
        {
            return GetPlaylists().FirstOrDefault();
        }

        var pl = new Playlist(name, path);
        LoadTracks(pl);
        EnsureWatcher(pl);
        return pl;
    }

    public void SetCurrentPlaylist(string name)
    {
        _settings.Current.CurrentPlaylist = name;
        _settings.Save();
    }

    public Playlist CreatePlaylist(string name)
    {
        var clean = Sanitize(name);
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new ArgumentException("Playlist name cannot be empty.");
        }

        var path = Path.Combine(Root, clean);
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("A playlist with that name already exists.");
        }

        Directory.CreateDirectory(path);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);

        var pl = new Playlist(clean, path);
        EnsureWatcher(pl);
        return pl;
    }

    public void RenamePlaylist(string oldName, string newName)
    {
        var clean = Sanitize(newName);
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new ArgumentException("New name cannot be empty.");
        }

        var oldPath = Path.Combine(Root, oldName);
        var newPath = Path.Combine(Root, clean);

        if (!Directory.Exists(oldPath))
        {
            throw new InvalidOperationException("Playlist does not exist.");
        }
        if (Directory.Exists(newPath))
        {
            throw new InvalidOperationException("Another playlist with that name already exists.");
        }

        DisposeWatcher(oldName);
        Directory.Move(oldPath, newPath);

        _orders.RenamePlaylist(oldName, clean);

        if (string.Equals(_settings.Current.CurrentPlaylist, oldName, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Current.CurrentPlaylist = clean;
            _settings.Save();
        }

        EnsureWatcher(new Playlist(clean, newPath));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeletePlaylist(string name)
    {
        var path = Path.Combine(Root, name);
        if (!Directory.Exists(path)) return;

        DisposeWatcher(name);
        Directory.Delete(path, recursive: true);

        _orders.RemovePlaylist(name);

        if (string.Equals(_settings.Current.CurrentPlaylist, name, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Current.CurrentPlaylist = null;
            _settings.Save();
        }

        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveTrack(string playlistName, string fileName)
    {
        var path = Path.Combine(Root, playlistName, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
            _orders.RemovePath(playlistName, path);
        }
    }

    /// <summary>
    /// Renames a track on disk while keeping its original extension. The new display name
    /// is sanitized to remove characters that aren't legal on Windows file systems.
    /// Returns the new full path.
    /// </summary>
    public string RenameTrack(string playlistName, string oldFileName, string newDisplayName)
    {
        var folder = Path.Combine(Root, playlistName);
        var oldPath = Path.Combine(folder, oldFileName);
        if (!File.Exists(oldPath))
        {
            throw new FileNotFoundException("Track no longer exists.", oldPath);
        }

        var clean = Sanitize(newDisplayName);
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new ArgumentException("Name cannot be empty.");
        }

        var ext = Path.GetExtension(oldFileName);
        var newFileName = clean + ext;
        var newPath = Path.Combine(folder, newFileName);

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return oldPath;
        }
        if (File.Exists(newPath))
        {
            throw new InvalidOperationException("A song with that name already exists in this playlist.");
        }

        File.Move(oldPath, newPath);
        _orders.ReplacePath(playlistName, oldPath, newPath);
        return newPath;
    }

    /// <summary>
    /// Renames a track by its absolute path, keeping the original extension. Used when
    /// the user renames a track from a virtual playlist (Liked Songs) where we don't
    /// know which folder the file belongs to. Returns the new full path.
    /// </summary>
    public string RenameTrackAtPath(string oldFullPath, string newDisplayName)
    {
        if (!File.Exists(oldFullPath))
        {
            throw new FileNotFoundException("Track no longer exists.", oldFullPath);
        }

        var clean = Sanitize(newDisplayName);
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new ArgumentException("Name cannot be empty.");
        }

        var folder = Path.GetDirectoryName(oldFullPath) ?? throw new InvalidOperationException("Bad file path.");
        var ext = Path.GetExtension(oldFullPath);
        var newPath = Path.Combine(folder, clean + ext);

        if (string.Equals(oldFullPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return oldFullPath;
        }
        if (File.Exists(newPath))
        {
            throw new InvalidOperationException("A song with that name already exists in this folder.");
        }

        File.Move(oldFullPath, newPath);

        var folderName = Path.GetFileName(folder);
        if (!string.IsNullOrEmpty(folderName))
        {
            _orders.ReplacePath(folderName, oldFullPath, newPath);
            PlaylistTracksChanged?.Invoke(this, folderName);
        }

        return newPath;
    }

    public string EditTrack(string playlistName, string oldFileName, string artist, string title, string? coverImagePath)
    {
        var folder = Path.Combine(Root, playlistName);
        var oldPath = Path.Combine(folder, oldFileName);
        if (!File.Exists(oldPath))
        {
            throw new FileNotFoundException("Track no longer exists.", oldPath);
        }

        return EditTrackAtPath(oldPath, artist, title, coverImagePath);
    }

    /// <summary>
    /// Updates a track's artist, title, and optional cover art. Renames the file to
    /// match the new display name when needed. Returns the final full path.
    /// </summary>
    public string EditTrackAtPath(string oldFullPath, string artist, string title, string? coverImagePath)
    {
        if (!File.Exists(oldFullPath))
        {
            throw new FileNotFoundException("Track no longer exists.", oldFullPath);
        }

        var displayName = TrackNameFormatter.BuildDisplayName(artist, title);
        var clean = Sanitize(displayName);
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new ArgumentException("Song name cannot be empty.");
        }

        var folder = Path.GetDirectoryName(oldFullPath) ?? throw new InvalidOperationException("Bad file path.");
        var ext = Path.GetExtension(oldFullPath);
        var newPath = Path.Combine(folder, clean + ext);

        if (!string.Equals(oldFullPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(newPath))
            {
                throw new InvalidOperationException("A song with that name already exists in this folder.");
            }

            File.Move(oldFullPath, newPath);

            var folderName = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(folderName))
            {
                _orders.ReplacePath(folderName, oldFullPath, newPath);
            }
        }

        TrackTagService.WriteMetadata(newPath, artist, title, coverImagePath);
        return newPath;
    }

    public void AddTrackByCopy(string playlistName, string sourceFilePath)
    {
        var destDir = Path.Combine(Root, playlistName);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(sourceFilePath));
        if (File.Exists(dest)) return;
        File.Copy(sourceFilePath, dest);
    }

    /// <summary>
    /// Moves a track file from one on-disk playlist into another, updating orders and
    /// Liked/Saves path pointers. Returns the new absolute path.
    /// </summary>
    public string MoveTrackToPlaylist(string sourcePlaylistName, string destPlaylistName, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePlaylistName)
            || string.IsNullOrWhiteSpace(destPlaylistName)
            || string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new ArgumentException("Missing playlist or file path.");
        }

        if (string.Equals(sourcePlaylistName, destPlaylistName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("That song is already in this playlist.");
        }

        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Track no longer exists.", sourceFilePath);
        }

        var destDir = EnsurePlaylistFolder(destPlaylistName);
        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(destDir, fileName);
        if (File.Exists(destPath))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var i = 2;
            do
            {
                destPath = Path.Combine(destDir, $"{stem} ({i}){ext}");
                i++;
            }
            while (File.Exists(destPath));
        }

        File.Move(sourceFilePath, destPath);
        _orders.RemovePath(sourcePlaylistName, sourceFilePath);
        _orders.AppendToOrder(destPlaylistName, [destPath]);

        try { App.LikedSongs.ReplacePath(sourceFilePath, destPath); } catch { /* best-effort */ }
        try { App.SavedSongs.ReplacePath(sourceFilePath, destPath); } catch { /* best-effort */ }

        PlaylistTracksChanged?.Invoke(this, sourcePlaylistName);
        PlaylistTracksChanged?.Invoke(this, destPlaylistName);
        return destPath;
    }

    /// <summary>
    /// Copies a track into another on-disk playlist (used from Liked/Saves). Returns the new path.
    /// </summary>
    public string CopyTrackToPlaylist(string destPlaylistName, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(destPlaylistName) || string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new ArgumentException("Missing playlist or file path.");
        }

        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Track no longer exists.", sourceFilePath);
        }

        var destDir = EnsurePlaylistFolder(destPlaylistName);
        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(destDir, fileName);
        if (File.Exists(destPath))
        {
            throw new InvalidOperationException($"\"{fileName}\" is already in \"{destPlaylistName}\".");
        }

        File.Copy(sourceFilePath, destPath);
        _orders.AppendToOrder(destPlaylistName, [destPath]);
        PlaylistTracksChanged?.Invoke(this, destPlaylistName);
        return destPath;
    }

    /// <summary>
    /// Copies multiple audio files into the playlist folder. Skips non-audio paths, missing files, and duplicates.
    /// </summary>
    public (int Added, int Skipped) AddTracksFromFiles(string playlistName, IEnumerable<string> filePaths)
    {
        var added = 0;
        var skipped = 0;
        var addedPaths = new List<string>();
        var destDir = Path.Combine(Root, playlistName);
        Directory.CreateDirectory(destDir);

        foreach (var raw in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = raw.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                skipped++;
                continue;
            }

            if (!IsAudioFile(path))
            {
                skipped++;
                continue;
            }

            var dest = Path.Combine(destDir, Path.GetFileName(path));
            if (File.Exists(dest))
            {
                skipped++;
                continue;
            }

            try
            {
                File.Copy(path, dest);
                added++;
                addedPaths.Add(dest);
            }
            catch
            {
                skipped++;
            }
        }

        if (added > 0)
        {
            _orders.AppendToOrder(playlistName, addedPaths);
            PlaylistTracksChanged?.Invoke(this, playlistName);
        }

        return (added, skipped);
    }

    /// <summary>
    /// Copies all audio files from a folder into the playlist (optionally recursive).
    /// </summary>
    public (int Added, int Skipped) AddTracksFromFolder(
        string playlistName,
        string folderPath,
        SearchOption searchOption = SearchOption.AllDirectories)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return (0, 0);
        }

        var files = Directory.EnumerateFiles(folderPath, "*", searchOption)
            .Where(IsAudioFile);
        return AddTracksFromFiles(playlistName, files);
    }

    public string EnsurePlaylistFolder(string playlistName)
    {
        var clean = Sanitize(playlistName);
        var path = Path.Combine(Root, clean);
        Directory.CreateDirectory(path);
        EnsureWatcher(new Playlist(clean, path));
        return path;
    }

    /// <summary>
    /// Re-reads the playlist's folder from disk and repopulates its <c>Tracks</c> list,
    /// preserving sort order. The same <c>Playlist</c> instance is mutated so existing
    /// bindings (e.g. the songs ListBox) continue to point at the right object.
    /// </summary>
    public void ReloadTracks(Playlist pl) => LoadTracks(pl);

    public List<string> EnumerateTrackFilesOnDisk(Playlist pl)
    {
        if (!Directory.Exists(pl.FolderPath))
        {
            return new List<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(pl.FolderPath, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains(".part", StringComparison.OrdinalIgnoreCase))
            .Where(seen.Add)
            .ToList();

        return OrderFiles(pl.Name, files).ToList();
    }

    public void ApplyTrackFiles(Playlist pl, IReadOnlyList<string> files)
    {
        var existing = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in pl.Tracks)
        {
            if (!existing.ContainsKey(track.FilePath))
            {
                existing[track.FilePath] = track;
            }
        }

        pl.Tracks.Clear();
        foreach (var f in files)
        {
            pl.Tracks.Add(existing.TryGetValue(f, out var track) ? track : new Track(f));
        }
    }

    /// <summary>
    /// Notifies listeners that a playlist folder changed on disk (e.g. during a batch download).
    /// </summary>
    public void RequestTracksRefresh(string playlistName) =>
        NotifyTracksChangedDebounced(playlistName);

    private void LoadTracks(Playlist pl) =>
        ApplyTrackFiles(pl, EnumerateTrackFilesOnDisk(pl));

    private IEnumerable<string> OrderFiles(string playlistName, List<string> files)
    {
        var savedOrder = _orders.GetOrder(playlistName);
        if (savedOrder is null || savedOrder.Count == 0)
        {
            return files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        }

        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in savedOrder)
        {
            if (!fileSet.Contains(path)) continue;
            ordered.Add(path);
            known.Add(path);
        }

        foreach (var path in files
                     .Where(f => !known.Contains(f))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(path);
        }

        return ordered;
    }

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void EnsureWatcher(Playlist pl)
    {
        if (_watchers.ContainsKey(pl.Name)) return;
        if (!Directory.Exists(pl.FolderPath)) return;

        try
        {
            var watcher = new FileSystemWatcher(pl.FolderPath)
            {
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler handler = (_, _) => NotifyTracksChangedDebounced(pl.Name);
            RenamedEventHandler renamed = (_, _) => NotifyTracksChangedDebounced(pl.Name);
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Changed += handler;
            watcher.Renamed += renamed;
            _watchers[pl.Name] = watcher;
        }
        catch
        {
            // Watcher is optional polish; absence is fine.
        }
    }

    private void NotifyTracksChangedDebounced(string playlistName)
    {
        if (App.BackgroundDownloads.IsRunning)
        {
            return;
        }

        lock (_watchDebounceLock)
        {
            if (!_watchDebounce.TryGetValue(playlistName, out var timer))
            {
                timer = new System.Timers.Timer(200) { AutoReset = false };
                timer.Elapsed += (_, _) =>
                {
                    try
                    {
                        PlaylistTracksChanged?.Invoke(this, playlistName);
                    }
                    catch
                    {
                        // UI handlers must not bubble back into the watcher thread.
                    }
                };
                _watchDebounce[playlistName] = timer;
            }

            timer.Stop();
            timer.Start();
        }
    }

    private void DisposeWatcher(string name)
    {
        lock (_watchDebounceLock)
        {
            if (_watchDebounce.TryGetValue(name, out var timer))
            {
                try
                {
                    timer.Stop();
                    timer.Dispose();
                }
                catch { }
                _watchDebounce.Remove(name);
            }
        }

        if (_watchers.TryGetValue(name, out var w))
        {
            try { w.Dispose(); } catch { }
            _watchers.Remove(name);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned;
    }
}
