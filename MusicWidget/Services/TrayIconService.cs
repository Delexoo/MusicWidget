using System;
using System.IO;
using System.Windows;
using MusicWidget.Views;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using Forms = System.Windows.Forms;

namespace MusicWidget.Services;

/// <summary>
/// Shows MusicWidget in the Windows notification area ("Show hidden icons") so the
/// app stays reachable when the floating widget is off-screen or hidden.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private WidgetWindow? _widget;
    private bool _disposed;

    public void Attach(WidgetWindow widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrayIconService));
        }

        _widget = widget;
        DisposeIconOnly();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => Ui(() => _widget?.ToggleVisibility()));
        menu.Items.Add("Open Dashboard", null, (_, _) => Ui(() => _widget?.OpenDashboardFromTray()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Ui(() => Application.Current?.Shutdown()));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = AppBranding.DisplayName,
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                Ui(() => _widget?.ToggleVisibility());
            }
        };

        _notifyIcon.DoubleClick += (_, _) => Ui(() => _widget?.OpenDashboardFromTray());
    }

    private static void Ui(Action action)
    {
        UiDispatcher.BeginInvokeSafe(action);
    }

    private static DrawingIcon LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppIcon.ico"));
            if (resource?.Stream is Stream stream)
            {
                using (stream)
                using (var temp = new DrawingIcon(stream))
                {
                    return (DrawingIcon)temp.Clone();
                }
            }
        }
        catch
        {
            /* fall through to exe icon / system default */
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var fromFile = DrawingIcon.ExtractAssociatedIcon(exe);
                if (fromFile is not null)
                {
                    return fromFile;
                }
            }
        }
        catch
        {
            /* ignore */
        }

        return DrawingSystemIcons.Application;
    }

    private void DisposeIconOnly()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            /* best-effort */
        }

        _notifyIcon = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeIconOnly();
        _widget = null;
    }
}
