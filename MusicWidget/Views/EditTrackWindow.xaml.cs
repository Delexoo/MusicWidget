using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MusicWidget.Models;
using MusicWidget.Services;

namespace MusicWidget.Views;

public sealed class EditTrackResult
{
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? CoverImagePath { get; init; }
}

public partial class EditTrackWindow : Window
{
    private readonly Track _track;
    private string? _coverImagePath;

    public EditTrackResult? Result { get; private set; }
    public bool Accepted { get; private set; }

    public EditTrackWindow(Track track)
    {
        _track = track;
        InitializeComponent();

        var (artist, title) = TrackTagService.ReadDisplayMetadata(track.FilePath, track);
        ArtistBox.Text = artist;
        TitleBox.Text = title;

        Loaded += (_, _) =>
        {
            SetCoverPreview(_track.ArtworkSource);
            if (_track.ArtworkSource is null)
            {
                var embedded = TrackTagService.ReadEmbeddedCover(track.FilePath);
                if (embedded is { Length: > 0 })
                {
                    SetCoverPreview(LoadBitmapFromBytes(embedded));
                }
            }

            TitleBox.Focus();
            TitleBox.SelectAll();
        };
        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose cover image",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        DismissibleWindow.SetSuppressDismiss(this, true);
        bool? picked;
        try
        {
            picked = dlg.ShowDialog(this);
        }
        finally
        {
            DismissibleWindow.SetSuppressDismiss(this, false);
        }

        if (picked != true)
        {
            return;
        }

        _coverImagePath = dlg.FileName;
        try
        {
            SetCoverPreview(LoadBitmapFromFile(dlg.FileName));
        }
        catch
        {
            ModernMessageBox.ShowWarning("Could not load the selected image.");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            ModernMessageBox.ShowWarning("Song name cannot be empty.");
            TitleBox.Focus();
            return;
        }

        Result = new EditTrackResult
        {
            Artist = ArtistBox.Text?.Trim() ?? string.Empty,
            Title = title,
            CoverImagePath = _coverImagePath,
        };
        Accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }

    private void SetCoverPreview(ImageSource? source)
    {
        if (source is null)
        {
            CoverPreviewBrush.ImageSource = null;
            CoverPreview.Visibility = Visibility.Collapsed;
            CoverPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        CoverPreviewBrush.ImageSource = source;
        CoverPreview.Visibility = Visibility.Visible;
        CoverPlaceholder.Visibility = Visibility.Collapsed;
    }

    private static ImageSource? LoadBitmapFromFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.DecodePixelWidth = 144;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static ImageSource? LoadBitmapFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream;
        bmp.DecodePixelWidth = 144;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
