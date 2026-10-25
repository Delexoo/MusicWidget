using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicWidget.Models;

using MusicWidget;

namespace MusicWidget.Services;

public sealed class ArtworkService
{
    private static readonly HttpClient _http = CreateClient();
    private static readonly SemaphoreSlim _loadConcurrency = new(6, 6);
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _inflight = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _memCache = new();
    private readonly ConcurrentDictionary<string, byte> _knownMisses = new();
    private readonly object _memCacheLock = new();
    private readonly Queue<string> _memCacheOrder = new();
    private const int MaxMemCacheEntries = 400;

    public ArtworkService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public Task<ImageSource?> GetArtworkAsync(Track track, CancellationToken ct = default)
    {
        var key = NormalizeKey(track.FilePath);
        if (_memCache.TryGetValue(key, out var cached) && cached is not null)
        {
            return Task.FromResult<ImageSource?>(cached);
        }

        if (_knownMisses.ContainsKey(key))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        return _inflight.GetOrAdd(key, _ => LoadAsync(track, ct));
    }

    /// <summary>
    /// Applies a cached bitmap to the track immediately when available (memory or disk cache).
    /// </summary>
    public bool TryHydrateTrackArtwork(Track track)
    {
        var key = NormalizeKey(track.FilePath);
        if (_memCache.TryGetValue(key, out var cached) && cached is not null)
        {
            track.ArtworkSource = cached;
            return true;
        }

        var cachePath = Path.Combine(_cacheDir, key + ".png");
        if (!File.Exists(cachePath))
        {
            return false;
        }

        var fromDisk = LoadBitmapOnUi(cachePath);
        if (fromDisk is null)
        {
            return false;
        }

        RememberInMemCache(key, fromDisk);
        track.ArtworkSource = fromDisk;
        return true;
    }

    private async Task<ImageSource?> LoadAsync(Track track, CancellationToken ct)
    {
        await _loadConcurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = NormalizeKey(track.FilePath);
            var cachePath = Path.Combine(_cacheDir, key + ".png");
            if (File.Exists(cachePath))
            {
                var fromDisk = await LoadBitmapOnUiAsync(cachePath).ConfigureAwait(false);
                if (fromDisk is not null)
                {
                    RememberInMemCache(key, fromDisk);
                    return fromDisk;
                }

                try
                {
                    File.Delete(cachePath);
                }
                catch
                {
                    // Stale cache cleanup is best-effort.
                }
            }

            string? title = null;
            string? artist = null;
            byte[]? embedded = null;

            try
            {
                (title, artist, embedded) = ReadTagsWithRetry(track.FilePath);
            }
            catch
            {
                // Reading tags is best-effort; some files don't have any.
            }

            if (embedded is null || embedded.Length == 0)
            {
                embedded = TryReadSidecarCover(track.FilePath);
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            {
                if (TrackNameFormatter.TryParseArtistTitle(track.DisplayName, out var parsedArtist, out var parsedTitle))
                {
                    title = parsedTitle;
                    artist = parsedArtist;
                }
                else
                {
                    title = track.FriendlyDisplayName;
                }
            }
            else if (!string.IsNullOrWhiteSpace(title))
            {
                title = TrackNameFormatter.Beautify(title);
            }

            UpdateTrackTagsOnUi(track, title, artist);

            byte[]? bytes = embedded;
            if (bytes is null || bytes.Length == 0)
            {
                bytes = await TryFetchOnlineAsync(track, title, artist, ct).ConfigureAwait(false);
            }

            if (bytes is null || bytes.Length == 0)
            {
                _knownMisses[key] = 1;
                return null;
            }

            var bmp = await CreateFrozenBitmapOnUiAsync(bytes).ConfigureAwait(false);
            if (bmp is null)
            {
                _knownMisses[key] = 1;
                return null;
            }

            _knownMisses.TryRemove(key, out _);

            try
            {
                await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
            }
            catch
            {
                // Disk cache is best-effort; keep the in-memory bitmap.
            }

            RememberInMemCache(key, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            _loadConcurrency.Release();
            _inflight.TryRemove(NormalizeKey(track.FilePath), out _);
        }
    }

    private static void UpdateTrackTagsOnUi(Track track, string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        void Apply()
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                track.Title = title;
            }

            if (!string.IsNullOrWhiteSpace(artist))
            {
                track.Artist = artist;
            }
        }

