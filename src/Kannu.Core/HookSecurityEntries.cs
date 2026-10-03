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
using System.Text.Json.Serialization;

namespace Kannu.Core;

// The four finding lists of a status file. JSON keys are the macOS hook's, so the files stay
// cross-compatible. Each type's Carried() is the macOS carried_* reader: the status file is untrusted
// input (any process running as the user can write one), so only well-typed entries survive, every
// string is re-sanitised and every number re-capped, and only the newest N are kept.

/// <summary>Text an agent can read but a person cannot see, found in one place.</summary>
public sealed class HiddenTextEntry
{
    /// <summary>"tags", "variation_selectors", "bidi" or "zero_width".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>"tool_result", "prompt", "tool_input", "agent_reply" or "other".</summary>
    [JsonPropertyName("where")] public string Where { get; set; } = "";

    [JsonPropertyName("tool")] public string Tool { get; set; } = "";

    [JsonPropertyName("chars")] public int Chars { get; set; }

    [JsonPropertyName("events")] public int Events { get; set; }

    /// <summary>What the characters decode to, printable ASCII only, at most 160.</summary>
    [JsonPropertyName("preview")] public string Preview { get; set; } = "";

    [JsonPropertyName("first_ts")] public long FirstTs { get; set; }

    [JsonPropertyName("last_ts")] public long LastTs { get; set; }

    [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; } = "";

    public HiddenTextEntry Clone() => (HiddenTextEntry)MemberwiseClone();

    internal static List<HiddenTextEntry>? Carried(JsonElement root)
    {
        var out_ = new List<HiddenTextEntry>();
        foreach (var item in SecurityText.LastObjects(root, "hidden_text", HiddenText.MaxEntries))
        {
            var kind = SecurityText.Str(item, "kind");
            var first = SecurityText.Int(item, "first_ts");
            if (kind is null || !HiddenText.KindRank.ContainsKey(kind) || first < HookSecurity.PlausibleMs) continue;
            var where = SecurityText.Str(item, "where");
            out_.Add(new HiddenTextEntry
            {
                Kind = kind,
                Where = where is not null && HiddenText.WhereRank.ContainsKey(where) ? where : "other",
                Tool = SecurityText.Token(SecurityText.Str(item, "tool")),
                Chars = (int)Math.Min(SecurityText.Int(item, "chars"), 999999),
                Events = (int)Math.Max(1, Math.Min(SecurityText.Int(item, "events", 1), 999)),
                Preview = SecurityText.PrintableAscii(SecurityText.Str(item, "preview")),
                FirstTs = first,
                LastTs = Math.Max(first, SecurityText.Int(item, "last_ts")),
                ToolUseId = SecurityText.Token(SecurityText.Str(item, "tool_use_id")),
            });
        }
        return out_.Count > 0 ? out_ : null;
    }
}

/// <summary>An API key or private key in a prompt or a tool's input. Never the secret itself.</summary>
public sealed class SecretEntry
{
    /// <summary>e.g. "anthropic_key", "private_key"; see the pattern table in the hook.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>"prompt" or "tool_input".</summary>
    [JsonPropertyName("where")] public string Where { get; set; } = "";

    [JsonPropertyName("tool")] public string Tool { get; set; } = "";

    /// <summary>The vendor prefix only (e.g. "sk-ant-"), or the key type of a private key.</summary>
    [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";

    [JsonPropertyName("length")] public int Length { get; set; }

    /// <summary>The first 12 hex digits of the secret's SHA-256.</summary>
    [JsonPropertyName("fp")] public string Fp { get; set; } = "";

    [JsonPropertyName("events")] public int Events { get; set; }

    [JsonPropertyName("first_ts")] public long FirstTs { get; set; }

    [JsonPropertyName("last_ts")] public long LastTs { get; set; }

    [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; } = "";

    public SecretEntry Clone() => (SecretEntry)MemberwiseClone();

    internal static List<SecretEntry>? Carried(JsonElement root)
    {
        var out_ = new List<SecretEntry>();
        foreach (var item in SecurityText.LastObjects(root, "secrets", Secrets.MaxEntries))
        {
            var kind = SecurityText.Str(item, "kind");
            var where = SecurityText.Str(item, "where");
            var fp = SecurityText.Str(item, "fp");
            var first = SecurityText.Int(item, "first_ts");
            if (kind is null || !Secrets.Kinds.Contains(kind) || where is not ("prompt" or "tool_input")
                || fp is null || !Secrets.IsFingerprint(fp) || first < HookSecurity.PlausibleMs)
            {
                continue;
            }
            out_.Add(new SecretEntry
            {
                Kind = kind,
                Where = where,
                Tool = SecurityText.Token(SecurityText.Str(item, "tool")),
                Prefix = SecurityText.PrintableAscii(SecurityText.Str(item, "prefix"), 40),
                Length = (int)Math.Min(SecurityText.Int(item, "length"), 99999),
                Fp = fp,
                Events = (int)Math.Max(1, Math.Min(SecurityText.Int(item, "events", 1), 999)),
                FirstTs = first,
                LastTs = Math.Max(first, SecurityText.Int(item, "last_ts")),
                ToolUseId = SecurityText.Token(SecurityText.Str(item, "tool_use_id")),
            });
        }
        return out_.Count > 0 ? out_ : null;
    }
}

/// <summary>A sensitive file (keys, credential stores, autorun, agent config…) a tool read or changed.</summary>
public sealed class SensitivePathEntry
{
    /// <summary>One of <see cref="HookSecurity.SensitiveCategories"/>.</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";

