// Kannu for Windows
// Copyright (C) 2026 Kannu Contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program. If
// not, see <https://www.gnu.org/licenses/>.


using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Kannu.App.Interop;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The notch at the top centre of the primary display. Windows has no physical notch to hide it
/// behind, so by default it is out of view and slides in when an agent's light changes, when the tray
/// eye is clicked, or (if the user allows it) when the pointer rests at the top edge; the rules are
/// <see cref="NotchPresence"/>. Closed it shows one dot per session and a summary; open, its tabs and
/// the session list. It never takes focus.
/// </summary>
public partial class NotchWindow : Window
{
    private const double CollapsedWidth = 200;
    private const double CollapsedHeight = 32;
    private const double ExpandedWidth = 440;
    private const double RowHeight = 40;
    private const double ExpandedChrome = 10 + 30 + 6 + 12; // top padding, header row, gap, bottom padding
    private const double EmptyHeight = 30;

    /// <summary>macOS Kannu's Dynamic Island sits this far below the top edge.</summary>
    private const double PillTopOffset = 6;

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SlideInDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan SlideOutDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan EdgePollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TabHoverDelay = TimeSpan.FromMilliseconds(80);

    private readonly NotchViewModel _model;
    private readonly SettingsStore _settings;
    private readonly NotchPresence _presence = new();
    private readonly DispatcherTimer _deadline;
    private readonly DispatcherTimer _edgePoll;

    /// <summary>Only while the notch is always visible and must keep out of full-screen apps.</summary>
    private readonly DispatcherTimer _fullscreenPoll;
    private readonly DispatcherTimer _tabHover;

    private NotchPresenceState _shown = NotchPresenceState.Hidden;
    private NotchStyle _style;
    private long _edgeInsideSince;

    /// <summary>Opened from the top edge with no WPF MouseEnter yet: the poll watches for the pointer leaving.</summary>
    private bool _edgeUnconfirmed;

    private NotchTab? _pendingTab;

    public event Action? SettingsRequested;

    /// <summary>The sun button: keep the PC awake now, or stop.</summary>
    public event Action? CaffeinateRequested;

