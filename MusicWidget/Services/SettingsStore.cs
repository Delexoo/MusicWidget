using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MusicWidget.Models;

namespace MusicWidget.Services;

using MusicWidget;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public AppSettings Current { get; private set; } = new();

    public SettingsStore(string path)
    {
        _path = path;
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        // Older builds stored SlowedReverb; map onto Reverb when missing.
                        if (!loaded.Reverb
                            && json.Contains("\"SlowedReverb\"", StringComparison.Ordinal)
                            && (json.Contains("\"SlowedReverb\": true", StringComparison.Ordinal)
                                || json.Contains("\"SlowedReverb\":true", StringComparison.Ordinal)))
                        {
                            loaded.Reverb = true;
                        }

                        Current = loaded;
                    }
                }
            }
            catch
            {
                Current = new AppSettings();
            }

            if (MigrateLegacyPlaylistsRoot(Current))
            {
                try
                {
                    // Persist renamed Music\Beats → Music\MusicWidget immediately.
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var json = JsonSerializer.Serialize(Current, JsonOptions);
                    File.WriteAllText(_path, json);
                }
                catch
                {
                    // Best-effort; EnsureRootExists still creates the folder.
                }
            }

            Directory.CreateDirectory(Current.PlaylistsRoot);
        }
    }

    /// <summary>
    /// Renames <c>Music\Beats</c> to <c>Music\MusicWidget</c> and points PlaylistsRoot there.
    /// </summary>
    private static bool MigrateLegacyPlaylistsRoot(AppSettings settings)
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var legacyRoot = Path.Combine(music, AppBranding.LegacyAppDataFolderName); // Beats
        var newRoot = Path.Combine(music, AppBranding.DefaultPlaylistsFolderName); // MusicWidget
        var changed = false;

        static bool SamePath(string? a, string b) =>
            !string.IsNullOrWhiteSpace(a)
            && string.Equals(
                Path.GetFullPath(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Path.GetFullPath(b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                StringComparison.OrdinalIgnoreCase);

        static bool EndsWithFolder(string? path, string folderName)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase);
        }

        // Rename on-disk library folder when Beats still exists.
        if (Directory.Exists(legacyRoot))
        {
            if (!Directory.Exists(newRoot))
            {
                try
                {
                    Directory.Move(legacyRoot, newRoot);
                    changed = true;
                }
                catch
                {
                    // Locked files: create MusicWidget and merge what we can.
                    try
                    {
                        Directory.CreateDirectory(newRoot);
                        MergeDirectoryBestEffort(legacyRoot, newRoot);
                        changed = true;
                    }
                    catch
                    {
                        // Still retarget settings below.
                    }
                }
            }
            else if (!SamePath(legacyRoot, newRoot))
            {
                // Both exist — merge leftover Beats content into MusicWidget, then drop Beats if empty.
                MergeDirectoryBestEffort(legacyRoot, newRoot);
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
                    {
                        Directory.Delete(legacyRoot, recursive: false);
                    }
                }
                catch
                {
                    // Explorer / player may still lock Beats.
                }

                changed = true;
            }
        }

        var effectiveRoot = Directory.Exists(newRoot) ? newRoot
            : Directory.Exists(legacyRoot) ? legacyRoot
            : newRoot;

        // Retarget PlaylistsRoot away from Music\Beats or a bare Music folder.
        if (SamePath(settings.PlaylistsRoot, legacyRoot)
            || EndsWithFolder(settings.PlaylistsRoot, AppBranding.LegacyAppDataFolderName)
            || SamePath(settings.PlaylistsRoot, music)
            || string.IsNullOrWhiteSpace(settings.PlaylistsRoot))
        {
            if (!SamePath(settings.PlaylistsRoot, effectiveRoot))
            {
                settings.PlaylistsRoot = effectiveRoot;
                changed = true;
            }
        }

        if (string.Equals(settings.CurrentPlaylist, AppBranding.LegacyAppDataFolderName, StringComparison.OrdinalIgnoreCase))
        {
            settings.CurrentPlaylist = null;
            changed = true;
        }

        if (string.Equals(settings.LastDownloadPlaylist, AppBranding.LegacyAppDataFolderName, StringComparison.OrdinalIgnoreCase))
        {
            settings.LastDownloadPlaylist = null;
            changed = true;
        }

        // Rewrite any MusicFolders entries that still point under \Beats\.
        for (var i = 0; i < settings.MusicFolders.Count; i++)
        {
            var folder = settings.MusicFolders[i];
            if (string.IsNullOrWhiteSpace(folder)) continue;
            if (folder.Contains($"\\{AppBranding.LegacyAppDataFolderName}\\", StringComparison.OrdinalIgnoreCase)
                || folder.EndsWith($"\\{AppBranding.LegacyAppDataFolderName}", StringComparison.OrdinalIgnoreCase))
            {
                settings.MusicFolders[i] = folder.Replace(
                    $"\\{AppBranding.LegacyAppDataFolderName}",
                    $"\\{AppBranding.DefaultPlaylistsFolderName}",
                    StringComparison.OrdinalIgnoreCase);
                changed = true;
            }
        }

        // Ensure default root when nothing sensible is configured.
        if (string.IsNullOrWhiteSpace(settings.PlaylistsRoot))
        {
            settings.PlaylistsRoot = newRoot;
            changed = true;
        }

        return changed;
    }

    private static void MergeDirectoryBestEffort(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var target = Path.Combine(dest, name);
            try
            {
                if (Directory.Exists(entry))
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(entry, target);
                    }
                    else
                    {
                        MergeDirectoryBestEffort(entry, target);
                        if (!Directory.EnumerateFileSystemEntries(entry).Any())
                        {
                            Directory.Delete(entry, recursive: false);
                        }
                    }
                }
                else if (File.Exists(entry) && !File.Exists(target))
                {
                    File.Move(entry, target, overwrite: false);
                }
            }
            catch
            {
                // Skip locked items; next launch can finish the move.
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(Current, JsonOptions);
                var tempPath = _path + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _path, overwrite: true);
            }
            catch
            {
                // Best-effort; settings will fall back to defaults on next launch.
            }
        }
    }

    private System.Timers.Timer? _saveDebounceTimer;

    /// <summary>Coalesces rapid setting tweaks (e.g. volume slider) into one disk write.</summary>
    public void ScheduleSave(int delayMs = 400)
    {
        lock (_lock)
        {
            _saveDebounceTimer ??= new System.Timers.Timer { AutoReset = false };
            _saveDebounceTimer.Interval = delayMs;
            _saveDebounceTimer.Elapsed -= OnSaveDebounceElapsed;
            _saveDebounceTimer.Elapsed += OnSaveDebounceElapsed;
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }
    }

    private void OnSaveDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Save();
    }
}
