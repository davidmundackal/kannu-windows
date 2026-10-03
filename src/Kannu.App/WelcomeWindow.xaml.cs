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
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// First-run welcome, shown once after the Terms are accepted (macOS Kannu's onboarding, reduced to
/// what applies on Windows): what the lights mean, connecting agents, finding the tray eye.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly Action<string> _openSettings;

    private WelcomeWindow(Action<string> openSettings)
    {
        InitializeComponent();
        ThemeManager.FollowTitleBar(this);
        _openSettings = openSettings;
        ActiveDot.Fill = KannuColors.BrushFor(TrafficLight.Green);
        AwaitingDot.Fill = KannuColors.BrushFor(TrafficLight.Yellow);
        StoppedDot.Fill = KannuColors.BrushFor(TrafficLight.Red);
    }

    /// <summary>Once per install: marks it shown as soon as it opens, so a crash cannot repeat it forever.</summary>
    internal static void ShowOnce(SettingsStore settings, Action<string> openSettings)
    {
        if (settings.Current.OnboardingDone) return;
        settings.Update(s => s with { OnboardingDone = true });
        new WelcomeWindow(openSettings).Show();
    }

    private void Agents_Click(object sender, RoutedEventArgs e)
    {
        _openSettings("Agents");
        Close();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _openSettings("Notch");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
