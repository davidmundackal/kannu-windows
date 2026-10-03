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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kannu.Core;

public enum FindingSeverity
{
    Info = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// One security finding. Additive to the traffic light (it never becomes a light colour) and stable
/// across re-scans: the id is a digest of what the finding is about, so acknowledgements hold.
/// Port of macOS <c>AgentSecurityFinding</c>, its texts word for word ("Mac" made "PC").
/// </summary>
public sealed record SecurityFinding
{
    public required string Id { get; init; }

    /// <summary>Who raised it: <c>kannu</c> (the hook and Kannu's own checks), <c>discovery</c> or <c>detection</c> (ADR).</summary>
    public string Source { get; init; } = SecurityFindings.KannuSource;

    public required string Rule { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Title { get; init; }

    /// <summary>One line for the card. Never pushed: it names the chat and the file.</summary>
    public required string Summary { get; init; }

    /// <summary>Short proofs; never raw payloads, never a secret.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Shown in Kannu only, never pushed or copied (the decoded hidden text).</summary>
    public IReadOnlyList<string> KannuOnlyEvidence { get; init; } = [];

    /// <summary>The file "Show in folder" selects; null when there is none.</summary>
    public string? RevealPath { get; init; }

    public long FirstSeenMs { get; init; }
    public long LastSeenMs { get; init; }
    public int Occurrences { get; init; } = 1;
    public string? ChatName { get; init; }

    /// <summary>The tool or server an ADR Discovery finding is about (or the chat, for ADR Detection).</summary>
    public string? AssetName { get; init; }

    /// <summary>The problem's identity with the churning parts left out; null: a group of one.</summary>
    public string? GroupSubject { get; init; }

    /// <summary>For a policy match: "ran" or "blocked". A change brings an acknowledged group back.</summary>
    public string? OutcomeTag { get; init; }

    public string GroupId => GroupSubject is null ? Id : SecurityFindings.StableId(Source, Rule, GroupSubject, []);

    /// <summary>What a push carries: severity and source, never <see cref="Summary"/>.</summary>
    public string PushBody => $"{SecurityFindings.SeverityLabel(Severity)} severity, reported by {SecurityFindings.SourceName(Source)}. Details are in Settings › Security.";
}

/// <summary>One row in Settings: the findings that are one problem.</summary>
public sealed record FindingGroup(string Id, IReadOnlyList<SecurityFinding> Members)
{
    public FindingSeverity Severity => Members.Max(m => m.Severity);
    public int Occurrences => Members.Sum(m => m.Occurrences);
    public long FirstSeenMs => Members.Min(m => m.FirstSeenMs);
    public long LastSeenMs => Members.Max(m => m.LastSeenMs);

    /// <summary>The worst member, then the most recent.</summary>
    public SecurityFinding Representative => Members.OrderByDescending(m => m.Severity).ThenByDescending(m => m.LastSeenMs).First();

    public string OutcomeSignature =>
        (int)Severity + "|" + string.Join(",", Members.Select(m => m.OutcomeTag).OfType<string>().Distinct().Order(StringComparer.Ordinal));

    public IReadOnlyList<string> Chats => Members.Select(m => m.ChatName).OfType<string>().Distinct().ToList();
}

public static class SecurityFindings
{
    public const string UnattendedRule = "unattended_execution";
    public const string McpAddedRule = "mcp_server_added";

    public const string KannuSource = "kannu";

    /// <summary>SHA-256 of rule, subject and evidence, first 12 bytes as hex: macOS's id for Kannu's own checks.</summary>
    public static string StableId(string rule, string subject, IEnumerable<string> evidence) => StableId(KannuSource, rule, subject, evidence);

