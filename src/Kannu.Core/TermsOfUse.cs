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

using System.Text;

namespace Kannu.Core;

/// <summary>
/// The Terms of Use (<c>TERMS.md</c>, bundled with the app). Nothing starts until the current version
/// is accepted, as on macOS: raising <see cref="CurrentVersion"/> asks everyone again, so raise it only
/// when a change matters, and update the "Version" line in <c>TERMS.md</c> with it.
/// </summary>
public static class TermsOfUse
{
    public const int CurrentVersion = 1;

    public static bool NeedsAcceptance(AppSettings settings) => (settings.TermsAcceptedVersion ?? 0) < CurrentVersion;

    public static AppSettings Accepted(AppSettings settings, DateTimeOffset at) => settings with
    {
        TermsAcceptedVersion = CurrentVersion,
        TermsAcceptedAt = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };
}

/// <summary>
/// Just enough Markdown to show <c>TERMS.md</c>: <c>#</c> headings, paragraphs, <c>-</c> bullets and
/// <c>**bold**</c>. Anything else is shown as plain text, never dropped.
/// </summary>
public static class MarkdownLite
{
    public sealed record Span(string Text, bool Bold);

    public abstract record Block(IReadOnlyList<Span> Spans);

    public sealed record Heading(int Level, IReadOnlyList<Span> Spans) : Block(Spans);

    public sealed record Paragraph(IReadOnlyList<Span> Spans) : Block(Spans);

    public sealed record Bullet(IReadOnlyList<Span> Spans) : Block(Spans);

    public static IReadOnlyList<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new Paragraph(Spans(paragraph.ToString())));
            paragraph.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            var hashes = line.TakeWhile(c => c == '#').Count();
            if (hashes is > 0 and <= 6 && line.Length > hashes && line[hashes] == ' ')
            {
                FlushParagraph();
                blocks.Add(new Heading(hashes, Spans(line[(hashes + 1)..].Trim())));
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                FlushParagraph();
                blocks.Add(new Bullet(Spans(line[2..].Trim())));
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }
        }
        FlushParagraph();
        return blocks;
    }

    /// <summary>Splits on <c>**</c>; an unmatched marker is kept as text.</summary>
    public static IReadOnlyList<Span> Spans(string text)
    {
        var parts = text.Split("**");
        if (parts.Length % 2 == 0) return [new Span(text, false)];
        var spans = new List<Span>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) spans.Add(new Span(parts[i], i % 2 == 1));
        }
        return spans;
    }
}
