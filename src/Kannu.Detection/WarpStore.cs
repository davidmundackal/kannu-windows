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

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kannu.Core;

namespace Kannu.Detection;

/// <summary>
/// Warp's agent mode, which has no hooks: <c>warp.sqlite</c>'s <c>ai_queries</c> holds one row per
/// exchange with its status (Pending, Completed, Cancelled, Failed), start time and directory. Enough
/// for green and red and a run error, never for yellow. Port of macOS <c>WarpAgentStore</c>.
/// </summary>
public sealed class WarpStore(IReadOnlyList<string> candidateDatabases)
{
    public const string ProviderKey = "warp";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private (string Path, DateTime At, IReadOnlyList<Exchange> Exchanges)? _cache;

    /// <summary>Where Warp keeps its database on Windows (the first that exists is read).</summary>
    public static IReadOnlyList<string> DefaultCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Path.Combine(local, "warp", "Warp", "data", "warp.sqlite"),
            Path.Combine(local, "warp", "Warp-Stable", "data", "warp.sqlite"),
            Path.Combine(roaming, "warp", "Warp", "data", "warp.sqlite"),
        ];
    }

    public string? Database => candidateDatabases.FirstOrDefault(File.Exists);

    public static bool IsWarpRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("warp");
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <param name="Status">Pending, Completed, Cancelled or Failed, quotes stripped.</param>
    /// <param name="InputPrefix">The first bytes of the input JSON: enough for the prompt.</param>
    public sealed record Exchange(string ExchangeId, string ConversationId, long? StartedAtMs, string Status, string? WorkingDirectory, string? InputPrefix);

    public IReadOnlyList<AgentSession> Sessions(AgentTimings timings, long nowMs, bool warpRunning)
    {
        if (Database is not { } database) return [];
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        if (_cache is not { } c || c.Path != database || nowUtc - c.At >= CacheTtl)
        {
            _cache = (database, nowUtc, Query(database, nowUtc.AddMinutes(-timings.StaleMinutes)));
        }
        return SessionsFrom(_cache.Value.Exchanges, timings, nowMs, warpRunning);
    }

    /// <summary>Pure: exchanges (newest first) to one session per conversation.</summary>
    public static IReadOnlyList<AgentSession> SessionsFrom(IReadOnlyList<Exchange> exchanges, AgentTimings timings, long nowMs, bool warpRunning)
    {
        var seen = new HashSet<string>();
        var sessions = new List<AgentSession>();
        foreach (var exchange in exchanges)
        {
            if (!seen.Add(exchange.ConversationId)) continue;
            var started = exchange.StartedAtMs ?? nowMs;
            var age = nowMs - started;
            var raw = RawState(exchange.Status, age, warpRunning);
            var (state, visible) = AgentStateMachine.ResolveHookState(raw, age, timings.CollapseMs, timings.InactiveMs);
            var failed = exchange.Status == "Failed";
            sessions.Add(new AgentSession
            {
                Id = $"{ProviderKey}-{exchange.ConversationId}",
                Provider = ProviderKey,
                ConversationId = exchange.ConversationId,
                ChatName = QueryTitle(exchange.InputPrefix),
                ProjectName = StatusPaths.ProjectName(exchange.WorkingDirectory),
                RawState = raw,
                DisplayState = state,
                UpdatedAtMs = started,
                IsVisible = visible,
                Cwd = exchange.WorkingDirectory,
                // A failed exchange is the run's outcome; a cancelled one is the user's choice.
                ToolErrorCount = failed ? 1 : 0,
                RunError = failed ? RunError.Failed : null,
            });
        }
        return sessions;
    }

    /// <summary>
    /// Pending rows are left behind by interrupted runs, so pending counts as running only while it
    /// is younger than the active window and Warp is running (REGRESSIONS entry 2's 360 s).
    /// </summary>
    public static string RawState(string status, long ageMs, bool warpRunning) => NormalizedStatus(status) switch
    {
        "Pending" => warpRunning && ageMs <= AgentStateMachine.ActiveStaleMs ? "executing" : "aborted",
        "Completed" => "stopped",
        "Cancelled" => "aborted",
        "Failed" => "error",
        _ => "stopped",
    };

    public static string NormalizedStatus(string raw) => raw.Trim('"', ' ', '\n');

    /// <summary>The prompt from <c>[{"Query":{"text":"…"}}]</c>, read from a truncated prefix. Warp has no titles.</summary>
    public static string? QueryTitle(string? prefix)
    {
        if (prefix is null) return null;
        var query = prefix.IndexOf("\"Query\"", StringComparison.Ordinal);
        if (query < 0) return null;
        var key = prefix.IndexOf("\"text\":\"", query, StringComparison.Ordinal);
        if (key < 0) return null;
        var result = new StringBuilder();
        var escaped = false;
        foreach (var ch in prefix[(key + 8)..])
        {
            if (escaped)
            {
                escaped = false;
                if (ch is 'n' or 'r' or 't') result.Append(' ');
                else if (ch == 'u') break; // a \u escape: stop rather than misdecode
                else result.Append(ch);
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (ch == '"' || result.Length >= 60) break;
            result.Append(ch);
        }
        var trimmed = result.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }

    /// <summary><c>2026-06-06 19:20:37.931790</c> (UTC), with or without the fraction.</summary>
    public static long? ParseTimestamp(string? text) =>
        text is not null && DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : null;

    /// <summary>
    /// Exchanges since <paramref name="sinceUtc"/>, newest first, capped. Read-only with the WAL
    /// honoured: the main file lags the log on a busy install.
    /// </summary>
    private static IReadOnlyList<Exchange> Query(string database, DateTime sinceUtc)
    {
        var exchanges = new List<Exchange>();
        try
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            };
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT exchange_id, conversation_id, start_ts, output_status, working_directory, substr(input, 1, 400)
                FROM ai_queries WHERE start_ts >= $since ORDER BY start_ts DESC LIMIT 64
                """;
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                if (Text(0) is not { } exchangeId || Text(1) is not { } conversationId) continue;
                exchanges.Add(new Exchange(exchangeId, conversationId, ParseTimestamp(Text(2)), NormalizedStatus(Text(3) ?? ""), Text(4), Text(5)));
            }
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }
        return exchanges;
    }
}