    /// <summary>macOS <c>AgentSecurityFinding.stableID(source:rule:subject:evidence:)</c>, byte for byte.</summary>
    public static string StableId(string source, string rule, string subject, IEnumerable<string> evidence)
    {
        var material = string.Join("\u001F", new[] { source, rule, subject }.Concat(evidence));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)), 0, 12).ToLowerInvariant();
    }

    public static string SourceName(string source) => source switch
    {
        AdrSnapshot.Source => "ADR Discovery",
        AdrAnalysis.Source => "ADR Detection",
        _ => "Kannu's own check",
    };

    /// <summary>A full path on this machine, or a Windows drive path read on any platform (tests run on Linux too).</summary>
    public static bool IsAbsolute(string path) =>
        Path.IsPathFullyQualified(path) || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    public static string SeverityLabel(FindingSeverity severity) => severity switch
    {
        FindingSeverity.High => "High",
        FindingSeverity.Medium => "Medium",
        _ => "Info",
    };

    public static string FirstSeen(long ms) =>
        "First seen " + DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    private static IReadOnlyList<string> Distinct(params string?[] lines) => lines.OfType<string>().Distinct().ToList();

    // ---- Hidden text ----

    public static SecurityFinding HiddenText(string conversationId, string provider, string chatName, string kind, string where,
        string? tool, int chars, string preview, long firstMs, long lastMs, int events)
    {
        var bidi = kind == "bidi";
        var title = bidi ? "Text that shows differently than it reads" : where switch
        {
            "tool_result" => "Hidden text in a tool result",
            "prompt" => "Hidden text in your prompt",
            "tool_input" => "Hidden text the agent wrote",
            "agent_reply" => "Hidden text in the agent's reply",
            _ => "Hidden text in agent input",
        };
        var place = where switch
        {
            "tool_result" => tool is { Length: > 0 } ? $"a {tool} result" : "a tool result",
            "tool_input" => tool is { Length: > 0 } ? $"the input of a {tool} call" : "a tool call's input",
            "prompt" => "your prompt",
            "agent_reply" => "the agent's reply",
            _ => "the agent's input",
        };
        var word = bidi ? (chars == 1 ? "text-direction control" : "text-direction controls")
            : chars == 1 ? "invisible character" : "invisible characters";
        var technical = kind switch
        {
            "tags" => "tag characters (U+E0000–E007F)",
            "variation_selectors" => "variation selectors",
            "bidi" => "bidirectional controls",
            _ => "zero-width characters",
        };
        var detail = $"{chars} {technical} · {where.Replace('_', ' ')}" + (tool is { Length: > 0 } ? $" · {tool}" : "") + $" · {AgentSession.ProviderLabelFor(provider)}";
        return new SecurityFinding
        {
            Id = StableId("hidden_text_" + kind, conversationId, [where, firstMs.ToString(CultureInfo.InvariantCulture)]),
            Rule = "hidden_text_" + kind,
            Severity = !bidi && preview.Length >= 4 ? FindingSeverity.High : FindingSeverity.Medium,
            Title = title,
            Summary = $"{chars} {word} in {place}, in “{chatName}”.",
            Evidence = Distinct(detail, FirstSeen(firstMs)),
            KannuOnlyEvidence = preview.Length == 0 ? [] : [bidi ? $"Logical order: {preview}" : $"Decodes to: “{preview}”"],
            FirstSeenMs = firstMs,
            LastSeenMs = lastMs,
            Occurrences = Math.Max(1, events),
            ChatName = chatName,
            GroupSubject = kind + "|" + where,
        };
    }

    // ---- Secrets ----

    private static readonly HashSet<string> FileEditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "multiedit", "notebookedit", "writefile", "editfile", "searchreplace", "strreplaceeditor",
        "applypatch", "replace", "create",
    };

    public static string SecretName(string kind) => kind switch
    {
        "private_key" => "Private key",
        "anthropic_key" => "Anthropic API key",
        "openai_key" => "OpenAI API key",
        "aws_access_key" => "AWS access key",
        "github_token" => "GitHub token",
        "gitlab_token" => "GitLab token",
        "slack_token" => "Slack token",
        "stripe_key" => "Stripe live key",
        "google_api_key" => "Google API key",
        "npm_token" => "npm token",
        "huggingface_token" => "Hugging Face token",
        _ => "Secret",
    };

    private static string SecretPhrase(string kind) => kind switch
    {
        "private_key" => "a private key",
        "anthropic_key" => "an Anthropic API key",
        "openai_key" => "an OpenAI API key",
        "aws_access_key" => "an AWS access key",
        "npm_token" => "an npm token",
        _ => "a " + SecretName(kind),
    };

    public static SecurityFinding Secret(string conversationId, string provider, string chatName, string kind, string where,
        string? tool, string prefix, int length, string fingerprint, long firstMs, long lastMs, int events)
    {
        var prompt = where == "prompt";
        var edit = tool is { Length: > 0 } && FileEditTools.Contains(HookEventMapper.Compact(tool));
        var title = prompt ? "A secret in your prompt" : edit ? "The agent wrote a secret into a file" : "The agent used a secret in a tool call";
        var summary = prompt
            ? $"Your prompt in “{chatName}” held {SecretPhrase(kind)}, so it went to the model provider."
            : tool is { Length: > 0 } ? $"The agent put {SecretPhrase(kind)} into a {tool} call in “{chatName}”."
            : $"The agent put {SecretPhrase(kind)} into a tool call in “{chatName}”.";
        var shape = prefix.Length == 0 || kind == "private_key"
            ? $"{(prefix.Length == 0 ? SecretName(kind) : prefix)} · {length} characters"
            : $"{SecretName(kind)} · starts “{prefix}” · {length} characters";
        return new SecurityFinding
        {
            Id = StableId("secret_" + kind, conversationId, [where, fingerprint]),
            Rule = "secret_" + kind,
            Severity = !prompt && where == "tool_input" && !edit ? FindingSeverity.High : FindingSeverity.Medium,
            Title = title,
            Summary = summary,
            Evidence = Distinct(shape,
                $"Fingerprint {fingerprint}: the first 12 hex digits of its SHA-256. Kannu never keeps the secret.",
                (prompt ? "Prompt" : tool is { Length: > 0 } ? tool : "Tool call") + " · " + AgentSession.ProviderLabelFor(provider),
                FirstSeen(firstMs)),
            FirstSeenMs = firstMs,
            LastSeenMs = lastMs,
            Occurrences = Math.Max(1, events),
            ChatName = chatName,
            GroupSubject = $"{kind}|{prefix}|{tool ?? ""}",
        };
    }

    // ---- Sensitive files ----

    private static string Object(string category) => category switch
    {
        "ssh_key" => "an SSH private key",
        "cloud_credentials" => "cloud credentials",
        "token_file" => "a file of access tokens",
        "agent_credentials" => "an AI tool's sign-in",
        "gpg_key" => "a GPG private key",
        "password_store" => "a password store",
        "keychain" => "Windows' saved credentials",
        "keychain_item" => "a saved credential",
        "browser_data" => "browser data",
        "env_file" => "a .env file",
        "shell_history" => "shell history",
        "autorun" => "something that runs on its own",
        "shell_startup" => "a shell startup file",
        "agent_config" => "an agent's settings",
        _ => "a sensitive file",
    };

    private static string Why(string category) => category switch
    {
        "ssh_key" => "Anyone with this file can sign in to your servers and Git hosts as you.",
        "cloud_credentials" => "These files let a program act on your cloud accounts.",
        "token_file" => "This file holds tokens for Git hosts or package registries.",
        "agent_credentials" => "This file signs an AI tool in to your account.",
        "gpg_key" => "A GPG private key signs and decrypts in your name.",
        "password_store" => "This is a password manager's data.",
        "keychain" => "Windows keeps your saved passwords and keys here.",
        "keychain_item" => "It listed saved credentials, without reading a password.",
        "browser_data" => "Browser profiles hold cookies, saved passwords and history.",
        "env_file" => ".env files usually hold API keys and passwords.",
        "shell_history" => "Shell history often holds tokens typed on the command line.",
        "autorun" => "Files and settings here run programs on their own: at sign-in, on a schedule or on Git events.",
        "shell_startup" => "Your shell runs this file every time a terminal opens.",
        "agent_config" => "Agent settings decide what agents may run without asking.",
        _ => "",
    };

    public static SecurityFinding SensitivePath(string conversationId, string provider, string chatName, string category, string access,
        string path, string? tool, bool failed, long firstMs, long lastMs, int events, int pathLimit = 512)
    {
        var read = access == "read";
        var what = Object(category);
        var title = category == "keychain_item"
            ? (failed ? $"The agent tried to look up {what}" : $"The agent looked up {what}")
            : (read, failed) switch
            {
                (true, false) => $"The agent read {what}",
                (true, true) => $"The agent tried to read {what}",
                (false, false) => $"The agent changed {what}",
                _ => $"The agent tried to change {what}",
            };
        var how = category == "keychain_item"
            ? (tool is { Length: > 0 } ? $"Looked up with {tool}" : "Looked up")
            : read ? (tool is { Length: > 0 } ? $"Read with {tool}" : "Read") : (tool is { Length: > 0 } ? $"Changed with {tool}" : "Changed");
        how += " · " + AgentSession.ProviderLabelFor(provider) + (failed ? " · the call failed" : "");
        var severity = category switch
        {
            "env_file" or "shell_history" or "keychain_item" => FindingSeverity.Medium,
            "agent_config" when path.Replace('\\', '/').EndsWith(".vscode/settings.json", StringComparison.OrdinalIgnoreCase) => FindingSeverity.Medium,
            _ => FindingSeverity.High,
        };
        var revealable = path.Length < pathLimit && (IsAbsolute(path) || path.StartsWith('~'));
        return new SecurityFinding
        {
            Id = StableId("sensitive_file_" + category, conversationId, [access, path]),
            Rule = "sensitive_file_" + category,
            Severity = severity,
            Title = title,
            Summary = tool is { Length: > 0 } ? $"{path}, with {tool}, in “{chatName}”." : $"{path}, in “{chatName}”.",
            Evidence = Distinct(path, Why(category), how, FirstSeen(firstMs)),
            RevealPath = revealable ? path : null,
            FirstSeenMs = firstMs,
            LastSeenMs = lastMs,
            Occurrences = Math.Max(1, events),
            ChatName = chatName,
            GroupSubject = $"{category}|{access}|{path}",
        };
    }

    // ---- Policy ----

    public static SecurityFinding Policy(string conversationId, string provider, string chatName, string kind, string matched,
        string? tool, bool blocked, long firstMs, long lastMs, int events)
    {
        var what = kind == "tool" ? $"the {matched} tool" : $"a “{matched}” command";
        return new SecurityFinding
        {
            Id = StableId("policy_" + kind, conversationId, [kind, matched, blocked ? "blocked" : "ran"]),
            Rule = "policy_" + kind,
            Severity = blocked ? FindingSeverity.Medium : FindingSeverity.High,
            Title = blocked ? $"Policy blocked “{matched}”" : $"Policy matched “{matched}” — it ran",
            Summary = blocked
                ? $"Kannu refused {what} in “{chatName}” and told the agent why."
                : $"The agent used {what} in “{chatName}”. Blocking was off, so it ran; this is the report.",
            Evidence = Distinct(
                kind == "tool" ? $"Rule: tool “{matched}”" : $"Rule: command “{matched}”",
                blocked ? "Refused before it ran; the agent was told the rule." : "Ran — blocking is off, or this agent's hook cannot refuse a call.",
                (tool is { Length: > 0 } ? tool : "Tool call") + " · " + AgentSession.ProviderLabelFor(provider),
                FirstSeen(firstMs)),
            FirstSeenMs = firstMs,
            LastSeenMs = lastMs,
            Occurrences = Math.Max(1, events),
            ChatName = chatName,
            GroupSubject = $"{kind}|{matched}",
            OutcomeTag = blocked ? "blocked" : "ran",
        };
    }

    // ---- Unattended ----

    public static SecurityFinding Unattended(string conversationId, string provider, string chatName, string? project, long lastEventMs) => new()
    {
        Id = StableId(UnattendedRule, conversationId, []),
        Rule = UnattendedRule,
        Severity = FindingSeverity.High,
        Title = "Permission checks bypassed",
        Summary = $"“{chatName}” runs with its permission checks off, so the agent can run commands and change files without asking.",
        Evidence = Distinct($"{AgentSession.ProviderLabelFor(provider)} · launched with permission checks bypassed"),
        FirstSeenMs = lastEventMs,
        LastSeenMs = lastEventMs,
        ChatName = chatName,
        GroupSubject = $"unattended|{provider}|{project ?? chatName}",
    };

    // ---- Grouping and visibility ----

    public static IReadOnlyList<FindingGroup> Group(IEnumerable<SecurityFinding> findings) =>
        findings.GroupBy(f => f.GroupId)
            .Select(g => new FindingGroup(g.Key, g.ToList()))
            .OrderByDescending(g => g.Severity).ThenByDescending(g => g.LastSeenMs)
            .ToList();
}

