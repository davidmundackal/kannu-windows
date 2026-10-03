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
/// The hook's in-process security checks, ported from the macOS hook script (v34 hidden text, v35
/// secrets and sensitive files, v42 agent policy). Everything is local and model-free; findings go
/// into the session's status file (<see cref="StatusRecord.HiddenText"/>, <see cref="StatusRecord.Secrets"/>,
/// <see cref="StatusRecord.SensitivePaths"/>, <see cref="StatusRecord.Policy"/>) for Kannu to show.
/// The marker files below live in the status directory; the app creates and deletes them from Settings.
/// </summary>
public static class HookSecurity
{
    /// <summary>Present: the hidden-Unicode check is off and its list is dropped on the next write.</summary>
    public const string HiddenTextOffMarker = ".kannu-hidden-text-off";

    /// <summary>Present: a NEW hidden-text sighting is also told to the agent (and to the user, on Claude Code).</summary>
    public const string HiddenTextWarnMarker = ".kannu-hidden-text-warn-agent";

    /// <summary>Present: the secret check is off and its list is dropped on the next write.</summary>
    public const string SecretsOffMarker = ".kannu-secrets-off";

    /// <summary>Present: the sensitive-file check is off and its list is dropped on the next write.</summary>
    public const string SensitivePathsOffMarker = ".kannu-sensitive-paths-off";

    /// <summary>
    /// Present: a policy match is refused on hosts whose hook contract has a deny (Claude Code
    /// PreToolUse, Cursor's pre events). Absent: matches are only recorded.
    /// </summary>
    public const string PolicyEnforceMarker = ".kannu-policy-enforce";

    internal const int PolicyMaxBytes = 65536;
    internal const int PolicyMaxRules = 200;
    internal const int PolicyMaxLength = 200;

    /// <summary>A sighting's first_ts below this (2001) is not a real time: the entry is dropped.</summary>
    internal const long PlausibleMs = 1_000_000_000_000;

    /// <summary>The user-authored policy file: <c>%USERPROFILE%\.kannu\agent-policy.json</c>.</summary>
    public static string PolicyPath(string home) => Path.Combine(home, ".kannu", "agent-policy.json");

    /// <summary>Every category id a sensitive-path entry can carry (the macOS ids).</summary>
    public static IReadOnlyList<string> SensitiveCategories => SensitivePaths.Categories;

