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

using System.Text.RegularExpressions;
using Kannu.Core;

namespace Kannu.Core.Tests;

public class ReleaseInfoTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine([dir, .. parts]);
    }

    [Fact]
    public void TheVersionIsTheOneInDirectoryBuildProps()
    {
        var props = File.ReadAllText(RepoFile("Directory.Build.props"));
        Assert.Equal(Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value, ReleaseInfo.Version);
    }

    [Fact]
    public void EveryVersionHasReleaseNotes() =>
        Assert.True(File.Exists(RepoFile("docs", "release-notes", ReleaseInfo.Version + ".md")),
            $"docs/release-notes/{ReleaseInfo.Version}.md is the release's body; write it when bumping <Version>.");

    [Fact]
    public void BuildMetadataIsDropped() => Assert.Equal("1.2.3-test.1", ReleaseInfo.WithoutMetadata("1.2.3-test.1+abc123"));
}
