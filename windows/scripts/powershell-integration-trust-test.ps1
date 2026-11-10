<#
.SYNOPSIS
  Regression test for the unsigned portable build's PowerShell trust story.

.DESCRIPTION
  Proves three behaviours of the SHIPPED ghostty.ps1 on Windows with NTFS,
  without changing any persisted execution policy and without any GUI:

    1. A copy stamped with an Internet-zone Mark-of-the-Web (ZoneId 3) is
       REFUSED under a process-scoped RemoteSigned policy -- the failure
       users see after Explorer extracts a browser-downloaded ZIP.
    2. The exact same copy, once reviewed and unblocked with Unblock-File,
       LOADS under process RemoteSigned and the integration reports.
    3. That unblocked-but-unsigned copy is STILL REFUSED under a
       process-scoped AllSigned policy -- the shape of a Group-Policy
       enforced AllSigned, which unblocking cannot satisfy and which only
       a trusted Authenticode signature would (unsigned builds carry none).

  The script copies the shipped file into its own fresh temp directory, so
  Zone stamping touches only files it created; the repo source is only
  read (and hash-compared). Each child runs with -NoProfile
  -NonInteractive and a process -ExecutionPolicy, exactly the
  process-scope mechanism wintty's own launch uses (wintty passes
  -NoExit -Command for the interactive tab; this test passes
  -EncodedCommand so the check is bounded). No Set-ExecutionPolicy, no
  Bypass/Unrestricted, no Invoke-Expression, no profile or registry
  writes. Cleanup removes only the scratch directory this run created.

  Trust model: the copy is made by this script from -SourceScript, and
  Unblock-File is applied only to that exact copy after the person running
  the test has reviewed that source. This mirrors the documented user
  flow: review first, then unblock the file you received.

  A child proves loading with the integration's own marker, not merely a
  zero exit code: $global:__GhosttyShellIntegrationLoaded must be true,
  Get-GhosttyPromptReport must exist, and it must return a non-empty
  report for a fixed path. Dot-sourcing failures can be non-terminating,
  so the child checks the marker explicitly and exits non-zero when it is
  absent, whatever the error stream did.

.PARAMETER SourceScript
  The ghostty.ps1 under test. Defaults to the shipped script in this
  checkout; point it at an installer or publish tree to validate that
  output instead, e.g.:
    -SourceScript 'C:\publish\shell-integration\powershell\ghostty.ps1'

.PARAMETER TimeoutMs
  Per-child-process timeout in milliseconds.

.EXAMPLE
  powershell -NoProfile -File windows/scripts/powershell-integration-trust-test.ps1
  pwsh -NoProfile -File windows/scripts/powershell-integration-trust-test.ps1

  Runs under Windows PowerShell 5.1, PowerShell 7+, or both when present;
  the report names which shells ran.

.NOTES
  Windows + NTFS only: Zone.Identifier alternate data streams need NTFS,
  and ADS support is probed before anything is asserted. Exit codes:
  0 = every assertion held; 1 = a behaviour assertion failed (or a child
  timed out, so the behaviour could not be shown); 2 = environment/usage
  error (not Windows, no NTFS ADS, source missing, no shell found).
#>
param(
    [string]$SourceScript = (Join-Path $PSScriptRoot '..\..\src\shell-integration\powershell\ghostty.ps1'),
    [int]$TimeoutMs = 60000
)

$ErrorActionPreference = 'Stop'

# The config-isolation scan reads every process launch under
# windows/scripts as a possible app launch. These children are pwsh, not
# the app, and they only dot-source the integration script; arming the
# guard costs nothing and keeps the scan honest.
$env:WINTTY_TEST_CONFIG = '1'

$script:fails = 0
function TestTrustFail([string]$msg) { $script:fails++; Write-Host "FAIL: $msg" }
function TestTrustPass([string]$msg) { Write-Host "ok: $msg" }

