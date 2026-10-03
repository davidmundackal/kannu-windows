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
/// One usage window: how full a limit is and when it resets. Port of macOS
/// <c>ClaudeUsageSnapshot.Window</c> and <c>UsageWindowReading</c>.
/// </summary>
/// <param name="Percent">0 to 100.</param>
/// <param name="ResetsAtMs">Unix milliseconds; null when not reported.</param>
/// <param name="Severity">Claude's own "normal", "warning" or "critical", when it sends one.</param>
public sealed record UsageWindow(string Key, string? Label, double Percent, long? ResetsAtMs, string? Severity, long ObservedAtMs)
{
    public const string FiveHour = "five_hour";
    public const string SevenDay = "seven_day";

    public bool IsLive(long nowMs) => ResetsAtMs is not { } reset || reset > nowMs;

    /// <summary>The bar's title on the Usage tab.</summary>
    public string Title => Key switch
    {
        FiveHour => "5-hour",
        SevenDay => "Weekly · all models",
        "seven_day_opus" => "Weekly · Opus",
        "seven_day_sonnet" => "Weekly · Sonnet",
        _ when !string.IsNullOrEmpty(Label) => "Weekly · " + Label,
        _ => Key.Replace('_', ' '),
    };

    /// <summary>The short name in a near-limit notification ("5-hour", "weekly", "Fable weekly").</summary>
    public string AlertLabel => Key switch
    {
        FiveHour => "5-hour",
        SevenDay => "weekly",
        _ when !string.IsNullOrEmpty(Label) => Label + " weekly",
        _ => Key.Replace('_', ' '),
    };
}

/// <summary>
/// Claude Code's plan limits, read locally only: from Kannu's statusline (which Claude Code feeds its
/// <c>rate_limits</c>) and from the cache Claude Code keeps in <c>~/.claude.json</c>. No network call,
/// no credentials (D4). Port of macOS <c>ClaudeUsageSnapshot</c> and <c>ClaudeCachedUsage</c>.
/// </summary>
public static class ClaudeUsage
{
    /// <summary>In the status folder, the same name and shape macOS Kannu uses.</summary>
    public const string FileName = "claude-usage.json";

