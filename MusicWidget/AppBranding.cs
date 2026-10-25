namespace MusicWidget;

/// <summary>Product display name and on-disk folder names for MusicWidget.</summary>
public static class AppBranding
{
    public const string DisplayName = "MusicWidget";
    public const string AppDataFolderName = "MusicWidget";
    /// <summary>Previous AppData folder from the Windows Beats branding era.</summary>
    public const string LegacyAppDataFolderName = "Beats";
    public const string DefaultPlaylistsFolderName = "MusicWidget";
    public static string UserAgent
    {
        get
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "MusicWidget/0.0.0" : $"MusicWidget/{version.Major}.{version.Minor}.{version.Build}";
        }
    }
    public const string HelpPageUrl = "https://delexoo.github.io/beats/help.html";
    public const string PublisherName = "Delexo";
    public const string StoreUrl = "https://delexo.store";
    public const string GitHubOwner = "Delexoo";
    public const string GitHubRepo = "beats";
    public const string GitHubProfileUrl = "https://github.com/Delexoo";
    public const string GitHubRepoUrl = "https://github.com/Delexoo/beats";
    public const string GitHubReleasesUrl = "https://github.com/Delexoo/beats/releases";
    public const string GitHubIssuesUrl = "https://github.com/Delexoo/beats/issues";
}