    internal NotchWindow(NotchViewModel model, SettingsStore settings)
    {
        InitializeComponent();
        _model = model;
        _settings = settings;
        DataContext = model;

        _deadline = new DispatcherTimer();
        _deadline.Tick += (_, _) =>
        {
            _deadline.Stop();
            _presence.Tick(Now);
            Apply();
        };
        _fullscreenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _fullscreenPoll.Tick += (_, _) => ApplyFullscreen();
        _edgePoll = new DispatcherTimer { Interval = EdgePollInterval };
        _edgePoll.Tick += (_, _) => PollEdge();
        _tabHover = new DispatcherTimer { Interval = TabHoverDelay };
        _tabHover.Tick += (_, _) =>
        {
            _tabHover.Stop();
            if (_pendingTab is { } tab) tab.IsSelected = true;
        };

        model.SessionsChanged += () =>
        {
            if (_shown == NotchPresenceState.Open && !model.IsUsageTab) AnimateSize(open: true);
        };
        model.TabChanged += () =>
        {
            if (_shown == NotchPresenceState.Open) AnimateSize(open: true);
        };
        model.AggregateChanged += _ => ApplyLights();
        KannuColors.Changed += ApplyLights;
        model.HudShown += () =>
        {
            if (_settings.Current.HideInFullscreen && NativeMethods.IsFullscreenBusy()) return;
            _presence.Hud(Now);
            Apply();
        };
        model.Activity += () =>
        {
            // A full-screen app, game or presentation is not interrupted; the tray eye still opens the notch.
            if (_settings.Current.HideInFullscreen && NativeMethods.IsFullscreenBusy()) return;
            _presence.AgentActivity(Now);
            Apply();
        };
        settings.Changed += ApplySettings;

        SourceInitialized += (_, _) => NativeMethods.MakeNonActivatingToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            DockToTop();
            ApplySettings(settings.Current);
        };
        DpiChanged += (_, _) => DockToTop();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _fullscreenPoll.Stop();
            StopWatchingClicks();
            settings.Changed -= ApplySettings;
            KannuColors.Changed -= ApplyLights;
        };
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A click on the tray eye.</summary>
    public void ToggleFromTray()
    {
        // The click on the tray eye already closed the open notch as a click outside it: that click
        // meant "close", not "close and open again".
        if (Now - _closedByOutsideAt < 600) return;
        _presence.ToggleFromTray(Now);
        Apply();
    }

    // SystemEvents raise on their own thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(DockToTop);

    /// <summary>
    /// Top centre of the chosen monitor's work area, in that monitor's pixels: on a monitor with another
    /// scale WPF then raises DpiChanged, and this runs again at the new size.
    /// </summary>
    private void DockToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && TargetWorkArea() is var (work, scale))
        {
            var width = (int)Math.Round(Width * scale);
            NativeMethods.MoveWindow(hwnd, work.Left + (work.Right - work.Left - width) / 2, work.Top);
            return;
        }
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    /// <summary>The chosen monitor's work area; the main display when the chosen one is not connected.</summary>
    private (NativeMethods.RECT Work, double Scale)? TargetWorkArea()
    {
        var s = _settings.Current;
        if (s.Display == NotchDisplay.Chosen && s.DisplayDevice is { } device
            && System.Windows.Forms.Screen.AllScreens.FirstOrDefault(m => m.DeviceName == device) is { } screen)
        {
            var bounds = screen.Bounds;
            return NativeMethods.MonitorWorkAreaAt(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }
        return NativeMethods.MonitorWorkArea(s.Display == NotchDisplay.Pointer);
    }

    /// <summary>The always-visible notch steps aside while a full-screen app, game or presentation is in front.</summary>
    private void ApplyFullscreen()
    {
        var s = _settings.Current;
        var watch = !s.HideUntilActivity && s.HideInFullscreen;
        if (watch && !_fullscreenPoll.IsEnabled) _fullscreenPoll.Start();
        else if (!watch && _fullscreenPoll.IsEnabled) _fullscreenPoll.Stop();
        var hide = watch && _shown != NotchPresenceState.Open && NativeMethods.IsFullscreenBusy();
        Pill.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
    }

    private void ApplySettings(AppSettings settings)
    {
        _style = settings.NotchStyle;
        var floating = _style == NotchStyle.FloatingPill;
        Pill.Margin = new Thickness(0, floating ? PillTopOffset : 0, 0, 0);
        Pill.BorderThickness = floating ? new Thickness(1) : new Thickness(1, 0, 1, 1);
        ApplyCorners(_shown == NotchPresenceState.Open);
        ApplySkin(settings);
        ApplyLights();
        _presence.Configure(settings.HideUntilActivity, settings.OpenOnHover, Now);
        if (new WindowInteropHelper(this).Handle is var handle && handle != IntPtr.Zero)
        {
            NativeMethods.ExcludeFromCapture(handle, settings.HideFromCapture);
        }
        DockToTop();
        Apply(force: true);
        ApplyFullscreen();
    }

    /// <summary>macOS radii: notch 14 closed and 24 open below a flat top; pill a capsule closed, 24 open.</summary>
    private void ApplyCorners(bool open)
    {
        var radius = open ? 24 : _style == NotchStyle.FloatingPill ? Math.Max(CollapsedHeight / 2, 16) : 14;
        Pill.CornerRadius = _style == NotchStyle.FloatingPill ? new CornerRadius(radius) : new CornerRadius(0, 0, radius, radius);
    }

    // ---- Lights and skin ----

    private TrafficLight _litLight = (TrafficLight)(-1);
    private LightStyle _litStyle;

    /// <summary>
    /// The closed notch's light, as macOS draws it: Classic shows all three with the unlit two dimmed,
    /// Minimal only the lit one. A lit green or yellow breathes (scale 1.3, opacity 0.5, 0.7 s each
    /// way); a fresh red pulses for 4 s, then holds. Still with Windows' animation effects off.
    /// </summary>
    private void ApplyLights()
    {
        var light = _model.Aggregate;
        var style = _settings.Current.LightStyle;
        var lights = new[] { (ActiveLight, TrafficLight.Green), (AwaitingLight, TrafficLight.Yellow), (StoppedLight, TrafficLight.Red) };
        foreach (var (ellipse, slot) in lights)
        {
            ellipse.Fill = KannuColors.BrushFor(slot);
            var lit = slot == light;
            ellipse.Visibility = style == LightStyle.Classic || lit ? Visibility.Visible : Visibility.Collapsed;
            if (!lit) StopBreathing(ellipse, opacity: style == LightStyle.Classic ? 0.22 : 1);
        }
        // Minimal with nothing lit (every chat idle): one dim dot, so the notch still says "agents here".
        if (style == LightStyle.Minimal && light == TrafficLight.Inactive)
        {
            ActiveLight.Visibility = Visibility.Visible;
            ActiveLight.Fill = KannuColors.BrushFor(TrafficLight.Inactive);
        }

        if (light == _litLight && style == _litStyle) return;
        _litLight = light;
        _litStyle = style;
        var litEllipse = light switch
        {
            TrafficLight.Green => ActiveLight,
            TrafficLight.Yellow => AwaitingLight,
            TrafficLight.Red => StoppedLight,
            _ => null,
        };
        if (litEllipse is not null) Breathe(litEllipse, forever: light != TrafficLight.Red);
    }

    private static void Breathe(System.Windows.Shapes.Ellipse ellipse, bool forever)
    {
        StopBreathing(ellipse, 1);
        if (!SystemParameters.ClientAreaAnimation) return;
        var half = TimeSpan.FromMilliseconds(700);
        var repeat = forever ? RepeatBehavior.Forever : new RepeatBehavior(TimeSpan.FromSeconds(4));
        var scale = (ScaleTransform)ellipse.RenderTransform;
        var grow = new DoubleAnimation(1, 1.3, half) { AutoReverse = true, RepeatBehavior = repeat, EasingFunction = new SineEase() };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ellipse.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.5, half) { AutoReverse = true, RepeatBehavior = repeat, EasingFunction = new SineEase() });
    }

    private static void StopBreathing(System.Windows.Shapes.Ellipse ellipse, double opacity)
    {
        var scale = (ScaleTransform)ellipse.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ellipse.BeginAnimation(OpacityProperty, null);
        ellipse.Opacity = opacity;
    }

    /// <summary>A picture behind the notch, cropped to fill it, with the user's scrim over it.</summary>
    private void ApplySkin(AppSettings settings)
    {
        ImageSource? image = null;
        if (settings.SkinPath is { } path && System.IO.File.Exists(path))
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = 900;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception e) when (e is System.IO.IOException or NotSupportedException or UriFormatException or InvalidOperationException)
            {
                Diagnostics.Error("Could not load the notch skin", e);
            }
        }
        Pill.Background = image is null
            ? new SolidColorBrush(Color.FromArgb(0xF2, 0x12, 0x12, 0x14))
            : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        SkinScrim.Opacity = image is null ? 0 : settings.SkinScrim;
    }

    // ---- Presence ----

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _edgeUnconfirmed = false;
        _presence.PointerEntered(Now);
        Apply();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _presence.PointerLeft(Now);
        Apply();
    }

    /// <summary>Shows the presence the state machine settled on, then arms its next deadline.</summary>
    private void Apply(bool force = false)
    {
        var state = _presence.State;
        if (state != _shown || force)
        {
            var wasHidden = _shown == NotchPresenceState.Hidden;
            var wasOpen = _shown == NotchPresenceState.Open;
            _shown = state;
            if (state == NotchPresenceState.Hidden) _edgeUnconfirmed = false;
            // "The monitor the pointer is on": chosen each time the notch comes out.
            if (wasHidden && state != NotchPresenceState.Hidden && _settings.Current.Display == NotchDisplay.Pointer) DockToTop();
            if (state == NotchPresenceState.Open) Pill.Visibility = Visibility.Visible;
            if (wasHidden != (state == NotchPresenceState.Hidden) || force) Slide(visible: state != NotchPresenceState.Hidden);
            if (state == NotchPresenceState.Open) WatchClicksOutside();
            else StopWatchingClicks();
            if (wasOpen != (state == NotchPresenceState.Open) || force)
            {
                ApplyCorners(state == NotchPresenceState.Open);
                AnimateSize(state == NotchPresenceState.Open);
            }
        }

        _deadline.Stop();
        if (_presence.NextDeadlineMs is { } due)
        {
            _deadline.Interval = TimeSpan.FromMilliseconds(Math.Max(1, due - Now));
            _deadline.Start();
        }
        SyncEdgePoll();
    }

    /// <summary>In: slides down from above the edge and fades in. Out: the reverse, then ignores the pointer.</summary>
    private void Slide(bool visible)
    {
        var motion = SystemParameters.ClientAreaAnimation;
        var duration = motion ? (visible ? SlideInDuration : SlideOutDuration) : TimeSpan.Zero;
        var hiddenY = -(Pill.Height + Pill.Margin.Top + 8);
        IEasingFunction ease = visible
            ? new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };
        PillShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(visible ? 0 : hiddenY, duration) { EasingFunction = ease });
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, duration));
        Pill.IsHitTestVisible = visible;
    }

    private double ExpandedHeight()
    {
        var card = _model.HasSecurityAlert && !_model.IsUsageTab ? 64 : 0;
        var content = card + (_model.IsUsageTab
            ? (_model.UsageBars.Count == 0 ? EmptyHeight + 18 : _model.UsageBars.Count * UsageBar.Height) + (_model.HasOtherUsage ? 22 : 0)
            : _model.Sessions.Count == 0 ? EmptyHeight : _model.Sessions.Count * RowHeight);
        // Leave room inside the window for the open motion's overshoot and the pill's offset.
        return Math.Min(ExpandedChrome + content, Height - 12 - PillTopOffset);
    }

    /// <summary>
    /// Open: the pill grows with a slight overshoot, then the tabs and list fade in. Close: they fade out
    /// first, then the pill eases back. With Windows' animation effects off, both snap instantly.
    /// </summary>
    private void AnimateSize(bool open)
    {
        var motion = SystemParameters.ClientAreaAnimation;
        TimeSpan Ms(int ms) => motion ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

        IEasingFunction ease = open
            ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = motion ? (open ? OpenDuration : CloseDuration) : TimeSpan.Zero;
        var sizeDelay = open ? TimeSpan.Zero : Ms(60);

        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(open ? ExpandedWidth : CollapsedWidth, duration)
        {
            EasingFunction = ease,
            BeginTime = sizeDelay,
        });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(open ? ExpandedHeight() : CollapsedHeight, duration)
        {
            EasingFunction = ease,
            BeginTime = sizeDelay,
        });
        ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, open ? Ms(120) : Ms(80))
        {
            BeginTime = open ? Ms(130) : TimeSpan.Zero,
        });
        CollapsedRow.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 0 : 1, Ms(100))
        {
            BeginTime = open ? TimeSpan.Zero : Ms(120),
        });
        ExpandedContent.IsHitTestVisible = open;
    }

    // ---- Click outside ----

    private IntPtr _mouseHook;
    private long _closedByOutsideAt;
    private NativeMethods.LowLevelMouseProc? _mouseProc;

    /// <summary>
    /// Only while the notch is open: a low-level mouse hook sees a click anywhere. Removed as soon as
    /// the notch closes, so a closed notch costs nothing.
    /// </summary>
    private void WatchClicksOutside()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc ??= OnMouse;
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandleW(null), 0);
    }

    private void StopWatchingClicks()
    {
        if (_mouseHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (code >= 0 && message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN
            && NativeMethods.GetCursorPos(out var point) && !PillContains(point))
        {
            // Never act inside the hook: Windows drops a hook that takes too long.
            Dispatcher.BeginInvoke(() =>
            {
                if (_presence.State == NotchPresenceState.Open) _closedByOutsideAt = Now;
                _presence.ClickedOutside(Now);
                Apply();
            });
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool PillContains(NativeMethods.POINT point)
    {
        if (PresentationSource.FromVisual(Pill) is not { } source) return true;
        var toDevice = source.CompositionTarget.TransformToDevice;
        var origin = Pill.PointToScreen(new Point(0, 0));
        var rect = new Rect(origin, new Size(Pill.ActualWidth * toDevice.M11, Pill.ActualHeight * toDevice.M22));
        return rect.Contains(new Point(point.X, point.Y));
    }

    // ---- Top edge (opt-in) ----

    private void SyncEdgePoll()
    {
        var s = _settings.Current;
        var wanted = (s.HideUntilActivity && s.RevealOnTopEdge && _shown == NotchPresenceState.Hidden) || _edgeUnconfirmed;
        if (wanted && !_edgePoll.IsEnabled)
        {
            _edgeInsideSince = 0;
            _edgePoll.Start();
        }
        else if (!wanted && _edgePoll.IsEnabled)
        {
            _edgePoll.Stop();
        }
    }

    /// <summary>
    /// 20 times a second, only while the notch is hidden and the user turned the edge reveal on: has the
    /// pointer rested in the strip at the top centre for <see cref="NotchPresence.EdgeDwellMs"/>? After an
    /// edge-open it also watches for the pointer leaving, because WPF raises no MouseEnter for a pointer
    /// that has not moved since the notch appeared under it.
    /// </summary>
    private void PollEdge()
    {
        if (!NativeMethods.GetCursorPos(out var point) || PresentationSource.FromVisual(this) is not { } source) return;
        var toDevice = source.CompositionTarget.TransformToDevice;
        var now = Now;

        if (_edgeUnconfirmed)
        {
            var origin = Pill.PointToScreen(new Point(0, 0));
            var pill = new Rect(origin, new Size(Pill.ActualWidth * toDevice.M11, Pill.ActualHeight * toDevice.M22));
            pill.Inflate(8 * toDevice.M11, 8 * toDevice.M22);
            if (!pill.Contains(new Point(point.X, point.Y)))
            {
                _edgeUnconfirmed = false;
                _presence.PointerLeft(now);
                Apply();
            }
            return;
        }

        // macOS's entry zone: the closed notch's width plus 8 each side, its height plus 10, at the top.
        // From the window's real screen position: Left and Top are not pixels on a scaled monitor.
        var corner = PointToScreen(new Point(0, 0));
        var zone = new Rect(
            corner.X + (Width - CollapsedWidth - 16) / 2 * toDevice.M11,
            corner.Y,
            (CollapsedWidth + 16) * toDevice.M11,
            (CollapsedHeight + 10) * toDevice.M22);
        if (!zone.Contains(new Point(point.X, point.Y)))
        {
            _edgeInsideSince = 0;
            return;
        }
        if (_edgeInsideSince == 0) _edgeInsideSince = now;
        if (now - _edgeInsideSince < NotchPresence.EdgeDwellMs) return;

        _edgeInsideSince = 0;
        _edgeUnconfirmed = true;
        _presence.TopEdgeDwell(now);
        Apply();
    }

    // ---- Header ----

    private void Tab_MouseEnter(object sender, MouseEventArgs e)
    {
        _pendingTab = (sender as FrameworkElement)?.DataContext as NotchTab;
        _tabHover.Stop();
        _tabHover.Start();
    }

    private void Tab_MouseLeave(object sender, MouseEventArgs e)
    {
        _tabHover.Stop();
        _pendingTab = null;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>The pinned security card's Details: Settings › Security.</summary>
    public event Action? SecurityRequested;

    private void SecurityDetails_Click(object sender, RoutedEventArgs e) => SecurityRequested?.Invoke();

    private void SecurityAcknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (_model.SecurityAlert is { } group) SecurityMonitor.Shared?.Acknowledge(group);
    }

    /// <summary>Click-through (D2): the agent's window comes forward; the exact tab is not chosen.</summary>
    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionRow row) return;
        e.Handled = true;
        if (!Kannu.Shared.HostProcess.Activate(row.Session))
        {
            Diagnostics.Info($"click-through: no window for a {row.Session.Provider} session");
            System.Media.SystemSounds.Beep.Play();
        }
    }

    private void Caffeinate_Click(object sender, RoutedEventArgs e) => CaffeinateRequested?.Invoke();
}