# --- environment -----------------------------------------------------------

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    Write-Host 'ENV: this test requires Windows (Zone.Identifier streams and the Windows PowerShell hosts). Nothing was changed.'
    exit 2
}

$source = (Resolve-Path -LiteralPath $SourceScript -ErrorAction SilentlyContinue)
if (-not $source) {
    Write-Host ("ENV: SourceScript not found: $SourceScript")
    exit 2
}
$source = $source.Path

$sourceText = [System.IO.File]::ReadAllText($source)
if ($sourceText -notmatch '__GhosttyShellIntegrationLoaded') {
    Write-Host 'ENV: SourceScript does not carry the __GhosttyShellIntegrationLoaded marker this test asserts; refusing to assert against the wrong file.'
    exit 2
}
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
Write-Host "source: $source"
Write-Host "source sha256: $sourceHash"

# Shells under test: Windows PowerShell 5.1 and PowerShell 7+, each when
# present. The report names which ran, so a single-edition machine still
# gives a readable result.
$shells = @()
$sysPs = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path -LiteralPath $sysPs) {
    $shells += [pscustomobject]@{ Key = 'windows-powershell'; Exe = $sysPs }
} else {
    $found = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($found) { $shells += [pscustomobject]@{ Key = 'windows-powershell'; Exe = $found.Source } }
}
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCmd) {
    $isDup = @($shells | Where-Object { $_.Exe -ieq $pwshCmd.Source }).Count -gt 0
    if (-not $isDup) { $shells += [pscustomobject]@{ Key = 'pwsh'; Exe = $pwshCmd.Source } }
}
if ($shells.Count -eq 0) {
    Write-Host 'ENV: no PowerShell host found (neither windows-powershell nor pwsh). Nothing was changed.'
    exit 2
}
Write-Host ("shells: " + (($shells | ForEach-Object { "$($_.Key) [$($_.Exe)]" }) -join '; '))

if ($TimeoutMs -lt 1000) {
    Write-Host 'ENV: -TimeoutMs is below 1000ms; a child cannot be judged on less.'
    exit 2
}

# --- child harness ----------------------------------------------------------
#
# Fixed command text: the script path travels in GHOSTTY_TEST_PS1 (data via
# the environment block, never interpolated into code) and the command
# itself goes over -EncodedCommand, so no quoting layer can splice a path
# into code. $Policy is caller-side and restricted to an allowlist below.
$childTemplate = @'
$ErrorActionPreference='Stop'
try { . $env:GHOSTTY_TEST_PS1 } catch { Write-Host ('GHOSTTY_TEST_LOAD_ERROR catch=' + $_.FullyQualifiedErrorId + ' ' + $_.Exception.Message); exit 11 }
if (-not $global:__GhosttyShellIntegrationLoaded) { Write-Host 'GHOSTTY_TEST_LOAD_ERROR marker __GhosttyShellIntegrationLoaded missing'; exit 12 }
if (-not (Get-Command Get-GhosttyPromptReport -CommandType Function -ErrorAction SilentlyContinue)) { Write-Host 'GHOSTTY_TEST_LOAD_ERROR Get-GhosttyPromptReport missing'; exit 13 }
$ghosttyTestSeq = Get-GhosttyPromptReport 'C:\Users\me' 7
if ([string]::IsNullOrEmpty($ghosttyTestSeq)) { Write-Host 'GHOSTTY_TEST_LOAD_ERROR prompt report empty'; exit 14 }
Write-Host ('GHOSTTY_TEST_LOADED len=' + $ghosttyTestSeq.Length)
exit 0
'@

