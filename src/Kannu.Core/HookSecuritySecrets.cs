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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kannu.Core;

/// <summary>
/// Secrets (macOS hook v35): API keys and private keys in a prompt or in what the agent hands a tool —
/// never in a tool's result. Only the kind, the vendor prefix, the length and a 12-hex SHA-256
/// fingerprint are kept, never the secret.
/// </summary>
internal static class Secrets
{
    internal const int MaxEntries = 5;
    private const int Budget = 2_000_000;

    internal readonly record struct Hit(string Kind, string Where, string Prefix, int Length, string Fp);

    internal static readonly HashSet<string> PromptEvents = ["UserPromptSubmit", "beforeSubmitPrompt", "BeforeAgent"];

    internal static readonly HashSet<string> ToolEvents =
        ["PreToolUse", "preToolUse", "beforeShellExecution", "beforeMCPExecution", "BeforeTool"];

    private sealed record Pattern(string Kind, string[] Anchors, Regex Regex);

    // Every pattern starts with its literal; the "no word character before" edge is checked in code.
    private static readonly Pattern[] Patterns =
    [
        P("private_key", ["PRIVATE KEY"], "-----BEGIN (?:[A-Z0-9]{2,12} ){0,2}PRIVATE KEY(?: BLOCK)?-----"),
        P("anthropic_key", ["sk-ant-"], "sk-ant-[A-Za-z0-9_-]{32,300}"),
        P("openai_key", ["sk-"], "sk-(?!ant-)(?:proj-|svcacct-|admin-)?[A-Za-z0-9_-]{32,300}"),
        P("aws_access_key", ["AKIA", "ASIA"], "(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])"),
        P("github_token", ["ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_"],
            "(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{50,255})(?![A-Za-z0-9_])"),
        P("gitlab_token", ["glpat-"], "glpat-[A-Za-z0-9_-]{20,64}"),
        P("slack_token", ["xox"], "xox[abprs]-[A-Za-z0-9-]{10,250}"),
        P("stripe_key", ["k_live_"], "(?:sk|rk)_live_[A-Za-z0-9]{20,250}"),
        P("google_api_key", ["AIza"], "AIza[A-Za-z0-9_-]{35}(?![A-Za-z0-9_-])"),
        P("npm_token", ["npm_"], "npm_[A-Za-z0-9]{36}(?![A-Za-z0-9])"),
        P("huggingface_token", ["hf_"], "hf_[A-Za-z0-9]{34,64}(?![A-Za-z0-9])"),
    ];

