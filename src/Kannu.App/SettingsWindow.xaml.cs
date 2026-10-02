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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Kannu's Settings, laid out like the Windows 11 Settings app. Only pages for features that exist
/// on Windows are here; each later feature brings its own page. One window at a time.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly SettingsStore _settings;
    private readonly UpdateService _updates;
    private readonly string _statusDirectory;
    private bool _loading;

    private SettingsWindow(SettingsStore settings, UpdateService updates, string statusDirectory, string page)
    {
        InitializeComponent();
        ThemeManager.FollowTitleBar(this);
        _settings = settings;
        _updates = updates;
        _statusDirectory = statusDirectory;

        var version = $"Version {ReleaseInfo.Version} ({ReleaseInfo.Codename})";
        NavVersion.Text = version;
        AboutVersion.Text = version;
        UpdateStatus.Text = updates.IsEnabled ? "Updates install automatically." : "Updates are off in this build.";
        CheckUpdatesButton.IsEnabled = updates.IsEnabled;
        StatusFolderPath.Text = statusDirectory;

        Load(settings.Current);
        settings.Changed += Load;
        Closed += (_, _) =>
        {
            settings.Changed -= Load;
            _open = null;
        };
        Select(page);
    }

    /// <summary>Opens Settings on a page ("Notch", "Agents", "About"), or brings the open window forward.</summary>
    internal static void Open(SettingsStore settings, UpdateService updates, string statusDirectory, string page = "Notch")
    {
        if (_open is { } window)
        {
            window.Select(page);
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }
        _open = new SettingsWindow(settings, updates, statusDirectory, page);
        _open.Show();
        _open.Activate();
    }

    private void Select(string page)
    {
        foreach (ListBoxItem item in Nav.Items)
        {
            if ((string)item.Tag == page) Nav.SelectedItem = item;
        }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var page = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "Notch";
        NotchPage.Visibility = page == "Notch" ? Visibility.Visible : Visibility.Collapsed;
        AgentsPage.Visibility = page == "Agents" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Agents") BuildAgentRows();
    }

    // ---- Notch ----

    private void Load(AppSettings s)
    {
        _loading = true;
        ShapeNotch.IsChecked = s.NotchStyle == NotchStyle.Notch;
        ShapePill.IsChecked = s.NotchStyle == NotchStyle.FloatingPill;
        HideToggle.IsChecked = s.HideUntilActivity;
        EdgeToggle.IsChecked = s.RevealOnTopEdge;
        EdgeToggle.IsEnabled = s.HideUntilActivity;
        HoverToggle.IsChecked = s.OpenOnHover;
        TermsStatus.Text = s.TermsAcceptedAt is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var when)
            ? $"Version {s.TermsAcceptedVersion}, accepted on {when.LocalDateTime:D}"
            : $"Version {TermsOfUse.CurrentVersion}";
        _loading = false;
    }

    private void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var style = ShapePill.IsChecked == true ? NotchStyle.FloatingPill : NotchStyle.Notch;
        _settings.Update(s => s with { NotchStyle = style });
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s with
        {
            HideUntilActivity = HideToggle.IsChecked == true,
            RevealOnTopEdge = EdgeToggle.IsChecked == true,
            OpenOnHover = HoverToggle.IsChecked == true,
        });
    }

    // ---- Agents ----

    private void BuildAgentRows()
    {
        AgentRows.Children.Clear();
        foreach (var provider in Enum.GetValues<AgentProvider>()) AgentRows.Children.Add(AgentRow(provider));
    }

    private Border AgentRow(AgentProvider provider)
    {
        var installed = HookSetup.IsInstalled(provider);
        var present = installed || HookSetup.ToolIsPresent(provider);

        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Source = ProviderIcons.For(provider.Id()),
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = provider.DisplayName(), Style = (Style)FindResource("Kannu.RowTitle") });
        text.Children.Add(new TextBlock
        {
            Text = installed ? "Installed" : present ? "Not installed" : "Not found on this PC",
            Style = (Style)FindResource("Kannu.RowDescription"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = installed ? "Remove" : "Install",
            Style = (Style)FindResource(installed ? "Kannu.Button" : "Kannu.AccentButton"),
            IsEnabled = present,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) =>
        {
            try
            {
                if (installed) HookSetup.Uninstall(provider);
                else HookSetup.Install(provider);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HookInstallException)
            {
                MessageBox.Show(this, ex.Message, "Kannu", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            BuildAgentRows();
        };
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);

        return new Border { Style = (Style)FindResource("Kannu.Card"), Child = grid };
    }

    private void OpenStatusFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_statusDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_statusDirectory}\"") { UseShellExecute = true });
    }

    // ---- About ----

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for updates…";
        UpdateStatus.Text = await _updates.CheckAsync();
        CheckUpdatesButton.IsEnabled = true;
    }

    private void ViewTerms_Click(object sender, RoutedEventArgs e) => TermsWindow.ShowTerms(this);

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = ((FrameworkElement)sender).Tag switch
        {
            "license" => ReleaseInfo.RepositoryUrl + "/blob/main/LICENSE",
            "issue" => ReleaseInfo.RepositoryUrl + "/issues/new",
            "security" => ReleaseInfo.RepositoryUrl + "/security/advisories/new",
            _ => ReleaseInfo.RepositoryUrl,
        };
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
