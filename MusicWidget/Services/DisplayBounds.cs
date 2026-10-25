using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MusicWidget.Services;

/// <summary>
/// Multi-monitor / DPI-safe screen geometry helpers for WPF (PerMonitorV2).
/// Prefer these over <see cref="SystemParameters.WorkArea"/> alone — that API
/// only describes the primary monitor and mis-centers widgets on other displays.
/// </summary>
public static class DisplayBounds
{
    private const int MonitorDefaultToNearest = 2;

    /// <summary>Full virtual desktop in DIP coordinates.</summary>
    public static Rect GetVirtualScreenDip() =>
        new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Work area (excludes taskbar) for the monitor that currently hosts
    /// <paramref name="window"/>. Falls back to the primary work area.
    /// </summary>
    public static Rect GetWorkAreaForWindow(Window window)
    {
        try
        {
            if (window is not null)
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                    if (TryGetMonitorWorkAreaDip(monitor, window, out var work))
                    {
                        return work;
                    }
                }

                // Handle not ready yet — estimate from the window's DIP rect.
                if (!double.IsNaN(window.Left) && !double.IsNaN(window.Top))
                {
                    var width = window.ActualWidth > 0 ? window.ActualWidth :
                        (!double.IsNaN(window.Width) && window.Width > 0 ? window.Width : 300);
                    var height = window.ActualHeight > 0 ? window.ActualHeight :
                        (!double.IsNaN(window.Height) && window.Height > 0 ? window.Height : 80);
                    var center = new Point(window.Left + width / 2.0, window.Top + height / 2.0);
                    return GetWorkAreaNearPointDip(center, window);
                }
            }
        }
        catch
        {
            // Fall through to primary.
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// Work area for the monitor nearest to a DIP point. Optional
    /// <paramref name="dpiVisual"/> improves physical→DIP conversion.
    /// </summary>
    public static Rect GetWorkAreaNearPointDip(Point dipPoint, Visual? dpiVisual = null)
    {
        try
        {
            var (scaleX, scaleY) = GetDpiScale(dpiVisual);
            var pt = new POINT
            {
                X = (int)Math.Round(dipPoint.X * scaleX),
                Y = (int)Math.Round(dipPoint.Y * scaleY),
            };
            var monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
            if (TryGetMonitorWorkAreaDip(monitor, dpiVisual, out var work))
            {
                return work;
            }
        }
        catch
        {
            // Fall through.
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// True when at least <paramref name="minVisible"/> DIP of the window
    /// intersect any monitor (virtual desktop).
    /// </summary>
    public static bool IsPositionOnAnyScreen(double left, double top, double width, double height, double minVisible = 48)
    {
        if (width <= 0) width = 200;
        if (height <= 0) height = 80;

        var windowRect = new Rect(left, top, width, height);
        var virtualScreen = GetVirtualScreenDip();
        if (!windowRect.IntersectsWith(virtualScreen))
        {
            return false;
        }

        var intersection = Rect.Intersect(windowRect, virtualScreen);
        return intersection.Width >= Math.Min(minVisible, width) &&
               intersection.Height >= Math.Min(minVisible, height);
    }

    /// <summary>
    /// Clamps preferred dashboard size so it always fits the given work area,
    /// even on very small laptop panels where the design mins would overflow.
    /// </summary>
    public static (double Width, double Height) ClampDashboardSize(
        Rect workArea,
        double preferredWidth,
        double preferredHeight,
        double designMinWidth,
        double designMinHeight,
        double widthMargin = 40,
        double heightMargin = 80)
    {
        // Hard caps: leave a little chrome for the pill + margins.
        var maxW = Math.Max(280, workArea.Width - widthMargin);
        var maxH = Math.Max(220, workArea.Height - heightMargin);

        // Soft mins shrink when the screen can't host the design minimums.
        var minW = Math.Min(designMinWidth, maxW);
        var minH = Math.Min(designMinHeight, maxH);

        var w = Math.Clamp(preferredWidth, minW, maxW);
        var h = Math.Clamp(preferredHeight, minH, maxH);
        return (w, h);
    }

    /// <summary>
    /// Nudges a window rect fully into the nearest work area when it sits
    /// mostly off-screen (e.g. after a resolution / monitor change).
    /// </summary>
    public static (double Left, double Top) EnsureRectInWorkArea(
        double left,
        double top,
        double width,
        double height,
        Rect workArea,
        double margin = 8)
    {
        if (width <= 0) width = 200;
        if (height <= 0) height = 80;

        var maxLeft = workArea.Right - Math.Min(width, workArea.Width - margin);
        var maxTop = workArea.Bottom - Math.Min(height, workArea.Height - margin);
        var minLeft = workArea.Left + margin;
        var minTop = workArea.Top + margin;

        if (maxLeft < minLeft) maxLeft = minLeft;
        if (maxTop < minTop) maxTop = minTop;

        return (
            Math.Clamp(left, minLeft, maxLeft),
            Math.Clamp(top, minTop, maxTop));
    }

    private static bool TryGetMonitorWorkAreaDip(IntPtr monitor, Visual? dpiVisual, out Rect workAreaDip)
    {
        workAreaDip = default;
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var rc = info.rcWork;

        if (dpiVisual is not null)
        {
            var source = PresentationSource.FromVisual(dpiVisual);
            if (source?.CompositionTarget is not null)
            {
                var topLeft = source.CompositionTarget.TransformFromDevice
                    .Transform(new Point(rc.Left, rc.Top));
                var bottomRight = source.CompositionTarget.TransformFromDevice
                    .Transform(new Point(rc.Right, rc.Bottom));
                workAreaDip = new Rect(topLeft, bottomRight);
                return workAreaDip.Width > 0 && workAreaDip.Height > 0;
            }
        }

        var (scaleX, scaleY) = GetDpiScale(dpiVisual);
        workAreaDip = new Rect(
            rc.Left / scaleX,
            rc.Top / scaleY,
            (rc.Right - rc.Left) / scaleX,
            (rc.Bottom - rc.Top) / scaleY);
        return workAreaDip.Width > 0 && workAreaDip.Height > 0;
    }

    private static (double ScaleX, double ScaleY) GetDpiScale(Visual? visual)
    {
        try
        {
            if (visual is not null)
            {
                var dpi = VisualTreeHelper.GetDpi(visual);
                return (
                    dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0,
                    dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
            }
        }
        catch
        {
            // Fall through.
        }

        try
        {
            // Primary-monitor DPI via Win32 (no System.Drawing dependency).
            var hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                try
                {
                    var dpiX = GetDeviceCaps(hdc, 88); // LOGPIXELSX
                    var scale = dpiX > 0 ? dpiX / 96.0 : 1.0;
                    return (scale, scale);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }
        catch
        {
            // Fall through.
        }

        return (1.0, 1.0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
