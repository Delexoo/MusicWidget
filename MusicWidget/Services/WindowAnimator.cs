using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace MusicWidget.Services;

public static class WindowAnimator
{
    private sealed class HiddenState
    {
        public double Left { get; init; }
        public double Top { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    private static readonly Dictionary<Window, HiddenState> _hidden = new();
    private const double DurationMs = 260;

    private enum Edge { Left, Right, Top, Bottom }

    public static void SlideOffNearestEdge(Window window, Action? onCompleted = null)
    {
        if (window is null) return;
        if (!window.IsVisible)
        {
            onCompleted?.Invoke();
            return;
        }

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(width) || width <= 0) width = 380;
        if (double.IsNaN(height) || height <= 0) height = 80;

        // Use the monitor the widget is actually on (not always primary).
        var screen = DisplayBounds.GetWorkAreaForWindow(window);

        _hidden[window] = new HiddenState
        {
            Left = window.Left,
            Top = window.Top,
            Width = width,
            Height = height,
        };

        var cx = window.Left + width / 2;
        var cy = window.Top + height / 2;

        var distLeft = cx - screen.Left;
        var distRight = screen.Right - cx;
        var distTop = cy - screen.Top;
        var distBottom = screen.Bottom - cy;

        var min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));
        Edge edge =
            min == distLeft ? Edge.Left :
            min == distRight ? Edge.Right :
            min == distTop ? Edge.Top :
            Edge.Bottom;

        DoubleAnimation pos;
        DependencyProperty prop;
        switch (edge)
        {
            case Edge.Left:
                pos = new DoubleAnimation(window.Left, screen.Left - width - 12,
                    TimeSpan.FromMilliseconds(DurationMs));
                prop = Window.LeftProperty;
                break;
            case Edge.Right:
                pos = new DoubleAnimation(window.Left, screen.Right + 12,
                    TimeSpan.FromMilliseconds(DurationMs));
                prop = Window.LeftProperty;
                break;
            case Edge.Top:
                pos = new DoubleAnimation(window.Top, screen.Top - height - 12,
                    TimeSpan.FromMilliseconds(DurationMs));
                prop = Window.TopProperty;
                break;
            default:
                pos = new DoubleAnimation(window.Top, screen.Bottom + 12,
                    TimeSpan.FromMilliseconds(DurationMs));
                prop = Window.TopProperty;
                break;
        }
        pos.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };

        var finalLeft = window.Left;
        var finalTop = window.Top;
        if (prop == Window.LeftProperty) finalLeft = (double)pos.To!;
        else finalTop = (double)pos.To!;

        pos.Completed += (_, _) =>
        {
            // Clear the animation so future reads of Left/Top reflect later changes
            // (e.g. DragMove after the widget is re-shown).
            window.BeginAnimation(prop, null);
            window.Left = finalLeft;
            window.Top = finalTop;
        };

        var fade = new DoubleAnimation(window.Opacity, 0,
            TimeSpan.FromMilliseconds(DurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            window.Visibility = Visibility.Hidden;
            onCompleted?.Invoke();
        };

        window.BeginAnimation(prop, pos);
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void SlideBackIn(Window window, Action? onCompleted = null)
    {
        if (window is null) return;

        if (!_hidden.TryGetValue(window, out var state))
        {
            window.Visibility = Visibility.Visible;
            window.Opacity = 1;
            onCompleted?.Invoke();
            return;
        }

        // Place offscreen first based on current Left/Top, then animate back.
        window.Visibility = Visibility.Visible;

        // Prefer the monitor where the widget was parked before hide.
        var screen = DisplayBounds.GetWorkAreaNearPointDip(
            new Point(state.Left + state.Width / 2, state.Top + state.Height / 2),
            window);
        // Stop any in-flight animations so we can set Left/Top directly.
        window.BeginAnimation(Window.LeftProperty, null);
        window.BeginAnimation(Window.TopProperty, null);
        window.BeginAnimation(UIElement.OpacityProperty, null);

        // Snap to the original hidden start position (offscreen) to animate back in.
        var width = state.Width;
        var height = state.Height;

        var cx = state.Left + width / 2;
        var cy = state.Top + height / 2;
        var distLeft = cx - screen.Left;
        var distRight = screen.Right - cx;
        var distTop = cy - screen.Top;
        var distBottom = screen.Bottom - cy;
        var min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        if (min == distLeft) window.Left = screen.Left - width - 12;
        else if (min == distRight) window.Left = screen.Right + 12;
        else if (min == distTop) window.Top = screen.Top - height - 12;
        else window.Top = screen.Bottom + 12;

        DoubleAnimation posLeft = new(window.Left, state.Left,
            TimeSpan.FromMilliseconds(DurationMs))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        DoubleAnimation posTop = new(window.Top, state.Top,
            TimeSpan.FromMilliseconds(DurationMs))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        var targetLeft = state.Left;
        var targetTop = state.Top;
        posLeft.Completed += (_, _) =>
        {
            // Settle the local value so DragMove and subsequent SlideOff calls
            // observe the live window position instead of an animation's held end.
            window.BeginAnimation(Window.LeftProperty, null);
            window.Left = targetLeft;
        };
        posTop.Completed += (_, _) =>
        {
            window.BeginAnimation(Window.TopProperty, null);
            window.Top = targetTop;
        };

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(DurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) =>
        {
            window.BeginAnimation(UIElement.OpacityProperty, null);
            window.Opacity = 1;
            onCompleted?.Invoke();
        };

        window.Opacity = 0;
        window.BeginAnimation(Window.LeftProperty, posLeft);
        window.BeginAnimation(Window.TopProperty, posTop);
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
