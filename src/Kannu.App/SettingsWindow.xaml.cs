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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        if (SecurityMonitor.Shared is { } security) security.Changed += OnFindingsChanged;
        Closed += (_, _) =>
        {
            if (SecurityMonitor.Shared is { } monitor) monitor.Changed -= OnFindingsChanged;
            settings.Changed -= Load;
            _open = null;
        };
        Select(page);
    }

    /// <summary>Opens Settings on a page ("Notch", "Agents", "About"), or brings the open window forward.</summary>
    internal static void Open(SettingsStore settings, UpdateService updates, string statusDirectory, string page = "General")
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
        var page = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "General";
        GeneralPage.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "General") LoadLogin();
        NotchPage.Visibility = page == "Notch" ? Visibility.Visible : Visibility.Collapsed;
        AgentsPage.Visibility = page == "Agents" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPage.Visibility = page == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        SecurityPage.Visibility = page == "Security" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Security") BuildFindings();
        if (page == "Notifications") LoadSecrets();
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Agents") BuildAgentRows();
    }

    // ---- Search ----

    private sealed record SearchHit(string Page, string Title, string Description, FrameworkElement Target);

    /// <summary>
    /// Every row and section title on every page, read from the pages themselves, so a new row is
    /// searchable without a hand-kept index to drift (macOS's settingsSearchIndex lesson).
    /// </summary>
    private List<SearchHit> SearchIndex()
    {
        var hits = new List<SearchHit>();
        var rowTitle = FindResource("Kannu.RowTitle");
        var sectionTitle = FindResource("Kannu.SectionTitle");
        var description = FindResource("Kannu.RowDescription");
        foreach (var (page, panel) in new (string, FrameworkElement)[]
                 {
                     ("General", GeneralPage), ("Notch", NotchPage), ("Agents", AgentsPage),
                     ("Security", SecurityPage), ("Notifications", NotificationsPage), ("About", AboutPage),
                 })
        {
            foreach (var text in Descendants(panel))
            {
                if (text.Style != rowTitle && text.Style != sectionTitle || string.IsNullOrWhiteSpace(text.Text)) continue;
                var detail = (text.Parent as Panel)?.Children.OfType<TextBlock>()
                    .FirstOrDefault(t => t.Style == description)?.Text ?? "";
                var target = text.Style == rowTitle ? FindRow(text) : text;
                hits.Add(new SearchHit(page, text.Text, detail, target));
            }
        }
        return hits;
    }

    private static IEnumerable<TextBlock> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock text) yield return text;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>The settings row (or choice tile) a title belongs to, for the highlight.</summary>
    private static FrameworkElement FindRow(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (node is Grid { MinHeight: > 0 } row) return row;
            if (node is RadioButton tile) return tile;
            if (node is Border { Style: not null } card && card.Style == card.TryFindResource("Kannu.Card")) return card;
        }
        return element;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Items.Clear();
        if (query.Length == 0)
        {
            SearchResults.Visibility = Visibility.Collapsed;
            Nav.Visibility = Visibility.Visible;
            return;
        }
        var hits = SearchIndex()
            .Where(h => h.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || h.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || h.Page.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(h => h.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .Take(12);
        foreach (var hit in hits)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = hit.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock
            {
                Text = hit.Page,
                FontSize = 12,
                Foreground = (Brush)FindResource("Kannu.TextSecondary"),
            });
            SearchResults.Items.Add(new ListBoxItem { Content = label, Tag = hit });
        }
        if (SearchResults.Items.Count == 0)
        {
            SearchResults.Items.Add(new ListBoxItem { Content = "No settings found", IsEnabled = false });
        }
        SearchResults.Visibility = Visibility.Visible;
        Nav.Visibility = Visibility.Collapsed;
    }

    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) SearchBox.Clear();
        else if (e.Key == System.Windows.Input.Key.Enter && SearchResults.Items.Count > 0 && ((ListBoxItem)SearchResults.Items[0]).Tag is SearchHit)
            SearchResults.SelectedIndex = 0;
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((SearchResults.SelectedItem as ListBoxItem)?.Tag is not SearchHit hit) return;
        SearchBox.Clear();
        Select(hit.Page);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            hit.Target.BringIntoView();
            Highlight(hit.Target);
        });
    }

    /// <summary>A short accent wash over the found row, gone in a second and a half (instant with animations off).</summary>
    private void Highlight(FrameworkElement target)
    {
        var accent = ((SolidColorBrush)FindResource("Kannu.Accent")).Color;
        var wash = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        var original = target switch
        {
            Panel panel => panel.Background,
            Control control => control.Background,
            Border border => border.Background,
            _ => null,
        };
        void Set(Brush? brush)
        {
            switch (target)
            {
                case Panel panel: panel.Background = brush; break;
                case Control control: control.Background = brush; break;
                case Border border: border.Background = brush; break;
            }
        }
        Set(wash);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromMilliseconds(600))
        {
            BeginTime = TimeSpan.FromMilliseconds(300),
        };
        fade.Completed += (_, _) => Set(original);
        wash.BeginAnimation(Brush.OpacityProperty, fade);
    }

    // ---- General ----

    private void LoadLogin()
    {
        if (LaunchAtLoginManager.StablePath is null)
        {
            LoginToggle.IsChecked = false;
            LoginToggle.IsEnabled = false;
            LoginDescription.Text = "Available when Kannu is installed with its installer.";
            return;
        }
        LoginToggle.IsEnabled = true;
        LoginToggle.IsChecked = LaunchAtLoginManager.IsEnabled;
        LoginDescription.Text = LaunchAtLoginManager.DisabledInTaskManager
            ? "Turned off in Task Manager › Startup apps. Turning it on here turns it back on."
            : "Kannu starts quietly in the background so the notch is ready when your agents are.";
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        LaunchAtLoginManager.Set(LoginToggle.IsChecked == true);
        LoadLogin();
    }

    private void Display_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var display = DisplayPointer.IsChecked == true ? NotchDisplay.Pointer : NotchDisplay.Primary;
        _settings.Update(s => s with { Display = display });
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) =>
        _settings.Update(s => s with { HideInFullscreen = FullscreenToggle.IsChecked == true });

    private void Shortcut_Click(object sender, RoutedEventArgs e) =>
        _settings.Update(s => s with { ShortcutsEnabled = ShortcutToggle.IsChecked == true });

    // ---- Security ----

    private void LoadSecurity(AppSettings s)
    {
        HiddenTextToggle.IsChecked = s.DetectHiddenText;
        WarnAgentToggle.IsChecked = s.WarnAgentAboutHiddenText;
        WarnAgentToggle.IsEnabled = s.DetectHiddenText;
        SecretsToggle.IsChecked = s.DetectSecrets;
        PathsToggle.IsChecked = s.DetectSensitivePaths;
        McpToggle.IsChecked = s.WatchMcpServers;
        EnforceToggle.IsChecked = s.EnforceAgentPolicy;
        PushHighToggle.IsChecked = s.PushHighFindings;
        PushMediumToggle.IsChecked = s.PushMediumFindings;

        var path = HookSecurity.PolicyPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        PolicyDescription.Text = !File.Exists(path)
            ? $"No policy. To block commands or tools, create {path} — see docs/SECURITY.md for the format."
            : HookSecurity.LoadPolicy(path) is { } policy
                ? $"{path}: {PolicyRuleCount(policy)} rules. Every match is a finding."
                : $"{path} could not be read (bad JSON, too large, or a link), so no rules apply. Agents are never blocked by a broken policy.";
    }

    private static int PolicyRuleCount(AgentPolicy policy) => policy.Rules.Count;

    private void SecurityToggle_Click(object sender, RoutedEventArgs e) => _settings.Update(s => s with
    {
        DetectHiddenText = HiddenTextToggle.IsChecked == true,
        WarnAgentAboutHiddenText = WarnAgentToggle.IsChecked == true,
        DetectSecrets = SecretsToggle.IsChecked == true,
        DetectSensitivePaths = PathsToggle.IsChecked == true,
        WatchMcpServers = McpToggle.IsChecked == true,
        EnforceAgentPolicy = EnforceToggle.IsChecked == true,
        PushHighFindings = PushHighToggle.IsChecked == true,
        PushMediumFindings = PushMediumToggle.IsChecked == true,
    });

    private void OpenPolicyFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kannu");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OnFindingsChanged()
    {
        if (SecurityPage.Visibility == Visibility.Visible) BuildFindings();
    }

    private async void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        if (SecurityMonitor.Shared is { } monitor) await monitor.ScanNowAsync();
    }

    private void Unhide_Click(object sender, RoutedEventArgs e) => SecurityMonitor.Shared?.Unhide();

    /// <summary>One card per problem: what happened, how often, where, and what to do about it.</summary>
    private void BuildFindings()
    {
        FindingRows.Children.Clear();
        var monitor = SecurityMonitor.Shared;
        var groups = monitor?.Visible ?? [];
        if (groups.Count == 0)
        {
            var hidden = (monitor?.All.Count ?? 0) - groups.Count;
            FindingRows.Children.Add(new Border
            {
                Style = (Style)FindResource("Kannu.Card"),
                Child = new TextBlock
                {
                    Text = hidden > 0 ? $"Nothing new. {hidden} acknowledged or snoozed." : "Nothing found. Kannu keeps watching while agents run.",
                    Style = (Style)FindResource("Kannu.RowDescription"),
                    FontSize = 13,
                },
            });
            return;
        }
        foreach (var group in groups) FindingRows.Children.Add(FindingCard(group));
    }

    private Border FindingCard(FindingGroup group)
    {
        var f = group.Representative;
        var high = group.Severity == FindingSeverity.High;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Kannu.Icon"),
            Text = high ? "\uEA18" : "\uE83D",
            Foreground = high ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) : (Brush)FindResource("Kannu.TextSecondary"),
            Margin = new Thickness(0, 2, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        var heading = new DockPanel();
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Background = high ? new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B)) : (Brush)FindResource("Kannu.ControlFill"),
            Child = new TextBlock { Text = SecurityFindings.SeverityLabel(group.Severity), FontSize = 11 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(badge, Dock.Right);
        heading.Children.Add(badge);
        heading.Children.Add(new TextBlock { Text = f.Title, Style = (Style)FindResource("Kannu.RowTitle"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = f.Summary, Style = (Style)FindResource("Kannu.RowDescription"), TextWrapping = TextWrapping.Wrap, MaxHeight = 40 });

        var when = group.Occurrences > 1
            ? $"{group.Occurrences} occurrences · first {Local(group.FirstSeenMs)} · last {Local(group.LastSeenMs)}"
            : Local(group.LastSeenMs);
        if (group.Chats.Count > 1) when += $" · in {group.Chats.Count} chats";
        body.Children.Add(new TextBlock { Text = when, Style = (Style)FindResource("Kannu.RowDescription"), Margin = new Thickness(0, 4, 0, 0) });

        var details = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var line in f.Evidence.Concat(f.KannuOnlyEvidence))
        {
            details.Children.Add(new TextBlock
            {
                Text = line,
                Style = (Style)FindResource("Kannu.RowDescription"),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            });
        }
        body.Children.Add(details);

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Action(string text, Action click, bool accent = false)
        {
            var button = new Button
            {
                Content = text,
                Style = (Style)FindResource(accent ? "Kannu.AccentButton" : "Kannu.Button"),
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) => click();
            actions.Children.Add(button);
            return button;
        }
        Button? detailsButton = null;
        detailsButton = Action("Details", () =>
        {
            var open = details.Visibility == Visibility.Visible;
            details.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            detailsButton!.Content = open ? "Details" : "Hide details";
        });
        Action("Acknowledge", () => SecurityMonitor.Shared?.Acknowledge(group), accent: true);
        Action("Snooze 24 h", () => SecurityMonitor.Shared?.Snooze(group));
        if (f.RevealPath is { } reveal)
        {
            var path = reveal.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + reveal[1..] : reveal;
            Action("Show in folder", () =>
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            });
        }
        // What an agent can be told: title and evidence, never the summary's chat name or the decoded text.
        Action("Copy for agent", () => Clipboard.SetText(
            $"Kannu security finding ({SecurityFindings.SeverityLabel(group.Severity)}): {f.Title}\n" + string.Join("\n", f.Evidence)));
        body.Children.Add(actions);
        grid.Children.Add(body);
        return new Border { Style = (Style)FindResource("Kannu.Card"), Child = grid, Margin = new Thickness(0, 0, 0, 4) };
    }

    private static string Local(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    // ---- Notifications ----

    private static readonly int[] ReminderMinutes = [0, 5, 10, 15, 30];

    private void LoadNotifications(AppSettings s)
    {
        ToastToggle.IsChecked = s.ToastsEnabled;
        PushToggle.IsChecked = s.PushEnabled;
        InactiveToggle.IsChecked = s.PushOnInactive;
        ProviderNtfy.IsChecked = s.PushProvider == PushProvider.Ntfy;
        ProviderPushover.IsChecked = s.PushProvider == PushProvider.Pushover;
        ProviderWebhook.IsChecked = s.PushProvider == PushProvider.Webhook;
        ProviderDescription.Text = s.PushProvider switch
        {
            PushProvider.Ntfy => "Free. Install the ntfy app on your phone and subscribe to the same topic.",
            PushProvider.Pushover => "Install Pushover on your phone (a one-time purchase after the trial).",
            _ => "POST JSON to your own service, for your own integrations.",
        };
        NtfyRows.Visibility = s.PushProvider == PushProvider.Ntfy ? Visibility.Visible : Visibility.Collapsed;
        PushoverRows.Visibility = s.PushProvider == PushProvider.Pushover ? Visibility.Visible : Visibility.Collapsed;
        WebhookRows.Visibility = s.PushProvider == PushProvider.Webhook ? Visibility.Visible : Visibility.Collapsed;
        if (!NtfyServerBox.IsKeyboardFocused) NtfyServerBox.Text = s.NtfyServer;

        ReminderChoices.Children.Clear();
        foreach (var minutes in ReminderMinutes)
        {
            var choice = new RadioButton
            {
                Style = (Style)FindResource("Kannu.ChoiceTile"),
                GroupName = "Reminder",
                Margin = new Thickness(0, 0, 6, 0),
                Content = minutes == 0 ? "Off" : $"{minutes} min",
                IsChecked = s.WaitReminderMinutes == minutes,
            };
            var value = minutes;
            choice.Checked += (_, _) =>
            {
                if (!_loading) _settings.Update(x => x with { WaitReminderMinutes = value });
            };
            ReminderChoices.Children.Add(choice);
        }
    }

    /// <summary>Read from Credential Manager only when the page is shown, never kept in a field.</summary>
    private void LoadSecrets()
    {
        NtfyTopicBox.Password = SecretStore.Get(SecretStore.NtfyTopic);
        PushoverUserBox.Password = SecretStore.Get(SecretStore.PushoverUserKey);
        PushoverTokenBox.Password = SecretStore.Get(SecretStore.PushoverAppToken);
        WebhookBox.Password = SecretStore.Get(SecretStore.WebhookUrl);
    }

    private void Notify_Click(object sender, RoutedEventArgs e) => _settings.Update(s => s with
    {
        ToastsEnabled = ToastToggle.IsChecked == true,
        PushEnabled = PushToggle.IsChecked == true,
        PushOnInactive = InactiveToggle.IsChecked == true,
    });

    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var provider = ProviderPushover.IsChecked == true ? PushProvider.Pushover
            : ProviderWebhook.IsChecked == true ? PushProvider.Webhook : PushProvider.Ntfy;
        _settings.Update(s => s with { PushProvider = provider });
    }

    private void NtfyServer_LostFocus(object sender, RoutedEventArgs e)
    {
        var server = NtfyServerBox.Text.Trim();
        if (server.Length == 0) server = new AppSettings().NtfyServer;
        _settings.Update(s => s with { NtfyServer = server });
    }

    private void Secret_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender == NtfyTopicBox) SecretStore.Set(SecretStore.NtfyTopic, NtfyTopicBox.Password);
        else if (sender == PushoverUserBox) SecretStore.Set(SecretStore.PushoverUserKey, PushoverUserBox.Password);
        else if (sender == PushoverTokenBox) SecretStore.Set(SecretStore.PushoverAppToken, PushoverTokenBox.Password);
        else if (sender == WebhookBox) SecretStore.Set(SecretStore.WebhookUrl, WebhookBox.Password);
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        // A field still being typed in has not lost focus yet: save everything first.
        Secret_LostFocus(NtfyTopicBox, e);
        Secret_LostFocus(PushoverUserBox, e);
        Secret_LostFocus(PushoverTokenBox, e);
        Secret_LostFocus(WebhookBox, e);
        NtfyServer_LostFocus(NtfyServerBox, e);
        if (NotificationManager.Shared is not { } manager) return;
        TestResult.Text = "Sending…";
        var error = await manager.SendTestAsync();
        TestResult.Text = error is null ? "Sent. Check your phone." : "Not sent: " + error;
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
        StyleClassic.IsChecked = s.LightStyle == LightStyle.Classic;
        StyleMinimal.IsChecked = s.LightStyle == LightStyle.Minimal;
        StyleDescription.Text = s.LightStyle == LightStyle.Classic
            ? "All three lights, with the inactive two dimmed."
            : "Only the light that is currently lit.";
        BuildColorRows(s.LightColors);
        LoadNotifications(s);
        LoadSecurity(s);
        DisplayPrimary.IsChecked = s.Display == NotchDisplay.Primary;
        DisplayPointer.IsChecked = s.Display == NotchDisplay.Pointer;
        FullscreenToggle.IsChecked = s.HideInFullscreen;
        ShortcutToggle.IsChecked = s.ShortcutsEnabled;
        ShortcutDescription.Text = s.ShortcutsEnabled && ShortcutManager.Taken
            ? "Another app already uses Ctrl+Alt+K, so Kannu could not take it. Free it in that app, then turn this off and on."
            : "Works from any app. Off by default, because a shortcut that works everywhere takes those keys from every other app.";
        SkinDescription.Text = s.SkinPath is { } skin && File.Exists(skin)
            ? Path.GetFileName(skin)
            : "A picture behind the notch: PNG, JPEG, GIF or BMP, cropped to fit.";
        RemoveSkinButton.IsEnabled = s.SkinPath is not null;
        ScrimSlider.Value = s.SkinScrim;
        ScrimSlider.IsEnabled = s.SkinPath is not null;
        SmartAwakeToggle.IsChecked = s.CaffeinateSmart;
        ManualAwakeToggle.IsChecked = s.CaffeinateManual;
        ManualAwakeToggle.IsEnabled = !s.CaffeinateSmart;
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

    // ---- Traffic light and skin ----

    private void LightStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var style = StyleMinimal.IsChecked == true ? LightStyle.Minimal : LightStyle.Classic;
        _settings.Update(s => s with { LightStyle = style });
    }

    /// <summary>One row per state with the palette as round swatches; a colour another state uses is disabled.</summary>
    private void BuildColorRows(LightColors colors)
    {
        ColorRows.Children.Clear();
        foreach (var (slot, title, detail) in new[]
                 {
                     (LightSlot.Active, "Working", "The agent is thinking or running tools."),
                     (LightSlot.Awaiting, "Needs you", "The agent is waiting for your answer or approval."),
                     (LightSlot.Stopped, "Finished", "The agent finished or stopped."),
                 })
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32, 0, 24, 0) };
            text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Kannu.RowTitle") });
            text.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Kannu.RowDescription") });
            grid.Children.Add(text);

            var swatches = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
            foreach (var color in Enum.GetValues<PaletteColor>())
            {
                var chosen = colors[slot] == color;
                var taken = colors.IsTakenByOther(slot, color);
                var swatch = new Button
                {
                    Width = 22,
                    Height = 22,
                    Margin = new Thickness(3),
                    Cursor = taken ? null : System.Windows.Input.Cursors.Hand,
                    IsEnabled = !taken,
                    ToolTip = taken ? $"{color} (used by another light)" : color.ToString(),
                    Template = SwatchTemplate(KannuColors.BrushFor(color), chosen, taken),
                };
                var (s, c) = (slot, color);
                swatch.Click += (_, _) => _settings.Update(x => x with { LightColors = x.LightColors.With(s, c) });
                swatches.Children.Add(swatch);
            }
            Grid.SetColumn(swatches, 1);
            grid.Children.Add(swatches);
            ColorRows.Children.Add(grid);
        }
    }

    private ControlTemplate SwatchTemplate(Brush fill, bool chosen, bool taken)
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(chosen ? 2 : 0));
        ring.SetValue(Border.BorderBrushProperty, FindResource("Kannu.Text"));
        ring.SetValue(Border.PaddingProperty, new Thickness(chosen ? 2 : 0));
        var dot = new FrameworkElementFactory(typeof(Border));
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        dot.SetValue(Border.BackgroundProperty, fill);
        dot.SetValue(OpacityProperty, taken ? 0.25 : 1.0);
        ring.AppendChild(dot);
        return new ControlTemplate(typeof(Button)) { VisualTree = ring };
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e) =>
        _settings.Update(s => s with { LightColors = LightColors.Default });

    /// <summary>Copies the picture into Kannu's own folder, so moving or deleting the original does not break the skin.</summary>
    private void ChooseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath())!, "skins");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, $"skin-{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(dialog.FileName).ToLowerInvariant()}");
            File.Copy(dialog.FileName, copy);
            var old = _settings.Current.SkinPath;
            _settings.Update(s => s with { SkinPath = copy, SkinScrim = s.SkinPath is null ? 0.3 : s.SkinScrim });
            if (old is not null && old != copy && File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Kannu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSkin_Click(object sender, RoutedEventArgs e)
    {
        var old = _settings.Current.SkinPath;
        _settings.Update(s => s with { SkinPath = null, SkinScrim = 0 });
        try
        {
            if (old is not null && File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Scrim_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        var value = Math.Round(e.NewValue, 1);
        _settings.Update(s => s with { SkinScrim = value });
    }

    // ---- Agents ----

    private void Caffeinate_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s with
        {
            CaffeinateSmart = SmartAwakeToggle.IsChecked == true,
            CaffeinateManual = ManualAwakeToggle.IsChecked == true,
        });
    }

    private void BuildAgentRows()
    {
        AgentRows.Children.Clear();
        foreach (var provider in Enum.GetValues<AgentProvider>()) AgentRows.Children.Add(AgentRow(provider));
        BuildUsageRow();
    }

    /// <summary>Claude Code's plan limits on the notch's Usage tab, through Kannu's statusline.</summary>
    private void BuildUsageRow()
    {
        UsageRows.Children.Clear();
        var installed = HookSetup.UsageStatuslineInstalled;
        var present = installed || HookSetup.ToolIsPresent(AgentProvider.Claude);

        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Style = (Style)FindResource("Kannu.Icon"), Text = "\uE9D2", Margin = new Thickness(0, 0, 16, 0) });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = "Claude Code plan limits", Style = (Style)FindResource("Kannu.RowTitle") });
        text.Children.Add(new TextBlock
        {
            Text = installed
                ? "On. The notch's Usage tab shows your 5-hour and weekly limits. Your own statusline still shows in Claude Code."
                : "Show your 5-hour and weekly limits on the notch's Usage tab. Kannu adds a statusline to Claude Code that saves them; a statusline you already have keeps showing. Nothing is fetched from the internet.",
            Style = (Style)FindResource("Kannu.RowDescription"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = installed ? "Remove" : "Turn on",
            Style = (Style)FindResource(installed ? "Kannu.Button" : "Kannu.AccentButton"),
            IsEnabled = present,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) =>
        {
            try
            {
                if (installed) HookSetup.UninstallUsageStatusline();
                else HookSetup.InstallUsageStatusline();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HookInstallException)
            {
                MessageBox.Show(this, ex.Message, "Kannu", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            BuildUsageRow();
        };
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        UsageRows.Children.Add(new Border { Style = (Style)FindResource("Kannu.Card"), Child = grid });
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

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Diagnostics.LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Diagnostics.LogsDirectory}\"") { UseShellExecute = true });
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"kannu-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExt = ".zip",
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
            Diagnostics.ExportLogs(dialog.FileName);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Kannu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ViewTerms_Click(object sender, RoutedEventArgs e) => TermsWindow.ShowTerms(this);

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var url = ((FrameworkElement)sender).Tag switch
        {
            "license" => ReleaseInfo.RepositoryUrl + "/blob/main/LICENSE",
            "issue" => Diagnostics.NewIssueUrl(),
            "security" => ReleaseInfo.RepositoryUrl + "/security/advisories/new",
            _ => ReleaseInfo.RepositoryUrl,
        };
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
