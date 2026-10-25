using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MusicWidget.Services;
using MusicWidget.Views;

namespace MusicWidget;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static bool _ownsSingleInstanceMutex;

    public static SettingsStore Settings { get; private set; } = null!;
    public static PlaylistManager Playlists { get; private set; } = null!;
    public static AudioPlayer Player { get; private set; } = null!;
    public static DownloadService Downloader { get; private set; } = null!;
    public static BackgroundDownloadService BackgroundDownloads { get; private set; } = null!;
    public static ToolBootstrapper Tools { get; private set; } = null!;
    public static ArtworkService Artwork { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;
    public static TrackListStore LikedSongs { get; private set; } = null!;
    public static TrackListStore SavedSongs { get; private set; } = null!;
    public static PlaylistOrderStore PlaylistOrders { get; private set; } = null!;

    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppBranding.AppDataFolderName);

    public static string ToolsDir { get; } = Path.Combine(AppDataDir, "tools");
    public static string ArtworkDir { get; } = Path.Combine(AppDataDir, "artwork");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            MigrateLegacyAppData();

            const string mutexName = "MusicWidget_SingleInstance_4F2A8B11";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                ModernMessageBox.ShowInfo($"{AppBranding.DisplayName} is already running.", "Already running");
                Shutdown();
                return;
            }

            _ownsSingleInstanceMutex = true;

            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(ToolsDir);
            Directory.CreateDirectory(ArtworkDir);

            Settings = new SettingsStore(Path.Combine(AppDataDir, "settings.json"));
            Settings.Load();

            PlaylistOrders = new PlaylistOrderStore(Path.Combine(AppDataDir, "playlist-orders.json"));
            PlaylistOrders.Load();

            Playlists = new PlaylistManager(Settings, PlaylistOrders);
            Player = new AudioPlayer();
            Tools = new ToolBootstrapper(ToolsDir);
            Downloader = new DownloadService(Tools);
            BackgroundDownloads = new BackgroundDownloadService();
            Artwork = new ArtworkService(ArtworkDir);
            Updates = new UpdateService();
            LikedSongs = new TrackListStore(Path.Combine(AppDataDir, "liked-songs.json"));
            LikedSongs.Load();
            SavedSongs = new TrackListStore(Path.Combine(AppDataDir, "saved-songs.json"));
            SavedSongs.Load();

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    CrashLog.Write(ex, "AppDomain.UnhandledException");
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                CrashLog.Write(args.Exception, "TaskScheduler.UnobservedTaskException");
                args.SetObserved();
            };

            DispatcherUnhandledException += (_, args) =>
            {
                CrashLog.Write(args.Exception, "Dispatcher.UnhandledException");
                ModernMessageBox.ShowError($"Unexpected error: {args.Exception.Message}");
                args.Handled = true;
            };

            StartApplicationAsync();
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "MusicWidget-startup-error.txt");
            try
            {
                File.WriteAllText(logPath, ex.ToString());
            }
            catch
            {
                // ignore secondary failures
            }

            MessageBox.Show(
                $"{AppBranding.DisplayName} could not start.\n\nDetails were saved to:\n{logPath}\n\n{ex.Message}",
                AppBranding.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartApplicationAsync()
    {
        try
        {
            var widget = new WidgetWindow();
            var toggle = new HideToggleWindow(widget);

            widget.Show();
            toggle.Show();

            _ = Updates.CheckForUpdatesOnStartupAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "App.StartApplicationAsync");
            ModernMessageBox.ShowError($"Could not start {AppBranding.DisplayName}: {ex.Message}");
            Shutdown(1);
        }
    }

    private static void MigrateLegacyAppData()
    {
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppBranding.LegacyAppDataFolderName);

        if (!Directory.Exists(legacyDir) || Directory.Exists(AppDataDir))
        {
            return;
        }

        try
        {
            Directory.Move(legacyDir, AppDataDir);
        }
        catch
        {
            // Best-effort: if move fails (e.g. partial lock), the app still starts fresh under MusicWidget.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Player?.Dispose();
            Settings?.Save();
        }
        catch { /* best-effort shutdown */ }

        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
