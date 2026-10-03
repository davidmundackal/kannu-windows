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
using System.Text.Json;

namespace Kannu.Core;

/// <summary>
/// Hidden Unicode (macOS hook v34): text an agent can read but a person cannot see — Unicode tag
/// characters (ASCII smuggling), bytes hidden in variation selectors, right-to-left overrides on lines
/// with no right-to-left letters (Trojan Source), long zero-width runs. Works on code points, as the
/// Python original does, so counts and offsets match it for text outside the BMP.
/// </summary>
internal static class HiddenText
{
    internal const int MaxEntries = 3;
    private const long WindowMs = 600_000;
    private const int Budget = 4_000_000;

    internal readonly record struct Hit(string Kind, string Where, int Chars, string Preview);

    internal static readonly HashSet<string> SkipEvents =
        ["SessionStart", "SessionEnd", "Notification", "PermissionRequest", "beforeShellExecution", "beforeMCPExecution"];

    private static readonly HashSet<string> PostEvents =
        ["PostToolUse", "postToolUse", "PostToolUseFailure", "postToolUseFailure", "AfterTool"];

    private static readonly HashSet<string> PostSkipKeys = ["tool_input", "input", "arguments", "tool"];

    private static readonly Dictionary<string, string> WhereByKey = new()
    {
        ["tool_response"] = "tool_result", ["tool_output"] = "tool_result", ["result_json"] = "tool_result",
        ["output"] = "tool_result", ["error"] = "tool_result", ["error_message"] = "tool_result",
        ["prompt"] = "prompt",
        ["tool_input"] = "tool_input", ["input"] = "tool_input", ["arguments"] = "tool_input",
        ["tool"] = "tool_input", ["command"] = "tool_input", ["edits"] = "tool_input",
        ["last_assistant_message"] = "agent_reply", ["text"] = "agent_reply", ["prompt_response"] = "agent_reply",
    };

    internal static readonly Dictionary<string, int> KindRank = new()
    {
        ["tags"] = 4, ["variation_selectors"] = 3, ["bidi"] = 2, ["zero_width"] = 1,
    };

    internal static readonly Dictionary<string, int> WhereRank = new()
    {
        ["tool_result"] = 5, ["prompt"] = 4, ["tool_input"] = 3, ["agent_reply"] = 2, ["other"] = 1,
    };

    private static readonly Dictionary<string, string> KindWords = new()
    {
        ["tags"] = "Unicode tag", ["variation_selectors"] = "variation selector",
        ["bidi"] = "bidirectional control", ["zero_width"] = "zero-width",
    };

    private static readonly Dictionary<int, string> BidiNames = new()
    {
        [0x202A] = "LRE", [0x202B] = "RLE", [0x202C] = "PDF", [0x202D] = "LRO", [0x202E] = "RLO",
        [0x2066] = "LRI", [0x2067] = "RLI", [0x2068] = "FSI", [0x2069] = "PDI",
    };

    /// <summary>Events whose documented output carries context to the model, per provider. Never Stop.</summary>
    internal static readonly Dictionary<string, HashSet<string>> NoteEvents = new()
    {
        ["claude"] = ["PostToolUse", "PostToolUseFailure", "UserPromptSubmit", "PreToolUse"],
        ["vscode"] = ["PostToolUse"],
        ["codex"] = ["PostToolUse"],
        ["cursor"] = ["postToolUse"],
    };

