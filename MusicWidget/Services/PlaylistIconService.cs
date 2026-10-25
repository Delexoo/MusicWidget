using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicWidget.Models;

namespace MusicWidget.Services;

/// <summary>
/// Loads and persists custom playlist cover images.
/// Normal playlists store <c>playlist-cover.png</c> inside the playlist folder;
/// Liked/Saves store icons under AppData.
/// </summary>
public static class PlaylistIconService
{
    public const string CoverFileName = "playlist-cover.png";

    public static string GetIconPath(Playlist playlist)
    {
        if (playlist.Kind == PlaylistKind.Normal && !string.IsNullOrWhiteSpace(playlist.FolderPath))
        {
            return Path.Combine(playlist.FolderPath, CoverFileName);
        }

        var key = playlist.Kind switch
        {
            PlaylistKind.Liked => "liked",
            PlaylistKind.Saves => "saves",
            _ => SanitizeKey(playlist.Name),
        };

        return Path.Combine(App.AppDataDir, "playlist-icons", key + ".png");
    }

    public static void Hydrate(Playlist playlist)
    {
        var path = GetIconPath(playlist);
        if (!File.Exists(path))
        {
            playlist.IconSource = null;
            return;
        }

        playlist.IconSource = LoadBitmap(path);
    }

    public static ImageSource? SetIconFromFile(Playlist playlist, string sourceImagePath)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
        {
            return null;
        }

        var dest = GetIconPath(playlist);
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Re-encode to PNG so the on-disk name always matches and formats are consistent.
        var bmp = LoadBitmap(sourceImagePath);
        if (bmp is null)
        {
            return null;
        }

        SaveAsPng(bmp, dest);
        playlist.IconSource = LoadBitmap(dest) ?? bmp;
        return playlist.IconSource;
    }

    public static void ClearIcon(Playlist playlist)
    {
        var path = GetIconPath(playlist);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        playlist.IconSource = null;
    }

    public static void RenameNormalPlaylist(string oldName, string newName, string playlistsRoot)
    {
        // Cover lives inside the folder and moves with Directory.Move — nothing to do.
        _ = oldName;
        _ = newName;
        _ = playlistsRoot;
    }

    private static string SanitizeKey(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "playlist" : name.Trim();
    }

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 160;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveAsPng(ImageSource source, string path)
    {
        BitmapSource bitmap = source switch
        {
            BitmapSource bs => bs,
            _ => throw new InvalidOperationException("Unsupported image source."),
        };

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
