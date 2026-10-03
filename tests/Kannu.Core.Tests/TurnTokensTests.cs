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
using Kannu.Core;

namespace Kannu.Core.Tests;

public sealed class TurnTokensTests : IDisposable
{
    private const long T0 = 1_790_000_000_000;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "kannu-tokens-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string Assistant(long ms, int input, int output, string id = "m1", string req = "r1", int cacheRead = 0) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + Iso(ms) + "\",\"requestId\":\"" + req + "\",\"message\":{\"id\":\"" + id
        + "\",\"model\":\"claude\",\"usage\":{\"input_tokens\":" + input + ",\"cache_read_input_tokens\":" + cacheRead
        + ",\"output_tokens\":" + output + "}}}\n";

    [Theory]
    [InlineData(812, "812")]
    [InlineData(4_500, "4.5k")]
    [InlineData(10_000, "10k")]
    [InlineData(999_600, "1M")]
    [InlineData(1_400_000, "1.4M")]
    [InlineData(1_200_000_000, "1.2B")]
    public void CompactRollsOverUnits(long value, string expected) => Assert.Equal(expected, TurnTokens.Compact(value));

    [Fact]
    public void SplitRecordsOfOneMessageCountOnceAndCacheReadsAreInput()
    {
        var acc = new TranscriptTokenAccumulator(0, T0);
        acc.Consume(Encoding.UTF8.GetBytes(Assistant(T0 + 1, 10, 5, cacheRead: 100) + Assistant(T0 + 2, 10, 5, cacheRead: 100) + Assistant(T0 + 3, 1, 1, "m2")));
        Assert.Equal(111, acc.Input);
        Assert.Equal(6, acc.Output);
    }

    [Fact]
    public void RecordsBeforeTheWindowAndUserRecordsDoNotCount()
    {
        var acc = new TranscriptTokenAccumulator(0, T0);
        acc.Consume(Encoding.UTF8.GetBytes(Assistant(T0 - 1, 50, 50, "old") + """{"type":"user","usage":{"input_tokens":9}}""" + "\n"));
        Assert.Equal(0, acc.Input + acc.Output);
    }

    [Fact]
    public void ALineSplitAcrossChunksIsCountedOnceWhole()
    {
        var bytes = Encoding.UTF8.GetBytes(Assistant(T0 + 1, 7, 3));
        var acc = new TranscriptTokenAccumulator(0, T0);
        acc.Consume(bytes.AsSpan(0, 20));
        Assert.Equal(0, acc.Input);
        acc.Consume(bytes.AsSpan(20));
        Assert.Equal(7, acc.Input);
        Assert.Equal(bytes.Length, acc.ReadOffset);
    }

    [Fact]
    public void AReplacedOrShrunkFileIsReadAgainFromTheStart()
    {
        var acc = new TranscriptTokenAccumulator(100, T0);
        Assert.Equal((100L, 50), acc.Step(150, fileId: 1));
        acc.Consume(new byte[50]);
        Assert.Null(acc.Step(150, 1));
        Assert.Equal((0L, 150), acc.Step(150, fileId: 2));
        Assert.Equal((0L, 10), new TranscriptTokenAccumulator(100, T0).Step(10, 1));
    }

    [Fact]
    public void TheReaderCountsTheTurnFromItsOffsetPlusSubagents()
    {
        var project = Path.Combine(_home, ".claude", "projects", "p");
        Directory.CreateDirectory(Path.Combine(project, "s", "subagents"));
        var main = Path.Combine(project, "s.jsonl");
        var before = Assistant(T0 - 120_000, 1000, 1000, "earlier");
        File.WriteAllText(main, before + Assistant(T0 + 1_000, 20, 10, "now"));
        File.WriteAllText(Path.Combine(project, "s", "subagents", "agent-a.jsonl"), Assistant(T0 + 2_000, 5, 5, "sub"));

        var reader = new TurnTokenReader(Path.Combine(_home, ".claude", "projects"));
        var request = new TurnTokenRequest("s", main, Encoding.UTF8.GetByteCount(before), T0);
        var pass = reader.Read([request], T0 + 5_000);
        Assert.False(pass.Behind);
        Assert.Equal(new TurnTokens(T0, request.StartOffset, 25, 15), pass.Tokens["s"]);
    }

    [Fact]
    public void AReaderBehindItsBudgetReportsNothingYetRatherThanAPartialCount()
    {
        var project = Path.Combine(_home, ".claude", "projects", "p");
        Directory.CreateDirectory(project);
        var main = Path.Combine(project, "s.jsonl");
        File.WriteAllText(main, Assistant(T0 + 1, 1, 1) + Assistant(T0 + 2, 1, 1, "m2"));
        var reader = new TurnTokenReader(Path.Combine(_home, ".claude", "projects")) { PassBudget = 10 };
        var pass = reader.Read([new TurnTokenRequest("s", main, 0, T0)], T0);
        Assert.True(pass.Behind);
        Assert.Empty(pass.Tokens);
    }

    [Fact]
    public void ATranscriptOutsideTheProjectsFolderIsNeverRead()
    {
        Directory.CreateDirectory(_home);
        var outside = Path.Combine(_home, "secret.jsonl");
        File.WriteAllText(outside, Assistant(T0 + 1, 1, 1));
        var pass = new TurnTokenReader(Path.Combine(_home, ".claude", "projects")).Read([new TurnTokenRequest("s", outside, 0, T0)], T0);
        Assert.Empty(pass.Tokens);
    }
}

public class ProviderAppsTests
{
    [Fact]
    public void TheFirstExistingInstallIsUsed()
    {
        var found = ProviderApps.FindExecutable("vscode", "L", "P", p => p.StartsWith('P'));
        Assert.Equal(Path.Combine("P", @"Microsoft VS Code\Code.exe"), found);
        Assert.Null(ProviderApps.FindExecutable("gemini", "L", "P", _ => true));
    }

    [Fact]
    public void TerminalAgentsGetAnInitial() => Assert.Equal("Q", ProviderApps.For("qwen").Initial);
}
