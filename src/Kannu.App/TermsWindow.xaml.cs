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


using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The Terms of Use: asked for before anything else starts (Accept / Decline), and shown again from
/// Settings › About (Close only). The text is the bundled <c>TERMS.md</c>.
/// </summary>
public partial class TermsWindow : Window
{
    private TermsWindow(bool asking)
    {
        InitializeComponent();
        ThemeManager.FollowTitleBar(this);
        Document.Document = Render(ReadTerms());
        if (!asking)
        {
            Heading.Text = "Terms of Use";
            Subheading.Text = $"Version {TermsOfUse.CurrentVersion}";
            DeclineButton.Visibility = Visibility.Collapsed;
            AcceptButton.Content = "Close";
        }
    }

    /// <returns>True when the user accepted.</returns>
    public static bool AskForAcceptance() => new TermsWindow(asking: true).ShowDialog() == true;

    public static void ShowTerms(Window? owner)
    {
        var window = new TermsWindow(asking: false) { Owner = owner };
        if (owner is not null) window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowDialog();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string ReadTerms()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TERMS.md");
        if (stream is null) return "The Terms of Use are missing from this build.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private FlowDocument Render(string markdown)
    {
        var text = (Brush)FindResource("Kannu.Text");
        var document = new FlowDocument
        {
            FontFamily = (FontFamily)FindResource("Kannu.Font.Text"),
            FontSize = 14,
            PagePadding = new Thickness(20, 16, 20, 16),
            Foreground = text,
            Background = Brushes.Transparent,
        };
        List? list = null;
        foreach (var block in MarkdownLite.Parse(markdown))
        {
            if (block is MarkdownLite.Bullet bullet)
            {
                if (list is null)
                {
                    list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 12) };
                    document.Blocks.Add(list);
                }
                list.ListItems.Add(new ListItem(Paragraph(bullet.Spans, new Thickness(0, 0, 0, 4))));
                continue;
            }
            list = null;
            var paragraph = Paragraph(block.Spans, new Thickness(0, 0, 0, 12));
            if (block is MarkdownLite.Heading heading)
            {
                paragraph.FontFamily = (FontFamily)FindResource("Kannu.Font.Display");
                paragraph.FontSize = heading.Level == 1 ? 22 : 18;
                paragraph.FontWeight = FontWeights.SemiBold;
            }
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private static Paragraph Paragraph(IReadOnlyList<MarkdownLite.Span> spans, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin, LineHeight = 21 };
        foreach (var span in spans)
        {
            paragraph.Inlines.Add(new Run(span.Text) { FontWeight = span.Bold ? FontWeights.SemiBold : FontWeights.Normal });
        }
        return paragraph;
    }
}