    /// <summary>
    /// Reads and validates the policy file exactly as strictly as the hook does: anything malformed,
    /// oversized, a link/reparse point, or not version 1 means no policy (null) — the hook never fails
    /// closed on its own configuration. Never throws. Unlike the macOS reader, a UTF-8 BOM is accepted
    /// (Windows editors write one); invalid UTF-8 still means no policy.
    /// </summary>
    public static AgentPolicy? LoadPolicy(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || info.Length > PolicyMaxBytes)
            {
                return null;
            }
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > PolicyMaxBytes) return null;
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return ParsePolicy(doc.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static AgentPolicy? ParsePolicy(JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object) return null;
        // `True == 1` in Python, so the macOS check rejects a boolean explicitly; 1.0 == 1 passes there.
        if (!doc.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetDouble(out var v) || v != 1)
        {
            return null;
        }
        if (!doc.TryGetProperty("block", out var block) || block.ValueKind != JsonValueKind.Array
            || block.GetArrayLength() > PolicyMaxRules)
        {
            return null;
        }
        var rules = new List<PolicyRule>();
        foreach (var rule in block.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) return null;
            var reason = "";
            if (rule.TryGetProperty("reason", out var r) && r.ValueKind != JsonValueKind.Null)
            {
                // JSON null is an absent reason, as it is for "command" and "tool".
                if (r.ValueKind != JsonValueKind.String) return null;
                reason = r.GetString()!;
                if (reason.Length > 0 && !IsPolicyText(reason)) return null;
            }
            var command = rule.TryGetProperty("command", out var c) && c.ValueKind != JsonValueKind.Null ? c : default;
            var tool = rule.TryGetProperty("tool", out var t) && t.ValueKind != JsonValueKind.Null ? t : default;
            if (command.ValueKind != JsonValueKind.Undefined)
            {
                if (tool.ValueKind != JsonValueKind.Undefined || command.ValueKind != JsonValueKind.String) return null;
                var value = command.GetString()!;
                var words = PySplit(value);
                if (!IsPolicyText(value) || words.Count == 0) return null;
                rules.Add(new PolicyRule("command", value, reason, words));
            }
            else if (tool.ValueKind != JsonValueKind.Undefined)
            {
                if (tool.ValueKind != JsonValueKind.String) return null;
                var value = tool.GetString()!;
                if (!IsPolicyText(value) || value.Trim() != value || value.Contains(' ')) return null;
                rules.Add(new PolicyRule("tool", value, reason, [value]));
            }
            else
            {
                return null;
            }
        }
        return new AgentPolicy(rules);
    }

    private static bool IsPolicyText(string value) =>
        value.Length is > 0 and <= PolicyMaxLength && value.All(ch => ch >= 32);

    /// <summary>Python's <c>str.split()</c>: runs of whitespace separate, none empty.</summary>
    internal static List<string> PySplit(string value) =>
        [.. value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>The deny reason the agent and the transcript see.</summary>
    internal static string PolicyDenial(string matched, string reason)
    {
        var text = "Kannu policy: \"" + matched + "\" is blocked on this PC by the user's agent policy. ";
        if (reason.Length > 0) text += reason.Trim() + " ";
        return text + "Ask the user before trying another way.";
    }

    internal static bool MarkerExists(string statusDirectory, string marker)
    {
        try
        {
            return File.Exists(Path.Combine(statusDirectory, marker));
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A validated <c>agent-policy.json</c>: the rules in file order.</summary>
public sealed class AgentPolicy(IReadOnlyList<PolicyRule> rules)
{
    public IReadOnlyList<PolicyRule> Rules { get; } = rules;
}

/// <summary>
/// One block rule. <see cref="Kind"/> is "command" (a word prefix of a simple command) or "tool" (an
/// exact tool name); <see cref="Value"/> is the text as written; <see cref="Reason"/> may be empty.
/// </summary>
public sealed class PolicyRule(string kind, string value, string reason, IReadOnlyList<string> words)
{
    public string Kind { get; } = kind;

    public string Value { get; } = value;

    public string Reason { get; } = reason;

    /// <summary>For a command rule, the words of <see cref="Value"/>; for a tool rule, the name alone.</summary>
    public IReadOnlyList<string> Words { get; } = words;
}

/// <summary>Sanitisers shared by every check, ports of the macOS helpers of the same names.</summary>
internal static class SecurityText
{
    /// <summary><c>printable_ascii</c>: only U+0020..U+007E, at most <paramref name="limit"/>.</summary>
    public static string PrintableAscii(string? chars, int limit = 160)
    {
        if (string.IsNullOrEmpty(chars)) return "";
        var sb = new StringBuilder();
        foreach (var ch in chars)
        {
            if (ch >= 32 && ch < 127)
            {
                sb.Append(ch);
                if (sb.Length >= limit) break;
            }
        }
        return sb.ToString();
    }

    /// <summary><c>ht_token</c>: [A-Za-z0-9_.:-] only, cut before filtering, at most <paramref name="limit"/>.</summary>
    public static string Token(string? value, int limit = 64)
    {
        if (value is null) return "";
        var cut = value.Length > limit * 4 ? value[..(limit * 4)] : value;
        var sb = new StringBuilder();
        foreach (var ch in cut)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or ':' or '-')
            {
                sb.Append(ch);
                if (sb.Length >= limit) break;
            }
        }
        return sb.ToString();
    }

    /// <summary><c>ht_int</c>: a non-negative JSON integer, else <paramref name="fallback"/>.</summary>
    public static long Int(JsonElement obj, string key, long fallback = 0) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0
            ? n : fallback;

    public static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool IsTrue(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The last <paramref name="max"/> object items of a JSON array (the macOS <c>[-N:]</c>, then the type filter).</summary>
    public static IEnumerable<JsonElement> LastObjects(JsonElement root, string key, int max)
    {
        if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        var count = list.GetArrayLength();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (index++ < count - max) continue;
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }

    /// <summary>A JSON string literal as Python's <c>json.dumps</c> writes it (ensure_ascii).</summary>
    public static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch > 0x7E) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
