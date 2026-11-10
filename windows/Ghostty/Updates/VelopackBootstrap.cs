using System;
using Ghostty.Core;
using Velopack;

namespace Ghostty.Updates;

/// <summary>
/// Runs the Velopack SDK startup hooks. Must be the first thing
/// <see cref="Program.Main"/> does, before the STA thread, the native
/// resolver, the single-instance election, and any GUI work: the SDK
/// inspects the raw command line for its <c>--veloapp-*</c> hooks
/// (install / update / obsolete / uninstall fast-exit callbacks) and
/// those hooks exit the process before our own argument handling may see
/// them. Never throws: a Velopack failure must degrade to "no updates",
/// never to a refused launch.
/// </summary>
/// <remarks>
/// Verified against the pinned SDK source
/// (<c>src/lib-csharp/VelopackApp.cs</c> at 1.2.161):
/// <list type="bullet">
/// <item><c>SetAutoApplyOnStartup(false)</c> exists and defaults to true;
/// leaving it on would restart-apply a staged update on next launch
/// without asking, which this app's explicit-approval flow forbids.</item>
/// <item><c>SetAppUserModelId</c> overrides the AUMID the SDK would
/// otherwise derive from the package id; ours is
/// <see cref="AppIdentity.AumId"/> (the <c>_WinttyAumId</c> build
/// override the packager sets per install), matching the explicit AUMID
/// <c>App.OnLaunched</c> sets before any shell interop.</item>
/// <item><c>Run()</c> with no custom args reads the process command line
/// itself; fast-exit hooks terminate via the locator's process exit.
/// The non-exiting startup path only cleans old packages and runs
/// first-run/restarted user hooks.</item>
/// </list>
/// </remarks>
internal static class VelopackBootstrap
{
    /// <summary>
    /// Whether this process was restarted by Update.exe after an apply
    /// (the <c>VELOPACK_RESTART</c> environment marker). Captured before
    /// <see cref="RunStartupHooks"/> runs the SDK, which clears the
    /// variable as part of startup; the launch guard reads this to tell a
    /// failed apply (our own restart, old version) apart from a live one
    /// (a concurrent launch during the apply).
    /// </summary>
    public static bool WasRestartedByUpdater { get; private set; }

    public static void RunStartupHooks()
    {
        try
        {
            WasRestartedByUpdater = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VELOPACK_RESTART"));
        }
        catch
        {
            WasRestartedByUpdater = false;
        }

        try
        {
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .SetAppUserModelId(AppIdentity.AumId)
                .Run();
        }
        catch (Exception ex)
        {
            // Best effort by contract (see the class doc): the app runs
            // un-updated rather than not at all.
            Program.WriteStartupDiagnostic(
                $"Velopack startup hooks skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