    private static readonly string[] CacheWindows =
        ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "seven_day_oauth_apps", "cinder_cove"];

    // ---- Statusline ----

    /// <summary>
    /// What Claude Code's statusline input says about limits, as the file Kannu keeps, plus the line
    /// the statusline shows when no other statusline is chained ("Opus | 5h 32%@14:05 | 7d 41%@Tue").
    /// Null when the input carries no limits (an API-key login, or an older Claude Code).
    /// </summary>
    public static (string FileJson, string Line)? FromStatusline(string stdin, long nowMs, TimeZoneInfo zone)
    {
        JsonObject root;
        try
        {
            if (JsonNode.Parse(stdin) is not JsonObject parsed) return null;
            root = parsed;
        }
        catch (JsonException)
        {
            return null;
        }
        if (root["rate_limits"] is not JsonObject limits) return null;

        var windows = new JsonArray();
        var parts = new List<string>();
        if (Str(root["model"]?["display_name"]) is { Length: > 0 } model) parts.Add(model);
        foreach (var (key, value) in limits)
        {
            if (key == "model_scoped" && value is JsonArray scoped)
            {
                foreach (var entry in scoped.OfType<JsonObject>())
                {
                    if (Number(entry["utilization"]) is not { } pct || Str(entry["display_name"]) is not { Length: > 0 } name) continue;
                    windows.Add((JsonNode)Window("model_scoped:" + name, name, pct, EpochSeconds(entry["resets_at"]), Str(entry["severity"])));
                }
                continue;
            }
            if (value is not JsonObject window || Number(window["used_percentage"]) is not { } used) continue;
            var resets = EpochSeconds(window["resets_at"]);
            windows.Add((JsonNode)Window(key, null, used, resets, Str(window["severity"])));
            var at = resets is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).ToOffset(zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(s))) : (DateTimeOffset?)null;
            if (key == UsageWindow.FiveHour) parts.Add($"5h {Math.Round(used):0}%" + (at is { } a ? "@" + a.ToString("HH:mm", CultureInfo.InvariantCulture) : ""));
            if (key == UsageWindow.SevenDay) parts.Add($"7d {Math.Round(used):0}%" + (at is { } b ? "@" + b.ToString("ddd", CultureInfo.InvariantCulture) : ""));
        }
        if (windows.Count == 0) return null;

        var file = new JsonObject { ["ts"] = nowMs, ["windows"] = windows };
        if (Str(root["session_id"]) is { Length: > 0 } session) file["session_id"] = session;
        return (file.ToJsonString(), string.Join(" | ", parts));

        static JsonObject Window(string key, string? label, double pct, long? resets, string? severity)
        {
            var w = new JsonObject { ["key"] = key, ["pct"] = Math.Round(Math.Clamp(pct, 0, 100), 1) };
            if (label is not null) w["label"] = label;
            if (resets is { } r) w["resets_at"] = r;
            if (severity is { Length: > 0 }) w["severity"] = severity;
            return w;
        }
    }

    /// <summary>Kannu's statusline file (or macOS Kannu's, same shape, with its legacy flat keys).</summary>
    public static IReadOnlyList<UsageWindow> ParseStatuslineFile(string json)
    {
        if (Parse(json) is not { } root || Number(root["ts"]) is not { } ts || ts <= 0) return [];
        var observed = (long)ts;
        var result = new List<UsageWindow>();
        if (root["windows"] is JsonArray windows)
        {
            foreach (var w in windows.OfType<JsonObject>())
            {
                if (Str(w["key"]) is not { Length: > 0 } key || Number(w["pct"]) is not { } pct) continue;
                result.Add(new UsageWindow(key, Str(w["label"]), Math.Clamp(pct, 0, 100), EpochSeconds(w["resets_at"]) * 1000, Str(w["severity"]), observed));
            }
        }
        else
        {
            foreach (var key in new[] { UsageWindow.FiveHour, UsageWindow.SevenDay })
            {
                if (Number(root[key + "_pct"]) is not { } pct) continue;
                result.Add(new UsageWindow(key, null, Math.Clamp(pct, 0, 100), EpochSeconds(root[key + "_resets_at"]) * 1000, null, observed));
            }
        }
        return result;
    }

    // ---- ~/.claude.json ----

    /// <summary>
    /// Claude Code's own cache of its last <c>/usage</c>. Ignored when it belongs to another login
    /// (its account id differs from the signed-in one).
    /// </summary>
    public static IReadOnlyList<UsageWindow> ParseCache(string claudeJson)
    {
        if (Parse(claudeJson) is not { } root || root["cachedUsageUtilization"] is not JsonObject cache
            || Number(cache["fetchedAtMs"]) is not { } fetched || fetched <= 0 || cache["utilization"] is not JsonObject utilization) return [];
        if (Str(cache["accountUuid"]) is { } cached && Str(root["oauthAccount"]?["accountUuid"]) is { } current && cached != current) return [];

        var limits = utilization["limits"] as JsonArray ?? [];
        var severityByKind = new Dictionary<string, string>();
        foreach (var entry in limits.OfType<JsonObject>())
        {
            if (Str(entry["kind"]) is { } kind && Str(entry["severity"]) is { Length: > 0 } severity) severityByKind[kind] = severity;
        }
        var observed = (long)fetched;
        var result = new List<UsageWindow>();
        foreach (var key in CacheWindows)
        {
            if (utilization[key] is not JsonObject bucket || Number(bucket["utilization"]) is not { } pct) continue;
            var kind = key switch { "five_hour" => "session", "seven_day" => "weekly_all", _ => key };
            result.Add(new UsageWindow(key, null, Math.Clamp(pct, 0, 100), IsoMs(bucket["resets_at"]),
                severityByKind.GetValueOrDefault(kind), observed));
        }
        foreach (var entry in limits.OfType<JsonObject>())
        {
            if (Str(entry["kind"]) != "weekly_scoped" || Str(entry["scope"]?["model"]?["display_name"]) is not { Length: > 0 } name
                || Number(entry["percent"]) is not { } pct) continue;
            result.Add(new UsageWindow("model_scoped:" + name, name, Math.Clamp(pct, 0, 100), IsoMs(entry["resets_at"]), Str(entry["severity"]), observed));
        }
        return result;
    }

    // ---- Merge ----

    /// <summary>
    /// Per window, the newest observation wins (ties: the earlier source); windows past their reset
    /// are dropped. Order: 5-hour, weekly, then the rest by title.
    /// </summary>
    public static IReadOnlyList<UsageWindow> Merge(long nowMs, params IReadOnlyList<UsageWindow>[] sources)
    {
        var best = new Dictionary<string, UsageWindow>();
        foreach (var source in sources)
        {
            foreach (var window in source)
            {
                if (!window.IsLive(nowMs)) continue;
                if (!best.TryGetValue(window.Key, out var held) || window.ObservedAtMs > held.ObservedAtMs) best[window.Key] = window;
            }
        }
        return best.Values
            .OrderBy(w => w.Key == UsageWindow.FiveHour ? 0 : w.Key == UsageWindow.SevenDay ? 1 : 2)
            .ThenBy(w => w.Title, StringComparer.Ordinal)
            .ToList();
    }

    // ---- JSON helpers ----

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    /// <summary>Epoch seconds, from a number or an ISO 8601 string.</summary>
    private static long? EpochSeconds(JsonNode? node)
    {
        if (Number(node) is { } n && n > 0) return (long)n;
        return IsoMs(node) is { } ms ? ms / 1000 : null;
    }

    private static long? IsoMs(JsonNode? node) =>
        Str(node) is { Length: > 0 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;
}

