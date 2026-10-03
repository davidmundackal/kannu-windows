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
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kannu.Core;

/// <summary>
/// The ADR Detection invocation as data (port of macOS <c>ADRDetectionCommand</c>). Kannu ships nothing of ADR:
/// the user prepares a Detection checkout (<c>git clone</c> + <c>uv sync</c>, Python 3.10–3.12), and Kannu runs
/// its own GPL adapter (<see cref="AdrAdapter"/>) inside that project with <c>uv run --project</c>.
/// <para>
/// What leaves the PC when it runs: the chosen transcript's text, to Anthropic through the Claude CLI (the
/// user's login). Off by default; one chat at a time, on explicit request only. Triage with OpenAI is not
/// offered on Windows, so the adapter always runs with <c>--triage off</c>.
/// </para>
/// <para>
/// Windows limit: Detection hands the whole transcript to <c>claude</c> as one command-line argument, and
/// Windows caps a command line at 32,767 characters, so the transcript budget is
/// <see cref="WindowsMaxCharacters"/> (macOS: 150,000). A chat that still does not fit fails with an error;
/// it never yields a clean verdict.
/// </para>
/// </summary>
public static class AdrDetection
{
    public const int WindowsMaxCharacters = 18_000;
    public const string CloneCommand = "git clone https://github.com/uber/ADR && cd ADR/Detection && uv sync";

    public sealed record Options
    {
        public string ReasoningModel { get; init; } = "claude-sonnet-5";
        public bool ThreatIntelligence { get; init; } = true;
        public bool SourceCode { get; init; } = true;
        public bool Policy { get; init; } = true;
        public int TimeoutSeconds { get; init; } = 300;
        public int MaxTurns { get; init; } = 60;
        public int MaxMessages { get; init; } = 400;
        public int MaxCharacters { get; init; } = WindowsMaxCharacters;

        public string ContextList => string.Join(",",
            new[] { ThreatIntelligence ? "threat_intelligence" : null, SourceCode ? "source_code" : null, Policy ? "policy" : null }.OfType<string>());
    }

    /// <summary>Upstream's reasoning timeout is the largest single wait; the rest is uv and MCP start-up.</summary>
    public static TimeSpan ProcessTimeout(int reasoningTimeoutSeconds) => TimeSpan.FromSeconds(reasoningTimeoutSeconds + 120);

    public static string Directory(string home) => Path.Combine(home, ".kannu", "adr", "detection");

