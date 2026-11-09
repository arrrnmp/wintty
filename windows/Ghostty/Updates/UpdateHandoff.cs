using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Ghostty.Core.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Ghostty.Updates;

/// <summary>
/// Post-shutdown side of the apply gate. The update-ready notice's accept
/// action records a pending request and closes windows through the normal
/// path; only when the UI thread has joined and teardown is complete does
/// <see cref="Program.Main"/> call <see cref="PerformHandoffIfRequested"/>.
/// </summary>
/// <remarks>
/// Safety contract (load-bearing):
/// <list type="bullet">
/// <item>Never <c>ApplyUpdatesAndRestart</c> / <c>ApplyUpdatesAndExit</c>:
/// both call the equivalent of <c>Environment.Exit</c>, skipping the
/// teardown above (PTYs, hosts, election) and every line after the call.
/// The only handoff used here is the non-exiting
/// <see cref="UpdateExe.Apply"/>, invoked last, after which the caller
/// returns immediately and the process exits on its own.</item>
/// <item>Never <c>WaitExitThenApplyUpdates</c> while UI/PTYs are alive:
/// Update.exe waits at most 60 seconds and then unconditionally
/// force-stops every process image under the install root. By the time
/// this runs, the UI thread has joined, all windows (regular and quake)
/// are closed, and every PTY is disposed -- the only processes that may
/// still be under the root belong to other live instances, and those
/// block the handoff (see below) instead of being killed.</item>
/// <item>The gate (the marker file opened with an exclusive share) is
/// held across marker-write, sibling scan, and the Update.exe spawn, and
/// the marker is written BEFORE the scan. A launch racing us either takes
/// the lock first (our scan then sees it and we stand down) or fails to
/// take it and exits before creating shells. See
/// <see cref="UpdateLaunchGuard"/> for the launch side.</item>
/// <item>Only processes whose executable basename matches an installed
/// <c>*.exe</c> file are ever queried, so protected unrelated system PIDs
/// cannot block the apply -- and a child shell (pwsh, cmd) lives outside
/// the install root, so Update.exe's force-stop never touches it. A
/// candidate whose path cannot be read fails closed (blocks).</item>
/// </list>
/// A vetoed close never reaches this: the accept action re-checks for
/// open windows on the dispatcher and rescinds the request when any
/// remain, so an ordinary later quit does not apply an update nobody just
/// approved.
/// </remarks>
internal static class UpdateHandoff
{
    private static readonly object Sync = new();
    private static string? _pendingTargetVersion;

    /// <summary>
    /// Record the user's explicit approval to apply
    /// <paramref name="targetVersion"/> at the coming shutdown.
    /// </summary>
    public static void RequestApply(string targetVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        lock (Sync)
        {
            _pendingTargetVersion = targetVersion;
        }
    }

    /// <summary>
    /// Withdraw a pending request (a close was vetoed: the shutdown this
    /// approval belonged to is not happening).
    /// </summary>
    public static void Rescind()
    {
        lock (Sync)
        {
            _pendingTargetVersion = null;
        }
    }

    /// <summary>
    /// Take and clear the pending request, if any.
    /// </summary>
    public static bool TryTakeRequest(out string? targetVersion)
    {
        lock (Sync)
        {
            targetVersion = _pendingTargetVersion;
            _pendingTargetVersion = null;
            return !string.IsNullOrWhiteSpace(targetVersion);
        }
    }