        if (dispatcher.CheckAccess()) Apply();
        else UiDispatcher.BeginInvokeSafe(Apply);
    }

    private static async Task<byte[]?> TryFetchOnlineAsync(
        Track track, string? title, string? artist, CancellationToken ct)
    {
        var query = BuildQuery(track, title, artist);
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var fromItunes = await TryFetchItunesAsync(query, ct).ConfigureAwait(false);
        if (fromItunes is { Length: > 0 })
        {
            return fromItunes;
        }

        return await TryFetchDeezerAsync(query, title, artist, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> TryFetchItunesAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query) +
                      "&entity=song&limit=5";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("artworkUrl100", out var artProp))
                {
                    continue;
                }

                var artUrl = artProp.GetString();
                if (string.IsNullOrEmpty(artUrl)) continue;

                artUrl = artUrl
                    .Replace("100x100bb", "600x600bb")
                    .Replace("100x100", "600x600");
                var bytes = await _http.GetByteArrayAsync(artUrl, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    return bytes;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryFetchDeezerAsync(
        string query, string? title, string? artist, CancellationToken ct)
    {
        try
        {
            // Prefer artist+title when available; otherwise use the combined query.
            var term = !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title)
                ? $"artist:\"{artist}\" track:\"{title}\""
                : query;

            var url = "https://api.deezer.com/search?q=" + Uri.EscapeDataString(term) + "&limit=5";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("album", out var album) ||
                    album.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? artUrl = null;
                if (album.TryGetProperty("cover_xl", out var xl))
                {
                    artUrl = xl.GetString();
                }
                else if (album.TryGetProperty("cover_big", out var big))
                {
                    artUrl = big.GetString();
                }
                else if (album.TryGetProperty("cover_medium", out var med))
                {
                    artUrl = med.GetString();
                }

                if (string.IsNullOrEmpty(artUrl)) continue;

                var bytes = await _http.GetByteArrayAsync(artUrl, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    return bytes;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildQuery(Track track, string? title, string? artist)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(artist)) sb.Append(artist).Append(' ');
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append(title);
        }
        else
        {
            sb.Append(track.FriendlyDisplayName);
        }
        return sb.ToString().Trim();
    }

    public void Invalidate(string filePath)
    {
        var key = NormalizeKey(filePath);
        _memCache.TryRemove(key, out _);
        _inflight.TryRemove(key, out _);
        _knownMisses.TryRemove(key, out _);

        var cachePath = Path.Combine(_cacheDir, key + ".png");
        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
            // Cache cleanup is best-effort.
        }
    }

    private static string SanitizeFilename(string name) => TrackNameFormatter.Beautify(name);

    private static (string? Title, string? Artist, byte[]? Cover) ReadTagsWithRetry(string filePath)
    {
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var title = tagFile.Tag.Title;
                var artist = tagFile.Tag.FirstPerformer ?? tagFile.Tag.FirstAlbumArtist;
                var cover = ReadBestEmbeddedCover(tagFile.Tag.Pictures);
                return (title, artist, cover);
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                Thread.Sleep(80);
            }
        }

        return (null, null, null);
    }

    private static byte[]? TryReadSidecarCover(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        if (dir is null || string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        string[] candidates =
        {
            Path.Combine(dir, baseName + ".jpg"),
            Path.Combine(dir, baseName + ".jpeg"),
            Path.Combine(dir, baseName + ".png"),
            Path.Combine(dir, baseName + ".webp"),
            Path.Combine(dir, "cover.jpg"),
            Path.Combine(dir, "cover.png"),
            Path.Combine(dir, "folder.jpg"),
            Path.Combine(dir, "folder.png"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return File.ReadAllBytes(path);
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static byte[]? ReadBestEmbeddedCover(TagLib.IPicture[]? pictures)
    {
        if (pictures is null || pictures.Length == 0)
        {
            return null;
        }

        TagLib.IPicture? bestFront = null;
        TagLib.IPicture? bestAny = null;
        foreach (var pic in pictures)
        {
            var data = pic.Data?.Data;
            if (data is null || data.Length == 0)
            {
                continue;
            }

            if (pic.Type is TagLib.PictureType.FrontCover or TagLib.PictureType.Other)
            {
                bestFront ??= pic;
            }

            bestAny ??= pic;
        }

        var chosen = bestFront ?? bestAny;
        return chosen?.Data?.Data;
    }

    private static Task<ImageSource?> CreateFrozenBitmapOnUiAsync(byte[] bytes)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(LoadBitmapFromBytes(bytes));
        }

        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                tcs.TrySetResult(LoadBitmapFromBytes(bytes));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Normal);

        return tcs.Task;
    }

    private static Task<ImageSource?> LoadBitmapOnUiAsync(string path)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(LoadBitmap(path));
        }

        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                tcs.TrySetResult(LoadBitmap(path));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Normal);

        return tcs.Task;
    }

    private static ImageSource? LoadBitmapOnUi(string path)
    {
        ImageSource? result = null;
        UiDispatcher.InvokeSafe(() => result = LoadBitmap(path));
        return result;
    }

    private static ImageSource? LoadBitmapFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 300;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
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
            bmp.DecodePixelWidth = 300;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void RememberInMemCache(string key, ImageSource? image)
    {
        if (image is null)
        {
            return;
        }

        lock (_memCacheLock)
        {
            _memCache[key] = image;
            _memCacheOrder.Enqueue(key);
            while (_memCacheOrder.Count > MaxMemCacheEntries)
            {
                var oldest = _memCacheOrder.Dequeue();
                _memCache.TryRemove(oldest, out _);
            }
        }
    }

    private static string NormalizeKey(string filePath)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
        return c;
    }
}