    private static Pattern P(string kind, string[] anchors, string pattern) =>
        new(kind, anchors, new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));

    internal static readonly HashSet<string> Kinds = [.. Patterns.Select(p => p.Kind)];

    private static readonly string[] LiteralPrefixes =
        ["github_pat_", "sk-svcacct-", "sk-admin-", "sk-proj-", "sk-ant-", "sk_live_", "rk_live_", "glpat-", "npm_", "hf_"];

    private static readonly string[] PlaceholderWords = ["EXAMPLE", "XXXXXXXX", "PLACEHOLDER", "REDACTED", "YOUR_", "DUMMY"];

    // Python's encode("utf-8", "replace") writes "?" for a lone surrogate.
    private static readonly Encoding FingerprintEncoding =
        Encoding.GetEncoding("utf-8", new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);

    private static bool IsWordChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';

    internal static bool IsFingerprint(string fp) =>
        fp.Length == 12 && fp.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Every string inside a JSON value, depth-first, bounded (<c>iter_strings</c>).</summary>
    internal static IEnumerable<string> Strings(JsonElement value, int limit = 20000)
    {
        var stack = new List<(JsonElement Item, int Depth)> { (value, 0) };
        var nodes = 0;
        while (stack.Count > 0 && nodes < limit)
        {
            var (item, depth) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            nodes++;
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    if (item.GetString() is { Length: > 0 } s) yield return s;
                    break;
                case JsonValueKind.Object:
                    if (depth < 8) stack.AddRange(item.EnumerateObject().Select(p => (p.Value, depth + 1)));
                    break;
                case JsonValueKind.Array:
                    if (depth < 8) stack.AddRange(item.EnumerateArray().Select(v => (v, depth + 1)));
                    break;
            }
        }
    }

    private static string Prefix(string kind, string token)
    {
        if (kind == "private_key")
        {
            var stripped = token.Trim('-');
            return stripped.Length > 6 ? stripped[6..] : "";
        }
        foreach (var literal in LiteralPrefixes)
        {
            if (token.StartsWith(literal, StringComparison.Ordinal)) return literal;
        }
        if (kind == "slack_token") return token[..Math.Min(5, token.Length)];
        if (kind == "openai_key") return "sk-";
        return token[..Math.Min(4, token.Length)];
    }

    private static bool Plausible(string token, string prefix)
    {
        var upper = token.ToUpperInvariant();
        if (PlaceholderWords.Any(word => upper.Contains(word, StringComparison.Ordinal))) return false;
        var body = token[prefix.Length..];
        return body.Distinct().Count() >= 8 && body.Any(char.IsAsciiDigit) && body.Any(char.IsAsciiLetter);
    }

    internal static string Fingerprint(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(FingerprintEncoding.GetBytes(material)))[..12];

    /// <summary>What the check reads: the prompt, or what the agent hands a tool. Never a tool result.</summary>
    internal static List<(string Where, JsonElement Value)> Texts(HookPayload payload, string hookEvent)
    {
        if (PromptEvents.Contains(hookEvent)) return [("prompt", payload.Get("prompt"))];
        if (ToolEvents.Contains(hookEvent)) return [("tool_input", payload.ToolInput), ("tool_input", payload.Get("command"))];
        return [];
    }

    private static bool IsPyWhiteSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>One hit per distinct secret and place, at most the cap.</summary>
    internal static List<Hit> Scan(List<(string Where, JsonElement Value)> texts)
    {
        var hits = new List<Hit>();
        var seen = new HashSet<(string, string)>();
        var budget = Budget;
        foreach (var (where, value) in texts)
        {
            foreach (var text in Strings(value))
            {
                budget -= text.Length;
                if (budget < 0) return hits;
                foreach (var pattern in Patterns)
                {
                    if (!pattern.Anchors.Any(anchor => text.Contains(anchor, StringComparison.Ordinal))) continue;
                    for (var m = pattern.Regex.Match(text); m.Success; m = m.NextMatch())
                    {
                        if (pattern.Kind != "private_key" && m.Index > 0 && IsWordChar(text[m.Index - 1])) continue;
                        var token = m.Value;
                        var prefix = Prefix(pattern.Kind, token);
                        string material;
                        if (pattern.Kind == "private_key")
                        {
                            var after = m.Index + m.Length;
                            var end = Find(text, "-----END", after, after + 20000);
                            var close = end >= 0 ? Find(text, "-----", end + 8, end + 80) : -1;
                            if (close < 0) continue;
                            var block = text.Substring(m.Index, Math.Min(close + 5, text.Length) - m.Index);
                            material = new string(block.Where(c => !IsPyWhiteSpace(c)).ToArray());
                            // The body between BEGIN and END must be real key material, not a doc snippet.
                            if (material.Length - 2 * token.Length < 64) continue;
                        }
                        else
                        {
                            if (!Plausible(token, prefix)) continue;
                            material = token;
                        }
                        var fp = Fingerprint(material);
                        if (!seen.Add((fp, where))) continue;
                        hits.Add(new Hit(pattern.Kind, where, prefix, material.Length, fp));
                        if (hits.Count >= MaxEntries) return hits;
                    }
                }
            }
        }
        return hits;
    }

    /// <summary>Python's <c>str.find(sub, start, end)</c>: the match must lie wholly inside [start, end).</summary>
    private static int Find(string text, string sub, int start, int end)
    {
        end = Math.Min(end, text.Length);
        if (start < 0 || start > end) return -1;
        return text.IndexOf(sub, start, end - start, StringComparison.Ordinal);
    }

    /// <summary>The same secret in the same place is one sighting; the same tool call never counts twice.</summary>
    internal static void Record(List<SecretEntry> entries, Hit hit, long nowMs, string tool, string toolUseId)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Fp != hit.Fp || entry.Where != hit.Where) continue;
            if (!(toolUseId.Length > 0 && entry.ToolUseId == toolUseId)) entry.Events = Math.Min(entry.Events + 1, 999);
            entry.LastTs = nowMs;
            if (toolUseId.Length > 0) entry.ToolUseId = toolUseId;
            return;
        }
        entries.Add(new SecretEntry
        {
            Kind = hit.Kind,
            Where = hit.Where,
            Tool = hit.Where == "tool_input" ? tool : "",
            Prefix = hit.Prefix,
            Length = hit.Length,
            Fp = hit.Fp,
            Events = 1,
            FirstTs = nowMs,
            LastTs = nowMs,
            ToolUseId = toolUseId,
        });
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
    }
}
