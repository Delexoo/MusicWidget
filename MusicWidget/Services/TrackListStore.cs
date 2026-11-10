using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MusicWidget.Services;

/// <summary>
/// Persists an ordered collection of track file paths to a JSON file. Used both for
/// the "Liked Songs" pinned playlist.
///
/// Insertion order is preserved so views can show the most recently added entries
/// first (Insert at index 0 on Add).
/// </summary>
public sealed class TrackListStore
{
    private readonly string _path;
    private readonly List<string> _ordered = new();
    private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
    private System.Timers.Timer? _saveDebounceTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public event EventHandler? Changed;

    public TrackListStore(string path)
    {
        _path = path;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            if (loaded is null) return;

            _ordered.Clear();
            _set.Clear();
            foreach (var p in loaded)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (_set.Add(p)) _ordered.Add(p);
            }
        }
        catch
        {
            // Best-effort: malformed file means we start empty.
        }
    }

    public bool Contains(string filePath) => !string.IsNullOrWhiteSpace(filePath) && _set.Contains(filePath);

    public IReadOnlyList<string> GetAll() => _ordered.AsReadOnly();

    public bool Add(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        if (!_set.Add(filePath)) return false;
        // Most recently added appears first.
        _ordered.Insert(0, filePath);
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        if (!_set.Remove(filePath)) return false;
        _ordered.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Updates a stored path in place (used after renaming a file on disk so the list
    /// keeps pointing at the right track). Preserves position. No-op if the old path
    /// isn't stored or the new path is already stored.
    /// </summary>
    public bool ReplacePath(string oldFilePath, string newFilePath)
    {
        if (string.IsNullOrWhiteSpace(oldFilePath) || string.IsNullOrWhiteSpace(newFilePath)) return false;
        if (!_set.Contains(oldFilePath)) return false;
        if (_set.Contains(newFilePath) &&
            !string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
        {
            // Avoid duplicate entries: drop the old one and keep the existing new one.
            Remove(oldFilePath);
            return true;
        }

        var idx = _ordered.FindIndex(p => string.Equals(p, oldFilePath, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;

        _set.Remove(oldFilePath);
        _set.Add(newFilePath);
        _ordered[idx] = newFilePath;

        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Rewrites every stored path that starts with <paramref name="oldPrefix"/> to use
    /// <paramref name="newPrefix"/> instead. Used after renaming a playlist folder so
    /// the list keeps pointing at the moved files.
    /// </summary>
    public bool ReplacePathPrefix(string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrEmpty(oldPrefix) || string.IsNullOrEmpty(newPrefix)) return false;

        bool any = false;
        for (int i = 0; i < _ordered.Count; i++)
        {
            var p = _ordered[i];
            if (!p.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var newPath = newPrefix + p.Substring(oldPrefix.Length);
            _set.Remove(p);
            _set.Add(newPath);
            _ordered[i] = newPath;
            any = true;
        }

        if (any)
        {
            ScheduleSave();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return any;
    }

    /// <summary>
    /// Drops every stored path that lives inside <paramref name="folderPrefix"/>. Use
    /// after deleting a playlist so dead pointers don't linger.
    /// </summary>
    public bool RemovePathsUnder(string folderPrefix)
    {
        if (string.IsNullOrEmpty(folderPrefix)) return false;

        var removed = _ordered.RemoveAll(p => p.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        _set.Clear();
        foreach (var p in _ordered) _set.Add(p);
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _ordered.Count) return;
        if (toIndex < 0 || toIndex >= _ordered.Count) return;
        if (fromIndex == toIndex) return;

        var item = _ordered[fromIndex];
        _ordered.RemoveAt(fromIndex);
        _ordered.Insert(toIndex, item);
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the new contained-state after toggling.</summary>
    public bool Toggle(string filePath)
    {
        if (Contains(filePath))
        {
            Remove(filePath);
            return false;
        }
        Add(filePath);
        return true;
    }

    private void ScheduleSave()
    {
        _saveDebounceTimer ??= new System.Timers.Timer(400) { AutoReset = false };
        _saveDebounceTimer.Elapsed -= OnSaveDebounceElapsed;
        _saveDebounceTimer.Elapsed += OnSaveDebounceElapsed;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void OnSaveDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Save();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_ordered.ToList(), JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
