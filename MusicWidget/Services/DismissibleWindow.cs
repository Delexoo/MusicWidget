using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace MusicWidget.Services;

/// <summary>
/// Shows a non-modal window (so the owner stays clickable) and closes it when the
/// user clicks outside / the window loses activation. Blocks the caller until closed,
/// mimicking <see cref="Window.ShowDialog"/> without disabling other app windows.
/// </summary>
public static class DismissibleWindow
{
    /// <summary>
    /// Shows <paramref name="window"/> and returns when it closes.
    /// Returns <c>true</c> when the window set <see cref="Window.DialogResult"/> to true
    /// or when <paramref name="wasAccepted"/> reports acceptance.
    /// </summary>
    public static bool Show(Window window, Window? owner = null, Func<bool>? wasAccepted = null)
    {
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            if (window.WindowStartupLocation == WindowStartupLocation.Manual)
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }

        var accepted = false;
        var frame = new DispatcherFrame();
        var ignoreDeactivate = 0;

        void OnDeactivated(object? sender, EventArgs e)
        {
            if (ignoreDeactivate > 0 || !window.IsVisible)
            {
                return;
            }

            // Another of our windows is active (nested prompt / file picker) — stay open.
            var otherActive = Application.Current?.Windows
                .OfType<Window>()
                .Any(w => !ReferenceEquals(w, window) && w.IsActive && w.IsVisible) == true;
            if (otherActive)
            {
                return;
            }

            // Clicked outside — dismiss without accepting.
            try
            {
                window.Close();
            }
            catch
            {
                // Already closing.
            }
        }

        void OnClosed(object? sender, EventArgs e)
        {
            window.Deactivated -= OnDeactivated;
            window.Closed -= OnClosed;
            accepted = wasAccepted?.Invoke() == true
                       || window.DialogResult == true;
            frame.Continue = false;
        }

        window.Deactivated += OnDeactivated;
        window.Closed += OnClosed;

        // Allow callers to temporarily suppress dismiss (e.g. while a file dialog is open).
        window.SetValue(SuppressDismissProperty, new Action<bool>(suppress =>
        {
            if (suppress) ignoreDeactivate++;
            else if (ignoreDeactivate > 0) ignoreDeactivate--;
        }));

        window.Show();
        window.Activate();
        Dispatcher.PushFrame(frame);
        return accepted;
    }

    public static readonly DependencyProperty SuppressDismissProperty =
        DependencyProperty.RegisterAttached(
            "SuppressDismissCallback",
            typeof(Action<bool>),
            typeof(DismissibleWindow),
            new PropertyMetadata(null));

    /// <summary>While <paramref name="suppress"/> is true, outside clicks won't close the window.</summary>
    public static void SetSuppressDismiss(Window window, bool suppress)
    {
        if (window.GetValue(SuppressDismissProperty) is Action<bool> cb)
        {
            cb(suppress);
        }
    }
}