/// <summary>
/// Where a usage window is heading, from Kannu's own readings of it over time. Port of macOS
/// <c>UsageForecast</c>, constants and all.
/// </summary>
public static class UsageForecast
{
    public readonly record struct Sample(long AtMs, double Percent, long? ResetsAtMs);

    public abstract record Outlook
    {
        public sealed record InsufficientData : Outlook;
        public sealed record Steady : Outlook;
        public sealed record LastsUntilReset(double Projected) : Outlook;
        public sealed record HitsLimit(long AtMs) : Outlook;
        public sealed record AtLimit : Outlook;
    }

    public const long MinSampleIntervalMs = 120_000;
    public const int MaxSamples = 240;
    public const double RestartDrop = 5;
    public const long ResetShiftToleranceMs = 600_000;
    public const double SteadySlopePerHour = 0.1;
    public const double AtLimitPercent = 99.5;
    public const int MinimumSamples = 3;
    public const long HorizonWithoutResetMs = 7L * 24 * 3600_000;

    private static bool IsShort(string key) => key is UsageWindow.FiveHour or "session";

    private static long LookbackMs(string key) => IsShort(key) ? 90 * 60_000L : 24 * 3600_000L;

    private static long MinimumSpanMs(string key) => IsShort(key) ? 15 * 60_000L : 3 * 3600_000L;