    /// <summary>"read" or "write".</summary>
    [JsonPropertyName("access")] public string Access { get; set; } = "";

    /// <summary>The path as shown (home folded to <c>~</c>), or a command such as "schtasks /create".</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonPropertyName("tool")] public string Tool { get; set; } = "";

    /// <summary>True while every attempt at this path failed (PostToolUseFailure).</summary>
    [JsonPropertyName("failed")] public bool Failed { get; set; }

    [JsonPropertyName("events")] public int Events { get; set; }

    [JsonPropertyName("first_ts")] public long FirstTs { get; set; }

    [JsonPropertyName("last_ts")] public long LastTs { get; set; }

    [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; } = "";

    public SensitivePathEntry Clone() => (SensitivePathEntry)MemberwiseClone();

    internal static List<SensitivePathEntry>? Carried(JsonElement root)
    {
        var out_ = new List<SensitivePathEntry>();
        foreach (var item in SecurityText.LastObjects(root, "sensitive_paths", SensitivePaths.MaxEntries))
        {
            var category = SecurityText.Str(item, "category");
            var access = SecurityText.Str(item, "access");
            var path = SecurityText.PrintableAscii(SecurityText.Str(item, "path"), 160);
            var first = SecurityText.Int(item, "first_ts");
            if (category is null || !SensitivePaths.CategorySet.Contains(category) || access is not ("read" or "write")
                || path.Length == 0 || first < HookSecurity.PlausibleMs)
            {
                continue;
            }
            out_.Add(new SensitivePathEntry
            {
                Category = category,
                Access = access,
                Path = path,
                Tool = SecurityText.Token(SecurityText.Str(item, "tool")),
                Failed = SecurityText.IsTrue(item, "failed"),
                Events = (int)Math.Max(1, Math.Min(SecurityText.Int(item, "events", 1), 999)),
                FirstTs = first,
                LastTs = Math.Max(first, SecurityText.Int(item, "last_ts")),
                ToolUseId = SecurityText.Token(SecurityText.Str(item, "tool_use_id")),
            });
        }
        return out_.Count > 0 ? out_ : null;
    }
}

/// <summary>A call that matched the user's agent policy, and whether it was refused.</summary>
public sealed class PolicyEntry
{
    /// <summary>"command" or "tool".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>The rule's text: the command prefix or the tool name.</summary>
    [JsonPropertyName("matched")] public string Matched { get; set; } = "";

    [JsonPropertyName("tool")] public string Tool { get; set; } = "";

    /// <summary>The call was denied (enforce marker on, and a host whose contract has a deny).</summary>
    [JsonPropertyName("blocked")] public bool Blocked { get; set; }

    [JsonPropertyName("events")] public int Events { get; set; }

    [JsonPropertyName("first_ts")] public long FirstTs { get; set; }

    [JsonPropertyName("last_ts")] public long LastTs { get; set; }

    [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; } = "";

    public PolicyEntry Clone() => (PolicyEntry)MemberwiseClone();

    internal static List<PolicyEntry>? Carried(JsonElement root)
    {
        var out_ = new List<PolicyEntry>();
        foreach (var item in SecurityText.LastObjects(root, "policy", AgentPolicyCheck.MaxEntries))
        {
            var kind = SecurityText.Str(item, "kind");
            var matched = SecurityText.Str(item, "matched");
            var first = SecurityText.Int(item, "first_ts");
            if (kind is not ("command" or "tool") || matched is null || first < HookSecurity.PlausibleMs) continue;
            out_.Add(new PolicyEntry
            {
                Kind = kind,
                Matched = SecurityText.PrintableAscii(matched, HookSecurity.PolicyMaxLength),
                Tool = SecurityText.Token(SecurityText.Str(item, "tool")),
                Blocked = SecurityText.IsTrue(item, "blocked"),
                Events = (int)Math.Max(1, Math.Min(SecurityText.Int(item, "events", 1), 999)),
                FirstTs = first,
                LastTs = Math.Max(first, SecurityText.Int(item, "last_ts")),
                ToolUseId = SecurityText.Token(SecurityText.Str(item, "tool_use_id")),
            });
        }
        return out_.Count > 0 ? out_ : null;
    }
}
