using System;
using System.Collections.Generic;

namespace Ghostty.Core.Updates;

/// <summary>
/// One process the handoff scan found by installed-executable basename.
/// Candidacy (basename match) is decided by the caller, which enumerates
/// <c>*.exe</c> names from the install root and only queries those, so
/// unrelated system processes are never even opened. A null
/// <see cref="ExePath"/> means the path could not be read: that candidate
/// fails closed (blocks), it never reads as "outside".
/// </summary>
internal readonly record struct HandoffCandidate(int Pid, string? ExePath);

/// <summary>
/// Handoff verdict for a post-shutdown apply request.
/// </summary>
internal enum HandoffVerdict
{
    /// <summary>
    /// No other same-install process is alive: hand off to Update.exe.
    /// </summary>
    Proceed,

    /// <summary>
    /// A same-install process (or an unreadable same-named one) is alive:
    /// drop the request and exit normally. Never kill: a live sibling may
    /// own PTYs, and Update.exe would force-stop every image under the
    /// install root sixty seconds after we hand off.
    /// </summary>
    Blocked,
}

/// <summary>
/// Pure launch/handoff ordering decisions. The caller owns the file lock
/// (the apply marker opened with an exclusive share), the process
/// enumeration, and every exit; this only classifies what it is given, so
/// the cross-thread ordering (marker written before the scan, launch
/// check before any window) is unit-testable without processes or files.
/// </summary>
internal static class UpdateApplyGuard
{
    /// <summary>
    /// Whether the handoff may proceed. Every candidate is a process whose
    /// executable basename matches an installed <c>*.exe</c> name; the one
    /// exception to a block is a candidate provably outside the install
    /// root (a dev copy of the same binary elsewhere, or any same-named
    /// stranger): Update.exe only ever force-stops images under its own
    /// root, so those are unaffected either way. Everything else blocks,
    /// including candidates whose path could not be read: an unknown
    /// same-named process might be a sibling under the root that we
    /// simply could not query, so unknown fails closed.
    /// </summary>
    public static (HandoffVerdict Verdict, int BlockerPid) EvaluateHandoff(
        IReadOnlyList<HandoffCandidate> candidates, string installRoot, int currentPid)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(installRoot);

        foreach (var candidate in candidates)
        {
            if (candidate.Pid == currentPid) continue;
            if (!IsProvablyOutside(candidate.ExePath, installRoot))
                return (HandoffVerdict.Blocked, candidate.Pid);
        }

        return (HandoffVerdict.Proceed, -1);
    }

    private static bool IsProvablyOutside(string? exePath, string installRoot) =>
        !string.IsNullOrWhiteSpace(exePath) && !UpdatePolicy.UnderInstallRoot(exePath, installRoot);
}
