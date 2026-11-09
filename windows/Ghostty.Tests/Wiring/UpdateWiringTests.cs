using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Wiring guards for the built-in Velopack updater: the SDK runs first in
/// Main with auto-apply off, the launch gate refuses startup over a live
/// apply, the check starts once the GUI is ready, and the shutdown path
/// never exits through the SDK (only the non-exiting UpdateExe.Apply, after
/// the UI thread has joined). Behaviour lives in the Core policy/guard
/// unit tests; these pin the shape the fix left behind.
/// </summary>
public class UpdateWiringTests
{
    [Fact]
    public void Main_Runs_Velopack_Hooks_First()
    {
        var main = ShellSource.Load("Program.cs").Method("Main");

        var statements = main.Body!.Statements.ToList();
        var runAt = statements.FindIndex(
            s => s.ToString().Contains("VelopackBootstrap.RunStartupHooks"));
        var threadAt = statements.FindIndex(s => s.ToString().Contains("new Thread"));

        Assert.Equal(0, runAt);
        Assert.True(threadAt > runAt, "the STA thread must start after the Velopack hooks");
    }

    [Fact]
    public void Main_Checks_The_Apply_Gate_Before_MainImpl()
    {
        var main = ShellSource.Load("Program.cs").Method("Main");

        var thread = main.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "Thread");
        var lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(
            thread.ArgumentList!.Arguments[0].Expression);
        var body = Assert.IsType<BlockSyntax>(lambda.Body);

        Assert.Contains("CheckApplyMarkerAndExitIfApplying", body.Statements[0].ToString());
        Assert.Contains(body.Statements, s => s.ToString().Contains("MainImpl(args)"));
    }

    [Fact]
    public void Main_Hands_Off_After_Join_On_Clean_Shutdown_Only()
    {
        var main = ShellSource.Load("Program.cs").Method("Main");
        var text = main.Body!.ToString();

        var joinAt = text.IndexOf("main.Join()", System.StringComparison.Ordinal);
        var handoffAt = text.IndexOf("PerformHandoffIfRequested", System.StringComparison.Ordinal);
        Assert.True(joinAt >= 0 && handoffAt > joinAt,
            "the handoff must run after the UI thread joined, and nothing may follow it");

        // Gated on a clean shutdown: a crash never applies.
        var gate = main.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("exitCode == 0"));
        Assert.Contains("PerformHandoffIfRequested", gate.Statement.ToString());
    }

    [Fact]
    public void VelopackBootstrap_Disables_Auto_Apply_With_Our_Aumid()
    {
        var bootstrap = ShellSource.Load("Updates.VelopackBootstrap.cs");

        var autoApply = Assert.Single(bootstrap.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("SetAutoApplyOnStartup")));
        Assert.Equal("false", autoApply.Arg(0));

        var aumid = Assert.Single(bootstrap.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("SetAppUserModelId")));
        Assert.Contains("AppIdentity.AumId", aumid.Arg(0));

        Assert.Single(bootstrap.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(".Run")));
    }

    [Fact]
    public void VelopackBootstrap_Captures_The_Restart_Marker_Before_Run()
    {
        // Run() clears VELOPACK_RESTART; the launch guard reads the
        // capture to tell a failed apply (our own restart, old version)
        // from a live one (a concurrent launch during the apply).
        var bootstrap = ShellSource.Load("Updates.VelopackBootstrap.cs");

        var read = Assert.Single(bootstrap.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("GetEnvironmentVariable")));
        Assert.Contains("VELOPACK_RESTART", read.Arg(0));

        var text = bootstrap.Root.ToString();
        Assert.True(
            text.IndexOf("WasRestartedByUpdater", System.StringComparison.Ordinal)
            < text.IndexOf(".Run()", System.StringComparison.Ordinal),
            "the restart capture must happen before Run clears the variable");
    }

    [Fact]
    public void Shell_Never_Calls_The_Exiting_Updater_Entry_Points()
    {
        // ApplyUpdatesAndRestart / ApplyUpdatesAndExit call Environment.Exit
        // (skipping teardown); WaitExitThenApplyUpdates while UI/PTYs are
        // alive lets Update.exe force-stop the install root after 60s. The
        // only handoff is the non-exiting UpdateExe.Apply, after shutdown.
        // Invocation-level, not text: the safety comments name these.
        foreach (var file in ShellSource.AllFiles())
        {
            var exiting = file.Root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText().EndsWith("ApplyUpdatesAndRestart")
                    || i.CalleeText().EndsWith("ApplyUpdatesAndExit")
                    || i.CalleeText().EndsWith("WaitExitThenApplyUpdates"))
                .ToList();
            Assert.True(exiting.Count == 0,
                $"{file.Name} calls a self-exiting updater entry point");
        }
    }

    [Fact]
    public void Handoff_Uses_The_Non_Exiting_Apply_And_Never_Exits()
    {
        var handoff = ShellSource.Load("Updates.UpdateHandoff.cs");

        Assert.Single(handoff.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("UpdateExe.Apply")));

        Assert.Empty(handoff.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("Environment.Exit")));
    }

    [Fact]
    public void Launch_Guard_Aborts_Before_Any_UI()
    {
        var guard = ShellSource.Load("Updates.UpdateLaunchGuard.cs");
        var check = guard.Method("CheckApplyMarkerAndExitIfApplying");

        Assert.Contains("EvaluateLaunch", check.ToString());
        Assert.Contains("WasRestartedByUpdater", check.ToString());
        // The exit lives in the Abort helper the check calls (one exit
        // path for every abort reason), so the file scope — not the check
        // method alone — owns the "aborts via Environment.Exit" pin.
        Assert.NotEmpty(guard.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("Environment.Exit")));
    }

    [Fact]
    public void Update_Check_Starts_After_GUI_Ready()
    {
        var launched = ShellSource.Load("App.xaml.cs").Method("OnLaunched");
        Assert.Single(launched.Calls("StartUpdateCheck"));
    }

    [Fact]
    public void StartUpdateCheck_Honours_The_Opt_Out_And_Builds_The_Service()
    {
        var start = ShellSource.Load("App.xaml.cs").Method("StartUpdateCheck");
        var text = start.ToString();

        Assert.Contains("UpdatePolicy.ConfigKey", text);
        Assert.Contains("ParseBool", text);

        Assert.Single(start.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString().Contains("UpdateService")));
        Assert.Contains("CloseAllWindows", text);
        Assert.Single(start.CallsEndingWith("AllWindows.Any"));
    }

    [Fact]
    public void Last_Window_Teardown_Cancels_The_Update_Check()
    {
        var teardown = ShellSource.Load("App.xaml.cs").Method("OnAnyWindowClosedInternal");

        var cancel = Assert.Single(teardown.CallsEndingWith("Cancel"));
        Assert.Contains("_updateServiceCts", cancel.CalleeText());
    }

    [Fact]
    public void UpdateService_Checks_The_Stable_Feed_With_No_Overrides()
    {
        var service = ShellSource.Load("Updates.UpdateService.cs");

        // Default (packaged) channel, no token: installed builds track the
        // stable feed. Prereleases stay excluded.
        var source = Assert.Single(service.Root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "GithubSource"));
        Assert.Contains("FeedRepositoryUrl", source.ArgumentList!.ToString());
        Assert.Equal("false", source.ArgumentList!.Arguments[2].ToString());

        // No production feed backdoor: the URL is a constant, never read
        // from the environment. Invocation-level so a comment mentioning
        // the environment cannot fail this.
        Assert.Empty(service.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("GetEnvironmentVariable")));
    }
}