/// <summary>
/// What the user did about findings, and what the MCP watch has learned, in
/// <c>%APPDATA%\Kannu\security.json</c>. Port of the macOS UserDefaults keys.
/// </summary>
public sealed class SecurityState
{
    public sealed record Ack(FindingSeverity SeverityAtAck, string OutcomeAtAck, long AckedAtMs);

    public Dictionary<string, Ack> Acknowledged { get; } = [];
    public Dictionary<string, long> SnoozedUntilMs { get; } = [];
    public HashSet<string> Pushed { get; } = [];
    public McpWatch.Baseline McpBaseline { get; set; } = new();
    public List<McpWatch.Addition> McpAdditions { get; set; } = [];

    /// <summary>
    /// Findings from chats, kept after the chat's status file is gone (macOS keeps its sightings
    /// past the session): the newest <see cref="KeptPerKind"/> per kind.
    /// </summary>
    public Dictionary<string, SecurityFinding> Kept { get; } = [];

    // ADR: Kannu-run Discovery scans and Detection verdicts (macOS adrLastKannuScanAt, adrKannuScanFailures,
    // adrLastScan, adrSessionAnalyses).
    public long? AdrLastKannuScanMs { get; set; }
    public int AdrScanFailures { get; set; }
    public AdrScanRecord? AdrLastScan { get; set; }
    public List<AdrAnalysis> AdrAnalyses { get; set; } = [];

