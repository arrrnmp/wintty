using System;
using Ghostty.Core.Config;

namespace Ghostty.Core.Updates;

/// <summary>
/// Feed identity, opt-out key, throttle and apply-marker rules for the
/// built-in Velopack updater. Pure (no I/O, no Velopack types, no UI) so
/// the behaviour is unit-testable on any host; the shell's
/// <c>Ghostty.Updates</c> layer owns every side effect (file lock, process
/// scan, network, notices).
/// </summary>
internal static class UpdatePolicy
{
    /// <summary>
    /// Config key for the explicit opt-out. Read through the existing
    /// raw/ParseBool pattern (see <c>windows-single-instance</c>), enabled
    /// by default: only a literal <c>false</c> disables the check.
    /// Registered in <see cref="WindowsOnlyKeys"/> so libghostty's
    /// "unknown field" diagnostic is absorbed rather than surfaced.
    /// </summary>
    public const string ConfigKey = "windows-update-check";

    /// <summary>
    /// The only feed the product ever checks. There is deliberately no
    /// environment override for this in product code: a test feed is a
    /// test-host concern, outside the shipped binary.
    /// </summary>
    public const string FeedRepositoryUrl = "https://github.com/arrrnmp/wintty";

    /// <summary>
    /// Last-check timestamp file, under
    /// <c>AppStateBase.LocalRoot/AppIdentity.StateDirName</c> (no custom
    /// config root). Round-trip ("O") UTC, written after every remote
    /// attempt so an offline launch does not retry on every start.
    /// </summary>
    public const string LastCheckFileName = "update-last-check";

    /// <summary>
    /// Apply marker, at the install root (the one directory every instance
    /// of an install shares regardless of config/state roots). Doubles as
    /// the launch/apply gate: both sides open it with an exclusive lock,
    /// which is what closes the check-to-new-window race (see
    /// <c>Ghostty.Updates.UpdateHandoff</c>).
    /// </summary>
    public const string MarkerFileName = ".wintty-update-apply";

    /// <summary>
    /// Dedup key for the "update ready" sticky notice: one banner per
    /// session, not one per window.
    /// </summary>
    public const string NoticeDedupKey = "wintty-update-ready";