    private static bool IsTag(int cp) => cp is >= 0xE0000 and <= 0xE007F;
    private static bool IsVs(int cp) => cp is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);
    private static bool IsBidi(int cp) => cp is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);
    private static bool IsBidiRtl(int cp) => cp is 0x202B or 0x202E or 0x2067;
    private static bool IsZw(int cp) => cp is (>= 0x200B and <= 0x200D) or (>= 0x2060 and <= 0x2064) or 0xFEFF;

    private static bool IsRtlLetter(int cp) =>
        cp is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFC)
            or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    private static bool IsFlagTag(int cp) => cp is (>= 0xE0030 and <= 0xE0039) or (>= 0xE0061 and <= 0xE007A);

    /// <summary>The prefilter: any code point of the four classes (an ASCII string never gets past the first test).</summary>
    private static bool Suspect(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < 0x80) continue;
            if (IsVs(c) || IsBidi(c) || IsZw(c)) return true;
            if (c == 0xDB40 && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, value[i + 1]);
                if (IsTag(cp) || IsVs(cp)) return true;
            }
        }
        return false;
    }

    /// <summary>Code points; a lone surrogate stands for itself, as in a Python str.</summary>
    private static int[] CodePoints(string text)
    {
        var list = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                list.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else
            {
                list.Add(text[i]);
            }
        }
        return [.. list];
    }

    private static void AppendPrintable(StringBuilder sb, IEnumerable<int> cps, int limit)
    {
        var added = 0;
        foreach (var cp in cps)
        {
            if (added >= limit) break;
            if (cp is >= 32 and < 127)
            {
                sb.Append((char)cp);
                added++;
            }
        }
    }

    /// <summary>UTS #51 flag tag sequences (black flag, optional VS16, 3–7 tag letters/digits, cancel tag) removed.</summary>
    private static int[] WithoutFlags(int[] cps)
    {
        if (Array.IndexOf(cps, 0x1F3F4) < 0) return cps;
        var out_ = new List<int>(cps.Length);
        for (var i = 0; i < cps.Length; i++)
        {
            if (cps[i] == 0x1F3F4)
            {
                var j = i + 1;
                if (j < cps.Length && cps[j] == 0xFE0F) j++;
                var start = j;
                while (j < cps.Length && IsFlagTag(cps[j])) j++;
                var run = j - start;
                if (run is >= 3 and <= 7 && j < cps.Length && cps[j] == 0xE007F)
                {
                    i = j;
                    continue;
                }
            }
            out_.Add(cps[i]);
        }
        return [.. out_];
    }

    /// <summary>Maximal runs of <paramref name="member"/> at least <paramref name="min"/> long, as (start, length).</summary>
    private static IEnumerable<(int Start, int Length)> Runs(int[] cps, Func<int, bool> member, int min)
    {
        var i = 0;
        while (i < cps.Length)
        {
            if (!member(cps[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < cps.Length && member(cps[i])) i++;
            if (i - start >= min) yield return (start, i - start);
        }
    }

    /// <summary>Butler (2025): byte b is U+FE00+b (b &lt; 16) or U+E0100+(b-16).</summary>
    private static string VsText(int[] cps, int start, int length)
    {
        var count = Math.Min(length, 4096);
        var bytes = new byte[count];
        for (var k = 0; k < count; k++)
        {
            var cp = cps[start + k];
            bytes[k] = (byte)(cp < 0xFE10 ? cp - 0xFE00 : cp - 0xE0100 + 16);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Best effort for two-symbol binary runs (Rehberger's Sneaky Bits); "" when it does not read.</summary>
    private static string ZwBits(int[] cps, int start, int length)
    {
        length = Math.Min(length, 1280);
        var symbols = new SortedSet<int>();
        for (var k = 0; k < length; k++) symbols.Add(cps[start + k]);
        if (symbols.Count != 2 || length < 32) return "";
        length -= length % 8;
        var best = "";
        foreach (var one in symbols)
        {
            var data = new List<int>(length / 8);
            for (var i = 0; i < length; i += 8)
            {
                var value = 0;
                for (var k = i; k < i + 8; k++) value = value * 2 + (cps[start + k] == one ? 1 : 0);
                data.Add(value);
            }
            var sb = new StringBuilder();
            AppendPrintable(sb, data, 160);
            var text = sb.ToString();
            if (text.Length * 10 >= data.Count * 9 && text.Length > best.Length) best = text;
        }
        return best;
    }

    private static string BidiPreview(int[] cps, int lo, int hi)
    {
        var sb = new StringBuilder();
        for (var k = lo; k < hi; k++)
        {
            if (BidiNames.TryGetValue(cps[k], out var name)) sb.Append('<').Append(name).Append('>');
            else if (cps[k] is >= 32 and < 127) sb.Append((char)cps[k]);
        }
        var text = sb.ToString().Trim(' ');
        return text.Length > 160 ? text[..160] : text;
    }

    /// <summary>[(kind, chars, preview)] for one string that tripped the prefilter.</summary>
    internal static List<(string Kind, int Chars, string Preview)> Classify(string text)
    {
        var found = new List<(string, int, string)>();
        var cps = CodePoints(text);

        var tagCps = WithoutFlags(cps);
        var count = 0;
        var preview = new StringBuilder();
        foreach (var (start, length) in Runs(tagCps, IsTag, 1))
        {
            count += length;
            if (preview.Length < 160)
            {
                AppendPrintable(preview, tagCps.Skip(start).Take(Math.Min(length, 400)).Select(cp => cp - 0xE0000), 160 - preview.Length);
            }
        }
        if (count > 0) found.Add(("tags", count, preview.ToString()));

        count = 0;
        preview.Clear();
        foreach (var (start, length) in Runs(cps, IsVs, 4))
        {
            count += length;
            if (preview.Length < 160)
            {
                AppendPrintable(preview, VsText(cps, start, length).Select(ch => (int)ch), 160 - preview.Length);
            }
        }
        if (count > 0) found.Add(("variation_selectors", count, preview.ToString()));

        count = 0;
        var bidiPreview = "";
        int lineLo = 0, lineHi = -1;
        var lineOk = false;
        for (var i = 0; i < cps.Length; i++)
        {
            if (!IsBidiRtl(cps[i])) continue;
            if (i > lineHi)
            {
                lineLo = Array.LastIndexOf(cps, 10, i) + 1;
                var end = Array.IndexOf(cps, 10, i);
                lineHi = end < 0 ? cps.Length : end;
                lineOk = true;
                for (var k = lineLo; k < lineHi; k++)
                {
                    if (IsRtlLetter(cps[k]))
                    {
                        lineOk = false;
                        break;
                    }
                }
            }
            if (!lineOk) continue;
            count++;
            if (bidiPreview.Length == 0) bidiPreview = BidiPreview(cps, Math.Max(lineLo, i - 60), Math.Min(lineHi, i + 100));
            if (count >= 1000) break;
        }
        if (count > 0) found.Add(("bidi", count, bidiPreview));

        count = 0;
        var zwPreview = "";
        foreach (var (start, length) in Runs(cps, IsZw, 10))
        {
            count += length;
            if (zwPreview.Length == 0) zwPreview = ZwBits(cps, start, length);
        }
        if (count > 0) found.Add(("zero_width", count, zwPreview));
        return found;
    }

    private static (bool, int, int, int) Rank(Hit hit) =>
        (hit.Kind != "bidi" && hit.Preview.Length >= 4, KindRank[hit.Kind], WhereRank[hit.Where], hit.Chars);

    private static bool Better(Hit a, Hit b) => Rank(a).CompareTo(Rank(b)) > 0;

    /// <summary>One best hit per event, from the decoded payload, or null.</summary>
    internal static Hit? Scan(JsonElement payload, string hookEvent)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        var skip = PostEvents.Contains(hookEvent);
        var stack = new List<(JsonElement Value, string Where, int Depth)>();
        foreach (var property in payload.EnumerateObject())
        {
            if (skip && PostSkipKeys.Contains(property.Name)) continue;
            stack.Add((property.Value, WhereByKey.GetValueOrDefault(property.Name, "other"), 0));
        }
        Hit? best = null;
        var budget = Budget;
        var nodes = 0;
        while (stack.Count > 0 && budget > 0 && nodes < 50000)
        {
            var (value, where, depth) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            nodes++;
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (depth < 8) stack.AddRange(value.EnumerateObject().Select(p => (p.Value, where, depth + 1)));
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (depth < 8) stack.AddRange(value.EnumerateArray().Select(item => (item, where, depth + 1)));
                continue;
            }
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrEmpty(text)) continue;
            budget -= text.Length;
            var suspect = Suspect(text);
            // Cursor hands tool output over as a JSON string: judge what it decodes to, which also turns
            // an escaped surrogate pair into the real character.
            if ((suspect || text.Contains("\\u", StringComparison.Ordinal)) && depth < 8 && text.Length <= 1_000_000
                && LooksLikeJson(text) && TryParseContainer(text) is { } inner)
            {
                stack.Add((inner, where, depth + 1));
                continue;
            }
            if (!suspect) continue;
            foreach (var (kind, chars, preview) in Classify(text))
            {
                var hit = new Hit(kind, where, chars, preview);
                if (best is null || Better(hit, best.Value)) best = hit;
            }
        }
        return best;
    }

    internal static bool LooksLikeJson(string text)
    {
        var head = (text.Length > 64 ? text[..64] : text).TrimStart();
        return head.Length > 0 && head[0] is '{' or '[';
    }

    internal static JsonElement? TryParseContainer(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for a new sighting; false when it is the same tool call seen again (parallel PreToolUse
    /// groups, PostToolUse after PreToolUse).
    /// </summary>
    internal static bool Record(List<HiddenTextEntry> entries, Hit hit, long nowMs, string tool, string toolUseId)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Kind != hit.Kind) continue;
            var sameCall = toolUseId.Length > 0 && entry.ToolUseId == toolUseId;
            var samePlace = entry.Where == hit.Where
                            && (nowMs - entry.LastTs <= WindowMs || (hit.Preview.Length > 0 && hit.Preview == entry.Preview));
            if (!sameCall && !samePlace) continue;
            if (!sameCall) entry.Events = Math.Min(entry.Events + 1, 999);
            entry.Chars = Math.Max(entry.Chars, Math.Min(hit.Chars, 999999));
            if (hit.Preview.Length > entry.Preview.Length) entry.Preview = hit.Preview;
            entry.LastTs = nowMs;
            if (toolUseId.Length > 0) entry.ToolUseId = toolUseId;
            return !sameCall;
        }
        entries.Add(new HiddenTextEntry
        {
            Kind = hit.Kind,
            Where = hit.Where,
            Tool = hit.Where is "tool_result" or "tool_input" ? tool : "",
            Chars = Math.Min(hit.Chars, 999999),
            Events = 1,
            Preview = hit.Preview,
            FirstTs = nowMs,
            LastTs = nowMs,
            ToolUseId = toolUseId,
        });
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
        return true;
    }

    /// <summary>(context for the model, line for the user). Factual; never the decoded text, nothing imperative.</summary>
    internal static (string Agent, string User) Notes(Hit hit, string tool)
    {
        var what = hit.Chars + " invisible " + KindWords[hit.Kind] + (hit.Chars == 1 ? " character" : " characters");
        string place, seen;
        switch (hit.Where)
        {
            case "tool_result":
                (place, seen) = tool.Length > 0
                    ? ("the result of this " + tool + " call", "a " + tool + " result")
                    : ("this tool result", "a tool result");
                break;
            case "tool_input":
                (place, seen) = tool.Length > 0
                    ? ("the input of this " + tool + " call", "the input of a " + tool + " call")
                    : ("the input of this tool call", "a tool call");
                break;
            case "prompt":
                (place, seen) = ("this prompt", "your prompt");
                break;
            default:
                (place, seen) = ("this hook input", "hook input");
                break;
        }
        var effect = hit.Kind == "bidi"
            ? "Characters like these change the order in which text is displayed, so what the user sees can differ from the text itself."
            : "Characters like these do not render, so the user cannot see the text they encode.";
        return ("Kannu, a local monitor on this PC, found " + what + " in " + place + ". " + effect,
            "Kannu found " + what + " in " + seen + ". Details are in Kannu's security findings.");
    }
}