function Invoke-GhosttyTrustChild {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$Policy,
        [Parameter(Mandatory)][string]$CopyPath,
        [int]$ChildTimeoutMs = 60000
    )
    if (@('RemoteSigned', 'AllSigned') -notcontains $Policy) { throw "internal: unexpected policy '$Policy'" }
    # The path reaches the child as data: inherited environment, read back
    # as a value, never concatenated into the command text.
    $env:GHOSTTY_TEST_PS1 = $CopyPath
    $b64 = [System.Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($childTemplate))
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy ' + $Policy + ' -EncodedCommand ' + $b64
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    # Output is a few lines, so reading after exit cannot deadlock; the wait
    # itself is bounded and a hung child is killed, never awaited forever.
    $exited = $p.WaitForExit($ChildTimeoutMs)
    if (-not $exited) {
        try { $p.Kill() } catch { }
        $p.WaitForExit() | Out-Null
        $out = ''
        $err = ''
        try { $out = $p.StandardOutput.ReadToEnd() } catch { }
        try { $err = $p.StandardError.ReadToEnd() } catch { }
        $p.Close()
        return [pscustomobject]@{ Exit = -1; Out = $out; Err = $err; TimedOut = $true }
    }
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $code = $p.ExitCode
    $p.Close()
    return [pscustomobject]@{ Exit = $code; Out = $out; Err = $err; TimedOut = $false }
}