    public const int KeptPerKind = 50;

    /// <summary>The kind a rule belongs to, for the cap and for "turning a check off forgets it".</summary>
    public static string KindOf(string rule) =>
        rule.StartsWith("hidden_text_", StringComparison.Ordinal) ? "hidden_text"
        : rule.StartsWith("secret_", StringComparison.Ordinal) ? "secrets"
        : rule.StartsWith("sensitive_file_", StringComparison.Ordinal) ? "sensitive_paths"
        : rule.StartsWith("policy_", StringComparison.Ordinal) ? "policy"
        : rule;

    /// <summary>Adds or refreshes sightings; keeps the newest per kind.</summary>
    public void Keep(IEnumerable<SecurityFinding> findings)
    {
        foreach (var finding in findings) Kept[finding.Id] = finding;
        foreach (var kind in Kept.Values.GroupBy(f => KindOf(f.Rule)).Where(g => g.Count() > KeptPerKind).ToList())
        {
            foreach (var old in kind.OrderByDescending(f => f.LastSeenMs).Skip(KeptPerKind)) Kept.Remove(old.Id);
        }
    }

    public void Forget(string kind)
    {
        foreach (var id in Kept.Values.Where(f => KindOf(f.Rule) == kind).Select(f => f.Id).ToList()) Kept.Remove(id);
    }

