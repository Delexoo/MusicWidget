using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;

namespace MusicWidget.Services;

public sealed class UpdateService
{
    public const string InstallerAssetName = "MusicWidget-Setup-x64.exe";

    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string CurrentVersion { get; } = GetCurrentVersion();

    public UpdateCheckResult? LastCheckResult { get; private set; }

    public bool IsAutoUpdateRunning { get; private set; }

    public bool StartupCheckCompleted { get; private set; }

    public static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Checks GitHub Releases in the background after startup. Does not install automatically.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IsAutoUpdateRunning = true;

        try
        {
            LastCheckResult = await FetchLatestReleaseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "UpdateService.CheckForUpdatesOnStartupAsync");
            LastCheckResult = null;
        }
        finally
        {
            IsAutoUpdateRunning = false;
            StartupCheckCompleted = true;
            _gate.Release();
        }
    }

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await FetchLatestReleaseAsync(ct).ConfigureAwait(false);
            LastCheckResult = result;
            return result;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "UpdateService.CheckForUpdateAsync");
            LastCheckResult = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DownloadAndInstallAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        bool silentInstall = false,
        CancellationToken ct = default)
    {
        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            return false;
        }

        if (silentInstall)
        {
            return BeginDetachedUpdateAndShutdown(update);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var installerPath = await DownloadInstallerAsync(update.DownloadUrl, progress, ct)
                .ConfigureAwait(false);
            if (!LaunchInstaller(installerPath, silent: false))
            {
                return false;
            }

            ShutdownApplicationForUpdate();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Spawns a background updater, closes Beats immediately, then installs and relaunches.
    /// </summary>
    public bool BeginDetachedUpdateAndShutdown(UpdateCheckResult update)
    {
        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            return false;
        }

        try
        {
            var restartExe = ResolveRestartExecutablePath();
            var scriptPath = WriteDetachedUpdateScript(update.DownloadUrl, restartExe);
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (started is null)
            {
                return false;
            }

            ShutdownApplicationForUpdate();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "UpdateService.BeginDetachedUpdateAndShutdown");
            return false;
        }
    }

    private static string WriteDetachedUpdateScript(string downloadUrl, string? restartExePath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Beats-updates");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "apply-update.ps1");
        var installerPath = Path.Combine(dir, InstallerAssetName);
        var logPath = Path.Combine(dir, "apply-update.log");
        var restartExe = restartExePath ?? string.Empty;

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $url = '{{EscapeForPowerShellSingleQuoted(downloadUrl)}}'
            $out = '{{EscapeForPowerShellSingleQuoted(installerPath)}}'
            $log = '{{EscapeForPowerShellSingleQuoted(logPath)}}'
            $restart = '{{EscapeForPowerShellSingleQuoted(restartExe)}}'

            function Write-Log([string]$Message) {
                Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
            }

            try {
                New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
                Write-Log 'Downloading update...'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -Headers @{ 'User-Agent' = 'MusicWidget-Updater' }
                if (-not (Test-Path -LiteralPath $out) -or ((Get-Item -LiteralPath $out).Length -lt 1024)) {
                    throw 'Downloaded installer is missing or too small.'
                }
                Write-Log 'Launching installer...'
                $proc = Start-Process -FilePath $out -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART' -PassThru -Wait
                if ($null -eq $proc) {
                    throw 'Failed to start installer process.'
                }
                if ($proc.ExitCode -ne 0) {
                    throw "Installer exited with code $($proc.ExitCode)."
                }
                Write-Log 'Update finished.'
                Start-Sleep -Seconds 2
                if ($restart -and (Test-Path -LiteralPath $restart)) {
                    Write-Log "Relaunching $restart"
                    Start-Process -FilePath $restart
                }
            }
            catch {
                Write-Log $_.Exception.Message
                exit 1
            }
            """;

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private static string? ResolveRestartExecutablePath()
    {
        try
        {
            return Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeForPowerShellSingleQuoted(string value) =>
        value.Replace("'", "''");

    private async Task<UpdateCheckResult?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        var url =
            $"https://api.github.com/repos/{AppBranding.GitHubOwner}/{AppBranding.GitHubRepo}/releases/latest";

        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        if (release?.TagName is not { Length: > 0 } tagName)
        {
            return null;
        }

        var latest = NormalizeVersion(tagName);
        var asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, InstallerAssetName, StringComparison.OrdinalIgnoreCase));
        var downloadUrl = asset?.BrowserDownloadUrl;
        var isUpdateAvailable = IsNewerVersion(latest, CurrentVersion);

        return new UpdateCheckResult(latest, downloadUrl, isUpdateAvailable);
    }

    public async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Beats-updates");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, InstallerAssetName);

        using var response = await DownloadHttp
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);

        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            read += count;
            if (total > 0)
            {
                progress?.Report((double)read / total);
            }
        }

        return path;
    }

    public static bool LaunchInstaller(string installerPath, bool silent = false)
    {
        var args = silent
            ? "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS"
            : string.Empty;

        return Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = args,
            UseShellExecute = true,
        }) is not null;
    }

    public static void ShutdownApplicationForUpdate()
    {
        UiDispatcher.InvokeSafe(() =>
        {
            try
            {
                foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                        /* best-effort */
                    }
                }
            }
            finally
            {
                Application.Current.Shutdown();
            }
        });
    }

    public static bool IsNewerVersion(string candidate, string current)
    {
        var left = ParseVersionParts(candidate);
        var right = ParseVersionParts(current);
        for (var i = 0; i < 3; i++)
        {
            if (left[i] > right[i]) return true;
            if (left[i] < right[i]) return false;
        }

        return false;
    }

    private static int[] ParseVersionParts(string value)
    {
        return NormalizeVersion(value)
            .Split('.')
            .Select(ParseVersionPart)
            .Concat(Enumerable.Repeat(0, 3))
            .Take(3)
            .ToArray();
    }

    private static int ParseVersionPart(string part)
    {
        var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static string NormalizeVersion(string value)
    {
        return value.Trim().TrimStart('v', 'V');
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.UserAgent);
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

public sealed record UpdateCheckResult(
    string LatestVersion,
    string? DownloadUrl,
    bool IsUpdateAvailable);
