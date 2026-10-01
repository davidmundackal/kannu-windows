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
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Kannu.App.Interop;

namespace Kannu.App;

/// <summary>
/// The notch: a pill docked to the top centre of the primary display. Collapsed it shows one dot per
/// session and a one-line summary; hovering opens it to the session list. It never takes focus.
/// </summary>
public partial class NotchWindow : Window
{
    private const double CollapsedWidth = 200;
    private const double CollapsedHeight = 32;
    private const double ExpandedWidth = 440;
    private const double RowHeight = 40;
    private const double ExpandedChrome = 36 + 12; // header row + bottom padding
    private const double EmptyHeight = 30;

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(250);

    private readonly NotchViewModel _model;
    private readonly DispatcherTimer _collapseTimer;
    private bool _expanded;

    internal NotchWindow(NotchViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;

        // Leaving for a moment (a fast diagonal past the edge) should not slam the notch shut.
        _collapseTimer = new DispatcherTimer { Interval = CollapseDelay };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Pill.IsMouseOver) SetExpanded(false);
        };

        model.SessionsChanged += () =>
        {
            if (_expanded) Animate(expand: true);
        };

        SourceInitialized += (_, _) => NativeMethods.MakeNonActivatingToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => DockToTop();
        DpiChanged += (_, _) => DockToTop();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    // SystemEvents raise on their own thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(DockToTop);

    private void DockToTop()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        SetExpanded(true);
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void SetExpanded(bool expand)
    {
        if (_expanded == expand) return;
        _expanded = expand;
        Animate(expand);
    }

    private double ExpandedHeight()
    {
        var content = _model.Sessions.Count == 0 ? EmptyHeight : _model.Sessions.Count * RowHeight;
        // Leave room inside the window for the open motion's slight overshoot.
        return Math.Min(ExpandedChrome + content, Height - 12);
    }

    /// <summary>
    /// Open: the pill grows with a slight overshoot, then the list fades in. Close: the list fades out
    /// first, then the pill eases back. With Windows' animation effects off, both snap instantly.
    /// </summary>
    private void Animate(bool expand)
    {
        var motion = SystemParameters.ClientAreaAnimation;
        TimeSpan Ms(int ms) => motion ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

        IEasingFunction ease = expand
            ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = motion ? (expand ? OpenDuration : CloseDuration) : TimeSpan.Zero;
        var sizeDelay = expand ? TimeSpan.Zero : Ms(60);

        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(expand ? ExpandedWidth : CollapsedWidth, duration)
        {
            EasingFunction = ease,
            BeginTime = sizeDelay,
        });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(expand ? ExpandedHeight() : CollapsedHeight, duration)
        {
            EasingFunction = ease,
            BeginTime = sizeDelay,
        });
        ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(expand ? 1 : 0, expand ? Ms(120) : Ms(80))
        {
            BeginTime = expand ? Ms(130) : TimeSpan.Zero,
        });
        ExpandedContent.IsHitTestVisible = expand;
    }
}
