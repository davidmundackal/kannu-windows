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
/// Command lines as word lists, for the sensitive-file and policy checks. The macOS hook uses Python's
/// <c>shlex</c> (POSIX mode, punctuation split); on Windows the line may be Git Bash, PowerShell or cmd,
/// so this is a small tokenizer with these deliberate differences:
/// <list type="bullet">
/// <item>A backslash is a literal character, never an escape: it is the Windows path separator, and
/// <c>C:\Users\me\.ssh\id_rsa</c> must survive tokenizing.</item>
/// <item>Single and double quotes group as in POSIX (quotes removed, adjacent parts joined); an
/// unterminated quote falls back to a plain whitespace split, as the macOS code does on a shlex error.</item>
/// <item><c>( ) ; &lt; &gt; | &amp;</c> outside quotes form their own tokens; a token made only of
/// <c>; &amp; | ( ) { } !</c> separates simple commands. An unquoted <c>#</c> ends the line, as shlex's
/// comment handling (and PowerShell's) does.</item>
/// <item>Besides <c>sh -c "…"</c>, <c>cmd /c …</c>, <c>powershell -Command …</c> / <c>pwsh -c …</c> and
/// <c>-EncodedCommand</c> are opened, two levels deep.</item>
/// </list>
/// </summary>
internal static class ShellWords
{
    private const string PunctuationChars = "();<>|&";
    private const string SeparatorChars = ";&|(){}!";

    internal static readonly HashSet<string> Wrappers =
        ["sudo", "env", "command", "exec", "nohup", "time", "nice", "doas", "builtin", "caffeinate", "call"];

    private static readonly HashSet<string> PosixShells = ["bash", "sh", "zsh", "dash", "ksh"];
    private static readonly HashSet<string> PosixCommandFlags = ["-c", "-lc", "-ic", "-lic"];

    internal readonly record struct Segment(List<string> Words, bool IsShellWrapper);

    /// <summary>Words of one line; null when a quote is left open.</summary>
    internal static List<string>? Tokenize(string line)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c is '\'' or '"')
            {
                var close = line.IndexOf(c, i + 1);
                if (close < 0) return null;
                current.Append(line, i + 1, close - i - 1);
                inWord = true;
                i = close;
            }
            else if (c == '#')
            {
                break;
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else if (PunctuationChars.Contains(c))
            {
                Flush();
                var start = i;
                while (i + 1 < line.Length && PunctuationChars.Contains(line[i + 1])) i++;
                words.Add(line.Substring(start, i - start + 1));
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }
        Flush();
        return words;

        void Flush()
        {
            if (inWord) words.Add(current.ToString());
            current.Clear();
            inWord = false;
        }
    }

    private static bool IsSeparator(string word) => word.Length > 0 && word.All(c => SeparatorChars.Contains(c));

    /// <summary>Raw simple commands (word lists) of a command string: lines, then separators.</summary>
    internal static List<List<string>> RawSegments(string command)
    {
        var segments = new List<List<string>>();
        var lines = command.Split('\n');
        foreach (var line in lines.Take(200))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var words = Tokenize(line) ?? HookSecurity.PySplit(line);
            var current = new List<string>();
            foreach (var word in words.Append(";"))
            {
                if (IsSeparator(word))
                {
                    if (current.Count > 0) segments.Add(current);
                    current = [];
                }
                else
                {
                    current.Add(word);
                }
            }
            if (segments.Count > 200) break;
        }
        return segments;
    }

    /// <summary>The part after the last / or \.</summary>
    internal static string Basename(string word)
    {
        var cut = word.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? word : word[(cut + 1)..];
    }

    /// <summary>A command name for lookups: basename, lowercase, without <c>.exe</c> (Windows names are case-insensitive).</summary>
    internal static string CommandName(string word)
    {
        var name = Basename(word).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>Wrappers (<c>sudo</c>, <c>env</c>, <c>call</c>…, with their flags) and <c>VAR=x</c> assignments skipped.</summary>
    internal static List<string> StripWrappers(List<string> words)
    {
        var i = 0;
        while (i < words.Count)
        {
            var word = words[i];
            if (Wrappers.Contains(CommandName(word)) || (i > 0 && Wrappers.Contains(CommandName(words[i - 1])) && word.StartsWith('-')))
            {
                i++;
            }
            else if (word.IndexOf('=') is var eq and > 0 && word[..eq].Replace("_", "") is { Length: > 0 } key
                     && key.All(char.IsLetterOrDigit))
            {
                i++;
            }
            else
            {
                break;
            }
        }
        return words.GetRange(i, words.Count - i);
    }

    /// <summary>
    /// The inline command of a shell wrapper: <c>bash -c X</c> (X), <c>cmd /c X Y</c> ("X Y"),
    /// <c>powershell -Command X Y</c> ("X Y") or <c>pwsh -EncodedCommand B64</c> (decoded). Null otherwise.
    /// </summary>
    internal static string? ShellInner(List<string> words)
    {
        if (words.Count < 2) return null;
        var name = CommandName(words[0]);
        if (PosixShells.Contains(name))
        {
            return words.Count >= 3 && PosixCommandFlags.Contains(words[1]) ? words[2] : null;
        }
        if (name == "cmd")
        {
            for (var j = 1; j < words.Count; j++)
            {
                var flag = words[j].ToLowerInvariant();
                if (flag is "/c" or "/k" or "/r") return string.Join(" ", words.Skip(j + 1));
                if (!flag.StartsWith('/')) return null;
            }
            return null;
        }
        if (name is "powershell" or "pwsh")
        {
            for (var j = 1; j < words.Count; j++)
            {
                var flag = words[j].ToLowerInvariant();
                if (flag.Length < 2 || flag[0] is not ('-' or '/')) continue;
                var option = "-" + flag[1..];
                // Any unambiguous prefix of -Command (-c, -co, -com…), of -EncodedCommand (-e, -ec, -enc…).
                if ("-command".StartsWith(option, StringComparison.Ordinal)) return string.Join(" ", words.Skip(j + 1));
                if (option is "-e" or "-ec" || (option.Length >= 3 && "-encodedcommand".StartsWith(option, StringComparison.Ordinal)))
                {
                    return j + 1 < words.Count ? DecodePowerShell(words[j + 1]) : null;
                }
                if (option is "-f" or "-file") return null;
            }
            return null;
        }
        return null;
    }

    private static string? DecodePowerShell(string base64)
    {
        try
        {
            return base64.Length > 400_000 ? null : Encoding.Unicode.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Simple commands as word lists, wrappers stripped and the first word reduced to its basename,
    /// with shell wrappers opened (depth 2). A wrapper's own segment comes first, flagged. Bounded to 400.
    /// </summary>
    internal static List<Segment> Segments(string command, int depth = 0)
    {
        var out_ = new List<Segment>();
        if (depth > 2) return out_;
        foreach (var raw in RawSegments(command).Take(200))
        {
            AddSegment(out_, raw, depth);
            if (out_.Count >= 400) break;
        }
        return out_.Count > 400 ? out_.GetRange(0, 400) : out_;
    }

    /// <summary>One already-split word list (an argv), wrappers stripped and shell wrappers opened.</summary>
    internal static void AddSegment(List<Segment> out_, List<string> raw, int depth)
    {
        var words = StripWrappers(raw);
        if (words.Count == 0) return;
        words[0] = Basename(words[0]);
        var inner = ShellInner(words);
        out_.Add(new Segment(words, inner is not null));
        if (inner is not null) out_.AddRange(Segments(inner, depth + 1));
    }
}
