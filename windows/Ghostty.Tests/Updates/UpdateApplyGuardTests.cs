using System;
using System.Collections.Generic;
using Ghostty.Core.Updates;
using Xunit;

namespace Ghostty.Tests.Updates;

public class UpdateApplyGuardTests
{
    private const string Root = @"C:\Install";
    private const int Self = 4242;

    [Fact]
    public void EvaluateHandoff_NoCandidatesProceeds()
    {
        var (verdict, blocker) = UpdateApplyGuard.EvaluateHandoff(
            Array.Empty<HandoffCandidate>(), Root, Self);
        Assert.Equal(HandoffVerdict.Proceed, verdict);
        Assert.Equal(-1, blocker);
    }

    [Fact]
    public void EvaluateHandoff_SelfOnlyProceeds()
    {
        var candidates = new[] { new HandoffCandidate(Self, @"C:\Install\Wintty.exe") };
        var (verdict, _) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Proceed, verdict);
    }

    [Fact]
    public void EvaluateHandoff_OtherUnderRootBlocks()
    {
        var candidates = new[]
        {
            new HandoffCandidate(Self, @"C:\Install\Wintty.exe"),
            new HandoffCandidate(111, @"C:\Install\Wintty.exe"),
        };
        var (verdict, blocker) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Blocked, verdict);
        Assert.Equal(111, blocker);
    }

    [Fact]
    public void EvaluateHandoff_RootHelperBlocksApplyToo()
    {
        // OpenConsole lives under the install root: a live sibling's
        // backend blocks the apply rather than being killed from under it.
        var candidates = new[] { new HandoffCandidate(222, @"C:\Install\OpenConsole.exe") };
        var (verdict, blocker) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Blocked, verdict);
        Assert.Equal(222, blocker);
    }

    [Fact]
    public void EvaluateHandoff_OutsideRootProceeds()
    {
        // Same executable name elsewhere (a dev copy, another install):
        // Update.exe only force-stops images under its own root, so these
        // are unaffected either way. A child shell (pwsh, cmd) is outside
        // the root by the same rule and must never block.
        var candidates = new[]
        {
            new HandoffCandidate(333, @"D:\Dev\Wintty.exe"),
            new HandoffCandidate(444, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"),
        };
        var (verdict, _) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Proceed, verdict);
    }

    [Fact]
    public void EvaluateHandoff_UnreadablePathBlocks()
    {
        // Fail closed: an unknown same-named process might be a sibling
        // under the root that simply could not be queried.
        var candidates = new[] { new HandoffCandidate(555, null) };
        var (verdict, blocker) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Blocked, verdict);
        Assert.Equal(555, blocker);
    }

    [Fact]
    public void EvaluateHandoff_BlankPathBlocks()
    {
        var candidates = new[] { new HandoffCandidate(555, "  ") };
        var (verdict, _) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Blocked, verdict);
    }

    [Fact]
    public void EvaluateHandoff_SiblingPrefixProceeds()
    {
        // C:\Install2 is not under C:\Install: boundary-aware, so a
        // same-root-name neighbour never blocks.
        var candidates = new[] { new HandoffCandidate(666, @"C:\Install2\Wintty.exe") };
        var (verdict, _) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Proceed, verdict);
    }

    [Fact]
    public void EvaluateHandoff_MixedReportsFirstBlocker()
    {
        var candidates = new List<HandoffCandidate>
        {
            new(Self, @"C:\Install\Wintty.exe"),
            new(777, @"D:\Dev\Wintty.exe"),
            new(888, @"C:\Install\Update.exe"),
        };
        var (verdict, blocker) = UpdateApplyGuard.EvaluateHandoff(candidates, Root, Self);
        Assert.Equal(HandoffVerdict.Blocked, verdict);
        Assert.Equal(888, blocker);
    }

    [Fact]
    public void EvaluateHandoff_NullCandidatesThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => UpdateApplyGuard.EvaluateHandoff(null!, Root, Self));
    }

    [Fact]
    public void EvaluateHandoff_NullRootThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => UpdateApplyGuard.EvaluateHandoff(
                Array.Empty<HandoffCandidate>(), null!, Self));
    }
}