    /// <summary>
    /// Hand a pending apply request to Update.exe. No-op without one.
    /// Runs on <see cref="Program.Main"/>'s thread after the UI thread
    /// joined; returns immediately after spawning so the process exits and
    /// Update.exe's wait on our pid fires. Never throws and never exits
    /// itself: on any failure the request is dropped (the staged package
    /// stays; the next launch re-notices) and the process exits normally.
    /// </summary>
    public static void PerformHandoffIfRequested()
    {
        if (!TryTakeRequest(out var target) || string.IsNullOrWhiteSpace(target)) return;

        string installRoot;
        try
        {
            // The install root, not the app directory: Update.exe
            // force-stops images anywhere under it (including the launch
            // stub beside current/), so the scan must cover all of it.
            installRoot = UpdatePolicy.SharedRoot(LocatorRootDir(), AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(installRoot)) return;
        }
        catch
        {
            return;
        }

        string markerPath;
        try
        {
            markerPath = Path.Combine(installRoot, UpdatePolicy.MarkerFileName);
        }
        catch
        {
            return;
        }

        FileStream? gate;
        try
        {
            gate = new FileStream(
                markerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex)
        {
            // Another instance holds the gate (its own handoff or check):
            // stand down. The staged package survives; next launch notices.
            Program.WriteStartupDiagnostic(
                $"update handoff skipped: could not take the update gate: {ex.GetType().Name}");
            return;
        }

        using (gate)
        {
            // Decided under the gate; deleted after it is released (a
            // delete of an exclusively locked path refuses on Windows).
            bool deleteMarker = false;
            string detail;
            try
            {
                // Resolve what would actually apply BEFORE writing the
                // marker: a missing package must stand down quietly, never
                // arm the launch gate with nothing behind it (launches
                // would abort for the whole marker window).
                VelopackAsset? pending;
                try
                {
                    var manager = new UpdateManager(
                        new GithubSource(UpdatePolicy.FeedRepositoryUrl, null, false));
                    pending = manager.UpdatePendingRestart;
                }
                catch (Exception ex)
                {
                    deleteMarker = true;
                    detail =
                        $"update handoff stood down: could not resolve the staged package ({ex.GetType().Name})";
                    gate.Dispose();
                    TryDelete(markerPath);
                    Program.WriteStartupDiagnostic(detail);
                    return;
                }
                if (pending is null)
                {
                    // Nothing downloaded (a previous run cleaned up, or the
                    // notice outlived its package): no marker, no handoff.
                    Program.WriteStartupDiagnostic(
                        "update handoff stood down: no staged package to apply");
                    gate.Dispose();
                    return;
                }

                // Name exactly what Apply will install (usually the
                // approved target; a newer package that landed after the
                // notice takes precedence), so the next launch's
                // marker/target comparison judges the real outcome.
                var applyVersion = pending.Version.ToString();
                WriteMarker(gate, UpdatePolicy.FormatMarker(applyVersion, DateTimeOffset.UtcNow));

                var (verdict, blockerPid) = UpdateApplyGuard.EvaluateHandoff(
                    ScanForOtherInstances(installRoot), installRoot, Environment.ProcessId);
                if (verdict != HandoffVerdict.Proceed)
                {
                    deleteMarker = true;
                    detail =
                        $"update handoff stood down: another installed process is running (pid {blockerPid})";
                }
                else
                {
                    try
                    {
                        // Non-exiting by design (see the class doc): this only
                        // spawns Update.exe with --waitPid <our pid>. Verified
                        // against the pinned SDK source
                        // (src/lib-csharp/UpdateExe.cs at 1.2.161): Apply only
                        // starts the child process and logs; the Exit() calls
                        // live in ApplyUpdatesAndRestart/ApplyUpdatesAndExit,
                        // which are never used.
                        UpdateExe.Apply(
                            locator: null,
                            toApply: pending,
                            silent: false,
                            waitPid: (uint)Environment.ProcessId,
                            restart: true);
                        detail =
                            $"handing off to the updater to apply {applyVersion}; exiting for the apply";
                    }
                    catch (Exception ex)
                    {
                        deleteMarker = true;
                        detail =
                            $"update handoff failed to start the updater: {ex.GetType().Name}: {ex.Message}";
                    }
                }
            }
            catch (Exception ex)
            {
                deleteMarker = true;
                detail = $"update handoff aborted: {ex.GetType().Name}: {ex.Message}";
            }

            gate.Dispose();
            if (deleteMarker) TryDelete(markerPath);
            Program.WriteStartupDiagnostic(detail);
            return;
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

    private static void WriteMarker(FileStream gate, string text)
    {
        gate.SetLength(0);
        var bytes = Encoding.UTF8.GetBytes(text);
        gate.Write(bytes, 0, bytes.Length);
        gate.Flush();
    }

    private static void TryDelete(string markerPath)
    {
        // Always called after the gate is released (a delete of an
        // exclusively locked path refuses on Windows). Best effort: a
        // leftover marker re-resolves on the next launch through the
        // stale/target rules.
        try
        {
            File.Delete(markerPath);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Every other process that could own state under the install root:
    /// processes named like an installed <c>*.exe</c> (Wintty itself, the
    /// launch stub, OpenConsole, Update.exe), minus this process. Names
    /// are enumerated recursively: the stub lives beside <c>current/</c>
    /// while the app and its helpers live inside it. Only those names are
    /// ever queried, so unrelated processes cost nothing and cannot block.
    /// </summary>
    private static IReadOnlyList<HandoffCandidate> ScanForOtherInstances(string installRoot)
    {
        HashSet<string> names;
        try
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exe in Directory.EnumerateFiles(
                         installRoot, "*.exe", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        catch
        {
            // The install dir itself is unreadable: fail closed with one
            // unknown candidate rather than scanning nothing and calling
            // that clear.
            return new[] { new HandoffCandidate(-1, null) };
        }

        var found = new List<HandoffCandidate>();
        var self = Environment.ProcessId;
        foreach (var name in names)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                // Enumeration for this name failed: unknown, so blocking.
                found.Add(new HandoffCandidate(-1, null));
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    int pid;
                    try
                    {
                        pid = process.Id;
                    }
                    catch
                    {
                        found.Add(new HandoffCandidate(-1, null));
                        continue;
                    }
                    if (pid == self) continue;
                    string? path;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Unreadable (elevated/protected): fail closed.
                        path = null;
                    }
                    found.Add(new HandoffCandidate(pid, path));
                }
            }
        }

        return found;
    }
}
