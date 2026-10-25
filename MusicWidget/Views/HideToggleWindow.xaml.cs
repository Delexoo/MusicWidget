using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MusicWidget.Services;

namespace MusicWidget.Views;

public partial class HideToggleWindow : Window
{
    private readonly WidgetWindow _widget;
    private bool _showingWidget = true;

    public HideToggleWindow(WidgetWindow widget)
    {
        InitializeComponent();
        _widget = widget;
        _showingWidget = widget.IsLogicallyVisible;
        Loaded += OnLoaded;
        widget.VisibilityToggleRequested += Widget_VisibilityToggleRequested;
        Closed += (_, _) =>
        {
            widget.VisibilityToggleRequested -= Widget_VisibilityToggleRequested;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Anchor the chevron tab once at the top-center of the work area and never
        // move it again. It's a fixed shortcut, not something that follows the
        // widget around, so it stays put when the widget is dragged, hidden,
        // shown, resized, or reset.
        Top = SystemParameters.WorkArea.Top;
        AnchorToScreenTopCenter();
        Dispatcher.BeginInvoke(new Action(AnchorToScreenTopCenter), System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(AnchorToScreenTopCenter), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        SetChevronAngle(_showingWidget ? 0 : 180);
    }

    /// <summary>
    /// Places the tab once at the top-center of the primary work area. After this
    /// runs the tab's position is frozen for the rest of the session.
    /// </summary>
    private void AnchorToScreenTopCenter()
    {
        UpdateLayout();
        // Prefer the monitor hosting the widget so the chevron matches where the
        // user is looking on multi-monitor / different-DPI setups.
        var workArea = _widget is { IsLoaded: true }
            ? DisplayBounds.GetWorkAreaForWindow(_widget)
            : DisplayBounds.GetWorkAreaForWindow(this);
        var tabWidth = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 40);
        if (double.IsNaN(tabWidth) || tabWidth <= 0) tabWidth = 40;
        Left = workArea.Left + (workArea.Width - tabWidth) / 2.0;
        Top = workArea.Top;
    }

    private void Bg_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _widget.ToggleVisibility();
    }

    private void Widget_VisibilityToggleRequested(object? sender, bool willBeVisible)
    {
        if (_showingWidget == willBeVisible) return;
        _showingWidget = willBeVisible;
        AnimateChevronTo(willBeVisible ? 0 : 180);
    }

    private void SetChevronAngle(double angle)
    {
        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ChevronRotate.Angle = angle;
    }

    private void AnimateChevronTo(double target)
    {
        var from = ChevronRotate.Angle;
        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ChevronRotate.Angle = from;

        var anim = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        anim.Completed += (_, _) =>
        {
            ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            ChevronRotate.Angle = target;
        };
        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }
}
