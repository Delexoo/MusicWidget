using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MusicWidget.Services;

namespace MusicWidget.Views;

/// <summary>
/// Themed replacement for the Win32 MessageBox. Matches the surface/border/typography
/// of the rest of the widget and exposes simple static helpers for the common cases.
/// </summary>
public partial class ModernMessageBox : Window
{
    public enum Severity { Info, Question, Warning, Error, Success }
    public enum Kind { Ok, OkCancel, YesNo }

    private bool? _result;

    private ModernMessageBox()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    // ----- Static helpers -----

    public static void ShowInfo(string message, string title = AppBranding.DisplayName, Window? owner = null)
        => Display(owner, title, message, Severity.Info, Kind.Ok);

    public static void ShowWarning(string message, string title = AppBranding.DisplayName, Window? owner = null)
        => Display(owner, title, message, Severity.Warning, Kind.Ok);

    public static void ShowError(string message, string title = AppBranding.DisplayName, Window? owner = null)
        => Display(owner, title, message, Severity.Error, Kind.Ok);

    /// <summary>OK/Cancel confirmation. Returns true on OK.</summary>
    public static bool Confirm(
        string message,
        string title = AppBranding.DisplayName,
        Severity severity = Severity.Warning,
        Window? owner = null)
        => Display(owner, title, message, severity, Kind.OkCancel) == true;

    /// <summary>Yes/No confirmation. Returns true on Yes.</summary>
    public static bool ConfirmYesNo(
        string message,
        string title = AppBranding.DisplayName,
        Severity severity = Severity.Question,
        Window? owner = null)
        => Display(owner, title, message, severity, Kind.YesNo) == true;

    private static bool? Display(Window? owner, string title, string message, Severity severity, Kind kind)
    {
        var dlg = new ModernMessageBox();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ApplySeverity(severity);
        dlg.BuildButtons(kind);

        var picked = PickOwner(owner);
        // Non-modal + click-outside dismiss so the widget stays interactive.
        // Outside click / deactivate counts as Cancel / No.
        DismissibleWindow.Show(dlg, picked, () => dlg._result == true);
        return dlg._result;
    }

    private static Window? PickOwner(Window? explicitOwner)
    {
        if (explicitOwner is { IsLoaded: true }) return explicitOwner;
        var app = Application.Current;
        if (app is null) return null;
        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        if (active is not null) return active;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
    }

    // ----- Visuals -----

    private void ApplySeverity(Severity severity)
    {
        var resources = Application.Current.Resources;
        Geometry geo;
        Color accent;
        switch (severity)
        {
            case Severity.Warning:
                geo = (Geometry)resources["Icon.Warning"];
                accent = Color.FromRgb(0xF5, 0xA6, 0x23);
                break;
            case Severity.Error:
                geo = (Geometry)resources["Icon.Error"];
                accent = Color.FromRgb(0xEF, 0x44, 0x44);
                break;
            case Severity.Success:
                geo = (Geometry)resources["Icon.Info"];
                accent = Color.FromRgb(0x22, 0xC5, 0x5E);
                break;
            case Severity.Question:
                geo = (Geometry)resources["Icon.Question"];
                accent = Color.FromRgb(0x4F, 0x8C, 0xFF);
                break;
            case Severity.Info:
            default:
                geo = (Geometry)resources["Icon.Info"];
                accent = Color.FromRgb(0x4F, 0x8C, 0xFF);
                break;
        }

        IconPath.Data = geo;
        IconPath.Fill = new SolidColorBrush(accent);
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
    }

    private void BuildButtons(Kind kind)
    {
        var resources = Application.Current.Resources;
        var primaryStyle = (Style)resources["PrimaryButton"];
        var flatStyle = (Style)resources["FlatButton"];

        switch (kind)
        {
            case Kind.Ok:
            {
                var ok = CreateButton("OK", primaryStyle, () => { _result = true; Close(); });
                ok.IsDefault = true;
                ok.IsCancel = true;
                ButtonRow.Children.Add(ok);
                break;
            }
            case Kind.OkCancel:
            {
                var cancel = CreateButton("Cancel", flatStyle, () => { _result = false; Close(); });
                cancel.IsCancel = true;
                ButtonRow.Children.Add(cancel);

                var ok = CreateButton("OK", primaryStyle, () => { _result = true; Close(); });
                ok.IsDefault = true;
                ok.Margin = new Thickness(8, 0, 0, 0);
                ButtonRow.Children.Add(ok);
                break;
            }
            case Kind.YesNo:
            {
                var no = CreateButton("No", flatStyle, () => { _result = false; Close(); });
                no.IsCancel = true;
                ButtonRow.Children.Add(no);

                var yes = CreateButton("Yes", primaryStyle, () => { _result = true; Close(); });
                yes.IsDefault = true;
                yes.Margin = new Thickness(8, 0, 0, 0);
                ButtonRow.Children.Add(yes);
                break;
            }
        }
    }

    private static Button CreateButton(string content, Style style, Action onClick)
    {
        var btn = new Button
        {
            Content = content,
            Style = style,
            Padding = new Thickness(16, 6, 16, 6),
            MinWidth = 78,
            FontWeight = FontWeights.SemiBold,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ----- Window chrome -----

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* mouse capture lost */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) _result = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result ??= false;
            Close();
        }
    }
}
