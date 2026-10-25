using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicWidget.Services;

namespace MusicWidget.Views;

public partial class WidgetWindow : Window
{
    private bool _settingsExpanded;
    private const double DefaultDashboardWidth = 750;
    private const double DefaultDashboardHeight = 790;
    private const double MinDashboardWidth = 420;
    private const double MinDashboardHeight = 440;
    /// <summary>
    /// Pixels below the work area's top edge where the widget sits by default.
    /// Stays in sync between first-launch positioning and the Reset Layout action.
    /// </summary>
    private const double DefaultTopOffset = 16;
    /// <summary>Collapsed pill layout width before the 1.08 UI ScaleTransform.</summary>
    private const double DefaultPillWidthEstimate = 282;
    /// <summary>Must match RootGrid ScaleTransform in WidgetWindow.xaml.</summary>
    private const double UiScale = 1.08;
    /// <summary>20px buttons + 5px right margin — matches website .pill-hover-controls.</summary>
    private const double HoverControlsExpandedWidth = 25;
    private const double HoverWidthAnimMs = 280;
    private static readonly IEasingFunction HoverWidthEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction HoverCollapseEase = new CubicEase { EasingMode = EasingMode.EaseIn };
    private DispatcherTimer? _hoverCollapseTimer;
    private double _hoverClipWidth;
    private bool _hoverAnimTargetExpanded;
    private double _hoverAnimTargetWidth;
    private double _settingsTargetHeight = DefaultDashboardHeight;
    private bool _resizingDashboard;
    private double _resizeStartLeft;
    private double _resizeStartWidth;
    private HotkeyService? _hotkeys;
    private GlobalMediaKeyService? _mediaKeys;

    private bool _userMovedWidget;
    private bool _hoverExpanded;
    private bool _animatingHover;
    private double _pillBaseWidth;
    private double _hoverAnchorCenterX;
    private bool _settingsAnimOpen;
    private double _settingsAnimPillCenterX;
    private bool _awaitingHoverCollapseBeforeCloseComplete;
    private MusicWidget.Models.Track? _boundTrack;

    public WidgetWindow()
    {
        InitializeComponent();
        Opacity = 0;

        var workArea = SystemParameters.WorkArea; // HWND not ready yet — primary is fine for first paint
        var estimatedPillWidth = DefaultPillWidthEstimate * UiScale;
        Left = workArea.Left + (workArea.Width - estimatedPillWidth) / 2.0;
        Top = workArea.Top + DefaultTopOffset;

        SizeChanged += WidgetWindow_SizeChanged;
        Loaded += WidgetWindow_Loaded;
        Closed += WidgetWindow_Closed;
        Deactivated += WidgetWindow_Deactivated;
        PreviewMouseDown += WidgetWindow_PreviewMouseDown;

        App.Player.PlayStateChanged += OnPlayStateChanged;
        App.Player.CurrentTrackChanged += OnCurrentTrackChanged;
        App.Player.LoopCurrentChanged += OnLoopCurrentChanged;

        UpdateLoopIcon();
        UpdatePlayPauseIcon();
        BindNowPlayingTrack(App.Player.CurrentTrack);
    }

    private void WidgetWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDefaultDashboardSize();
        ScheduleStartupCentering();

        _hoverCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _hoverCollapseTimer.Tick += HoverCollapseTimer_Tick;

        // Keep the widget usable after DPI / resolution / monitor changes.
        DpiChanged += (_, _) => OnDisplayEnvironmentChanged();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;

        _hotkeys = new HotkeyService(this);
        _hotkeys.ToggleVisibilityPressed += OnToggleVisibilityHotkey;
        _hotkeys.ToggleSettingsPressed += OnToggleSettingsHotkey;
        _hotkeys.ResetLayoutPressed += OnResetLayoutHotkey;
        _hotkeys.Register();

        _mediaKeys = new GlobalMediaKeyService(this);
        _mediaKeys.Register();

