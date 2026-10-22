using System;
using System.IO;
using AoForge.Services;
using Xunit;

namespace AoForge.Tests;

public sealed class SelfUpdaterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "AoForgeSelfUpdate", Guid.NewGuid().ToString("N"));

    public SelfUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("v0.10.0", "0.9.3", true)]
    [InlineData("v1.0", "0.9.9", true)]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("v0.2.0", "0.2.0", false)]
    [InlineData("v0.1.9", "0.2.0", false)]
    [InlineData("v0.3.0-beta", "0.2.0", true)]
    [InlineData("latest", "0.2.0", false)]
    [InlineData(null, "0.2.0", false)]
    public void Newer_release_is_detected(string tag, string current, bool newer) =>
        Assert.Equal(newer, SelfUpdater.IsNewer(tag, Version.Parse(current)));

    [Fact]
    public void Apply_swaps_the_exe_and_keeps_the_old_one_until_cleanup()
    {
        string exe = Path.Combine(_dir, "AoForge.exe");
        string download = Path.Combine(_dir, "download.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(download, "new");

        // Like a running exe: renaming is allowed, deleting/overwriting isn't.
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            SelfUpdater.Apply(download, exe);

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));

        SelfUpdater.CleanupOld(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void Apply_replaces_a_leftover_old_file()
    {
        string exe = Path.Combine(_dir, "AoForge.exe");
        string download = Path.Combine(_dir, "download.exe");
        File.WriteAllText(exe, "v2");
        File.WriteAllText(exe + ".old", "v1");
        File.WriteAllText(download, "v3");

        SelfUpdater.Apply(download, exe);

        Assert.Equal("v3", File.ReadAllText(exe));
        Assert.Equal("v2", File.ReadAllText(exe + ".old"));
    }

    [Fact]
    public void Verify_accepts_only_aoforge_at_the_release_version()
    {
        string exe = typeof(SelfUpdater).Assembly.Location; // this build's AoForge.exe
        string tag = "v" + SelfUpdater.Current.ToString(3);
        SelfUpdater.Verify(exe, tag);

        Assert.Throws<InvalidDataException>(() => SelfUpdater.Verify(exe, "v99.0.0"));

        string other = Path.Combine(_dir, "other.exe");
        File.WriteAllText(other, "not a program");
        Assert.Throws<InvalidDataException>(() => SelfUpdater.Verify(other, tag));
    }

    [Fact]
    public void Writable_folder_can_be_replaced() =>
        Assert.True(SelfUpdater.CanReplace(Path.Combine(_dir, "AoForge.exe")));
}
