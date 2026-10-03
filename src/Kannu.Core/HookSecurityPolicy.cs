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

using System.Text.Json;

namespace Kannu.Core;

/// <summary>
/// The agent policy (macOS hook v42): a user-authored <c>%USERPROFILE%\.kannu\agent-policy.json</c>
/// names commands and tools an agent may not use. Every match is recorded; with the enforce marker
/// present, and only on hosts whose hook contract has a deny, the call is refused and the model is told
/// why. No regex from the file: a pattern from untrusted input is a ReDoS in a hook that must answer in
/// milliseconds.
/// </summary>
internal static class AgentPolicyCheck
{
    internal const int MaxEntries = 3;

    internal readonly record struct Hit(string Kind, string Matched, string Reason);

    /// <summary>Where a deny is part of the host's hook contract.</summary>
    internal static readonly Dictionary<string, HashSet<string>> DenyEvents = new()
    {
        ["claude"] = ["PreToolUse"],
        ["cursor"] = ["beforeShellExecution", "beforeMCPExecution", "preToolUse"],
    };

    internal static bool CanDeny(string provider, string hookEvent) =>
        DenyEvents.TryGetValue(provider, out var events) && events.Contains(hookEvent);

    /// <summary>
    /// The command strings a pre-tool event carries: tool_input.command, Cursor's top-level command, and
    /// a stringified tool_input (Cursor sends JSON text). A bare string is a command only for a shell tool
    /// or Cursor's shell event — a file path is not a command line.
    /// </summary>
    internal static List<string> Commands(HookPayload payload, string hookEvent)
    {
        var compact = new string(payload.Tool.ToLowerInvariant().Where(char.IsLetter).ToArray());
        var shellLike = hookEvent == "beforeShellExecution" || SensitivePaths.ShellTools.Contains(compact);
        var out_ = new List<string>();
        foreach (var original in new[] { payload.ToolInput, payload.Get("command") })
        {
            var item = original;
            if (item.ValueKind == JsonValueKind.String && HiddenText.LooksLikeJson(item.GetString()!)
                && HiddenText.TryParseContainer(item.GetString()!) is { } parsed)
            {
                item = parsed;
            }
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "command", "cmd", "commandLine" })
                {
                    if (!item.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind == JsonValueKind.String) out_.Add(value.GetString()!);
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        out_.Add(string.Join(" ", value.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())));
                    }
                }
            }
            else if (item.ValueKind == JsonValueKind.String && shellLike)
            {
                out_.Add(item.GetString()!);
            }
        }
        return out_.Where(c => c.Length > 0).Select(c => c.Length > 100_000 ? c[..100_000] : c).ToList();
    }

    /// <summary>
    /// The first rule that matches. Tool rules first: they are exact. A command rule matches a simple
    /// command whose words start with the rule's words; the command name compares case-insensitively and
    /// without <c>.exe</c> (Windows), the arguments exactly.
    /// </summary>
    internal static Hit? Match(AgentPolicy policy, string tool, List<string> commands)
    {
        foreach (var rule in policy.Rules)
        {
            if (rule.Kind == "tool" && tool.Length > 0 && tool == rule.Value) return new Hit("tool", rule.Value, rule.Reason);
        }
        var segments = new List<List<string>>();
        foreach (var command in commands)
        {
            segments.AddRange(ShellWords.Segments(command).Select(s => s.Words));
            if (segments.Count > 400) break;
        }
        foreach (var rule in policy.Rules)
        {
            if (rule.Kind != "command") continue;
            foreach (var words in segments)
            {
                if (StartsWith(words, rule.Words)) return new Hit("command", string.Join(" ", rule.Words), rule.Reason);
            }
        }
        return null;
    }

    private static bool StartsWith(List<string> words, IReadOnlyList<string> prefix)
    {
        if (words.Count < prefix.Count) return false;
        if (ShellWords.CommandName(words[0]) != ShellWords.CommandName(prefix[0])) return false;
        for (var i = 1; i < prefix.Count; i++)
        {
            if (words[i] != prefix[i]) return false;
        }
        return true;
    }

    /// <summary>The same rule with the same outcome is one sighting; the same tool call never counts twice.</summary>
    internal static void Record(List<PolicyEntry> entries, Hit hit, bool blocked, long nowMs, string tool, string toolUseId)
    {
        var matched = SecurityText.PrintableAscii(hit.Matched, HookSecurity.PolicyMaxLength);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Kind != hit.Kind || entry.Matched != matched || entry.Blocked != blocked) continue;
            if (!(toolUseId.Length > 0 && entry.ToolUseId == toolUseId)) entry.Events = Math.Min(entry.Events + 1, 999);
            entry.LastTs = nowMs;
            if (toolUseId.Length > 0) entry.ToolUseId = toolUseId;
            return;
        }
        entries.Add(new PolicyEntry
        {
            Kind = hit.Kind,
            Matched = matched,
            Tool = tool,
            Blocked = blocked,
            Events = 1,
            FirstTs = nowMs,
            LastTs = nowMs,
            ToolUseId = toolUseId,
        });
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
    }
}
