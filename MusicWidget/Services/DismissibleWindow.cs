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
    private static int _openCount;
    private static long _suppressOwnerMinimizeUntilMs;

    /// <summary>True while at least one dismissible dialog is open.</summary>
    public static bool HasOpenWindows => _openCount > 0;

    /// <summary>
    /// True while a dismissible dialog is open, or briefly after one closes from an
    /// outside click — so the owner dashboard does not collapse in the same gesture.
    /// </summary>
    public static bool ShouldSuppressOwnerMinimize =>
        _openCount > 0 || Environment.TickCount64 < _suppressOwnerMinimizeUntilMs;

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
        }

        // CenterOwner + SizeToContent often places chrome-less windows at the top of
        // the screen before height is measured. Always position manually after show.
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        var accepted = false;
        var frame = new DispatcherFrame();
        var ignoreDeactivate = 0;
        var centeredOnce = false;

        void ArmOwnerMinimizeSuppress(int ms = 450)
        {
            var until = Environment.TickCount64 + ms;
            if (until > _suppressOwnerMinimizeUntilMs)
            {
                _suppressOwnerMinimizeUntilMs = until;
            }
        }

        void CenterNow()
        {
            try
            {
                CenterOnOwnerOrScreen(window, owner);
            }
            catch
            {
                // Positioning is best-effort.
            }
        }

        void OnContentRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnContentRendered;
            if (!centeredOnce)
            {
                centeredOnce = true;
                CenterNow();
            }
        }

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

            // Outside click — dismiss child only; keep owner dashboard open for this gesture.
            ArmOwnerMinimizeSuppress();
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
            window.ContentRendered -= OnContentRendered;
            if (_openCount > 0)
            {
                _openCount--;
            }

            ArmOwnerMinimizeSuppress();
            accepted = wasAccepted?.Invoke() == true
                       || window.DialogResult == true;
            frame.Continue = false;
        }

        window.Deactivated += OnDeactivated;
        window.Closed += OnClosed;
        window.ContentRendered += OnContentRendered;

        // Allow callers to temporarily suppress dismiss (e.g. while a file dialog is open).
        window.SetValue(SuppressDismissProperty, new Action<bool>(suppress =>
        {
            if (suppress) ignoreDeactivate++;
            else if (ignoreDeactivate > 0) ignoreDeactivate--;
        }));

        _openCount++;
        window.Show();
        CenterNow();
        window.Activate();
        Dispatcher.PushFrame(frame);
        return accepted;
    }

    /// <summary>
    /// Centers <paramref name="window"/> on its owner when the owner is large enough
    /// (expanded dashboard); otherwise on the owner's monitor work area. Always clamps
    /// into the visible work area so dialogs never stick to the top of the screen.
    /// </summary>
    public static void CenterOnOwnerOrScreen(Window window, Window? owner)
    {
        window.UpdateLayout();

        var width = window.ActualWidth > 0
            ? window.ActualWidth
            : (!double.IsNaN(window.Width) && window.Width > 0 ? window.Width : 400);
        var height = window.ActualHeight > 0
            ? window.ActualHeight
            : (!double.IsNaN(window.Height) && window.Height > 0 ? window.Height : 180);

        Rect anchor;
        if (owner is { IsLoaded: true })
        {
            var ow = owner.ActualWidth > 0
                ? owner.ActualWidth
                : (!double.IsNaN(owner.Width) && owner.Width > 0 ? owner.Width : 0);
            var oh = owner.ActualHeight > 0
                ? owner.ActualHeight
                : (!double.IsNaN(owner.Height) && owner.Height > 0 ? owner.Height : 0);

            // Tiny owner (floating pill only) → center on that monitor's work area.
            if (ow < 240 || oh < 160)
            {
                anchor = DisplayBounds.GetWorkAreaForWindow(owner);
            }
            else
            {
                anchor = new Rect(owner.Left, owner.Top, ow, oh);
            }
        }
        else
        {
            anchor = DisplayBounds.GetWorkAreaNearPointDip(
                new Point(
                    SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2,
                    SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height / 2),
                window);
        }

        var left = anchor.Left + (anchor.Width - width) / 2.0;
        var top = anchor.Top + (anchor.Height - height) / 2.0;

        var work = owner is { IsLoaded: true }
            ? DisplayBounds.GetWorkAreaForWindow(owner)
            : DisplayBounds.GetWorkAreaNearPointDip(new Point(left + width / 2, top + height / 2), window);

        (left, top) = DisplayBounds.EnsureRectInWorkArea(left, top, width, height, work);
        window.Left = left;
        window.Top = top;
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
