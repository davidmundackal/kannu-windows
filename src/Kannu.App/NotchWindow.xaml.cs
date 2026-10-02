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
    private readonly DispatcherTimer _tabHover;

    private NotchPresenceState _shown = NotchPresenceState.Hidden;
    private NotchStyle _style;
    private long _edgeInsideSince;

    /// <summary>Opened from the top edge with no WPF MouseEnter yet: the poll watches for the pointer leaving.</summary>
    private bool _edgeUnconfirmed;

    private NotchTab? _pendingTab;

    public event Action? SettingsRequested;

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
            if (_shown == NotchPresenceState.Open) AnimateSize(open: true);
        };
        model.Activity += () =>
        {
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
            settings.Changed -= ApplySettings;
        };
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A click on the tray eye.</summary>
    public void ToggleFromTray()
    {
        _presence.ToggleFromTray(Now);
        Apply();
    }

    // SystemEvents raise on their own thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(DockToTop);

    private void DockToTop()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    private void ApplySettings(AppSettings settings)
    {
        _style = settings.NotchStyle;
        var floating = _style == NotchStyle.FloatingPill;
        Pill.Margin = new Thickness(0, floating ? PillTopOffset : 0, 0, 0);
        Pill.BorderThickness = floating ? new Thickness(1) : new Thickness(1, 0, 1, 1);
        ApplyCorners(_shown == NotchPresenceState.Open);
        _presence.Configure(settings.HideUntilActivity, settings.OpenOnHover, Now);
        Apply(force: true);
    }

    /// <summary>macOS radii: notch 14 closed and 24 open below a flat top; pill a capsule closed, 24 open.</summary>
    private void ApplyCorners(bool open)
    {
        var radius = open ? 24 : _style == NotchStyle.FloatingPill ? Math.Max(CollapsedHeight / 2, 16) : 14;
        Pill.CornerRadius = _style == NotchStyle.FloatingPill ? new CornerRadius(radius) : new CornerRadius(0, 0, radius, radius);
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
            if (wasHidden != (state == NotchPresenceState.Hidden) || force) Slide(visible: state != NotchPresenceState.Hidden);
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
        var content = _model.Sessions.Count == 0 ? EmptyHeight : _model.Sessions.Count * RowHeight;
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
        var width = (CollapsedWidth + 16) * toDevice.M11;
        var zone = new Rect(
            (Left + (Width - CollapsedWidth - 16) / 2) * toDevice.M11,
            Top * toDevice.M22,
            width,
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
}
