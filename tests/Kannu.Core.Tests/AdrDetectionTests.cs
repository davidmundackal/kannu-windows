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
using Kannu.Core;
using Xunit;

namespace Kannu.Core.Tests;

public sealed class AdrDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-adrt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IReadOnlyList<string> Args(AdrDetection.Options? options = null) => AdrDetection.Arguments(
        @"C:\ADR\Detection", @"C:\Users\dev\.kannu\adr\detection\" + AdrAdapter.FileName, @"C:\Users\dev\.claude\projects\p\c.jsonl",
        @"C:\Users\dev\.kannu\adr\detection\c.json", options ?? new AdrDetection.Options());

    [Fact]
    public void Arguments_run_kannus_adapter_in_the_users_checkout_within_the_windows_budget()
    {
        var args = Args();
        Assert.Equal(["run", "--project", @"C:\ADR\Detection", "python"], args.Take(4));
        Assert.Equal("off", args[args.ToList().IndexOf("--triage") + 1]);
        Assert.Equal("18000", args[args.ToList().IndexOf("--max-chars") + 1]);
        Assert.Equal("threat_intelligence,source_code,policy", args[args.ToList().IndexOf("--context") + 1]);
        Assert.True(AdrDetection.IsValidAnalysis(args));
        Assert.Equal("18000", Args(new AdrDetection.Options { MaxCharacters = 150_000 })[args.ToList().IndexOf("--max-chars") + 1]);
        Assert.Equal(TimeSpan.FromSeconds(420), AdrDetection.ProcessTimeout(300));
    }

    [Fact]
    public void Invalid_shapes_are_refused()
    {
        var args = Args().ToList();
        Assert.False(AdrDetection.IsValidAnalysis(args.Append("--dangerously-skip-permissions").ToList()));
        Assert.False(AdrDetection.IsValidAnalysis(args.Append("--allowedTools").ToList()));
        var other = args.ToList();
        other[4] = @"C:\evil.py";
        Assert.False(AdrDetection.IsValidAnalysis(other));
        var big = args.ToList();
        big[big.IndexOf("--max-chars") + 1] = "150000";
        Assert.False(AdrDetection.IsValidAnalysis(big));
        Assert.False(AdrDetection.IsValidAnalysis(["run"]));
    }

    [Fact]
    public void Environment_is_a_whitelist_with_no_keys()
    {
        var env = AdrDetection.Environment(@"C:\uv;C:\Users\dev\.local\bin", @"C:\Users\dev", @"C:\Users\dev\AppData\Roaming", null, @"C:\Windows", null, ".COM;.EXE");
        Assert.Equal(@"C:\Users\dev", env["USERPROFILE"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
        Assert.False(env.ContainsKey("LOCALAPPDATA"));
        Assert.DoesNotContain(env.Keys, k => k.Contains("KEY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Claude_resolves_like_createprocess_exe_only()
    {
        var dirs = new[] { @"C:\npm", @"C:\Users\dev\.local\bin" };
        var cmdOnly = AdrDetection.ResolveClaude(dirs, p => p == Path.Combine(@"C:\npm", "claude.cmd"));
        Assert.Null(cmdOnly.Exe);
        Assert.True(cmdOnly.CmdOnly);
        var native = AdrDetection.ResolveClaude(dirs, p => p == Path.Combine(@"C:\npm", "claude.cmd") || p == Path.Combine(@"C:\Users\dev\.local\bin", "claude.exe"));
        Assert.Equal(Path.Combine(@"C:\Users\dev\.local\bin", "claude.exe"), native.Exe);
        Assert.False(AdrDetection.ResolveClaude(dirs, _ => false).CmdOnly);
    }

    [Fact]
    public void Checkout_validation_names_what_is_missing()
    {
        Assert.Equal(AdrDetection.CheckoutState.NotConfigured, AdrDetection.ValidateCheckout(" ", "uv.exe", _ => true, _ => true).State);
        Assert.Contains("pyproject", AdrDetection.ValidateCheckout(_dir, "uv.exe", _ => false, _ => true).Reason);
        Assert.Contains("uv sync", AdrDetection.ValidateCheckout(_dir, "uv.exe", _ => true, _ => false).Reason);
        Assert.Contains("uv not found", AdrDetection.ValidateCheckout(_dir, null, _ => true, _ => true).Reason);
        Assert.Equal(AdrDetection.CheckoutState.Ready, AdrDetection.ValidateCheckout(_dir, "uv.exe", _ => true, _ => true).State);
    }

    [Fact]
    public void Verdict_becomes_a_finding_high_from_point_eight()
    {
        const string line = """{"schema":1,"is_malicious":true,"confidence":0.91,"tactic":"permission_abuse","explanation":"  Ran curl | sh.  ","threat_messages":1,"total_messages":12,"model_used":"claude-sonnet-5","triage":"off"}""";
        var analysis = AdrAnalysis.Parse(line, "conv-1", "Fix build", @"C:\r.json", 100, 5);
        Assert.Equal("permission abuse · 0.91", analysis.ShortLabel);
        var finding = analysis.Finding()!;
        // Computed independently with Python from macOS's id material.
        Assert.Equal("2300969afa0ba65b19531560", finding.Id);
        Assert.Equal("detection", finding.Source);
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal("Permission abuse in this chat", finding.Title);
        Assert.Equal("Ran curl | sh.", finding.Summary);
        Assert.Equal(["confidence 0.91", "1 of 12 messages flagged", "model claude-sonnet-5"], finding.Evidence);
        Assert.Equal("conv-1|detection_permission_abuse", finding.GroupSubject);
        Assert.Equal(FindingSeverity.Medium, (analysis with { Confidence = 0.79 }).Finding()!.Severity);
        Assert.Null((analysis with { IsMalicious = false }).Finding());
        Assert.Equal("clean · 0.79", (analysis with { IsMalicious = false, Confidence = 0.79 }).ShortLabel);
    }

    [Fact]
    public void Adapter_errors_are_surfaced_never_clean()
    {
        var e = Assert.Throws<AdrVerdictException>(() => AdrAnalysis.Parse("""{"schema":1,"error":"analysis failed: [WinError 206] The filename or extension is too long"}""", "c", null, null, null, 0));
        Assert.True(e.FromAdapter);
        Assert.Contains("WinError 206", e.Message);
        Assert.False(Assert.Throws<AdrVerdictException>(() => AdrAnalysis.Parse("""{"schema":2,"is_malicious":false}""", "c", null, null, null, 0)).FromAdapter);
        Assert.Throws<AdrVerdictException>(() => AdrAnalysis.Parse("""{"schema":1}""", "c", null, null, null, 0));
        Assert.Throws<AdrVerdictException>(() => AdrAnalysis.Parse("progress line", "c", null, null, null, 0));
    }

    [Fact]
    public void Tactic_titles_prettify_unknown_ones()
    {
        Assert.Equal("Harmful operational impact in this chat", AdrAnalysis.TitleForTactic("operational_impact"));
        Assert.Equal("Data exfiltration in this chat", AdrAnalysis.TitleForTactic("data_exfiltration"));
        Assert.Equal("Malicious activity in this chat", AdrAnalysis.TitleForTactic(null));
    }

    [Fact]
    public void Adapter_is_the_macOS_script()
    {
        Assert.StartsWith("#!/usr/bin/env python3\n# " + AdrAdapter.VersionMarker + "\n", AdrAdapter.Source);
        Assert.EndsWith("    sys.exit(main())\n", AdrAdapter.Source);
        Assert.Contains("from guardrail.adr_agent.adr_baseline import ADRBaseline", AdrAdapter.Source);
    }

    [Fact]
    public void Recent_transcripts_are_top_level_chats_newest_first()
    {
        var project = Path.Combine(_dir, "C--code-app");
        Directory.CreateDirectory(Path.Combine(project, "sub", "subagents"));
        File.WriteAllText(Path.Combine(project, "old.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(project, "new.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(project, "empty.jsonl"), "");
        File.WriteAllText(Path.Combine(project, "sub", "subagents", "agent.jsonl"), "{}\n");
        File.SetLastWriteTimeUtc(Path.Combine(project, "old.jsonl"), DateTime.UtcNow.AddDays(-1));
        var list = AdrDetection.RecentTranscripts(_dir, 10);
        Assert.Equal(["new", "old"], list.Select(t => t.ConversationId));
        Assert.Equal("C--code-app", list[0].Project);
        Assert.Single(AdrDetection.RecentTranscripts(_dir, 1));
        Assert.Empty(AdrDetection.RecentTranscripts(Path.Combine(_dir, "none"), 10));
    }
}
