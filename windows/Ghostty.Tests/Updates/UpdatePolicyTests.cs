using System;
using Ghostty.Core.Updates;
using Xunit;

namespace Ghostty.Tests.Updates;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" True ", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData(" False ", false)]
    [InlineData("0", true)]       // only canonical true/false; 1/0 diverge from Ghostty's parser
    [InlineData("1", true)]
    [InlineData("nope", true)]    // unparseable reads as enabled (the default)
    public void IsEnabled_OnlyFalseOptsOut(string? raw, bool expected)
    {
        Assert.Equal(expected, UpdatePolicy.IsEnabled(raw));
    }

    [Fact]
    public void ShouldCheckRemote_NoRecordMeansDue()
    {
        Assert.True(UpdatePolicy.ShouldCheckRemote(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ShouldCheckRemote_RecentCheckSuppresses()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.False(UpdatePolicy.ShouldCheckRemote(now - TimeSpan.FromHours(23), now));
    }

    [Fact]
    public void ShouldCheckRemote_OldCheckIsDue()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdatePolicy.ShouldCheckRemote(now - TimeSpan.FromHours(25), now));
    }

    [Fact]
    public void ShouldCheckRemote_ExactIntervalIsDue()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdatePolicy.ShouldCheckRemote(now - UpdatePolicy.CheckInterval, now));
    }

    [Fact]
    public void Marker_RoundTrips()
    {
        var stamped = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var text = UpdatePolicy.FormatMarker("1.2.4", stamped);
        Assert.True(UpdatePolicy.TryParseMarker(text, out var version, out var parsed));
        Assert.Equal("1.2.4", version);
        Assert.Equal(stamped, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2.4")]
    [InlineData("1.2.4|")]
    [InlineData("|2026-11-09T12:00:00.0000000+00:00")]
    [InlineData("1.2.4|not-a-date")]
    [InlineData("   |   ")]
    public void TryParseMarker_RejectsCorrupt(string? text)
    {
        Assert.False(UpdatePolicy.TryParseMarker(text, out _, out _));
    }

    [Fact]
    public void EvaluateLaunch_NoMarkerProceeds()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(UpdatePolicy.LaunchDecision.Proceed,
            UpdatePolicy.EvaluateLaunch(null, "1.2.3", now, restartedByUpdater: false));
        Assert.Equal(UpdatePolicy.LaunchDecision.Proceed,
            UpdatePolicy.EvaluateLaunch("", "1.2.3", now, restartedByUpdater: false));
    }

    [Fact]
    public void EvaluateLaunch_CorruptMarkerClears()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(UpdatePolicy.LaunchDecision.ProceedAfterClearingMarker,
            UpdatePolicy.EvaluateLaunch("junk-without-separator", "1.2.3", now, restartedByUpdater: false));
    }

    [Fact]
    public void EvaluateLaunch_TargetReachedClears()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var marker = UpdatePolicy.FormatMarker("1.2.4", now - TimeSpan.FromMinutes(1));
        Assert.Equal(UpdatePolicy.LaunchDecision.ProceedAfterClearingMarker,
            UpdatePolicy.EvaluateLaunch(marker, "1.2.4", now, restartedByUpdater: false));
    }

    [Fact]
    public void EvaluateLaunch_FreshMismatchAborts()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var marker = UpdatePolicy.FormatMarker("1.2.4", now - TimeSpan.FromMinutes(1));
        Assert.Equal(UpdatePolicy.LaunchDecision.AbortApplyInProgress,
            UpdatePolicy.EvaluateLaunch(marker, "1.2.3", now, restartedByUpdater: false));
    }

    [Fact]
    public void EvaluateLaunch_FreshMismatchWithUnknownVersionAborts()
    {
        // Fail closed when the running version cannot be read: a live
        // apply is the assumption, bounded by the stale rule.
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var marker = UpdatePolicy.FormatMarker("1.2.4", now - TimeSpan.FromMinutes(1));
        Assert.Equal(UpdatePolicy.LaunchDecision.AbortApplyInProgress,
            UpdatePolicy.EvaluateLaunch(marker, null, now, restartedByUpdater: false));
    }

    [Fact]
    public void EvaluateLaunch_RestartedByUpdaterWithMismatchClears()
    {
        // The updater's own restart, still on the old version: the apply
        // did not land. This must clear, never abort, or a failed update
        // refuses every launch until the marker goes stale.
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var marker = UpdatePolicy.FormatMarker("1.2.4", now - TimeSpan.FromMinutes(1));
        Assert.Equal(UpdatePolicy.LaunchDecision.ProceedAfterClearingMarker,
            UpdatePolicy.EvaluateLaunch(marker, "1.2.3", now, restartedByUpdater: true));
    }

    [Fact]
    public void EvaluateLaunch_StaleMismatchClears()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var marker = UpdatePolicy.FormatMarker("1.2.4", now - TimeSpan.FromHours(1));
        Assert.Equal(UpdatePolicy.LaunchDecision.ProceedAfterClearingMarker,
            UpdatePolicy.EvaluateLaunch(marker, "1.2.3", now, restartedByUpdater: false));
    }

    [Fact]
    public void EvaluateLaunch_StaleUnknownVersionClears()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var marker = UpdatePolicy.FormatMarker("1.2.4", now - TimeSpan.FromHours(1));
        Assert.Equal(UpdatePolicy.LaunchDecision.ProceedAfterClearingMarker,
            UpdatePolicy.EvaluateLaunch(marker, null, now, restartedByUpdater: false));
    }

    [Fact]
    public void MarkerStaleAfter_IsBounded()
    {
        // Crash recovery must be bounded: a dead updater's marker cannot
        // block launches forever.
        Assert.True(UpdatePolicy.MarkerStaleAfter <= TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(@"C:\Install\Wintty.exe", @"C:\Install", true)]
    [InlineData(@"C:\Install\current\Wintty.exe", @"C:\Install", true)]
    [InlineData(@"C:\INSTALL\wintty.exe", @"c:\install", true)]   // case-insensitive
    [InlineData("C:/Install/Wintty.exe", @"C:\Install", true)]  // forward slashes fold
    [InlineData(@"C:\Install", @"C:\Install", true)]
    [InlineData(@"C:\Install2\Wintty.exe", @"C:\Install", false)] // sibling prefix, not a child
    [InlineData(@"C:\Installable\Wintty.exe", @"C:\Install", false)]
    [InlineData(@"C:\Other\Wintty.exe", @"C:\Install", false)]
    [InlineData(@"C:\Install\Wintty.exe", @"C:\Other", false)]
    [InlineData(null, @"C:\Install", false)]
    [InlineData(@"C:\Install\Wintty.exe", null, false)]
    [InlineData(@"", @"C:\Install", false)]
    [InlineData(@"C:\Install\Wintty.exe", "", false)]
    public void UnderInstallRoot_Classifies(string? exe, string? root, bool expected)
    {
        Assert.Equal(expected, UpdatePolicy.UnderInstallRoot(exe, root!));
    }

    [Fact]
    public void UnderInstallRoot_StripsExtendedPrefix()
    {
        Assert.True(UpdatePolicy.UnderInstallRoot(
            @"\\?\C:\Install\Wintty.exe", @"C:\Install"));
        Assert.False(UpdatePolicy.UnderInstallRoot(
            @"\\?\C:\Other\Wintty.exe", @"C:\Install"));
    }
}
