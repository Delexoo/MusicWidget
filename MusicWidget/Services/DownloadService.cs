using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using MusicWidget;
using MusicWidget.Models;

namespace MusicWidget.Services;

/// <summary>Download outcome; TechnicalDetail carries raw yt-dlp logs when present.</summary>
public readonly record struct DownloadResult(
    bool Success,
    string? Error,
    string? TechnicalDetail = null,
    IReadOnlyList<string>? DownloadedPathsInOrder = null);

public sealed class DownloadService
{
    private static readonly Regex PercentRegex = new(
        @"\[download\]\s+(?<pct>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled);

    private static readonly Regex YtDlpItemRegex = new(
        @"Downloading item\s+(?<cur>\d+)\s+of\s+(?<tot>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YtDlpPlaylistItemsRegex = new(
        @"Downloading\s+(?<tot>\d+)\s+items",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpotDlCompleteRegex = new(
        @"(?<done>\d+)/(?<total>\d+)\s+complete",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpotDlFoundRegex = new(
        @"found\s+(?<total>\d+)\s+songs?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YtDlpDestinationRegex = new(
        @"Destination:\s*(?<file>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpotDlSongRegex = new(
        @"(?:Downloaded|Processing|Skipping)\s+[""']?(?<title>[^""':\n]+?)(?:\s*[""']|:|\s*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Query keys YouTube adds for tracking; safe to drop for extraction.</summary>
    private static readonly HashSet<string> YouTubeTrackingQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "si", "feature", "pp", "utm_source", "utm_medium", "utm_campaign", "gclid", "fbclid",
        "mc_cid", "mc_eid",
    };

    /// <summary>Tracking/noise query params on social links (Instagram, TikTok, etc.).</summary>
    private static readonly HashSet<string> SocialTrackingQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "igsh", "igshid", "ig_rid", "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term",
        "fbclid", "gclid", "mc_cid", "mc_eid", "lang", "is_from_webapp", "is_copy_url", "share_id",
        "share_music_id", "sender_device", "sender_web_id", "sec_uid", "checksum", "refer",
        "referer", "referrer", "embed_source", "feature", "si",
    };

    private static readonly Regex InstagramAudioPathRegex = new(
        @"^/reels/audio/(?<id>\d+)/?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstagramMediaPathRegex = new(
        @"^/(?<kind>reels?|p|tv)/(?<id>[^/]+)/?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstagramShareReelRegex = new(
        @"^/share/reel/(?<id>[^/]+)/?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstagramProfileReelRegex = new(
        @"^/[^/]+/reels?/(?<id>[^/]+)/?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstagramOgVideoRegex = new(
        @"og:video(?::secure_url)?""\s+content=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstagramVideoUrlJsonRegex = new(
        @"""video_url""\s*:\s*""(?<url>https:\\/\\/[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Extra headers that help yt-dlp fetch public Instagram reels (see community downloader patterns).</summary>
    private static readonly string[] InstagramYtDlpExtraArgs =
    [
        "--user-agent", BrowserUserAgent,
        "--add-header", "Referer:https://www.instagram.com/",
        "--add-header", "Origin:https://www.instagram.com",
    ];

    /// <summary>Mobile Instagram client headers (instaloader / InstaDownload-style public reel access).</summary>
    private static readonly string[] InstagramMobileYtDlpExtraArgs =
    [
        "--add-header", "Referer:https://www.instagram.com/",
        "--add-header", "Origin:https://www.instagram.com",
        "--add-header",
        "User-Agent:Instagram 339.0.0.12.95 (iPhone16,1; iOS 18_2; en_US; en-US; scale=3.00; 1179x2556) AppleWebKit/420+",
    ];

    private static readonly (string Label, string[] ExtraArgs)[] InstagramExtractorStrategies =
    [
        ("instagram app_id", MergeExtraArgs(InstagramYtDlpExtraArgs,
            "--extractor-args", "instagram:app_id=124024574287414")),
        ("instagram mobile app_id", MergeExtraArgs(InstagramMobileYtDlpExtraArgs,
            "--extractor-args", "instagram:app_id=124024574287414")),
    ];

    private static readonly (string Label, string[] ExtraArgs)[] TikTokExtractorStrategies =
    [
        ("tiktok mobile API", ["--extractor-args", "tiktok:api=mobile"]),
        ("tiktok app API", ["--extractor-args", "tiktok:api=app"]),
    ];

    /// <summary>
    /// Primary YouTube client stack — works on most public videos without login.
    /// </summary>
    private static readonly string[] YouTubePrimaryExtraArgs =
        ["--extractor-args", "youtube:player_client=tv_embedded,web_embedded"];

    /// <summary>
    /// Alternate client stack when the primary pass is blocked or rate-limited.
    /// </summary>
    private static readonly string[] YouTubeRetryExtraArgs =
        ["--extractor-args", "youtube:player_client=web_safari,ios"];

    /// <summary>Windows browsers tried for session-aware downloads (yt-dlp --cookies-from-browser).</summary>
    private static readonly string[] BrowserCookieSources =
    [
        "edge",
        "chrome",
        "chromium",
        "opera",
        "opera_gx",
        "brave",
        "firefox",
        "librewolf",
        "waterfox",
        "vivaldi",
        "yandex",
        "whale",
        "arc",
    ];

    private static string BrowserCookieSourcesLabel =>
        "Edge, Chrome, Chromium, Opera, Opera GX, Brave, Firefox, LibreWolf, Waterfox, Vivaldi, Yandex, Whale, or Arc";

    private record struct YtDlpAttempt(string Label, string[] ExtraArgs, string? CookiesFromBrowser = null);

    private readonly ToolBootstrapper _tools;

    public DownloadService(ToolBootstrapper tools)
    {
        _tools = tools;
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destFolder,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        await _tools.EnsureToolsAsync(progress, ct);

        url = NormalizeDownloadInput(url);
        url = await UnwrapShortLinkAsync(url, ct);

        var instagramAudioSearch = IsInstagramAudioUrl(url)
            ? await TryBuildInstagramAudioSearchAsync(url, ct)
            : null;

        if (IsSpotifyUrl(url))
        {
            progress?.Report(DownloadProgressUpdate.Status("Preparing Spotify downloader (spotDL)..."));
            await _tools.EnsureSpotDlAsync(progress, ct);
            if (_tools.IsSpotDlReady)
            {
                var spotResult = await RunSpotDlAsync(url, destFolder, progress, ct);
                if (spotResult.Success || ct.IsCancellationRequested)
                {
                    return spotResult;
                }

                progress?.Report(DownloadProgressUpdate.Status(
                    "spotDL could not finish; trying YouTube search fallback..."));
            }
        }

        var input = url;
        try
        {
            var uri = new Uri(url);
            if (uri.Host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(DownloadProgressUpdate.Status("Reading Spotify metadata..."));
                var title = await TryResolveSpotifyTitleAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    input = $"ytsearch1:{title}";
                    progress?.Report(DownloadProgressUpdate.Status(
                        $"Spotify track resolved: {title}. Searching YouTube..."));
                }
                else
                {
                    progress?.Report(DownloadProgressUpdate.Status(
                        "Could not read Spotify metadata; trying yt-dlp directly..."));
                }
            }
        }
        catch
        {
            // Not a valid URI; pass-through to yt-dlp which can handle search strings.
        }

        if (IsYouTubeDownload(url, input))
        {
            return await DownloadYouTubeAsync(input, destFolder, progress, ct);
        }

        if (IsInstagramUrl(url))
        {
            return await DownloadInstagramAsync(
                url, input, instagramAudioSearch, destFolder, progress, ct);
        }

        if (IsTikTokUrl(url))
        {
            return await DownloadTikTokAsync(input, destFolder, progress, ct);
        }

        progress?.Report(DownloadProgressUpdate.Status("Downloading..."));
        return await RunYtDlpAsync(input, destFolder, Array.Empty<string>(), progress, ct,
            sleepRequestsSeconds: 0.4);
    }

    private async Task<DownloadResult> DownloadYouTubeAsync(
        string input,
        string destFolder,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        progress?.Report(DownloadProgressUpdate.Status("Downloading from YouTube..."));

        var attemptLog = new List<string>();
        foreach (var attempt in BuildYouTubeAttempts())
        {
            ct.ThrowIfCancellationRequested();
            if (attemptLog.Count > 0)
            {
                progress?.Report(DownloadProgressUpdate.Status(attempt.Label + "..."));
            }

            var result = await RunYtDlpAsync(
                input,
                destFolder,
                attempt.ExtraArgs,
                progress,
                ct,
                sleepRequestsSeconds: attempt.CookiesFromBrowser is null ? 0.5 : 0.65,
                cookiesFromBrowser: attempt.CookiesFromBrowser,
                skipCookiesFile: attempt.CookiesFromBrowser is not null);
            attemptLog.Add(FormatAttemptLog(attempt.Label, attempt.ExtraArgs, result));
            if (result.Success)
            {
                return result;
            }

            if (attempt.CookiesFromBrowser is not null && LooksLikeBrowserCookiesUnavailable(result))
            {
                continue;
            }

            if (!LooksLikeRetryableYouTubeError(result.Error)
                && !LooksLikeRetryableYouTubeError(result.TechnicalDetail))
            {
                return result;
            }
        }

        return BuildFailedResult(BuildYouTubeFailureMessage(attemptLog), attemptLog);
    }

    private static string BuildYouTubeFailureMessage(List<string> attemptLog)
    {
        var blob = string.Join('\n', attemptLog);
        if (blob.Contains("dpapi", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("failed to decrypt", StringComparison.OrdinalIgnoreCase))
        {
            return HasYoutubeCookies()
                ? "YouTube blocked this download and MusicWidget could not refresh cookies from your browser. Re-export cookies.txt in Dashboard → Download cookies, wait a few minutes, and try again."
                : $"MusicWidget could not read login cookies from {BrowserCookieSourcesLabel} (Windows blocked decryption). Export a cookies.txt file in Dashboard → Download cookies, then try again.";
        }

        return HasYoutubeCookies()
            ? $"YouTube blocked or rate-limited this download. Confirm the link is public or unlisted, stay signed into YouTube in {BrowserCookieSourcesLabel}, wait a few minutes, and try again."
            : $"YouTube blocked this download. Sign into YouTube in {BrowserCookieSourcesLabel} on this PC and try again — MusicWidget reads that login automatically. You can also add a cookies.txt file under Download cookies in the dashboard.";
    }

    private async Task<DownloadResult> DownloadInstagramAsync(
        string url,
        string input,
        string? instagramAudioSearch,
        string destFolder,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        progress?.Report(DownloadProgressUpdate.Status("Downloading from Instagram..."));

        var attemptLog = new List<string>();
        var lastResult = await RunYtDlpAsync(
            input, destFolder, InstagramYtDlpExtraArgs, progress, ct, sleepRequestsSeconds: 0.65);
        attemptLog.Add(FormatAttemptLog("instagram", InstagramYtDlpExtraArgs, lastResult));
        if (lastResult.Success || ct.IsCancellationRequested)
        {
            return lastResult;
        }

        lastResult = await RunInstagramFallbacksAsync(
            url, input, instagramAudioSearch, destFolder, lastResult, attemptLog, progress, ct);
        if (lastResult.Success)
        {
            return lastResult;
        }

        if (LooksLikeInstagramLoginRequired(lastResult.Error))
        {
            foreach (var browser in BrowserCookieSources)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(DownloadProgressUpdate.Status(
                    $"Retrying Instagram with your {browser} login..."));

                var browserAttempt = await RunYtDlpAsync(
                    input,
                    destFolder,
                    InstagramYtDlpExtraArgs,
                    progress,
                    ct,
                    sleepRequestsSeconds: 0.75,
                    cookiesFromBrowser: browser,
                    skipCookiesFile: true);
                attemptLog.Add(FormatAttemptLog($"instagram ({browser} login)", InstagramYtDlpExtraArgs, browserAttempt));
                if (browserAttempt.Success)
                {
                    return browserAttempt;
                }

                if (LooksLikeBrowserCookiesUnavailable(browserAttempt))
                {
                    continue;
                }
            }

            return new DownloadResult(
                false,
                $"Instagram asked you to sign in or blocked this link. Stay logged into Instagram in {BrowserCookieSourcesLabel}, or export a cookies.txt file in Dashboard → Download cookies.",
                string.Join(Environment.NewLine + Environment.NewLine, attemptLog));
        }

        if (IsInstagramAudioUrl(url) && LooksLikeUnsupportedUrl(lastResult.Error))
        {
            if (!string.IsNullOrWhiteSpace(instagramAudioSearch))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(DownloadProgressUpdate.Status(
                    "Instagram audio page - searching YouTube for the closest match..."));
                lastResult = await RunYtDlpAsync(instagramAudioSearch, destFolder, Array.Empty<string>(), progress, ct,
                    sleepRequestsSeconds: 0.35);
                attemptLog.Add(FormatAttemptLog("instagram audio -> ytsearch", Array.Empty<string>(), lastResult));
                if (lastResult.Success)
                {
                    return lastResult;
                }
            }

            return new DownloadResult(
                false,
                "Instagram audio pages are not direct video links. Open a reel that uses this sound, tap Share, Copy link, and paste that reel URL (instagram.com/reel/...) instead.",
                string.Join(Environment.NewLine + Environment.NewLine, attemptLog));
        }

        return lastResult;
    }

    private async Task<DownloadResult> DownloadTikTokAsync(
        string input,
        string destFolder,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        input = NormalizeTikTokUrl(input);
        progress?.Report(DownloadProgressUpdate.Status("Downloading from TikTok..."));

        var attemptLog = new List<string>();
        var tikTokHeaders = new[] { "--user-agent", BrowserUserAgent };

        var first = await RunYtDlpAsync(
            input, destFolder, TikTokExtractorStrategies[0].ExtraArgs, progress, ct, sleepRequestsSeconds: 0.55);
        attemptLog.Add(FormatAttemptLog(TikTokExtractorStrategies[0].Label, TikTokExtractorStrategies[0].ExtraArgs, first));
        if (first.Success || ct.IsCancellationRequested)
        {
            return first;
        }

        var lastResult = await RunTikTokFallbacksAsync(
            input, input, destFolder, first, attemptLog, progress, ct);
        if (lastResult.Success)
        {
            return lastResult;
        }

        foreach (var browser in BrowserCookieSources)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status(
                $"Retrying TikTok with your {browser} login..."));

            var browserAttempt = await RunYtDlpAsync(
                input,
                destFolder,
                tikTokHeaders,
                progress,
                ct,
                sleepRequestsSeconds: 0.75,
                cookiesFromBrowser: browser,
                skipCookiesFile: true);
            attemptLog.Add(FormatAttemptLog($"tiktok ({browser} login)", tikTokHeaders, browserAttempt));
            if (browserAttempt.Success)
            {
                return browserAttempt;
            }

            if (LooksLikeBrowserCookiesUnavailable(browserAttempt))
            {
                continue;
            }
        }

        return lastResult;
    }

    private IEnumerable<YtDlpAttempt> BuildYouTubeAttempts()
    {
        if (HasYoutubeCookies())
        {
            yield return new YtDlpAttempt("youtube (saved cookies)", YouTubePrimaryExtraArgs);
        }

        yield return new YtDlpAttempt("youtube", YouTubePrimaryExtraArgs);
        yield return new YtDlpAttempt("youtube alternate client", YouTubeRetryExtraArgs);
        yield return new YtDlpAttempt(
            "youtube android_vr",
            ["--extractor-args", "youtube:player_client=android_vr"]);

        foreach (var browser in BrowserCookieSources)
        {
            yield return new YtDlpAttempt(
                $"youtube ({browser} login)",
                YouTubePrimaryExtraArgs,
                browser);
        }
    }

    private DownloadResult BuildFailedResult(string friendly, List<string> attemptLog)
    {
        var blob = "yt-dlp executable: " + _tools.YtDlpPath + "\n\n"
                   + string.Join("\n\n----------\n\n", attemptLog);
        if (blob.Length > 120_000)
        {
            blob = blob[..120_000] + "\n... (truncated)";
        }

        return new DownloadResult(false, friendly, blob);
    }

    private static bool IsYouTubeDownload(string originalUrl, string input)
    {
        if (input.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsYouTubeExtractorInput(input))
        {
            return true;
        }

        return Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri)
               && IsYouTubeHost(uri.IdnHost);
    }

    private static bool HasYoutubeCookies()
    {
        var path = App.Settings.Current.YoutubeCookiesFilePath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static IReadOnlyList<string> BeautifyDownloadedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return paths;
        }

        var result = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            result.Add(BeautifyDownloadedFile(path));
        }

        return result;
    }

    private static string BeautifyDownloadedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return filePath;
        }

        var dir = Path.GetDirectoryName(filePath)!;
        var ext = Path.GetExtension(filePath);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var friendly = TrackNameFormatter.SanitizeFileName(baseName);

        string resolvedPath;
        if (string.Equals(baseName, friendly, StringComparison.OrdinalIgnoreCase))
        {
            resolvedPath = filePath;
        }
        else
        {
            var target = Path.Combine(dir, friendly + ext);
            target = EnsureUniqueFilePath(target);
            try
            {
                File.Move(filePath, target);
                resolvedPath = target;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "DownloadService.BeautifyDownloadedFile");
                resolvedPath = filePath;
                friendly = TrackNameFormatter.Beautify(baseName);
            }
        }

        WriteTagsFromFriendlyName(resolvedPath, friendly);
        return resolvedPath;
    }

    private static string EnsureUniqueFilePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 100; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    private static void WriteTagsFromFriendlyName(string path, string friendlyName)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            var hasTitle = !string.IsNullOrWhiteSpace(tagFile.Tag.Title);
            var hasArtist = tagFile.Tag.Performers is { Length: > 0 }
                && !string.IsNullOrWhiteSpace(tagFile.Tag.Performers[0]);

            if (hasTitle && hasArtist)
            {
                tagFile.Tag.Title = TrackNameFormatter.Beautify(tagFile.Tag.Title);
                tagFile.Save();
                return;
            }

            if (TrackNameFormatter.TryParseArtistTitle(friendlyName, out var artist, out var title))
            {
                if (!hasTitle)
                {
                    tagFile.Tag.Title = title;
                }

                if (!hasArtist)
                {
                    tagFile.Tag.Performers = [artist];
                }
            }
            else if (!hasTitle)
            {
                tagFile.Tag.Title = friendlyName;
            }

            tagFile.Save();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "DownloadService.WriteTagsFromFriendlyName");
        }
    }

    private static string FormatAttemptLog(string attemptName, IReadOnlyList<string> extraArgs, DownloadResult r)
    {
        var extra = extraArgs.Count == 0 ? string.Empty : " " + string.Join(" ", extraArgs);
        var body = !string.IsNullOrWhiteSpace(r.TechnicalDetail) ? r.TechnicalDetail : r.Error;
        return $"[{attemptName}{extra}]\n{body ?? "(no captured output)"}";
    }

    private static string NormalizeTikTokUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        url = StripSocialTrackingQuery(uri);

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            return url;
        }

        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }
        else if (!path.EndsWith('/'))
        {
            path += "/";
        }

        var host = uri.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)
            ? "www.tiktok.com"
            : uri.IdnHost;

        return uri.GetLeftPart(UriPartial.Scheme) + "//" + host + path + uri.Query;
    }

    private async Task<DownloadResult> RunInstagramFallbacksAsync(
        string url,
        string input,
        string? instagramAudioSearch,
        string destFolder,
        DownloadResult lastResult,
        List<string> attemptLog,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        if (TryCanonicalizeInstagramMediaUrl(url, out var canonical) &&
            !string.Equals(canonical, input, StringComparison.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status("Retrying with a canonical Instagram reel link..."));
            lastResult = await RunYtDlpAsync(canonical, destFolder, InstagramYtDlpExtraArgs, progress, ct,
                sleepRequestsSeconds: 0.85);
            attemptLog.Add(FormatAttemptLog("instagram canonical URL", InstagramYtDlpExtraArgs, lastResult));
            if (lastResult.Success)
            {
                return lastResult;
            }
        }

        if (TryGetInstagramEmbedUrl(url, out var embedUrl))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status("Retrying Instagram via embed page..."));
            lastResult = await RunYtDlpAsync(embedUrl, destFolder, InstagramYtDlpExtraArgs, progress, ct,
                sleepRequestsSeconds: 0.75);
            attemptLog.Add(FormatAttemptLog("instagram embed URL", InstagramYtDlpExtraArgs, lastResult));
            if (lastResult.Success)
            {
                return lastResult;
            }
        }

        foreach (var (label, extraArgs) in InstagramExtractorStrategies)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status($"Retrying Instagram ({label})..."));
            var target = TryCanonicalizeInstagramMediaUrl(url, out var canon) ? canon : input;
            lastResult = await RunYtDlpAsync(target, destFolder, extraArgs, progress, ct,
                sleepRequestsSeconds: 1.0);
            attemptLog.Add(FormatAttemptLog(label, extraArgs, lastResult));
            if (lastResult.Success)
            {
                return lastResult;
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(DownloadProgressUpdate.Status("Retrying Instagram with a slower request pace..."));
        lastResult = await RunYtDlpAsync(input, destFolder, InstagramYtDlpExtraArgs, progress, ct,
            sleepRequestsSeconds: 1.25);
        attemptLog.Add(FormatAttemptLog("instagram slow retry", InstagramYtDlpExtraArgs, lastResult));
        if (lastResult.Success)
        {
            return lastResult;
        }

        var directMedia = await TryResolveInstagramMediaUrlFromPageAsync(url, ct);
        if (!string.IsNullOrWhiteSpace(directMedia))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status("Found a direct media link; extracting audio..."));
            lastResult = await RunYtDlpAsync(directMedia, destFolder, InstagramYtDlpExtraArgs, progress, ct,
                sleepRequestsSeconds: 0.35);
            attemptLog.Add(FormatAttemptLog("instagram direct media URL", InstagramYtDlpExtraArgs, lastResult));
            if (lastResult.Success)
            {
                return lastResult;
            }
        }

        if (!string.IsNullOrWhiteSpace(instagramAudioSearch))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status(
                "Instagram audio page - searching YouTube for the closest match..."));
            lastResult = await RunYtDlpAsync(instagramAudioSearch, destFolder, Array.Empty<string>(), progress, ct,
                sleepRequestsSeconds: 0.35);
            attemptLog.Add(FormatAttemptLog("instagram audio -> ytsearch", Array.Empty<string>(), lastResult));
        }

        return lastResult;
    }

    private async Task<DownloadResult> RunTikTokFallbacksAsync(
        string url,
        string input,
        string destFolder,
        DownloadResult lastResult,
        List<string> attemptLog,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(DownloadProgressUpdate.Status("Retrying TikTok with a slower request pace..."));
        lastResult = await RunYtDlpAsync(input, destFolder, Array.Empty<string>(), progress, ct,
            sleepRequestsSeconds: 1.1);
        attemptLog.Add(FormatAttemptLog("tiktok slow retry", Array.Empty<string>(), lastResult));
        if (lastResult.Success)
        {
            return lastResult;
        }

        foreach (var (label, extraArgs) in TikTokExtractorStrategies)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(DownloadProgressUpdate.Status($"Retrying TikTok ({label})..."));
            lastResult = await RunYtDlpAsync(input, destFolder, extraArgs, progress, ct,
                sleepRequestsSeconds: 0.9);
            attemptLog.Add(FormatAttemptLog(label, extraArgs, lastResult));
            if (lastResult.Success)
            {
                return lastResult;
            }
        }

        return lastResult;
    }

    private static string[] MergeExtraArgs(IReadOnlyList<string> baseArgs, params string[] extra) =>
        baseArgs.Concat(extra).ToArray();

    private static void AppendCookiesArgs(ProcessStartInfo psi)
    {
        var path = App.Settings.Current.YoutubeCookiesFilePath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return;
        }

        psi.ArgumentList.Add("--cookies");
        psi.ArgumentList.Add(path);
    }

    private static void AppendSpotDlCookiesArgs(ProcessStartInfo psi)
    {
        var path = App.Settings.Current.YoutubeCookiesFilePath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return;
        }

        psi.ArgumentList.Add("--cookie-file");
        psi.ArgumentList.Add(path);
    }

    private static bool IsInstagramUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.IdnHost.Contains("instagram.com", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeInstagramLoginRequired(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("login", StringComparison.OrdinalIgnoreCase)
               || error.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
               || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
               || error.Contains("cookies", StringComparison.OrdinalIgnoreCase)
               || error.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
               || error.Contains("HTTP Error 401", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Private", StringComparison.OrdinalIgnoreCase)
               || error.Contains("not available", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCanonicalizeInstagramMediaUrl(string url, out string canonical)
    {
        canonical = url;
        if (IsInstagramAudioUrl(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.IdnHost.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? id = null;
        var segment = "reel";

        var shareMatch = InstagramShareReelRegex.Match(uri.AbsolutePath);
        if (shareMatch.Success)
        {
            id = shareMatch.Groups["id"].Value;
        }
        else
        {
            var profileMatch = InstagramProfileReelRegex.Match(uri.AbsolutePath);
            if (profileMatch.Success)
            {
                id = profileMatch.Groups["id"].Value;
            }
            else
            {
                var match = InstagramMediaPathRegex.Match(uri.AbsolutePath);
                if (!match.Success)
                {
                    return false;
                }

                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                id = match.Groups["id"].Value;
                segment = kind switch
                {
                    "reels" => "reel",
                    "reel" => "reel",
                    "p" => "p",
                    "tv" => "tv",
                    _ => "reel",
                };
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        canonical = $"https://www.instagram.com/{segment}/{id}/";
        return true;
    }

    private static bool TryGetInstagramEmbedUrl(string url, out string embedUrl)
    {
        embedUrl = url;
        if (!TryCanonicalizeInstagramMediaUrl(url, out var canonical))
        {
            return false;
        }

        if (!Uri.TryCreate(canonical, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        embedUrl = $"https://www.instagram.com/{parts[0]}/{parts[1]}/embed/captioned/";
        return true;
    }

    private static async Task<string?> TryResolveInstagramMediaUrlFromPageAsync(string url, CancellationToken ct)
    {
        var pageUrl = TryCanonicalizeInstagramMediaUrl(url, out var canonical) ? canonical : url;

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.instagram.com/");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            var html = await http.GetStringAsync(pageUrl, ct);

            var og = InstagramOgVideoRegex.Match(html);
            if (og.Success)
            {
                var direct = System.Net.WebUtility.HtmlDecode(og.Groups[1].Value.Trim());
                if (direct.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return direct;
                }
            }

            var json = InstagramVideoUrlJsonRegex.Match(html);
            if (json.Success)
            {
                var direct = UnescapeInstagramJsonUrl(json.Groups["url"].Value);
                if (direct.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return direct;
                }
            }
        }
        catch
        {
            /* page scrape is best-effort */
        }

        return null;
    }

    private static string UnescapeInstagramJsonUrl(string raw) =>
        raw.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.Ordinal)
            .Replace("\\u003d", "=", StringComparison.Ordinal);

    private static bool IsTikTokUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IdnHost.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("t.co", StringComparison.OrdinalIgnoreCase));

    private static string? TryResolveInstagramRedirect(Uri uri)
    {
        if (!uri.IdnHost.Equals("l.instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!segment.StartsWith("u=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var encoded = segment[2..];
            var decoded = Uri.UnescapeDataString(encoded);
            if (Uri.TryCreate(decoded, UriKind.Absolute, out _))
            {
                return decoded;
            }
        }

        return null;
    }

    /// <summary>
    /// One yt-dlp run with a given set of extra args. Extracted so the
    /// fallback loop in <see cref="DownloadAsync"/> stays readable.
    /// </summary>
    private async Task<DownloadResult> RunYtDlpAsync(
        string input,
        string destFolder,
        IReadOnlyList<string> extraArgs,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct,
        double sleepRequestsSeconds = 0.85,
        string? cookiesFromBrowser = null,
        bool skipCookiesFile = false)
    {
        var outTemplate = System.IO.Path.Combine(destFolder, "%(title)s.%(ext)s");

        var psi = new ProcessStartInfo
        {
            FileName = _tools.YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = destFolder,
        };
        // Users can have a global yt-dlp config that forces a specific format (-f / --format),
        // which can break extraction on some videos. We always want a predictable "best audio"
        // behavior inside the app.
        psi.ArgumentList.Add("--ignore-config");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("bestaudio/best");
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add("--audio-format");
        psi.ArgumentList.Add("mp3");
        psi.ArgumentList.Add("--audio-quality");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--ffmpeg-location");
        psi.ArgumentList.Add(_tools.FfmpegPath);

        if (!ShouldAllowPlaylistExpansion(input))
        {
            psi.ArgumentList.Add("--no-playlist");
        }
        else
        {
            psi.ArgumentList.Add("--ignore-errors");
        }

        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--extractor-retries");
        psi.ArgumentList.Add("3");
        // Slower extraction reduces rate-limit / bot triggers on busy IPs (see yt-dlp wiki).
        psi.ArgumentList.Add("--sleep-requests");
        psi.ArgumentList.Add(sleepRequestsSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--embed-metadata");
        psi.ArgumentList.Add("--embed-thumbnail");
        psi.ArgumentList.Add("--retries");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add("--fragment-retries");
        psi.ArgumentList.Add("3");
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookiesFromBrowser);
        }
        else if (!skipCookiesFile)
        {
            AppendCookiesArgs(psi);
        }
        foreach (var arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outTemplate);
        psi.ArgumentList.Add(input);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var tcs = new TaskCompletionSource<int>();
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

        // We collect stderr lines so we can surface a real error message to the user
        // when yt-dlp exits non-zero. Without this the dialog just shows a generic
        // "Download failed" message.
        var stderrLines = new List<string>();
        var aggregate = new AggregateDownloadProgress(progress);

        proc.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            var line = args.Data;
            aggregate.OnYtDlpLine(line);
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                stderrLines.Add(line);
            }
        };
        proc.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            var line = args.Data;
            stderrLines.Add(line);
            // yt-dlp writes per-item download progress to stderr.
            aggregate.OnYtDlpLine(line);
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, "Failed to launch yt-dlp: " + ex.Message, ex.ToString());
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            var exitCode = await tcs.Task;
            if (exitCode == 0)
            {
                var paths = BeautifyDownloadedPaths(aggregate.CompletedPaths);
                return new DownloadResult(true, null, null, paths);
            }

            return new DownloadResult(false,
                SummarizeError(stderrLines, exitCode),
                BuildRunTechnicalDetail(stderrLines, exitCode, extraArgs));
        }
    }

    /// <summary>
    /// spotDL handles Spotify tracks, albums, and playlists with metadata from YouTube Music.
    /// </summary>
    private async Task<DownloadResult> RunSpotDlAsync(
        string spotifyUrl,
        string destFolder,
        IProgress<DownloadProgressUpdate>? progress,
        CancellationToken ct)
    {
        var outTemplate = System.IO.Path.Combine(destFolder, "{artist} - {title}.{output-ext}");
        var filesBefore = SnapshotAudioFiles(destFolder);

        var psi = new ProcessStartInfo
        {
            FileName = _tools.SpotDlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = destFolder,
        };
        psi.Environment["PATH"] = _tools.ToolsDirectory + System.IO.Path.PathSeparator
            + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        psi.Environment["YTDLP"] = _tools.YtDlpPath;

        psi.ArgumentList.Add("download");
        psi.ArgumentList.Add(spotifyUrl);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outTemplate);
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("mp3");
        psi.ArgumentList.Add("--bitrate");
        psi.ArgumentList.Add("128k");
        psi.ArgumentList.Add("--threads");
        psi.ArgumentList.Add("4");
        AppendSpotDlCookiesArgs(psi);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var tcs = new TaskCompletionSource<int>();
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

        var logLines = new List<string>();
        var aggregate = new AggregateDownloadProgress(progress);

        void HandleLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            logLines.Add(line);
            aggregate.OnSpotDlLine(line);
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new DownloadProgressUpdate(-1, "Download error."));
            }
        }

        proc.OutputDataReceived += (_, args) => HandleLine(args.Data);
        proc.ErrorDataReceived += (_, args) => HandleLine(args.Data);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, "Failed to launch spotDL: " + ex.Message, ex.ToString());
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            var exitCode = await tcs.Task;
            var paths = BeautifyDownloadedPaths(aggregate.CompletedPaths);
            if (paths.Count == 0)
            {
                paths = DiscoverNewAudioFiles(destFolder, filesBefore);
            }

            // spotDL sometimes exits 0 after AudioProviderError with no files written.
            if (exitCode == 0 && paths.Count > 0)
            {
                progress?.Report(new DownloadProgressUpdate(100, "Done."));
                return new DownloadResult(true, null, null, paths);
            }

            var error = logLines.LastOrDefault(l =>
                            l.Contains("error", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(l))
                        ?? logLines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))
                        ?? (exitCode == 0
                            ? "spotDL finished but no audio files were saved. Try again or paste a YouTube link."
                            : $"spotDL exited with code {exitCode}.");

            if (error.Length > 280) error = error[..280] + "...";
            return new DownloadResult(false, error, string.Join(Environment.NewLine, logLines));
        }
    }

    private static HashSet<string> SnapshotAudioFiles(string destFolder)
    {
        try
        {
            if (!Directory.Exists(destFolder))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                EnumerateAudioFiles(destFolder),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<string> DiscoverNewAudioFiles(
        string destFolder,
        HashSet<string> filesBefore)
    {
        try
        {
            return EnumerateAudioFiles(destFolder)
                .Where(path => !filesBefore.Contains(path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(50)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateAudioFiles(string destFolder)
    {
        if (!Directory.Exists(destFolder))
        {
            yield break;
        }

        string[] exts = [".mp3", ".m4a", ".opus", ".flac", ".wav", ".ogg"];
        foreach (var path in Directory.EnumerateFiles(destFolder))
        {
            var ext = System.IO.Path.GetExtension(path);
            if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase)
                && !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static bool IsSpotifyUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldAllowPlaylistExpansion(string input)
    {
        if (input.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (uri.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase)
                   || path.Contains("/playlist", StringComparison.OrdinalIgnoreCase);
        }

        return path.Contains("/sets/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/playlist", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRunTechnicalDetail(
        IReadOnlyList<string> stderrLines,
        int exitCode,
        IReadOnlyList<string> extraArgs)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("exit code: ").AppendLine(exitCode.ToString());
        if (extraArgs.Count > 0)
        {
            sb.AppendLine("extra arguments: " + string.Join(" ", extraArgs));
        }

        sb.AppendLine("--- captured log (stderr + ERROR lines on stdout) ---");
        var lines = stderrLines.Count > 100
            ? stderrLines.Skip(stderrLines.Count - 100).ToList()
            : stderrLines.ToList();
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line.TrimEnd());
            }
        }

        var s = sb.ToString();
        return s.Length > 16_000 ? s[..16_000] + "\n... (truncated per attempt)" : s;
    }

    private static bool LooksLikeRetryableYouTubeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cookies-from-browser", StringComparison.OrdinalIgnoreCase)
            || error.Contains("consent", StringComparison.OrdinalIgnoreCase)
            || error.Contains("confirm you're human", StringComparison.OrdinalIgnoreCase)
            || error.Contains("bot check", StringComparison.OrdinalIgnoreCase)
            || error.Contains("try again later", StringComparison.OrdinalIgnoreCase)
            || error.Contains("po_token", StringComparison.OrdinalIgnoreCase)
            || error.Contains("PO Token", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
            || (error.Contains("403", StringComparison.OrdinalIgnoreCase)
                && error.Contains("youtube", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeBrowserCookiesUnavailable(DownloadResult result) =>
        LooksLikeBrowserCookiesUnavailable(result.Error)
        || LooksLikeBrowserCookiesUnavailable(result.TechnicalDetail);

    private static bool LooksLikeBrowserCookiesUnavailable(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("could not find", StringComparison.OrdinalIgnoreCase)
               || error.Contains("no such browser", StringComparison.OrdinalIgnoreCase)
               || error.Contains("unsupported browser", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Failed to decrypt", StringComparison.OrdinalIgnoreCase)
               || error.Contains("failed to decrypt", StringComparison.OrdinalIgnoreCase)
               || error.Contains("dpapi", StringComparison.OrdinalIgnoreCase)
               || error.Contains("browser is not installed", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Could not read browser cookies", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYouTubeExtractorInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Spotify → ytsearch1:… is resolved on YouTube by search; don't force youtube: args.
        if (value.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var uri = new Uri(value, UriKind.Absolute);
            return IsYouTubeHost(uri.IdnHost);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
        || host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.Contains("youtube-nocookie", StringComparison.OrdinalIgnoreCase);

    private static string SummarizeError(IReadOnlyList<string> stderrLines, int exitCode)
    {
        var browserCookieFailure = stderrLines.LastOrDefault(l =>
            l.Contains("failed to decrypt", StringComparison.OrdinalIgnoreCase)
            || l.Contains("dpapi", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(browserCookieFailure))
        {
            return "Could not read browser cookies. Export cookies.txt in Dashboard → Download cookies.";
        }

        // Pull the most useful line (usually starting with ERROR:) or fall back to the last line.
        var error = stderrLines
            .Where(l => l.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault()
            ?? stderrLines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (string.IsNullOrWhiteSpace(error))
        {
            return $"yt-dlp exited with code {exitCode}. The URL may be unsupported or blocked.";
        }

        // Trim the noisy "ERROR: [extractor] " prefix to make the message human-readable.
        var cleaned = Regex.Replace(error, @"^ERROR:\s*(\[[^\]]+\]\s*)?", string.Empty,
            RegexOptions.IgnoreCase).Trim();
        if (cleaned.Length > 280) cleaned = cleaned[..280] + "...";
        return cleaned.Length == 0 ? error : cleaned;
    }

    private static async Task<string?> TryResolveSpotifyTitleAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
            var oembed = "https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(url);
            var json = await http.GetStringAsync(oembed, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("title", out var t))
            {
                return t.GetString();
            }
        }
        catch
        {
            // Fall back to yt-dlp pass-through (which will fail gracefully).
        }
        return null;
    }

    /// <summary>
    /// Trim pasted text and normalize common YouTube URLs (e.g. youtu.be/…?si=…)
    /// to a canonical watch URL so yt-dlp always gets a stable video id and MP3 extraction.
    /// </summary>
    private static string NormalizeDownloadInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim().Trim('\uFEFF', '\u200B', '\u200C', '\u200D');

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return s;
        }

        var host = uri.IdnHost;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (id.Length >= 6)
            {
                return "https://www.youtube.com/watch?v=" + id;
            }
        }

        if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
        {
            return StripYouTubeTrackingQuery(uri);
        }

        if (IsSocialMediaHost(host))
        {
            if (host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)
                && TryCanonicalizeInstagramMediaUrl(s, out var canonical))
            {
                return canonical;
            }

            return StripSocialTrackingQuery(uri);
        }

        return s;
    }

    private static string StripSocialTrackingQuery(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            string key, value;
            if (eq < 0)
            {
                key = Uri.UnescapeDataString(segment);
                value = string.Empty;
            }
            else
            {
                key = Uri.UnescapeDataString(segment[..eq]);
                value = Uri.UnescapeDataString(segment[(eq + 1)..]);
            }

            if (SocialTrackingQueryKeys.Contains(key))
            {
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        var path = uri.AbsolutePath;
        if (path.Length > 1 && !path.EndsWith('/'))
        {
            path += "/";
        }

        if (pairs.Count == 0)
        {
            return uri.GetLeftPart(UriPartial.Authority) + path;
        }

        var qb = new System.Text.StringBuilder();
        qb.Append(uri.GetLeftPart(UriPartial.Authority));
        qb.Append(path);
        qb.Append('?');
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) qb.Append('&');
            qb.Append(Uri.EscapeDataString(pairs[i].Key));
            if (pairs[i].Value.Length > 0)
            {
                qb.Append('=').Append(Uri.EscapeDataString(pairs[i].Value));
            }
        }

        return qb.ToString();
    }

    private static async Task<string> UnwrapShortLinkAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var instagramRedirect = TryResolveInstagramRedirect(uri);
        if (!string.IsNullOrWhiteSpace(instagramRedirect))
        {
            url = NormalizeDownloadInput(instagramRedirect);
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return url;
            }
        }

        var host = uri.IdnHost;
        if (!host.Equals("vm.tiktok.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("vt.tiktok.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("t.tiktok.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("www.tiktok.com", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("tiktok", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("t.co", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("bit.ly", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("l.instagram.com", StringComparison.OrdinalIgnoreCase)
            && !(host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)
                 && uri.AbsolutePath.StartsWith("/share/", StringComparison.OrdinalIgnoreCase)))
        {
            return url;
        }

        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8,
            });
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.RequestMessage?.RequestUri?.ToString() ?? url;
        }
        catch
        {
            return url;
        }
    }

    private static bool IsInstagramAudioUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.IdnHost.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)
        && InstagramAudioPathRegex.IsMatch(uri.AbsolutePath);

    private static bool IsSocialMediaHost(string host) =>
        host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)
        || host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("vm.tiktok.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("vt.tiktok.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("t.co", StringComparison.OrdinalIgnoreCase)
        || host.Contains("twitter.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
        || host.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)
        || host.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase)
        || host.Contains("threads.net", StringComparison.OrdinalIgnoreCase)
        || host.Contains("reddit.com", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUnsupportedUrl(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> TryBuildInstagramAudioSearchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            var html = await http.GetStringAsync(url, ct);

            var titleMatch = Regex.Match(
                html,
                @"og:title""\s+content=""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (!titleMatch.Success)
            {
                return null;
            }

            var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            if (title.Length == 0)
            {
                return null;
            }

            var search = Regex.Replace(
                title,
                @"\s*\|\s*Original audio.*$",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();
            if (search.Length == 0)
            {
                search = title;
            }

            return $"ytsearch1:{search}";
        }
        catch
        {
            return null;
        }
    }

    private static string StripYouTubeTrackingQuery(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return uri.AbsoluteUri;
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            string key, value;
            if (eq < 0)
            {
                key = Uri.UnescapeDataString(segment);
                value = string.Empty;
            }
            else
            {
                key = Uri.UnescapeDataString(segment[..eq]);
                value = Uri.UnescapeDataString(segment[(eq + 1)..]);
            }

            if (YouTubeTrackingQueryKeys.Contains(key))
            {
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        if (pairs.Count == 0)
        {
            var u = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath;
            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                u += uri.Fragment;
            }

            return u;
        }

        var qb = new System.Text.StringBuilder();
        qb.Append(uri.GetLeftPart(UriPartial.Authority));
        qb.Append(uri.AbsolutePath);
        qb.Append('?');
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) qb.Append('&');
            qb.Append(Uri.EscapeDataString(pairs[i].Key));
            if (pairs[i].Value.Length > 0)
            {
                qb.Append('=').Append(Uri.EscapeDataString(pairs[i].Value));
            }
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            qb.Append(uri.Fragment);
        }

        return qb.ToString();
    }

    /// <summary>
    /// Maps per-song downloader output into one overall progress bar for playlists.
    /// </summary>
    private sealed class AggregateDownloadProgress
    {
        private readonly IProgress<DownloadProgressUpdate>? _progress;
        private readonly List<string> _completedPaths = new();
        private int _totalTracks;
        private int _completedTracks;
        private int _currentTrackIndex = -1;
        private double _currentSongPct;
        private string? _currentSongTitle;

        private double _lastReportedPercent = -1;
        private long _lastReportTicks;
        private string? _pendingCompletedFilePath;
        private bool _pendingRefreshPlaylistTracks;

        public AggregateDownloadProgress(IProgress<DownloadProgressUpdate>? progress)
        {
            _progress = progress;
        }

        public IReadOnlyList<string> CompletedPaths => _completedPaths;

        public void OnYtDlpLine(string line)
        {
            var item = YtDlpItemRegex.Match(line);
            if (item.Success)
            {
                var nextIndex = int.Parse(item.Groups["cur"].Value) - 1;
                if (nextIndex > _currentTrackIndex)
                {
                    _pendingRefreshPlaylistTracks = true;
                    _currentSongPct = 0;
                }

                _currentTrackIndex = nextIndex;
                _totalTracks = int.Parse(item.Groups["tot"].Value);
                Report();
                return;
            }

            var playlist = YtDlpPlaylistItemsRegex.Match(line);
            if (playlist.Success)
            {
                _totalTracks = int.Parse(playlist.Groups["tot"].Value);
                Report();
                return;
            }

            var pctMatch = PercentRegex.Match(line);
            if (pctMatch.Success
                && double.TryParse(
                    pctMatch.Groups["pct"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var pct))
            {
                _currentSongPct = pct;
                Report();
                return;
            }

            if (line.Contains("100%", StringComparison.Ordinal)
                && line.Contains("[download]", StringComparison.OrdinalIgnoreCase))
            {
                _currentSongPct = 100;
                _pendingRefreshPlaylistTracks = true;
                Report();
                return;
            }

            if (line.Contains("Deleting original file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[ffmpeg]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Destination:", StringComparison.OrdinalIgnoreCase))
            {
                _currentSongPct = 100;
                if (line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Deleting original file", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("[ffmpeg]", StringComparison.OrdinalIgnoreCase))
                {
                    _pendingRefreshPlaylistTracks = true;
                }

                TryParseYtDlpSongTitle(line);
                Report();
            }
        }

        public void OnSpotDlLine(string line)
        {
            TryParseSpotDlSongTitle(line);

            var complete = SpotDlCompleteRegex.Match(line);
            if (complete.Success)
            {
                var done = int.Parse(complete.Groups["done"].Value);
                if (done > _completedTracks)
                {
                    _pendingRefreshPlaylistTracks = true;
                }

                _completedTracks = done;
                _totalTracks = int.Parse(complete.Groups["total"].Value);
                _currentSongPct = 0;
                Report();
                return;
            }

            var found = SpotDlFoundRegex.Match(line);
            if (found.Success)
            {
                _totalTracks = int.Parse(found.Groups["total"].Value);
                Report();
                return;
            }

            if (line.Contains("Downloaded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Skipping", StringComparison.OrdinalIgnoreCase))
            {
                _completedTracks++;
                _currentSongPct = 0;
                _pendingRefreshPlaylistTracks = true;
                Report();
            }
        }

        private bool TryParseYtDlpSongTitle(string line)
        {
            var dest = YtDlpDestinationRegex.Match(line);
            if (!dest.Success)
            {
                return false;
            }

            var file = dest.Groups["file"].Value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(file))
            {
                return false;
            }

            RecordCompletedPath(file);
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            _currentSongTitle = TrackNameFormatter.Beautify(name);
            return true;
        }

        private void RecordCompletedPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (!PlaylistManager.IsAudioFile(filePath)) return;

            _completedPaths.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
            _completedPaths.Add(filePath);
            _pendingCompletedFilePath = filePath;
            _pendingRefreshPlaylistTracks = true;
        }

        private void TryParseSpotDlSongTitle(string line)
        {
            var song = SpotDlSongRegex.Match(line);
            if (!song.Success)
            {
                return;
            }

            var title = song.Groups["title"].Value.Trim();
            if (title.Length > 0)
            {
                _currentSongTitle = title;
            }
        }

        private void Report()
        {
            var percent = ComputePercent();
            if (_totalTracks <= 1 && _lastReportedPercent >= 0)
            {
                percent = Math.Max(percent, _lastReportedPercent);
            }

            var now = Environment.TickCount64;
            var hasTrackUpdate = _pendingRefreshPlaylistTracks
                || !string.IsNullOrWhiteSpace(_pendingCompletedFilePath);
            if (!hasTrackUpdate
                && _lastReportedPercent >= 0
                && Math.Abs(percent - _lastReportedPercent) < 0.75
                && now - _lastReportTicks < 150)
            {
                return;
            }

            _lastReportedPercent = percent;
            _lastReportTicks = now;

            var completedPath = _pendingCompletedFilePath;
            var refreshTracks = _pendingRefreshPlaylistTracks;
            _pendingCompletedFilePath = null;
            _pendingRefreshPlaylistTracks = false;

            _progress?.Report(new DownloadProgressUpdate(
                percent,
                FormatMessage(),
                _currentSongTitle,
                completedPath,
                refreshTracks));
        }

        private double ComputePercent()
        {
            if (_totalTracks <= 1)
            {
                return Math.Clamp(10 + _currentSongPct * 0.85, 10, 95);
            }

            double trackFraction;
            if (_currentTrackIndex >= 0 && _currentSongPct > 0)
            {
                trackFraction = (_currentTrackIndex + _currentSongPct / 100.0) / _totalTracks;
            }
            else
            {
                trackFraction = (_completedTracks + _currentSongPct / 100.0) / _totalTracks;
            }

            return Math.Clamp(10 + trackFraction * 85, 10, 95);
        }

        private string FormatMessage()
        {
            if (_totalTracks > 1)
            {
                var active = _currentTrackIndex >= 0
                    ? _currentTrackIndex + 1
                    : Math.Min(_completedTracks + 1, _totalTracks);
                return $"{active} of {_totalTracks} songs";
            }

            return "Downloading...";
        }
    }
}