    /// <summary>
    /// Remote checks happen at most this often per machine. A daily user
    /// checks roughly once a day even as open times drift.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// A marker older than this cannot be a live apply: the updater either
    /// restarted us already or died. Bounded crash recovery, so a failed
    /// apply never blocks launches forever.
    /// </summary>
    public static readonly TimeSpan MarkerStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The opt-out read: only <c>false</c> (any case, surrounding
    /// whitespace tolerated) disables; unset, blank and unparseable all
    /// mean enabled.
    /// </summary>
    public static bool IsEnabled(string? raw) =>
        WindowsOnlyKeyParsers.ParseBool(raw, defaultValue: true);

    /// <summary>
    /// Whether a remote check is due. No recorded check means due.
    /// </summary>
    public static bool ShouldCheckRemote(DateTimeOffset? lastCheckUtc, DateTimeOffset utcNow) =>
        lastCheckUtc is null || (utcNow - lastCheckUtc.Value) >= CheckInterval;

    /// <summary>
    /// Marker text verdict for a launching process.
    /// </summary>
    public enum LaunchDecision
    {
        /// <summary>No marker, or empty: start normally.</summary>
        Proceed,

        /// <summary>
        /// Marker names the running version, is stale, is corrupt, or this
        /// process is the updater's own restart with a mismatched version
        /// (the apply did not land): delete it and start normally. Every
        /// non-live marker resolves here, so nothing unparseable or failed
        /// can block launches permanently.
        /// </summary>
        ProceedAfterClearingMarker,

        /// <summary>
        /// Fresh marker naming a different version (or an unreadable
        /// current version): the updater is mid-apply. Exit before any
        /// shell, window or PTY exists; the updater's restart is the
        /// launch that counts.
        /// </summary>
        AbortApplyInProgress,
    }

    /// <summary>
    /// Marker format: <c>version|utc-timestamp</c>, timestamp in
    /// round-trip ("O") form. Written by the handoff, read by launches.
    /// Both sides produce the version from the same
    /// <c>SemanticVersion.ToString()</c>, so plain string equality is the
    /// comparison (no second semver parser to drift).
    /// </summary>
    public static string FormatMarker(string targetVersion, DateTimeOffset stampedAtUtc) =>
        $"{targetVersion}|{stampedAtUtc:O}";

    /// <summary>
    /// Parse marker text. False for null, empty, missing separator, empty
    /// version, or an unparseable timestamp.
    /// </summary>
    public static bool TryParseMarker(
        string? text, out string targetVersion, out DateTimeOffset stampedAtUtc)
    {
        targetVersion = string.Empty;
        stampedAtUtc = default;
        if (string.IsNullOrEmpty(text)) return false;
        var sep = text.IndexOf('|');
        if (sep <= 0 || sep == text.Length - 1) return false;
        var version = text[..sep].Trim();
        if (version.Length == 0) return false;
        if (!DateTimeOffset.TryParseExact(
                text[(sep + 1)..].Trim(),
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var stamped))
        {
            return false;
        }
        targetVersion = version;
        stampedAtUtc = stamped;
        return true;
    }

    /// <summary>
    /// Decide what a launch does about marker text. Pure: file reading,
    /// deletion and exiting all belong to the caller.
    /// </summary>
    /// <param name="restartedByUpdater">
    /// Whether this process was restarted by Update.exe after an apply
    /// (the <c>VELOPACK_RESTART</c> environment marker, captured before
    /// the SDK clears it). A restart with a mismatched version is a failed
    /// apply, not a live one: it must clear and report, never abort, or a
    /// failed update would refuse every launch until the marker goes stale.
    /// </param>
    public static LaunchDecision EvaluateLaunch(
        string? markerText,
        string? currentVersion,
        DateTimeOffset utcNow,
        bool restartedByUpdater)
    {
        if (!TryParseMarker(markerText, out var target, out var stamped))
            return string.IsNullOrEmpty(markerText)
                ? LaunchDecision.Proceed
                : LaunchDecision.ProceedAfterClearingMarker;
        if (!string.IsNullOrWhiteSpace(currentVersion) &&
            string.Equals(target, currentVersion.Trim(), StringComparison.Ordinal))
        {
            // The apply this marker announced already landed (this is the
            // updater's restart, or a relaunch after one): clear and run.
            return LaunchDecision.ProceedAfterClearingMarker;
        }
        if (restartedByUpdater)
        {
            // The updater's own restart, still on the old version: the
            // apply did not land. Clearing here is what keeps a failed
            // update from blocking the very restart that reports it.
            return LaunchDecision.ProceedAfterClearingMarker;
        }
        if (utcNow - stamped >= MarkerStaleAfter)
        {
            // Live applies finish in seconds; this old a marker is a dead
            // updater or a restart that never came back. Clear and run
            // rather than block forever.
            return LaunchDecision.ProceedAfterClearingMarker;
        }
        // Fresh marker for a version we are not (or cannot prove we are):
        // an apply is in flight. A null current version fails closed for
        // the same bounded window rather than guessing.
        return LaunchDecision.AbortApplyInProgress;
    }

    /// <summary>
    /// The directory the launch/apply gate coordinates in: every instance
    /// of one install shares it regardless of config or state roots. The
    /// Velopack install root (parent of <c>current/</c>, home of
    /// <c>Update.exe</c>, the packages dir, and the launch stub) when the
    /// locator knows it, else the app's own directory. Both the handoff
    /// and the launch guard resolve through this so the marker and the
    /// scan always agree; Update.exe force-stops images under the install
    /// root, so scanning anything narrower would miss killable processes
    /// (notably the launch stub beside <c>current/</c>).
    /// </summary>
    public static string SharedRoot(string? locatorRootDir, string baseDirectory) =>
        !string.IsNullOrWhiteSpace(locatorRootDir) ? locatorRootDir : baseDirectory;

    /// <summary>
    /// Whether <paramref name="exePath"/> lives under
    /// <paramref name="installRoot"/>. OrdinalIgnoreCase, separator-aware
    /// (<c>/</c> and <c>\</c> both fold), and boundary-aware:
    /// <c>C:\App2\Wintty.exe</c> is not under <c>C:\App</c>. Pure string
    /// work on purpose: the tests assert Windows-shaped paths on any
    /// host, so this must not depend on the host's separator.
    /// </summary>
    public static bool UnderInstallRoot(string? exePath, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(installRoot))
            return false;
        var exe = NormalizeForCompare(exePath).TrimEnd('\\');
        var root = NormalizeForCompare(installRoot).TrimEnd('\\');
        if (exe.Length == 0 || root.Length == 0) return false;
        if (exe.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return exe.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForCompare(string path)
    {
        var trimmed = path.Trim().Replace('/', '\\');
        // Extended-length prefix (\\?\) is spelling, not location.
        const string ExtendedPrefix = @"\\?\";
        if (trimmed.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            trimmed = trimmed[ExtendedPrefix.Length..];
        return trimmed;
    }
}
