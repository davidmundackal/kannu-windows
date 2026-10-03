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
using System.IO;
using System.Linq;
using System.Windows;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Offered once at launch when the last run crashed or froze (macOS Kannu offers its crash and hang
/// reports the same way). Shows exactly what would be shared, already scrubbed of the user name, the
/// PC name and the profile folder; nothing is sent unless the user opens the GitHub issue.
/// </summary>
public partial class ProblemReportWindow : Window
{
    private readonly string _path;
    private readonly ProblemReportFile _report;

    private ProblemReportWindow(string path, ProblemReportFile report, string text)
    {
        InitializeComponent();
        ThemeManager.FollowTitleBar(this);
        _path = path;
        _report = report;
        ReportText.Text = text;
        if (report.Kind == ProblemKind.Crash)
        {
            Heading.Text = "Kannu quit unexpectedly";
            Explanation.Text = "Sending this report helps fix it. Only what is shown below is shared, and only if you open the GitHub issue.";
        }
        else
        {
            Heading.Text = "Kannu stopped responding";
            Explanation.Text = "Kannu's window froze for a few seconds last time. Sending this report helps fix it. Only what is shown below is shared, and only if you open the GitHub issue.";
        }
    }

    /// <summary>The newest report not offered before, if any; remembers it so it is offered once.</summary>
    internal static void OfferIfAny(SettingsStore settings)
    {
        try
        {
            if (!Directory.Exists(Diagnostics.LogsDirectory)) return;
            var names = Directory.EnumerateFiles(Diagnostics.LogsDirectory).Select(Path.GetFileName).OfType<string>();
            if (ProblemReports.ToOffer(names, settings.Current.LastOfferedReport) is not { } report) return;
            settings.Update(s => s with { LastOfferedReport = report.FileName });
            var path = Path.Combine(Diagnostics.LogsDirectory, report.FileName);
            new ProblemReportWindow(path, report, File.ReadAllText(path)).Show();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Error("Could not offer the last problem report", e);
        }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        var title = _report.Kind == ProblemKind.Crash ? "Crash" : "Freeze";
        var body = $"**What were you doing when it happened?**\n\n\n**Report**\n\n```\n{ReportText.Text.TrimEnd()}\n```\n";
        Process.Start(new ProcessStartInfo(ProblemReports.IssueUrl(ReleaseInfo.RepositoryUrl, $"{title}: Kannu for Windows {ReleaseInfo.Version}", body))
        {
            UseShellExecute = true,
        });
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(ReportText.Text);

    private void ShowInFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
