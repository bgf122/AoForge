using System.Collections.Generic;
using AoForge.Services;
using Xunit;

namespace AoForge.Tests;

public sealed class GitHubReleasesTests
{
    [Fact]
    public void Latest_skips_drafts_and_prereleases()
    {
        var releases = new List<GhRelease> {
            new() { TagName = "v0.3.0", Draft = true },
            new() { TagName = "v0.2.1-beta", Prerelease = true },
            new() { TagName = "v0.2.0" },
            new() { TagName = "v0.1.0" },
        };
        Assert.Equal("v0.2.0", GitHubReleases.Latest(releases).TagName);
    }

    [Fact]
    public void Latest_of_no_releases_is_null() =>
        Assert.Null(GitHubReleases.Latest(new List<GhRelease>()));
}
