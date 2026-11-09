using System;
using System.IO;
using System.Text;
using System.Threading;
using Ghostty.Core.Updates;
using Velopack.Locators;

namespace Ghostty.Updates;

/// <summary>
/// Launch side of the apply gate. Called from <see cref="Program.Main"/>
/// after <see cref="VelopackBootstrap.RunStartupHooks"/> (SDK hooks exit
/// first and must never be blocked) and before <c>MainImpl</c>, i.e.
/// before the native resolver, the single-instance election, any window,
/// any shell, and any PTY.
/// </summary>
/// <remarks>
/// Why a gate at all: the default single-instance election is not
/// enough. It coordinates one config root in one session; a second
/// config root, another terminal-services session, or single-instance
/// disabled all start independent processes that could open windows and
/// spawn shells on top of a live apply. The marker plus exclusive lock
/// serializes the two sides instead:
/// <list type="bullet">
/// <item>The handoff (see <see cref="UpdateHandoff"/>) holds the
/// exclusive lock across marker-write, sibling scan, and the
/// <c>Update.exe</c> spawn.</item>
/// <item>A launch that finds a marker takes the same lock before reading
/// it. Whoever holds the lock first wins: the updater's scan then sees
/// the new process and stands down, or the launch sees the fresh marker
/// (or a still-held lock) and exits before creating anything.</item>
/// </list>
/// The fast path costs one <c>File.Exists</c>: launches with no marker
/// never touch the lock. Every failure here fails open (the launch
/// proceeds): a broken guard must not brick startup, and the handoff
/// side's own scan plus Update.exe's 60-second wait are the backstop.
/// </remarks>
internal static class UpdateLaunchGuard
{
    private const int LockAttempts = 20;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(100);

    public static void CheckApplyMarkerAndExitIfApplying()
    {
        string markerPath;
        try
        {
            // Same shared root as the handoff: the marker and the scan
            // must agree, and the install root covers the launch stub.
            markerPath = Path.Combine(
                UpdatePolicy.SharedRoot(LocatorRootDir(), AppContext.BaseDirectory),
                UpdatePolicy.MarkerFileName);
        }
        catch
        {
            return;
        }

        bool exists;
        try
        {
            exists = File.Exists(markerPath);
        }
        catch
        {
            return;
        }
        if (!exists) return;

        // Slow path only: a marker exists, so an apply was requested
        // within the last minutes (or a stale one was left behind).
        FileStream? gate = null;
        try
        {
            for (var i = 0; i < LockAttempts && gate is null; i++)
            {
                try
                {
                    gate = new FileStream(
                        markerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    // The handoff holds the lock across its scan + spawn.
                    // Wait it out briefly; still held afterwards means it
                    // is mid-handoff, and this launch must go away.
                    Thread.Sleep(LockRetryDelay);
                }
            }
            if (gate is null)
            {
                Abort("the update gate stayed locked; an update apply is in progress");
                return;
            }

            string? markerText;
            try
            {
                // Read through the gate stream itself: it holds an
                // exclusive lock, so any second open of the path refuses.
                using var buffer = new MemoryStream();
                gate.CopyTo(buffer);
                markerText = Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch
            {
                markerText = null;
            }

            var decision = UpdatePolicy.EvaluateLaunch(
                markerText,
                CurrentInstalledVersion(),
                DateTimeOffset.UtcNow,
                VelopackBootstrap.WasRestartedByUpdater);

            // Release the lock before acting: deleting the marker while it
            // is open refuses on Windows. Abort() exits, so the OS
            // releases there instead.
            gate.Dispose();
            gate = null;

            switch (decision)
            {
                case UpdatePolicy.LaunchDecision.AbortApplyInProgress:
                    Abort("an update apply is in progress; leaving startup to the updater restart");
                    return;
                default:
                    // Both proceed paths leave no marker behind: a cleared
                    // stale/target/failed marker, or an empty file the open
                    // above created in a delete race. Either way the next
                    // launch is back on the lock-free fast path.
                    ReportClearedMarker(
                        markerText, VelopackBootstrap.WasRestartedByUpdater);
                    TryDelete(markerPath);
                    return;
            }
        }
        catch
        {
            // Fail open (see the class doc). Abort() exits, so reaching
            // here means something else broke around the decision.
            return;
        }
        finally
        {
            gate?.Dispose();
        }
    }

    private static void ReportClearedMarker(string? markerText, bool restartedByUpdater)
    {
        // The empty-file race needs no report; every real clear does.
        if (string.IsNullOrEmpty(markerText)) return;
        if (restartedByUpdater &&
            UpdatePolicy.TryParseMarker(markerText, out var target, out _))
        {
            Program.WriteStartupDiagnostic(
                $"cleared the update apply marker after the updater restarted without applying {target}; starting normally");
            return;
        }
        Program.WriteStartupDiagnostic(
            "cleared the update apply marker; starting normally");
    }

    private static string? CurrentInstalledVersion()
    {
        try
        {
            return VelopackLocator.Current.CurrentlyInstalledVersion?.ToString();
        }
        catch
        {
            // Locator unset (Velopack init failed) or unreadable install:
            // EvaluateLaunch fails closed on a null current version for a
            // fresh marker, and the stale path still self-heals.
            return null;
        }
    }

    private static string? LocatorRootDir()
    {
        try
        {
            return VelopackLocator.Current.RootAppDir;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string markerPath)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch
        {
            // Best effort: a stale marker is re-evaluated (and re-cleared)
            // on the next launch.
        }
    }

    private static void Abort(string why)
    {
        // Exit code 0: this is a clean refusal, not a failure -- the
        // updater's restart is the launch that counts, and a nonzero code
        // would read as a crash to a launcher or harness.
        Program.WriteStartupDiagnostic($"not starting: {why}");
        Environment.Exit(0);
    }
}
