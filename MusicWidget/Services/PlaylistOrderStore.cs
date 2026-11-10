using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MusicWidget.Services;

/// <summary>
/// Persists user-defined (or download-derived) track order for on-disk playlists.
/// Virtual playlists (Liked Songs) use <see cref="TrackListStore"/> instead.
/// </summary>
public sealed class PlaylistOrderStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<string>> _orders = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public PlaylistOrderStore(string path)
    {
        _path = path;
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, JsonOptions);
                if (loaded is null) return;

                _orders.Clear();
                foreach (var (name, paths) in loaded)
                {
                    if (string.IsNullOrWhiteSpace(name) || paths is null) continue;
                    _orders[name] = Deduplicate(paths);
                }
            }
            catch
            {
                // Best-effort: malformed file means we start empty.
            }
        }
    }

    public IReadOnlyList<string>? GetOrder(string playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return null;

        lock (_lock)
        {
            return _orders.TryGetValue(playlistName, out var list) ? list.AsReadOnly() : null;
        }
    }

    public void SetOrder(string playlistName, IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return;

        lock (_lock)
        {
            _orders[playlistName] = Deduplicate(paths);
            SaveUnlocked();
        }
    }

    /// <summary>
    /// Appends newly downloaded or imported tracks to the end of the saved order.
    /// Paths already in the list are moved to the end in the given sequence.
    /// </summary>
    public void MergeDownloadOrder(string playlistName, IEnumerable<string> newPathsInOrder)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return;

        lock (_lock)
        {
            var merged = _orders.TryGetValue(playlistName, out var existing)
                ? new List<string>(existing)
                : new List<string>();

            foreach (var path in newPathsInOrder)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                merged.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                merged.Add(path);
            }

            _orders[playlistName] = merged;
            SaveUnlocked();
        }
    }

    public void AppendToOrder(string playlistName, IEnumerable<string> newPaths)
    {
        MergeDownloadOrder(playlistName, newPaths);
    }

    public void RemovePlaylist(string playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return;

        lock (_lock)
        {
            if (!_orders.Remove(playlistName)) return;
            SaveUnlocked();
        }
    }

    public void RenamePlaylist(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;

        lock (_lock)
        {
            if (_orders.TryGetValue(oldName, out var list))
            {
                _orders.Remove(oldName);
                _orders[newName] = list;
                SaveUnlocked();
            }
        }
    }

    public void RemovePath(string playlistName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(playlistName) || string.IsNullOrWhiteSpace(filePath)) return;

        lock (_lock)
        {
            if (!_orders.TryGetValue(playlistName, out var list)) return;

            var removed = list.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;

            if (list.Count == 0)
            {
                _orders.Remove(playlistName);
            }

            SaveUnlocked();
        }
    }

    public void ReplacePath(string playlistName, string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(playlistName)
            || string.IsNullOrWhiteSpace(oldPath)
            || string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        lock (_lock)
        {
            if (!_orders.TryGetValue(playlistName, out var list)) return;

            var idx = list.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;

            list[idx] = newPath;
            SaveUnlocked();
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_orders, JsonOptions);
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    private static List<string> Deduplicate(IEnumerable<string> paths)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
            list.Add(path);
        }
        return list;
    }
}
