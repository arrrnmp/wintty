# PowerShell Shell Integration

Wintty (the Windows build of Ghostty) ships a PowerShell shell-integration
script alongside the per-shell scripts for bash, zsh, fish, etc.

## How it loads

When wintty spawns a PowerShell child process, it exports the path to the
script in the `GHOSTTY_SHELL_INTEGRATION_PS1` environment variable.

For a bare interactive shell (the user configured just `pwsh`, with no
command or arguments of their own) wintty goes further and rewrites the
launch command to auto-source the script:

```powershell
<pwsh> -NoExit -ExecutionPolicy RemoteSigned -Command ". '<resource_dir>/.../ghostty.ps1'"
```

PowerShell still loads the user's `$PROFILE` first, then runs the
`-Command`, which dot-sources the script. The script wraps the now-final
`prompt` function and emits the OSC 133 marks.

If the user supplied their own command or arguments (e.g. `pwsh -NoLogo`
or a `-Command`/`-File` invocation), wintty leaves the command untouched
so it never clobbers the invocation; only the env var is exported. Those
shells opt in by adding one line to their `$PROFILE`:

```powershell
if ($env:GHOSTTY_SHELL_INTEGRATION_PS1) { . $env:GHOSTTY_SHELL_INTEGRATION_PS1 }
```

To find or create your profile:

```powershell
if (-not (Test-Path $PROFILE)) { New-Item -Type File -Path $PROFILE -Force }
notepad $PROFILE
```

## Manual sourcing

If `GHOSTTY_SHELL_INTEGRATION_PS1` is unset (e.g. shell integration is
disabled in the wintty config), you can source the script by absolute path:

```powershell
. "C:\path\to\ghostty\src\shell-integration\powershell\ghostty.ps1"
```

## Execution policy and downloaded ZIPs (Mark of the Web)

The `-ExecutionPolicy RemoteSigned` in the launch above applies to the
wintty tab's process only: it is stored in the `PSExecutionPolicyPreference`
environment variable, which child processes inherit and which disappears
when the session closes. It never touches the persisted machine or user
policy, and it is deliberately not `Bypass`: `RemoteSigned` still refuses
an unsigned script that carries Mark-of-the-Web (ZoneId 3), while `Bypass`
would run anything without checking. (See
[about_Execution_Policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies)
and
[Unblock-File](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file).)

That is exactly what Explorer's ZIP extractor stamps on every file it
extracts from a ZIP downloaded in a browser, so after extracting the
unsigned portable ZIP that way, the auto-sourced `ghostty.ps1` fails to
load ("not digitally signed", `PSSecurityException`) while an
installer-written tree -- where nothing carries the stamp -- loads fine.
The policy is correct; the stamp is a property of how the file arrived,
not a broken script.

After reviewing the script and its source and deciding you trust it,
remove the stamp from the file you received, then open a new tab:

```powershell
# On the downloaded ZIP, BEFORE extracting (preferred: nothing extracted carries the stamp):
Unblock-File -LiteralPath 'C:\path\to\wintty-unsigned-win-x64-<commit>.zip'

# Or, if already extracted, on the exact script copy:
Unblock-File -LiteralPath 'C:\path\to\ghostty.ps1'
```

`Unblock-File` deletes the `Zone.Identifier` alternate data stream
(ZoneId 3 = Internet). Before running it, review the file and its source
and verify it is safe to open. Use `-LiteralPath` (no wildcard
expansion) and unblock only the file you reviewed -- never sweep a
directory. Tabs that already started keep the failed load; the fix takes
effect in new tabs. To check whether the stamp is present:

```powershell
Get-Item 'C:\path\to\ghostty.ps1' -Stream Zone.Identifier -ErrorAction SilentlyContinue
```

A `Process`-scope `RemoteSigned` overrides a locally set
`CurrentUser`/`LocalMachine` policy (including `AllSigned` set with
`Set-ExecutionPolicy`), but it does not override Group Policy:
`MachinePolicy`/`UserPolicy` outrank `Process`, so an organisation that
enforces `AllSigned` through "Turn on Script Execution" still refuses
this unsigned script after unblocking. Only a trusted Authenticode
signature would load there, which unsigned builds do not carry. Do not
work around that by lowering any policy (`Bypass`, `Unrestricted`,
`Set-ExecutionPolicy`), by piping the script through `Invoke-Expression`,
or by stripping stamps indiscriminately -- those trade away the checking
that keeps the tab safe. The regression script
`windows/scripts/powershell-integration-trust-test.ps1` demonstrates all
three behaviours (refused-while-stamped, loads-once-unblocked,
still-refused-under-`AllSigned`) without changing any persisted policy.

## What it does

- Emits OSC 133 prompt marks (A/B/C/D) so wintty can identify prompt
  boundaries, command input regions, and command exit codes.
- Reports the current working directory via OSC 7 (file:// URI) and
  OSC 9;9 (raw Windows path).
- Wraps the user's existing `prompt` function, so Oh-My-Posh, Starship,
  posh-git, and similar prompt customizations keep working.

## Compatibility

- Works on Windows PowerShell 5.1 and PowerShell 7+.
- No external dependencies.
- Uses PSReadLine if present (it ships with both 5.1 and 7+) to bracket
  the running command with OSC 133;C / 133;D. If PSReadLine is absent or
  fails to load, A/B prompt marks still work and the script does not
  raise an error.
- Idempotent: sourcing the script a second time is a no-op.