        _ = App.Tools.EnsureToolsAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                CrashLog.Write(t.Exception.GetBaseException(), "WidgetWindow.EnsureToolsAsync");
            }
        }, TaskScheduler.Default);
    }

    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(OnDisplayEnvironmentChanged);
    }

    private void OnDisplayEnvironmentChanged()
    {
        try
        {
            if (!IsLoaded || IsBeingDragged)
            {
                return;
            }

            ApplyDefaultDashboardSize();
            if (!_userMovedWidget)
            {
                CenterPillOnScreen();
            }
            else
            {
                EnsureWidgetOnScreen();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.OnDisplayEnvironmentChanged");
        }
    }

    private void EnsureWidgetOnScreen()
    {
        var width = ActualWidth > 0 ? ActualWidth : DefaultPillWidthEstimate * UiScale;
        var height = ActualHeight > 0 ? ActualHeight : 80;
        if (!DisplayBounds.IsPositionOnAnyScreen(Left, Top, width, height))
        {
            CenterPillOnScreen();
            return;
        }

        var workArea = DisplayBounds.GetWorkAreaForWindow(this);
        var (left, top) = DisplayBounds.EnsureRectInWorkArea(Left, Top, width, height, workArea);
        Left = left;
        Top = top;
    }

    private void ScheduleStartupCentering()
    {
        // Drop legacy pinned coords — every cold start centers the pill.
        var settings = App.Settings.Current;
        if (settings.WindowPositionPinned)
        {
            settings.WindowPositionPinned = false;
            settings.WindowLeft = null;
            settings.WindowTop = null;
            App.Settings.Save();
        }

        void OnInitialContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OnInitialContentRendered;
            CenterPillOnScreen();
        }

        ContentRendered += OnInitialContentRendered;
        Dispatcher.BeginInvoke(new Action(CenterPillOnScreen), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CenterPillOnScreen();
            RevealWindow();
            // One more pass after DPI / layout settle — fixes scaled displays
            // where the first PointToScreen pass can still be slightly early.
            Dispatcher.BeginInvoke(new Action(CenterPillOnScreen), DispatcherPriority.ContextIdle);
        }), DispatcherPriority.ApplicationIdle);
    }

    private void WidgetWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // During resize, DragDelta owns Left adjustments — SizeChanged must not
        // also call PreservePillScreenX or the pill double-corrects and jitters.
        if (_resizingDashboard)
        {
            return;
        }

        if (e.WidthChanged && _animatingSettings && !IsBeingDragged)
        {
            if (_settingsAnimOpen)
            {
                AlignPillOverDashboardPreservingScreenX(_settingsAnimPillCenterX);
            }
            else
            {
                PreservePillScreenX(_hoverAnchorCenterX);
            }

            return;
        }

        if (!e.WidthChanged || IsBeingDragged || _userMovedWidget || _settingsExpanded ||
            _animatingSettings || _animatingHover || _hoverExpanded)
        {
            return;
        }

        CenterPillOnScreen();
    }

    private void RevealWindow()
    {
        Opacity = 1;
    }

    private bool IsPositionOnScreen(double left, double top)
    {
        var width = ActualWidth > 0 ? ActualWidth : DefaultPillWidthEstimate * UiScale;
        var height = ActualHeight > 0 ? ActualHeight : 80;
        return DisplayBounds.IsPositionOnAnyScreen(left, top, width, height);
    }

    private void ApplyDefaultDashboardSize()
    {
        var workArea = IsLoaded
            ? DisplayBounds.GetWorkAreaForWindow(this)
            : SystemParameters.WorkArea;
        var (w, h) = DisplayBounds.ClampDashboardSize(
            workArea,
            DefaultDashboardWidth,
            DefaultDashboardHeight,
            MinDashboardWidth,
            MinDashboardHeight);

        SettingsPanelControl.Width = w;
        _settingsTargetHeight = h;
        UpdatePillDashboardAlignment();
    }

    /// <summary>
    /// Nudges the window so the pill sits at the horizontal center of the work area.
    /// Uses DIP screen-space delta math so it stays correct after SizeToContent
    /// resizes and on Per-Monitor DPI scaled displays.
    /// </summary>
    private void CenterPillOnScreen()
    {
        try
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateLayout();
            if (PillBorder.ActualWidth <= 0)
            {
                return;
            }

            var workArea = DisplayBounds.GetWorkAreaForWindow(this);
            var targetCenterX = workArea.Left + workArea.Width / 2.0;
            var currentCenterX = GetPillScreenCenterX();
            if (double.IsNaN(currentCenterX) || double.IsInfinity(currentCenterX))
            {
                return;
            }

            Left += targetCenterX - currentCenterX;
            Top = workArea.Top + DefaultTopOffset;

            // Final clamp so a DPI glitch can't park the pill off-screen.
            var width = ActualWidth > 0 ? ActualWidth : DefaultPillWidthEstimate * UiScale;
            var height = ActualHeight > 0 ? ActualHeight : 80;
            var (safeLeft, safeTop) = DisplayBounds.EnsureRectInWorkArea(Left, Top, width, height, workArea);
            Left = safeLeft;
            Top = safeTop;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.CenterPillOnScreen");
        }
    }

    private void ApplyDefaultPosition() => CenterPillOnScreen();

    /// <summary>
    /// Horizontal center of the pill in DIP screen coordinates (same units as
    /// <see cref="Window.Left"/> and <see cref="SystemParameters.WorkArea"/>).
    /// </summary>
    private double GetPillScreenCenterX()
    {
        try
        {
            if (PillBorder.ActualWidth <= 0)
            {
                return Left + (DefaultPillWidthEstimate * UiScale) / 2.0;
            }

            // PointToScreen returns device pixels. Convert both edges to DIPs so
            // the center matches Window.Left / WorkArea on 125%/150%/200% displays.
            var leftDip = PointToScreenDip(PillBorder, new Point(0, 0));
            var rightDip = PointToScreenDip(PillBorder, new Point(PillBorder.ActualWidth, 0));
            return (leftDip.X + rightDip.X) / 2.0;
        }
        catch
        {
            return Left + (PillBorder.ActualWidth > 0
                ? (PillBorder.ActualWidth * UiScale) / 2.0
                : (DefaultPillWidthEstimate * UiScale) / 2.0);
        }
    }

    private static Point PointToScreenDip(Visual visual, Point localPoint)
    {
        var devicePoint = visual.PointToScreen(localPoint);
        var source = PresentationSource.FromVisual(visual);
        if (source?.CompositionTarget is not null)
        {
            return source.CompositionTarget.TransformFromDevice.Transform(devicePoint);
        }

        var dpi = VisualTreeHelper.GetDpi(visual);
        var scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
        return new Point(devicePoint.X / scaleX, devicePoint.Y / scaleY);
    }

    /// <summary>
    /// After the window width changes (dashboard open/close), nudge Left so the pill
    /// stays at the same screen X.
    /// </summary>
    private void PreservePillScreenX(double pillCenterXBefore)
    {
        try
        {
            if (!IsLoaded || IsBeingDragged)
            {
                return;
            }

            var delta = pillCenterXBefore - GetPillScreenCenterX();
            if (Math.Abs(delta) < 0.5)
            {
                return;
            }

            Left += delta;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.PreservePillScreenX");
        }
    }

    private void AlignPillOverDashboardPreservingScreenX(double pillCenterX)
    {
        UpdatePillDashboardAlignment();
        UpdateLayout();
        PreservePillScreenX(pillCenterX);
    }

    private bool IsDashboardHosted =>
        SettingsHost.Visibility == Visibility.Visible;

    /// <summary>
    /// True while the dashboard is opening/open (host visible + expanded flag).
    /// Height is intentionally excluded so the pill centers as soon as the window
    /// widens, not after the height animation passes zero.
    /// </summary>
    private bool ShouldAlignPillOverDashboard =>
        _settingsExpanded && IsDashboardHosted;

    private bool ShouldKeepHoverControlsExpanded() =>
        _settingsExpanded && IsDashboardHosted && (_settingsAnimOpen || !_animatingSettings);

    /// <summary>
    /// Keeps the pill centered over the dashboard when open, or pinned to its screen
    /// center while hover expands/collapses when the dashboard is closed.
    /// </summary>
    private void UpdatePillLayoutDuringHover()
    {
        if (IsBeingDragged)
        {
            return;
        }

        if (_animatingSettings)
        {
            if (ShouldAlignPillOverDashboard)
            {
                UpdatePillDashboardAlignment();
            }
            else if (_hoverExpanded || _animatingHover)
            {
                PreservePillScreenX(_hoverAnchorCenterX);
            }

            return;
        }

        if (ShouldAlignPillOverDashboard)
        {
            UpdatePillDashboardAlignment();
            return;
        }

        if (_hoverExpanded || _animatingHover)
        {
            PreservePillScreenX(_hoverAnchorCenterX);
        }
    }

    /// <summary>
    /// When the dashboard is open, center the pill stack over the panel
    /// (including while hover expands/collapses the pill width).
    /// </summary>
    private void UpdatePillDashboardAlignment()
    {
        try
        {
            if (!IsLoaded || IsBeingDragged)
            {
                return;
            }

            PillBorder.HorizontalAlignment = HorizontalAlignment.Left;
            PillBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
            PillBorder.Margin = new Thickness(0);

            PillStack.HorizontalAlignment = ShouldAlignPillOverDashboard
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.UpdatePillDashboardAlignment");
        }
    }

    private void ResetPillDashboardAlignment()
    {
        PillStack.HorizontalAlignment = HorizontalAlignment.Left;
        PillBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
        PillBorder.Margin = new Thickness(0);
    }

    /// <summary>
    /// Keeps the pill visually centered over the dashboard during resize without
    /// PointToScreen feedback loops (those caused the jitter).
    /// When width grows from the left edge, shift <see cref="Window.Left"/> left by
    /// half the delta so the screen-center of the pill stays put.
    /// </summary>
    private void SyncPillDuringDashboardResize()
    {
        if (!ShouldAlignPillOverDashboard)
        {
            return;
        }

        UpdatePillDashboardAlignment();
        UpdateLayout();

        var widthNow = ActualWidth;
        if (widthNow <= 0 || _resizeStartWidth <= 0)
        {
            return;
        }

        // Stable width-delta math — no GetPillScreenCenterX / PointToScreen during drag.
        Left = _resizeStartLeft - (widthNow - _resizeStartWidth) / 2.0;
    }

    private void CachePillBaseWidth()
    {
        if (!IsLoaded)
        {
            _pillBaseWidth = DefaultPillWidthEstimate;
            return;
        }

        UpdateLayout();
        var measured = PillBorder.ActualWidth - _hoverClipWidth;
        _pillBaseWidth = measured > 0 ? measured : DefaultPillWidthEstimate;
    }

    private double GetEffectivePillWidth()
    {
        var measured = PillBorder.ActualWidth;
        if (measured > 0)
        {
            return measured;
        }

        var clipW = HoverControlsClip.Width;
        if (double.IsNaN(clipW) || clipW < 0)
        {
            clipW = _hoverClipWidth;
        }

        return _pillBaseWidth + clipW;
    }

    private double GetDashboardWidth()
    {
        var dashboardW = SettingsPanelControl.Width;
        if (double.IsNaN(dashboardW) || dashboardW <= 0)
        {
            dashboardW = SettingsHost.ActualWidth;
        }

        if (dashboardW <= 0)
        {
            dashboardW = SettingsPanelControl.ActualWidth;
        }

        return dashboardW;
    }

    /// <summary>
    /// Computes the canonical default widget top and the left edge when the pill is
    /// horizontally centered in the work area.
    /// </summary>
    private (double Left, double Top) ComputeDefaultPosition()
    {
        UpdateLayout();
        var workArea = DisplayBounds.GetWorkAreaForWindow(this);
        var top = workArea.Top + DefaultTopOffset;
        if (PillBorder.ActualWidth <= 0)
        {
            return (Left, top);
        }

        var targetCenterX = workArea.Left + workArea.Width / 2.0;
        var left = Left + targetCenterX - GetPillScreenCenterX();
        var width = ActualWidth > 0 ? ActualWidth : DefaultPillWidthEstimate * UiScale;
        var height = ActualHeight > 0 ? ActualHeight : 80;
        return DisplayBounds.EnsureRectInWorkArea(left, top, width, height, workArea);
    }

    /// <summary>
    /// Clicking the player pill (or anywhere outside the dashboard panel) closes the dashboard.
    /// </summary>
    private void WidgetWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_settingsExpanded)
        {
            return;
        }

        if (IsDescendantOf(e.OriginalSource as DependencyObject, SettingsHost))
        {
            return;
        }

        MinimizeDashboard();
    }

    /// <summary>
    /// Clicking another app or the desktop closes the dashboard (not the whole widget).
    /// </summary>
    private void WidgetWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_settingsExpanded || _animatingSettings)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_settingsExpanded || _animatingSettings || ComponentDispatcher.IsThreadModal)
            {
                return;
            }

            MinimizeDashboard();
        }, DispatcherPriority.Background);
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        if (source is null)
        {
            return false;
        }

        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void WidgetWindow_Closed(object? sender, EventArgs e)
    {
        Deactivated -= WidgetWindow_Deactivated;
        PreviewMouseDown -= WidgetWindow_PreviewMouseDown;
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
        }
        catch
        {
            // Unhook is best-effort during shutdown.
        }

        App.Player.PlayStateChanged -= OnPlayStateChanged;
        App.Player.CurrentTrackChanged -= OnCurrentTrackChanged;
        App.Player.LoopCurrentChanged -= OnLoopCurrentChanged;

        if (_boundTrack is not null)
        {
            _boundTrack.PropertyChanged -= BoundTrack_PropertyChanged;
            _boundTrack = null;
        }

        if (_hoverCollapseTimer is not null)
        {
            _hoverCollapseTimer.Stop();
            _hoverCollapseTimer.Tick -= HoverCollapseTimer_Tick;
            _hoverCollapseTimer = null;
        }

        HoverControlsClip.BeginAnimation(FrameworkElement.WidthProperty, null);
        SettingsHost.BeginAnimation(HeightProperty, null);
        PillBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        _hotkeys?.Dispose();
        _hotkeys = null;
        _mediaKeys?.Dispose();
        _mediaKeys = null;
        Application.Current.Shutdown();
    }

    private void OnToggleVisibilityHotkey(object? sender, EventArgs e)
    {
        ToggleVisibility();
    }

    private void OnToggleSettingsHotkey(object? sender, EventArgs e)
    {
        // Make sure the widget is on-screen before opening settings, otherwise the
        // panel would expand from a hidden pill.
        if (!IsLogicallyVisible)
        {
            ToggleVisibility();
        }
        ToggleSettings();
    }

    private void OnResetLayoutHotkey(object? sender, EventArgs e)
    {
        // Hotkey path: skip the confirmation prompt (the user already typed a
        // three-key combo intentionally) and bring the widget back on-screen first
        // so they can see the reset land.
        if (!IsLogicallyVisible)
        {
            ToggleVisibility();
        }
        ResetLayoutToDefaults();
    }

    public event EventHandler<bool>? VisibilityToggleRequested;

    public bool IsLogicallyVisible { get; private set; } = true;

    private bool _animatingVisibility;

    public void ToggleVisibility()
    {
        if (_animatingVisibility)
        {
            return;
        }

        var hiding = IsLogicallyVisible;
        IsLogicallyVisible = !hiding;
        _animatingVisibility = true;
        VisibilityToggleRequested?.Invoke(this, IsLogicallyVisible);

        void OnDone() => _animatingVisibility = false;

        if (hiding)
        {
            CollapseSettingsImmediate();
            SetHoverControlsExpanded(false, immediate: true, force: true);
            WindowAnimator.SlideOffNearestEdge(this, OnDone);
        }
        else
        {
            WindowAnimator.SlideBackIn(this, () =>
            {
                try
                {
                    // After re-showing, ensure the widget is visible on the current screen
                    // (monitors/work area may have changed while hidden). If the user never
                    // dragged the widget, keep the experience "centered on open".
                    if (!IsPositionOnScreen(Left, Top))
                    {
                        CenterPillOnScreen();
                    }
                    else if (!_userMovedWidget && !_settingsExpanded && !_animatingSettings)
                    {
                        CenterPillOnScreen();
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "WidgetWindow.ToggleVisibility.SlideBackIn");
                }
                finally
                {
                    OnDone();
                }
            });
        }
    }

    /// <summary>
    /// Hides the widget off-screen (same as Ctrl+\ or the top chevron). Dashboard
    /// collapses first so only the pill slides away.
    /// </summary>
    public void MinimizeWidget()
    {
        if (!IsLogicallyVisible)
        {
            return;
        }

        if (_animatingVisibility)
        {
            Dispatcher.BeginInvoke(MinimizeWidget, DispatcherPriority.ApplicationIdle);
            return;
        }

        ToggleVisibility();
    }

    /// <summary>
    /// Closes only the dashboard panel, leaving the floating player widget visible.
    /// </summary>
    public void MinimizeDashboard()
    {
        if (_animatingSettings)
        {
            Dispatcher.BeginInvoke(MinimizeDashboard, DispatcherPriority.ApplicationIdle);
            return;
        }

        if (!_settingsExpanded)
        {
            return;
        }

        AnimateSettings(false);
    }

    /// <summary>
    /// True while the user is actively dragging the widget pill. The HideToggleWindow
    /// reads this and freezes the chevron tab in place during the drag so the tab
    /// doesn't slide around with the widget.
    /// </summary>
    public bool IsBeingDragged { get; private set; }

    private void MoveThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        _hoverCollapseTimer?.Stop();
        _animatingHover = false;

        HoverControlsClip.BeginAnimation(FrameworkElement.WidthProperty, null);
        PillBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
        HoverControlsClip.Width = _hoverClipWidth;
        PillBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
        PillBorder.Margin = new Thickness(0);

        IsBeingDragged = true;
    }

    private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!IsBeingDragged)
        {
            return;
        }

        Left += e.HorizontalChange;
        Top += e.VerticalChange;
    }

    private void MoveThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        IsBeingDragged = false;
        _hoverAnchorCenterX = GetPillScreenCenterX();

        if (Math.Abs(e.HorizontalChange) > 3 || Math.Abs(e.VerticalChange) > 3)
        {
            _userMovedWidget = true;
        }
    }

    private void PillBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer?.Stop();

        if (IsBeingDragged || _animatingSettings)
        {
            return;
        }

        SetHoverControlsExpanded(true);
    }

    private void PillBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        if (IsBeingDragged || _animatingSettings || ShouldKeepHoverControlsExpanded())
        {
            return;
        }

        // Debounced leave — avoids flicker when crossing child elements.
        _hoverCollapseTimer?.Stop();
        _hoverCollapseTimer?.Start();
    }

    private void HoverCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _hoverCollapseTimer?.Stop();
        if (PillBorder.IsMouseOver || IsBeingDragged || ShouldKeepHoverControlsExpanded() || _animatingSettings)
        {
            return;
        }

        SetHoverControlsExpanded(false);
    }

    private void SetHoverControlsExpanded(bool expanded, bool immediate = false, bool force = false)
    {
        if (!force && IsBeingDragged)
        {
            return;
        }

        // While the dashboard opens, defer hover expansion until the height animation finishes.
        if (!force && _animatingSettings && expanded)
        {
            return;
        }

        if (!force && !expanded && ShouldKeepHoverControlsExpanded())
        {
            return;
        }

        if (!immediate && !_animatingHover && _hoverExpanded == expanded)
        {
            return;
        }

        _hoverExpanded = expanded;
        _hoverCollapseTimer?.Stop();
        _hoverAnchorCenterX = GetPillScreenCenterX();

        CachePillBaseWidth();

        if (immediate)
        {
            HoverControlsClip.BeginAnimation(FrameworkElement.WidthProperty, null);
            _animatingHover = false;
            _hoverClipWidth = expanded ? HoverControlsExpandedWidth : 0;
            ApplyHoverVisualState(expanded, _hoverClipWidth);
            return;
        }

        _animatingHover = true;
        _hoverAnimTargetExpanded = expanded;
        _hoverAnimTargetWidth = expanded ? HoverControlsExpandedWidth : 0;

        if (expanded)
        {
            HoverControlsPanel.IsHitTestVisible = true;
        }

        var ease = expanded ? HoverWidthEase : HoverCollapseEase;
        var widthAnim = new DoubleAnimation
        {
            To = _hoverAnimTargetWidth,
            Duration = TimeSpan.FromMilliseconds(HoverWidthAnimMs),
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        widthAnim.CurrentTimeInvalidated += OnHoverWidthAnimationTick;
        widthAnim.Completed += OnHoverWidthAnimationCompleted;

        HoverControlsClip.BeginAnimation(FrameworkElement.WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnHoverWidthAnimationTick(object? sender, EventArgs e)
    {
        if (IsBeingDragged)
        {
            return;
        }

        UpdatePillLayoutDuringHover();
    }

    private void OnHoverWidthAnimationCompleted(object? sender, EventArgs e)
    {
        if (sender is DoubleAnimation anim)
        {
            anim.Completed -= OnHoverWidthAnimationCompleted;
        }

        _animatingHover = false;
        _hoverClipWidth = _hoverAnimTargetWidth;
        ApplyHoverVisualState(_hoverAnimTargetExpanded, _hoverClipWidth);

        if (_awaitingHoverCollapseBeforeCloseComplete && !_hoverAnimTargetExpanded)
        {
            _awaitingHoverCollapseBeforeCloseComplete = false;
            CompleteDashboardCloseLayout();
        }
    }

    private void ApplyHoverVisualState(bool expanded, double clipWidth)
    {
        HoverControlsClip.BeginAnimation(FrameworkElement.WidthProperty, null);
        HoverControlsClip.Width = clipWidth;
        HoverControlsPanel.Opacity = 1;
        HoverControlsPanel.IsHitTestVisible = expanded && clipWidth >= HoverControlsExpandedWidth - 0.5;

        if (!_animatingSettings || !_settingsAnimOpen)
        {
            UpdatePillLayoutDuringHover();
        }
    }

    /// <summary>
    /// Sets hover clip width without triggering dashboard-close layout side effects.
    /// </summary>
    private void ApplyHoverStateSilently(bool expanded)
    {
        HoverControlsClip.BeginAnimation(FrameworkElement.WidthProperty, null);
        _animatingHover = false;
        _hoverExpanded = expanded;
        _hoverClipWidth = expanded ? HoverControlsExpandedWidth : 0;
        HoverControlsClip.Width = _hoverClipWidth;
        HoverControlsPanel.Opacity = 1;
        HoverControlsPanel.IsHitTestVisible = expanded;
    }

    /// <summary>
    /// Eases the pill margin back after the dashboard height reaches zero, then collapses
    /// the host and hover controls in one layout pass so the window width does not stutter.
    /// </summary>
    private void FinalizeDashboardClose()
    {
        _hoverAnchorCenterX = GetPillScreenCenterX();
        _settingsExpanded = false;
        SettingsHost.BeginAnimation(HeightProperty, null);
        SettingsHost.Height = 0;

        var hoverStillVisible = _animatingHover || _hoverExpanded || _hoverClipWidth > 0.5;

        if (hoverStillVisible)
        {
            _awaitingHoverCollapseBeforeCloseComplete = true;
            if (!_animatingHover)
            {
                SetHoverControlsExpanded(false);
            }

            SettingsHost.Visibility = Visibility.Collapsed;
            ResetPillDashboardAlignment();
            UpdateLayout();
            PreservePillScreenX(_hoverAnchorCenterX);
            return;
        }

        CompleteDashboardCloseLayout();
    }

    private void CompleteDashboardCloseLayout()
    {
        PillBorder.BeginAnimation(FrameworkElement.MarginProperty, null);

        if (_animatingHover || _hoverClipWidth > 0.5)
        {
            if (!_animatingHover)
            {
                SetHoverControlsExpanded(false);
            }

            SettingsHost.Visibility = Visibility.Collapsed;
            ResetPillDashboardAlignment();
            UpdateLayout();
            PreservePillScreenX(_hoverAnchorCenterX);
            return;
        }

        ApplyHoverStateSilently(false);
        ResetPillDashboardAlignment();
        SettingsHost.Visibility = Visibility.Collapsed;

        UpdateLayout();
        PreservePillScreenX(_hoverAnchorCenterX);
        EndSettingsAnimation();
    }

    private void SchedulePillPositionAfterLayout(double pillCenterX, Action? onComplete = null)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded)
            {
                onComplete?.Invoke();
                return;
            }

            if (ShouldAlignPillOverDashboard)
            {
                AlignPillOverDashboardPreservingScreenX(pillCenterX);
            }
            else
            {
                UpdateLayout();
                PreservePillScreenX(pillCenterX);
            }

            onComplete?.Invoke();
        }), DispatcherPriority.Loaded);
    }

    private void EndSettingsAnimation()
    {
        _animatingSettings = false;

        if (_settingsExpanded || ShouldKeepHoverControlsExpanded())
        {
            return;
        }

        if (!PillBorder.IsMouseOver)
        {
            return;
        }

        // Defer hover re-expand until the close layout finishes so the pill does not
        // widen again in the same frame the dashboard host collapses.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_settingsExpanded && !_animatingSettings && PillBorder.IsMouseOver)
            {
                SetHoverControlsExpanded(true);
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private bool _animatingSettings;
    private double _pillCenterXBeforeResize;

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettings();
    }

    private void ToggleSettings()
    {
        if (_animatingSettings)
        {
            // Drop the spam: only one open/close animation runs at a time.
            return;
        }
        AnimateSettings(!_settingsExpanded);
    }

    private void AnimateSettings(bool open)
    {
        _settingsAnimOpen = open;
        _settingsAnimPillCenterX = GetPillScreenCenterX();
        _animatingSettings = true;
        _awaitingHoverCollapseBeforeCloseComplete = false;

        try
        {
            if (open)
            {
                _settingsExpanded = true;
                SettingsHost.Visibility = Visibility.Visible;
            }
            else
            {
                _hoverAnchorCenterX = _settingsAnimPillCenterX;
                SetHoverControlsExpanded(false);
            }

            double from = SettingsHost.Height;
            double to = open ? _settingsTargetHeight : 0;

            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(open ? 220 : HoverWidthAnimMs),
                EasingFunction = open
                    ? new CubicEase { EasingMode = EasingMode.EaseOut }
                    : HoverCollapseEase,
            };
            anim.Completed += OnSettingsHeightAnimationCompleted;
            SettingsHost.BeginAnimation(HeightProperty, anim, HandoffBehavior.SnapshotAndReplace);

            if (open)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        AlignPillOverDashboardPreservingScreenX(_settingsAnimPillCenterX);
                        SchedulePillPositionAfterLayout(_settingsAnimPillCenterX);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "WidgetWindow.AnimateSettings.OpenLayout");
                    }
                }, DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.AnimateSettings");
            _animatingSettings = false;
            if (open)
            {
                _settingsExpanded = false;
                SettingsHost.Visibility = Visibility.Collapsed;
                SettingsHost.Height = 0;
            }
        }
    }

    private void OnSettingsHeightAnimationCompleted(object? sender, EventArgs e)
    {
        if (sender is DoubleAnimation anim)
        {
            anim.Completed -= OnSettingsHeightAnimationCompleted;
        }

        var to = _settingsAnimOpen ? _settingsTargetHeight : 0;

        SettingsHost.BeginAnimation(HeightProperty, null);
        SettingsHost.Height = to;
        if (!_settingsAnimOpen)
        {
            FinalizeDashboardClose();
        }
        else
        {
            SetHoverControlsExpanded(true, immediate: true, force: true);
            AlignPillOverDashboardPreservingScreenX(_settingsAnimPillCenterX);
            SchedulePillPositionAfterLayout(_settingsAnimPillCenterX, EndSettingsAnimation);
        }
    }

    private void CollapseSettingsImmediate()
    {
        if (!_settingsExpanded && SettingsHost.Height <= 0)
        {
            _animatingSettings = false;
            return;
        }

        _settingsExpanded = false;
        _animatingSettings = false;
        var pillCenterX = GetPillScreenCenterX();
        SettingsHost.BeginAnimation(HeightProperty, null);
        SettingsHost.Height = 0;
        SettingsHost.Visibility = Visibility.Collapsed;
        ResetPillDashboardAlignment();
        ApplyHoverStateSilently(false);
        UpdateLayout();
        PreservePillScreenX(pillCenterX);
        _hoverAnchorCenterX = pillCenterX;
    }

    // ----- Dashboard resize grip -----

    private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_settingsExpanded || _animatingSettings)
        {
            // Drop the gesture; can't resize while the panel is mid open/close animation.
            _resizingDashboard = false;
            return;
        }

        _resizingDashboard = true;
        _pillCenterXBeforeResize = GetPillScreenCenterX();
        UpdateLayout();
        _resizeStartLeft = Left;
        _resizeStartWidth = ActualWidth > 0 ? ActualWidth : Width;

        // Stop any held animation so subsequent direct Height writes own the property.
        SettingsHost.BeginAnimation(HeightProperty, null);
        SettingsHost.Height = _settingsTargetHeight;
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_resizingDashboard) return;

        var workArea = DisplayBounds.GetWorkAreaForWindow(this);
        var currentW = double.IsNaN(SettingsPanelControl.Width) || SettingsPanelControl.Width <= 0
            ? SettingsPanelControl.ActualWidth
            : SettingsPanelControl.Width;
        var (newW, newH) = DisplayBounds.ClampDashboardSize(
            workArea,
            currentW + e.HorizontalChange,
            _settingsTargetHeight + e.VerticalChange,
            MinDashboardWidth,
            MinDashboardHeight);
        var widthChanged = Math.Abs(newW - currentW) > 0.5;

        SettingsPanelControl.Width = newW;
        _settingsTargetHeight = newH;
        SettingsHost.Height = newH;

        if (widthChanged)
        {
            SyncPillDuringDashboardResize();
        }
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizingDashboard) return;
        _resizingDashboard = false;

        if (_settingsExpanded)
        {
            // One final stable pass using screen coords after the drag ends.
            UpdateLayout();
            UpdatePillDashboardAlignment();
            PreservePillScreenX(_pillCenterXBeforeResize);
        }

        // The new dimensions are intentionally not persisted: every launch resets
        // the layout to defaults (see ClearPersistedLayoutOverrides in
        // WidgetWindow_Loaded). Resizing only affects the current session.
    }

    // ----- Reset layout -----

    /// <summary>
    /// Restores the widget position and dashboard dimensions to their first-launch
    /// defaults: centered horizontally at the top of the work area, default dashboard
    /// width/height, with the panel keeping its current open/closed state.
    /// </summary>
    public void ResetLayoutToDefaults()
    {
        // 1. Clamp the defaults against the current monitor so this still does the right
        //    thing on smaller / secondary displays.
        var workArea = DisplayBounds.GetWorkAreaForWindow(this);
        var (newW, newH) = DisplayBounds.ClampDashboardSize(
            workArea,
            DefaultDashboardWidth,
            DefaultDashboardHeight,
            MinDashboardWidth,
            MinDashboardHeight);

        // 2. Snap dashboard dimensions back. If the panel is open we animate the
        //    height change so the user sees the reset happening.
        SettingsPanelControl.Width = newW;
        _settingsTargetHeight = newH;

        // 2b. Restore the playlist <-> songs splitter inside the dashboard so the
        //     reset is full ("everything"), regardless of how this method is triggered
        //     (button, Ctrl+Shift+\ hotkey, or future callers).
        SettingsPanelControl.ResetSplitter();

        if (_settingsExpanded)
        {
            SettingsHost.Visibility = Visibility.Visible;
            UpdatePillDashboardAlignment();
            SettingsHost.BeginAnimation(HeightProperty, null);
            var heightAnim = new DoubleAnimation
            {
                From = SettingsHost.Height,
                To = newH,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            heightAnim.Completed += (_, _) =>
            {
                SettingsHost.BeginAnimation(HeightProperty, null);
                SettingsHost.Height = newH;
                UpdatePillDashboardAlignment();
            };
            SettingsHost.BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
        }

        // 3. Clear persisted overrides so the next launch picks up the same defaults.
        App.Settings.Current.WindowLeft = null;
        App.Settings.Current.WindowTop = null;
        App.Settings.Current.WindowPositionPinned = false;
        App.Settings.Current.DashboardWidth = null;
        App.Settings.Current.DashboardHeight = null;
        App.Settings.Save();

        _userMovedWidget = false;

        // 4. Animate the window back to the default position. Defer until layout has
        //    settled — width may have just changed, which affects ActualWidth and
        //    therefore the centering math.
        Dispatcher.BeginInvoke(new Action(AnimateBackToDefaultPosition), DispatcherPriority.Loaded);
    }

    private void AnimateBackToDefaultPosition()
    {
        UpdatePillDashboardAlignment();
        var (defaultLeft, defaultTop) = ComputeDefaultPosition();

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);

        var leftAnim = new DoubleAnimation
        {
            From = Left,
            To = defaultLeft,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var topAnim = new DoubleAnimation
        {
            From = Top,
            To = defaultTop,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        leftAnim.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = defaultLeft;
        };
        topAnim.Completed += (_, _) =>
        {
            BeginAnimation(TopProperty, null);
            Top = defaultTop;
        };
        BeginAnimation(LeftProperty, leftAnim, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty, topAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        App.Player.SetLoopCurrent(!App.Player.LoopCurrent);
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        App.Player.Previous();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        App.Player.TogglePlayPause();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        App.Player.Next();
    }

    private void OnPlayStateChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(UpdatePlayPauseIcon);
    }

    private void OnCurrentTrackChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(() =>
        {
            try
            {
                BindNowPlayingTrack(App.Player.CurrentTrack);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "WidgetWindow.OnCurrentTrackChanged");
            }
        });
    }

    private void BindNowPlayingTrack(MusicWidget.Models.Track? track)
    {
        if (ReferenceEquals(_boundTrack, track))
        {
            UpdateNowPlayingDisplay();
            return;
        }

        if (_boundTrack is not null)
        {
            _boundTrack.PropertyChanged -= BoundTrack_PropertyChanged;
        }

        _boundTrack = track;

        if (_boundTrack is not null)
        {
            _boundTrack.PropertyChanged += BoundTrack_PropertyChanged;
            if (_boundTrack.ArtworkSource is null)
            {
                var capture = _boundTrack;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var art = await App.Artwork.GetArtworkAsync(capture).ConfigureAwait(false);
                        if (art is null) return;
                        UiDispatcher.BeginInvokeSafe(() =>
                        {
                            if (!ReferenceEquals(_boundTrack, capture)) return;
                            capture.ArtworkSource = art;
                        }, DispatcherPriority.Background);
                    }
                    catch
                    {
                        // Best-effort: missing artwork falls back to initials.
                    }
                });
            }
        }

        UpdateNowPlayingDisplay();
    }

    private void BoundTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(UpdateNowPlayingDisplay);
    }

    private void UpdateNowPlayingDisplay()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var t = _boundTrack;
            if (t is null)
            {
            TrackTitle.Text = "Nothing playing";
            TrackArtist.Text = string.Empty;
            TrackTitle.ToolTip = null;
            TrackArtist.ToolTip = null;
                AlbumArtInitials.Text = "♪";
                AlbumArtInitials.Visibility = Visibility.Visible;
                AlbumArtImageHost.Visibility = Visibility.Collapsed;
                AlbumArtBrush.ImageSource = null;
                return;
            }

            TrackTitle.Text = !string.IsNullOrWhiteSpace(t.Title) ? t.Title : t.FriendlyDisplayName;
            TrackArtist.Text = !string.IsNullOrWhiteSpace(t.Artist) ? t.Artist : "Unknown artist";
            TrackTitle.ToolTip = TrackTitle.Text;
            TrackArtist.ToolTip = TrackArtist.Text;

            if (t.ArtworkSource is not null)
            {
                AlbumArtBrush.ImageSource = t.ArtworkSource;
                AlbumArtImageHost.Visibility = Visibility.Visible;
                AlbumArtInitials.Visibility = Visibility.Collapsed;
            }
            else
            {
                AlbumArtBrush.ImageSource = null;
                AlbumArtImageHost.Visibility = Visibility.Collapsed;
                AlbumArtInitials.Text = "♪";
                AlbumArtInitials.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.UpdateNowPlayingDisplay");
        }
    }

    private void OnLoopCurrentChanged(object? sender, EventArgs e)
    {
        UiDispatcher.BeginInvokeSafe(UpdateLoopIcon);
    }

    private void UpdatePlayPauseIcon()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var key = App.Player.IsPlaying ? "Icon.Pause" : "Icon.Play";
            PlayPauseIcon.Data = (Geometry)Application.Current.Resources[key];
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.UpdatePlayPauseIcon");
        }
    }

    private void UpdateLoopIcon()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var on = App.Player.LoopCurrent;
            var dark = (Brush)Application.Current.Resources["Brush.WidgetIcon"];
            var white = (Brush)Application.Current.Resources["Brush.WhiteButton"];

            if (on)
            {
                LoopButton.Background = dark;
                LoopIconRect.Fill = Brushes.White;
                LoopButton.ToolTip = "Loop current song (on)";
            }
            else
            {
                LoopButton.Background = white;
                LoopIconRect.Fill = dark;
                LoopButton.ToolTip = "Loop current song (off)";
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WidgetWindow.UpdateLoopIcon");
        }
    }
}