    /// <summary>Adds one reading. Repeats and readings closer than 2 minutes are skipped; a rollover starts over.</summary>
    public static IReadOnlyList<Sample> Admitting(Sample sample, IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0) return [sample];
        var last = samples[^1];
        if (sample.AtMs <= last.AtMs) return samples;
        if (sample.Percent < last.Percent - RestartDrop) return [sample];
        if (sample.ResetsAtMs is { } next && last.ResetsAtMs is { } old && Math.Abs(next - old) > ResetShiftToleranceMs) return [sample];
        if (sample.AtMs - last.AtMs < MinSampleIntervalMs) return samples;
        var result = samples.Append(sample).ToList();
        if (result.Count > MaxSamples) result.RemoveRange(0, result.Count - MaxSamples);
        return result;
    }

    /// <summary>Least-squares pace over the look-back, projected to 100 % and compared with the reset.</summary>
    public static Outlook For(IReadOnlyList<Sample> samples, UsageWindow current, long nowMs)
    {
        if (current.Percent >= AtLimitPercent) return new Outlook.AtLimit();
        var recent = samples.Where(s => s.AtMs <= nowMs && nowMs - s.AtMs <= LookbackMs(current.Key)).ToList();
        if (recent.Count < MinimumSamples || recent[^1].AtMs - recent[0].AtMs < MinimumSpanMs(current.Key)) return new Outlook.InsufficientData();
        var xs = recent.Select(s => (s.AtMs - recent[0].AtMs) / 3_600_000.0).ToList();
        var meanX = xs.Average();
        var meanY = recent.Average(s => s.Percent);
        double numerator = 0, denominator = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            numerator += (xs[i] - meanX) * (recent[i].Percent - meanY);
            denominator += (xs[i] - meanX) * (xs[i] - meanX);
        }
        if (denominator <= 0) return new Outlook.InsufficientData();
        var perHour = numerator / denominator;
        if (perHour <= SteadySlopePerHour) return new Outlook.Steady();
        var eta = nowMs + (long)((100 - current.Percent) / perHour * 3_600_000);
        if (current.ResetsAtMs is { } reset)
        {
            return eta >= reset
                ? new Outlook.LastsUntilReset(Math.Min(100, current.Percent + perHour * (reset - nowMs) / 3_600_000.0))
                : new Outlook.HitsLimit(eta);
        }
        return eta - nowMs > HorizonWithoutResetMs ? new Outlook.Steady() : new Outlook.HitsLimit(eta);
    }

    /// <summary>The line under a bar, only when it says something. <paramref name="clock"/> formats a time.</summary>
    public static (string Text, bool IsWarning)? Caption(Outlook outlook, long? resetsAtMs, Func<long, string> clock) => outlook switch
    {
        Outlook.HitsLimit hit => ($"At this pace: full by {clock(hit.AtMs)}", true),
        Outlook.AtLimit when resetsAtMs is { } reset => ($"Limit reached — resets {clock(reset)}", true),
        Outlook.AtLimit => ("Limit reached", true),
        Outlook.LastsUntilReset lasts when lasts.Projected >= 75 => ($"At this pace: about {Math.Round(lasts.Projected):0}% at reset", false),
        _ => null,
    };

    /// <summary>"resets in 2d 22h", "4h 56m", "56m".</summary>
    public static string Countdown(long untilMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, untilMs));
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }
}

/// <summary>Near-limit notifications. Port of macOS <c>UsageAlertPolicy</c>.</summary>
public static class UsageAlerts
{
    public const double NearLimitPercent = 95;

    public static bool IsNearLimit(UsageWindow window, long nowMs) =>
        window.IsLive(nowMs) && (window.Percent >= NearLimitPercent || window.Severity == "critical");

    /// <summary>One notification per window instance: the same window after its reset is a new one.</summary>
    public static string Key(UsageWindow window) => window.Key + "|" + (window.ResetsAtMs / 1000)?.ToString(CultureInfo.InvariantCulture);

    public static PushPayload Payload(UsageWindow window, Func<long, string> clock) => new(
        $"Claude {window.AlertLabel} limit at {Math.Round(window.Percent):0}%",
        window.ResetsAtMs is { } reset ? $"Resets {clock(reset)}." : "Kannu will tell you when it resets.",
        4, "usage-limit", "usage_limit");
}