# --- run --------------------------------------------------------------------
#
# One scratch root for the run, one subdirectory per shell, each holding
# its own stamped copy. Cleanup removes only this root.
$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ghostty-ps1-trust-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratchRoot | Out-Null
$oldTestPs1 = $env:GHOSTTY_TEST_PS1
try {
    # NTFS probe: if Zone streams cannot be written here, the stamp half of
    # the test is meaningless, so stop instead of asserting against FAT/ReFS.
    $probe = Join-Path $scratchRoot 'ads-probe.txt'
    [System.IO.File]::WriteAllText($probe, 'probe')
    try {
        Set-Content -LiteralPath $probe -Stream Zone.Identifier -Value @('[ZoneTransfer]', 'ZoneId=3') -ErrorAction Stop | Out-Null
    } catch {
        Write-Host 'ENV: this volume does not support Zone.Identifier streams (needs NTFS); stamping cannot be demonstrated here. Nothing else was changed.'
        exit 2
    }
    $probeZone = Get-Item -LiteralPath $probe -Stream Zone.Identifier -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    if (-not $probeZone) {
        Write-Host 'ENV: Zone.Identifier stream did not persist on this volume (needs NTFS). Nothing else was changed.'
        exit 2
    }
    TestTrustPass 'scratch volume supports Zone.Identifier streams (NTFS ADS probe held)'

    foreach ($s in $shells) {
        Write-Host ("--- shell: $($s.Key) [$($s.Exe)] ---")
        $dir = Join-Path $scratchRoot $s.Key
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $copy = Join-Path $dir 'ghostty.ps1'
        Copy-Item -LiteralPath $source -Destination $copy -Force

        $copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
        if ($copyHash -ne $sourceHash) {
            TestTrustFail ("$($s.Key): test copy hash differs from source; not testing the shipped file")
            continue
        }
        TestTrustPass ("$($s.Key): test copy matches source sha256")

        # Stamp ONLY this run's copy with the Internet zone, the way a
        # browser download plus Explorer extraction stamps every file.
        Set-Content -LiteralPath $copy -Stream Zone.Identifier -Value @('[ZoneTransfer]', 'ZoneId=3') -ErrorAction Stop | Out-Null
        $zoneLines = Get-Content -LiteralPath $copy -Stream Zone.Identifier -ErrorAction SilentlyContinue
        if (-not ($zoneLines -match 'ZoneId=3')) {
            TestTrustFail ("$($s.Key): ZoneId=3 stamp did not persist on the test copy")
            continue
        }
        TestTrustPass ("$($s.Key): test copy carries ZoneId=3 (Internet MOTW, scoped to this copy)")

        # 1. Stamped + unsigned must be refused under process RemoteSigned.
        $r = Invoke-GhosttyTrustChild -Exe $s.Exe -Policy 'RemoteSigned' -CopyPath $copy -ChildTimeoutMs $TimeoutMs
        if ($r.TimedOut) {
            TestTrustFail ("$($s.Key): stamped copy under process RemoteSigned timed out after ${TimeoutMs}ms; refusal could not be shown")
        } elseif (($r.Exit -eq 0) -or ($r.Out -match 'GHOSTTY_TEST_LOADED')) {
            TestTrustFail ("$($s.Key): stamped unsigned copy LOADED under process RemoteSigned (exit=$($r.Exit)); expected refusal. out=[$($r.Out)] err=[$($r.Err)]")
        } else {
            TestTrustPass ("$($s.Key): stamped unsigned copy refused under process RemoteSigned (exit=$($r.Exit))")
            Write-Host ("  refusal: " + (($r.Out + ' ' + $r.Err) -replace '\s+', ' ').Trim())
        }

        # The person running this test has reviewed -SourceScript (the copy
        # above hash-matches it); unblock exactly that reviewed copy, the
        # way the docs unblock exactly the file received.
        Unblock-File -LiteralPath $copy
        $stillZoned = Get-Item -LiteralPath $copy -Stream Zone.Identifier -ErrorAction SilentlyContinue
        if ($stillZoned) {
            TestTrustFail ("$($s.Key): Zone.Identifier stream survived Unblock-File on the test copy")
            continue
        }
        TestTrustPass ("$($s.Key): Unblock-File cleared the stamp on the exact test copy")

        # 2. Unblocked + unsigned must load under process RemoteSigned, with
        # the integration's own marker proving the file was read.
        $r = Invoke-GhosttyTrustChild -Exe $s.Exe -Policy 'RemoteSigned' -CopyPath $copy -ChildTimeoutMs $TimeoutMs
        if ($r.TimedOut) {
            TestTrustFail ("$($s.Key): unblocked copy under process RemoteSigned timed out after ${TimeoutMs}ms; loading could not be shown")
        } elseif (($r.Exit -ne 0) -or ($r.Out -notmatch 'GHOSTTY_TEST_LOADED')) {
            TestTrustFail ("$($s.Key): unblocked copy did NOT load under process RemoteSigned (exit=$($r.Exit)). out=[$($r.Out)] err=[$($r.Err)]")
        } else {
            TestTrustPass ("$($s.Key): unblocked copy loads under process RemoteSigned; marker present ($($r.Out.Trim()))")
        }

        # 3. Unblocked + unsigned must still be refused under process
        # AllSigned: the Group-Policy-enforced AllSigned shape, which only
        # a trusted Authenticode signature satisfies.
        $r = Invoke-GhosttyTrustChild -Exe $s.Exe -Policy 'AllSigned' -CopyPath $copy -ChildTimeoutMs $TimeoutMs
        if ($r.TimedOut) {
            TestTrustFail ("$($s.Key): unblocked copy under process AllSigned timed out after ${TimeoutMs}ms; refusal could not be shown")
        } elseif (($r.Exit -eq 0) -or ($r.Out -match 'GHOSTTY_TEST_LOADED')) {
            TestTrustFail ("$($s.Key): unblocked unsigned copy LOADED under process AllSigned (exit=$($r.Exit)); expected refusal. out=[$($r.Out)] err=[$($r.Err)]")
        } else {
            TestTrustPass ("$($s.Key): unblocked unsigned copy refused under process AllSigned (exit=$($r.Exit)); Group-Policy AllSigned needs a trusted signature")
        }
    }
} finally {
    if ($null -eq $oldTestPs1) { Remove-Item Env:\GHOSTTY_TEST_PS1 -ErrorAction SilentlyContinue } else { $env:GHOSTTY_TEST_PS1 = $oldTestPs1 }
    Remove-Item -LiteralPath $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:fails -gt 0) {
    Write-Host ("`n$($script:fails) failure(s)")
    exit 1
}
Write-Host ("`nall passed (shells: " + (($shells | ForEach-Object { $_.Key }) -join ', ') + ')')
exit 0