    /// <summary><c>uv run --project &lt;checkout&gt; python &lt;adapter&gt; --transcript … --report … &lt;options&gt;</c>.</summary>
    public static IReadOnlyList<string> Arguments(string checkout, string adapter, string transcript, string report, Options options) =>
    [
        "run", "--project", checkout,
        "python", adapter,
        "--transcript", transcript,
        "--report", report,
        "--triage", "off",
        "--triage-model", "gpt-4o",
        "--reasoning-model", options.ReasoningModel,
        "--context", options.ContextList,
        "--timeout", options.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        "--max-turns", options.MaxTurns.ToString(CultureInfo.InvariantCulture),
        "--max-messages", options.MaxMessages.ToString(CultureInfo.InvariantCulture),
        "--max-chars", Math.Clamp(options.MaxCharacters, 1, WindowsMaxCharacters).ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>The only shape Kannu may spawn: its adapter, inside the user's project, on one transcript, within the Windows budget.</summary>
    public static bool IsValidAnalysis(IReadOnlyList<string> args)
    {
        if (args.Count < 8 || args[0] != "run" || args[1] != "--project" || args[3] != "python") return false;
        if (!(args[4].EndsWith("\\" + AdrAdapter.FileName, StringComparison.Ordinal) || args[4].EndsWith("/" + AdrAdapter.FileName, StringComparison.Ordinal))) return false;
        var transcript = IndexOf(args, "--transcript");
        if (transcript < 0 || transcript + 1 >= args.Count || IndexOf(args, "--report") < 0) return false;
        var chars = IndexOf(args, "--max-chars");
        if (chars < 0 || chars + 1 >= args.Count || !int.TryParse(args[chars + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            || max < 1 || max > WindowsMaxCharacters) return false;
        // The adapter alone decides how upstream runs its Claude session; Kannu never passes permission or tool flags.
        return !args.Any(a => a.StartsWith("--dangerously", StringComparison.Ordinal) || a is "--allowedTools" or "--disallowedTools");
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++) if (args[i] == flag) return i;
        return -1;
    }

    /// <summary>
    /// A whitelist, never the inherited environment: what uv, Python and the Claude CLI need to find
    /// themselves and the user's login. No API keys: Windows Kannu runs Detection on the Claude login only.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(string path, string userProfile, string? appData, string? localAppData,
        string? systemRoot, string? temp, string? pathExt)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = path,
            ["USERPROFILE"] = userProfile,
            ["HOME"] = userProfile,
            ["LANG"] = "en_US.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
        };
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) env[key] = value;
        }
        Add("APPDATA", appData);
        Add("LOCALAPPDATA", localAppData);
        Add("SystemRoot", systemRoot);
        Add("TEMP", temp);
        Add("TMP", temp);
        Add("PATHEXT", pathExt);
        return env;
    }

    /// <summary>
    /// How Detection's <c>subprocess.run(["claude", …])</c> resolves on Windows: no shell, so CreateProcess appends
    /// only <c>.exe</c>. The native installer's <c>claude.exe</c> is found; npm's <c>claude.cmd</c> is not.
    /// </summary>
    public sealed record ClaudeResolution(string? Exe, bool CmdOnly);

    public static ClaudeResolution ResolveClaude(IEnumerable<string> pathDirectories, Func<string, bool> exists)
    {
        var cmd = false;
        foreach (var dir in pathDirectories)
        {
            string exe, shim;
            try
            {
                exe = Path.Combine(dir, "claude.exe");
                shim = Path.Combine(dir, "claude.cmd");
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (exists(exe)) return new ClaudeResolution(exe, false);
            cmd |= exists(shim);
        }
        return new ClaudeResolution(null, cmd);
    }

    public enum CheckoutState
    {
        NotConfigured,
        Invalid,
        Ready,
    }

    /// <summary>What a usable Detection checkout must hold, and why it is not usable otherwise.</summary>
    public static (CheckoutState State, string Reason) ValidateCheckout(string? path, string? uv, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var root = path?.Trim().Trim('"');
        if (string.IsNullOrEmpty(root)) return (CheckoutState.NotConfigured, "No checkout chosen");
        if (!fileExists(Path.Combine(root, "pyproject.toml"))) return (CheckoutState.Invalid, "No pyproject.toml here — choose the ADR\\Detection folder");
        if (!fileExists(Path.Combine(root, "guardrail", "adr_agent", "adr_baseline.py"))) return (CheckoutState.Invalid, "This is not ADR's Detection folder (guardrail\\adr_agent missing)");
        if (!directoryExists(Path.Combine(root, ".venv"))) return (CheckoutState.Invalid, "Not synced yet — run `uv sync` in this folder");
        if (uv is null) return (CheckoutState.Invalid, "uv not found (winget install astral-sh.uv)");
        return (CheckoutState.Ready, $"Ready — uv at {uv}");
    }

    /// <summary>A finished Claude Code chat on disk, for "Analyze a finished chat…".</summary>
    public sealed record Transcript(string Path, string ConversationId, string Project, long ModifiedMs, long Bytes);

    /// <summary>The newest top-level transcripts in <c>~\.claude\projects\&lt;project&gt;\&lt;id&gt;.jsonl</c> (no subagent files).</summary>
    public static IReadOnlyList<Transcript> RecentTranscripts(string projectsDirectory, int count)
    {
        var found = new List<Transcript>();
        try
        {
            if (!System.IO.Directory.Exists(projectsDirectory)) return found;
            foreach (var project in new DirectoryInfo(projectsDirectory).EnumerateDirectories())
            {
                if (project.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    foreach (var file in project.EnumerateFiles("*.jsonl"))
                    {
                        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length == 0) continue;
                        found.Add(new Transcript(file.FullName, System.IO.Path.GetFileNameWithoutExtension(file.Name), project.Name,
                            new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), file.Length));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return found.OrderByDescending(t => t.ModifiedMs).Take(count).ToList();
    }
}

/// <summary>One ADR Detection verdict on one finished chat (port of macOS <c>ADRSessionAnalysis</c>).</summary>
public sealed record AdrAnalysis
{
    public const string Source = "detection";
    public const string RulePrefix = "detection_";
    public const int Cap = 50;

    public required string ConversationId { get; init; }
    public string? ChatName { get; init; }
    public long DateMs { get; init; }
    public bool IsMalicious { get; init; }
    public double Confidence { get; init; }
    public string? Tactic { get; init; }
    public string Explanation { get; init; } = "";
    public int? ThreatMessages { get; init; }
    public int? TotalMessages { get; init; }
    public string? Method { get; init; }
    public string? ModelUsed { get; init; }
    public double? AnalysisSeconds { get; init; }
    public int? MessagesAnalyzed { get; init; }
    public int? InputCharacters { get; init; }
    public long? TranscriptBytes { get; init; }
    public string? ReportPath { get; init; }

    /// <summary>The adapter's stdout JSON (schema 1). An <c>error</c> key is the adapter saying why it could not run: surfaced, never a clean verdict.</summary>
    public static AdrAnalysis Parse(string line, string conversationId, string? chatName, string? reportPath, long? transcriptBytes, long nowMs)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            throw AdrVerdictException.NotAVerdict();
        }
        if (json is null || Number(json["schema"]) != 1) throw AdrVerdictException.NotAVerdict();
        if (json["error"] is JsonValue error && error.TryGetValue<string>(out var reason)) throw AdrVerdictException.Adapter(reason);
        if (json["is_malicious"] is not JsonValue m || !m.TryGetValue<bool>(out var malicious)) throw AdrVerdictException.NotAVerdict();
        int? Int(string key) => Number(json[key]) is { } d ? (int)d : null;
        string? Text(string key) => json[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new AdrAnalysis
        {
            ConversationId = conversationId,
            ChatName = chatName,
            DateMs = nowMs,
            IsMalicious = malicious,
            Confidence = Math.Clamp(Number(json["confidence"]) ?? 0, 0, 1),
            Tactic = Text("tactic") is { Length: > 0 } t ? t : null,
            Explanation = (Text("explanation") ?? "").Trim(),
            ThreatMessages = Int("threat_messages"),
            TotalMessages = Int("total_messages"),
            Method = Text("method"),
            ModelUsed = Text("model_used"),
            AnalysisSeconds = Number(json["analysis_seconds"]),
            MessagesAnalyzed = Int("messages_analyzed"),
            InputCharacters = Int("input_characters"),
            TranscriptBytes = transcriptBytes,
            ReportPath = reportPath,
        };
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    /// <summary>ADR's five tactics as sentences; anything else is prettified.</summary>
    public static string TitleForTactic(string? tactic) => tactic?.ToLowerInvariant() switch
    {
        "initial_compromise" => "Initial compromise attempt in this chat",
        "permission_abuse" => "Permission abuse in this chat",
        "security_control_bypass" => "Security control bypass in this chat",
        "reasoning_data_manipulation" => "Prompt or data manipulation in this chat",
        "operational_impact" => "Harmful operational impact in this chat",
        { Length: > 0 } => Pretty(tactic!),
        _ => "Malicious activity in this chat",
    };

    private static string Pretty(string tactic)
    {
        var words = tactic.Split('_');
        var first = words[0].Length > 0 ? char.ToUpperInvariant(words[0][0]) + words[0][1..] : "";
        return first + " " + string.Join(" ", words.Skip(1)) + " in this chat";
    }

    /// <summary>"clean · 0.08", "permission abuse · 0.91".</summary>
    public string ShortLabel
    {
        get
        {
            var score = Confidence.ToString("0.00", CultureInfo.InvariantCulture);
            return IsMalicious ? $"{(Tactic ?? "malicious").Replace('_', ' ')} · {score}" : $"clean · {score}";
        }
    }

    /// <summary>A malicious verdict is a finding: high when confidence ≥ 0.8, medium otherwise. A clean one is a record only.</summary>
    public SecurityFinding? Finding(long? existingFirstSeenMs = null)
    {
        if (!IsMalicious) return null;
        var rule = RulePrefix + (Tactic ?? "malicious_session");
        var evidence = new List<string> { "confidence " + Confidence.ToString("0.00", CultureInfo.InvariantCulture) };
        if (ThreatMessages is { } threat && TotalMessages is { } total) evidence.Add($"{threat} of {total} messages flagged");
        if (ModelUsed is { Length: > 0 } model) evidence.Add("model " + model);
        return new SecurityFinding
        {
            Id = SecurityFindings.StableId(Source, rule, ConversationId, evidence),
            Source = Source,
            Rule = rule,
            Severity = Confidence >= 0.8 ? FindingSeverity.High : FindingSeverity.Medium,
            Title = TitleForTactic(Tactic),
            Summary = Explanation.Length == 0 ? "ADR Detection judged this chat malicious." : Explanation[..Math.Min(240, Explanation.Length)],
            Evidence = evidence,
            AssetName = ChatName,
            RevealPath = ReportPath,
            FirstSeenMs = existingFirstSeenMs ?? DateMs,
            LastSeenMs = DateMs,
            ChatName = ChatName,
            // Stays per chat: a verdict is about one conversation. Re-analysing at 0.92 or with another model stays one row.
            GroupSubject = $"{ConversationId}|{rule}",
        };
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = ConversationId, ["date"] = DateMs, ["malicious"] = IsMalicious, ["confidence"] = Confidence, ["explanation"] = Explanation,
        };
        if (ChatName is { } chat) o["chat"] = chat;
        if (Tactic is { } tactic) o["tactic"] = tactic;
        if (ThreatMessages is { } threat) o["threat"] = threat;
        if (TotalMessages is { } total) o["total"] = total;
        if (Method is { } method) o["method"] = method;
        if (ModelUsed is { } model) o["model"] = model;
        if (AnalysisSeconds is { } seconds) o["seconds"] = seconds;
        if (MessagesAnalyzed is { } analyzed) o["analyzed"] = analyzed;
        if (InputCharacters is { } chars) o["chars"] = chars;
        if (TranscriptBytes is { } bytes) o["bytes"] = bytes;
        if (ReportPath is { } report) o["report"] = report;
        return o;
    }

    public static AdrAnalysis? FromJson(JsonObject o)
    {
        if (o["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)) return null;
        string? Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? Int(string key) => Number(o[key]) is { } d ? (int)d : null;
        return new AdrAnalysis
        {
            ConversationId = id,
            ChatName = Text("chat"),
            DateMs = (long)(Number(o["date"]) ?? 0),
            IsMalicious = o["malicious"] is JsonValue m && m.TryGetValue<bool>(out var b) && b,
            Confidence = Math.Clamp(Number(o["confidence"]) ?? 0, 0, 1),
            Tactic = Text("tactic"),
            Explanation = Text("explanation") ?? "",
            ThreatMessages = Int("threat"),
            TotalMessages = Int("total"),
            Method = Text("method"),
            ModelUsed = Text("model"),
            AnalysisSeconds = Number(o["seconds"]),
            MessagesAnalyzed = Int("analyzed"),
            InputCharacters = Int("chars"),
            TranscriptBytes = Number(o["bytes"]) is { } bytes ? (long)bytes : null,
            ReportPath = Text("report"),
        };
    }
}

public sealed class AdrVerdictException : Exception
{
    public bool FromAdapter { get; }

    private AdrVerdictException(string message, bool fromAdapter) : base(message) => FromAdapter = fromAdapter;

    public static AdrVerdictException NotAVerdict() => new("The adapter printed no verdict.", false);

    public static AdrVerdictException Adapter(string reason) => new(reason, true);
}