    public const long SnoozeMs = 24 * 3600_000L;

    /// <summary>
    /// Shown: not snoozed, and either never acknowledged or escalated since (a higher severity, or a
    /// different set of outcomes).
    /// </summary>
    public bool IsVisible(FindingGroup group, long nowMs)
    {
        if (SnoozedUntilMs.TryGetValue(group.Id, out var until) && until > nowMs) return false;
        if (!Acknowledged.TryGetValue(group.Id, out var ack)) return true;
        return group.Severity > ack.SeverityAtAck || group.OutcomeSignature != ack.OutcomeAtAck;
    }

    public void Acknowledge(FindingGroup group, long nowMs) =>
        Acknowledged[group.Id] = new Ack(group.Severity, group.OutcomeSignature, nowMs);

    public void Snooze(FindingGroup group, long nowMs) => SnoozedUntilMs[group.Id] = nowMs + SnoozeMs;

    /// <summary>"Show acknowledged and snoozed again".</summary>
    public void Unhide()
    {
        Acknowledged.Clear();
        SnoozedUntilMs.Clear();
    }

    public static SecurityState Load(string path)
    {
        var state = new SecurityState();
        JsonObject? root;
        try
        {
            root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            root = null;
        }
        if (root is null) return state;
        if (root["acknowledged"] is JsonObject acks)
        {
            foreach (var (id, value) in acks)
            {
                if (value is not JsonObject a) continue;
                state.Acknowledged[id] = new Ack(
                    (FindingSeverity)Math.Clamp(Int(a["severity"]) ?? 1, 0, 2),
                    Str(a["outcome"]) ?? "",
                    Long(a["at"]) ?? 0);
            }
        }
        if (root["snoozed"] is JsonObject snoozed)
        {
            foreach (var (id, value) in snoozed)
            {
                if (Long(value) is { } until) state.SnoozedUntilMs[id] = until;
            }
        }
        if (root["pushed"] is JsonArray pushed)
        {
            foreach (var id in pushed) if (Str(id) is { } s) state.Pushed.Add(s);
        }
        if (root["mcpBaseline"] is JsonObject baseline)
        {
            foreach (var (file, keys) in baseline)
            {
                if (keys is JsonArray list) state.McpBaseline.ServersByConfig[file] = list.Select(Str).OfType<string>().ToList();
            }
        }
        if (root["mcpAdditions"] is JsonArray additions)
        {
            foreach (var item in additions.OfType<JsonObject>())
            {
                if (Str(item["config"]) is not { } config || Str(item["name"]) is not { } name || Long(item["firstSeen"]) is not { } seen) continue;
                state.McpAdditions.Add(new McpWatch.Addition(config, Str(item["app"]) ?? "", Str(item["projectRoot"]), Str(item["scope"]), name, Str(item["runs"]) ?? "", seen));
            }
        }
        state.AdrLastKannuScanMs = Long(root["adrLastKannuScan"]);
        state.AdrScanFailures = Math.Clamp(Int(root["adrScanFailures"]) ?? 0, 0, 10);
        if (root["adrLastScan"] is JsonObject scan && Str(scan["file"]) is { } scanFile)
        {
            state.AdrLastScan = new AdrScanRecord(Long(scan["date"]) ?? 0, Str(scan["origin"]) ?? AdrScanRecord.OriginWatched, scanFile,
                Int(scan["assets"]) ?? 0, Int(scan["findings"]) ?? 0, Int(scan["review"]) ?? 0, scan["complete"] is JsonValue c && c.TryGetValue<bool>(out var b) && b,
                Int(scan["gaps"]) ?? 0, Str(scan["catalog"]) ?? "unknown", Str(scan["schema"]) ?? "", Str(scan["platform"]) ?? "");
        }
        if (root["adrAnalyses"] is JsonArray analyses)
        {
            state.AdrAnalyses = analyses.OfType<JsonObject>().Select(AdrAnalysis.FromJson).OfType<AdrAnalysis>().Take(AdrAnalysis.Cap).ToList();
        }
        if (root["kept"] is JsonArray kept)
        {
            foreach (var item in kept.OfType<JsonObject>())
            {
                if (Str(item["id"]) is not { } id || Str(item["rule"]) is not { } rule || Str(item["title"]) is not { } title) continue;
                state.Kept[id] = new SecurityFinding
                {
                    Id = id,
                    Rule = rule,
                    Severity = (FindingSeverity)Math.Clamp(Int(item["severity"]) ?? 1, 0, 2),
                    Title = title,
                    Summary = Str(item["summary"]) ?? "",
                    Evidence = Strings(item["evidence"]),
                    KannuOnlyEvidence = Strings(item["kannuOnly"]),
                    RevealPath = Str(item["reveal"]),
                    FirstSeenMs = Long(item["first"]) ?? 0,
                    LastSeenMs = Long(item["last"]) ?? 0,
                    Occurrences = Math.Max(1, Int(item["count"]) ?? 1),
                    ChatName = Str(item["chat"]),
                    GroupSubject = Str(item["group"]),
                    OutcomeTag = Str(item["outcome"]),
                };
            }
        }
        return state;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray list ? list.Select(Str).OfType<string>().ToList() : [];

    private static JsonArray Array(IEnumerable<string> items)
    {
        var list = new JsonArray();
        foreach (var item in items) list.Add((JsonNode)item);
        return list;
    }

    /// <summary>Atomic: a temp file beside the target, renamed over it.</summary>
    public void Save(string path, long nowMs)
    {
        // Expired snoozes go; everything else is small and kept.
        foreach (var id in SnoozedUntilMs.Where(s => s.Value <= nowMs).Select(s => s.Key).ToList()) SnoozedUntilMs.Remove(id);
        var acks = new JsonObject();
        foreach (var (id, ack) in Acknowledged)
        {
            acks[id] = new JsonObject { ["severity"] = (int)ack.SeverityAtAck, ["outcome"] = ack.OutcomeAtAck, ["at"] = ack.AckedAtMs };
        }
        var snoozed = new JsonObject();
        foreach (var (id, until) in SnoozedUntilMs) snoozed[id] = until;
        var pushed = new JsonArray();
        foreach (var id in Pushed.Order(StringComparer.Ordinal)) pushed.Add((JsonNode)id);
        var baseline = new JsonObject();
        foreach (var (file, keys) in McpBaseline.ServersByConfig)
        {
            var list = new JsonArray();
            foreach (var key in keys) list.Add((JsonNode)key);
            baseline[file] = list;
        }
        var additions = new JsonArray();
        foreach (var a in McpAdditions)
        {
            var item = new JsonObject { ["config"] = a.ConfigPath, ["app"] = a.AppName, ["name"] = a.Name, ["runs"] = a.Runs, ["firstSeen"] = a.FirstSeenMs };
            if (a.ProjectRoot is { } root) item["projectRoot"] = root;
            if (a.Scope is { } scope) item["scope"] = scope;
            additions.Add((JsonNode)item);
        }
        var kept = new JsonArray();
        foreach (var f in Kept.Values.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            var item = new JsonObject
            {
                ["id"] = f.Id, ["rule"] = f.Rule, ["severity"] = (int)f.Severity, ["title"] = f.Title, ["summary"] = f.Summary,
                ["evidence"] = Array(f.Evidence), ["kannuOnly"] = Array(f.KannuOnlyEvidence),
                ["first"] = f.FirstSeenMs, ["last"] = f.LastSeenMs, ["count"] = f.Occurrences,
            };
            if (f.RevealPath is { } reveal) item["reveal"] = reveal;
            if (f.ChatName is { } chat) item["chat"] = chat;
            if (f.GroupSubject is { } group) item["group"] = group;
            if (f.OutcomeTag is { } outcome) item["outcome"] = outcome;
            kept.Add((JsonNode)item);
        }
        var analyses = new JsonArray();
        foreach (var a in AdrAnalyses.Take(AdrAnalysis.Cap)) analyses.Add((JsonNode)a.ToJson());
        var doc = new JsonObject
        {
            ["adrAnalyses"] = analyses,
            ["adrScanFailures"] = AdrScanFailures,
            ["kept"] = kept,
            ["acknowledged"] = acks,
            ["snoozed"] = snoozed,
            ["pushed"] = pushed,
            ["mcpBaseline"] = baseline,
            ["mcpAdditions"] = additions,
        };
        if (AdrLastKannuScanMs is { } lastScan) doc["adrLastKannuScan"] = lastScan;
        if (AdrLastScan is { } r)
        {
            doc["adrLastScan"] = new JsonObject
            {
                ["date"] = r.DateMs, ["origin"] = r.Origin, ["file"] = r.FileName, ["assets"] = r.AssetCount, ["findings"] = r.FindingCount,
                ["review"] = r.ReviewCount, ["complete"] = r.CoverageComplete, ["gaps"] = r.CoverageGaps, ["catalog"] = r.CatalogVersion,
                ["schema"] = r.SchemaVersion, ["platform"] = r.Platform,
            };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
