using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Config;
using Ghostty.Core.Notifications;
using Ghostty.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace Ghostty.Updates;

/// <summary>
/// Once-per-launch background update check. Started at the end of
/// <c>App.OnLaunched</c> (first windows exist, notice hosts are bound);
/// cancelled in the last window's teardown so no notice lands on a dying
/// window tree and no download outlives the process it would restart.
/// Owns no Velopack state across launches: every decision (pending
/// package, throttle timestamp) is re-read from disk.
/// </summary>
/// <remarks>
/// Flow, each step best-effort and log-only on failure (a check must
/// never block startup or pop errors at the user):
/// <list type="bullet">
/// <item>Skip when the test-config guard is armed: a harness launch must
/// not touch the network or the real state tree.</item>
/// <item>If a downloaded update is already staged
/// (<c>UpdatePendingRestart</c>), notice immediately without networking.
/// This also re-notices after a dismissal or a vetoed close.</item>
/// <item>Otherwise check the feed at most once per
/// <see cref="UpdatePolicy.CheckInterval"/>, download automatically, and
/// notice only once the package is on disk ("ready" means ready).</item>
/// <item>Portable and dev runs are not installed
/// (<c>IsInstalled</c> false): skip quietly. There is no updater beside
/// the binary there.</item>
/// </list>
/// The notice's accept action records the pending apply and closes windows
/// through the normal path; if any window survives (a close vetoed by
/// confirm-close-surface), the request is rescinded and the notice
/// re-shown, so an ordinary later quit never applies an update nobody
/// just approved.
/// </remarks>
internal sealed class UpdateService
{
    private readonly ILogger<UpdateService> _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly INotificationService _notices;
    private readonly Func<string?> _readUpdateCheckRaw;
    private readonly string _stateDir;
    private readonly Action _closeAllWindows;
    private readonly Func<bool> _hasOpenWindows;

    public UpdateService(
        ILogger<UpdateService> logger,
        DispatcherQueue dispatcher,
        INotificationService notices,
        Func<string?> readUpdateCheckRaw,
        string stateDir,
        Action closeAllWindows,
        Func<bool> hasOpenWindows)
    {
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _readUpdateCheckRaw = readUpdateCheckRaw ?? throw new ArgumentNullException(nameof(readUpdateCheckRaw));
        _stateDir = !string.IsNullOrEmpty(stateDir)
            ? stateDir
            : throw new ArgumentException("state directory is required", nameof(stateDir));
        _closeAllWindows = closeAllWindows ?? throw new ArgumentNullException(nameof(closeAllWindows));
        _hasOpenWindows = hasOpenWindows ?? throw new ArgumentNullException(nameof(hasOpenWindows));
    }

    /// <summary>
    /// Run the check once on a threadpool thread. Returns immediately;
    /// every failure path logs and stops, and cancellation is silent.
    /// </summary>
    public void StartOnce(CancellationToken ct)
    {
        _ = Task.Run(() => RunAsync(ct), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (TestConfigGuard.IsArmed)
            {
                _log.LogInformation("Skipping update check: test config guard is armed.");
                return;
            }

            if (!UpdatePolicy.IsEnabled(SafeReadRaw()))
            {
                _log.LogInformation("Skipping update check: disabled by windows-update-check=false.");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var lastCheck = ReadLastCheckUtc();

            string? stagedVersion = null;
            try
            {
                // Default channel: the package's own (root CI packs stable,
                // so installed builds track the stable feed). No explicit
                // channel, no token: the repo is public and the daily
                // throttle stays far under the unauthenticated rate limit.
                var manager = new UpdateManager(
                    new GithubSource(UpdatePolicy.FeedRepositoryUrl, null, false));
                if (!manager.IsInstalled)
                {
                    _log.LogInformation("Skipping update check: not an installed Velopack app.");
                    return;
                }

                stagedVersion = StagedVersion(manager);
                if (stagedVersion is null && UpdatePolicy.ShouldCheckRemote(lastCheck, now))
                {
                    // Recorded before the attempt, not after: an offline
                    // launch must not retry on every start.
                    RecordCheckAttempt(now);
                    var found = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                    if (found is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        await manager.DownloadUpdatesAsync(found, null, ct).ConfigureAwait(false);
                        stagedVersion = StagedVersion(manager);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Update check failed; will retry on a later launch.");
                return;
            }

            if (stagedVersion is null) return;
            ShowReadyNotice(stagedVersion);
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancelled us: silent by contract.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed; will retry on a later launch.");
        }
    }

    private string? SafeReadRaw()
    {
        try
        {
            return _readUpdateCheckRaw();
        }
        catch (Exception ex)
        {
            // An unreadable config reads as enabled (the default): failing
            // closed here would silently strand installs on a broken read.
            _log.LogWarning(ex, "Could not read windows-update-check; treating updates as enabled.");
            return null;
        }
    }

    private static string? StagedVersion(UpdateManager manager)
    {
        try
        {
            // UpdatePendingRestart is the staged full package itself (or
            // null when nothing is downloaded), not an UpdateInfo.
            return manager.UpdatePendingRestart?.Version.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private DateTimeOffset? ReadLastCheckUtc()
    {
        try
        {
            var text = File.ReadAllText(LastCheckPath()).Trim();
            if (DateTimeOffset.TryParseExact(
                    text,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var stamped))
            {
                return stamped;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read the last update-check timestamp.");
        }
        return null;
    }

    private void RecordCheckAttempt(DateTimeOffset now)
    {
        try
        {
            File.WriteAllText(LastCheckPath(), $"{now:O}");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not record the update-check timestamp.");
        }
    }

    private string LastCheckPath() => Path.Combine(_stateDir, UpdatePolicy.LastCheckFileName);

    private void ShowReadyNotice(string version)
    {
        var notice = new Notice
        {
            Title = UpdateNoticeText.Title,
            Message = UpdateNoticeText.Body(version),
            Severity = NoticeSeverity.Informational,
            IsClosable = true,
            DedupKey = UpdatePolicy.NoticeDedupKey,
            Actions = new[]
            {
                new NoticeAction(
                    UpdateNoticeText.AcceptLabel,
                    () => AcceptUpdate(version),
                    IsPrimary: true),
            },
        };

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    _notices.Show(notice);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not show the update-ready notice.");
                }
            }))
        {
            _log.LogWarning("Dispatcher closed before the update-ready notice could be shown.");
        }
    }

    private void AcceptUpdate(string version)
    {
        try
        {
            UpdateHandoff.RequestApply(version);
            _closeAllWindows();
            // Window closes land asynchronously: a still-open window right
            // now is usually a close in flight, not a veto. Re-check a
            // bounded number of times before rescinding, so slow closes
            // keep the approval and only a genuine veto (a window that
            // stays open because confirm-close refused it) re-offers the
            // notice. An ordinary later quit never applies an update nobody
            // just approved because a veto always rescinds first.
            SettleAccept(version, remainingAttempts: 6);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start the update apply.");
        }
    }

    private void SettleAccept(string version, int remainingAttempts)
    {
        _dispatcher.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                try
                {
                    // No windows left: the approval stands for the
                    // shutdown handoff in Program.Main.
                    if (!_hasOpenWindows()) return;
                    if (remainingAttempts <= 1)
                    {
                        UpdateHandoff.Rescind();
                        ShowReadyNotice(version);
                        return;
                    }
                    var left = remainingAttempts - 1;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                            SettleAccept(version, left);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Update accept settle-check failed.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Update accept settle-check failed.");
                }
            });
    }
}
