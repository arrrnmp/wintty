#requires -Version 7
<#
    Headless A->B update smoke for the unsigned OSS packages. No GUI, no GPU.

    Packs two Velopack versions (A, then B) from the same NativeAOT publish
    tree, installs A per-user into a GUID temp dir, applies B with the real
    installed Update.exe, and proves the install- and update-time fast hooks
    exit quickly instead of launching the app. Cleans up only what it staged.

    Never launches Wintty normally: every execution is a documented
    --veloapp-* hook probe (fast exit, no UI by contract), Setup --silent
    (documented to skip the post-install launch), or Update.exe apply
    --norestart. If a hook ever blocks instead of exiting, the script kills
    only the process it started and fails: that means a real A->B needs a
    headless host API the product does not have, and must be reported, not
    faked.

    Runs on Windows only (install/apply/ADS), headless-safe on Server Core.
#>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$VersionA,
    [Parameter(Mandatory)][string]$VersionB,
    [string]$WorkRoot = [IO.Path]::GetTempPath(),
    [string]$PackId = 'arrrnmp.wintty.oss',
    [string]$PackTitle = 'Wintty OSS',
    [string]$Channel = 'stable',
    [string]$MainExe = 'Wintty.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'update-package-smoke runs on Windows only.' }

# The config-isolation scan reads every app launch under windows/scripts:
# stage an isolated config root for everything this run starts. The hook
# probes exit through the SDK fast hooks before config is read, and the
# install/apply/uninstall steps never launch the app, so the isolated root
# changes no verdict while keeping a harness run off the real %APPDATA%.
. (Join-Path $PSScriptRoot 'lib/test-config.ps1')
$null = Enter-WinttyTestConfig -PrivateStateBase

foreach ($v in @($VersionA, $VersionB)) {
    if ($v -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "Not a stable X.Y.Z semver: $v" }
}
if ([version]$VersionB -le [version]$VersionA) { throw "VersionB ($VersionB) must be greater than VersionA ($VersionA)." }

$packScript = Join-Path $PSScriptRoot 'package-unsigned-update.ps1'
$work = Join-Path $WorkRoot ("wintty-update-smoke-" + [guid]::NewGuid())
$releases = Join-Path $work 'releases'
$installA = Join-Path $work 'install-A'
$outside = Join-Path $work 'outside-settings'
foreach ($d in @($releases, $installA, $outside)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
# --installto must stay inside our own temp staging: never Program Files and
# never the real per-user %LOCALAPPDATA%\<packId> install.
$tempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd('\')
if (((Resolve-Path -LiteralPath $work).Path.TrimEnd('\') + '\') -notlike ($tempRoot + '\*')) {
    throw "Staging dir escaped the temp root: $work"
}
function Assert-InstalledVersion([string]$root, [string]$want, [string]$hash) {
        $current = Join-Path $root 'current'
        # sq.version is the package manifest (nuspec XML), not a bare version
        # string: the installed version is its <version> element.
        $sqRaw = Get-Content -LiteralPath (Join-Path $current 'sq.version') -Raw
        if ($sqRaw -notmatch "<version>\s*$([regex]::Escape($want))\s*</version>") {
            throw "sq.version manifest does not name version '$want'"
        }
        foreach ($f in @($MainExe, 'Wintty.pri', 'OpenConsole.dll',
                "licenses/Wintty-LICENSE.txt", 'share/ghostty/shell-integration')) {
            if (-not (Test-Path -LiteralPath (Join-Path $current $f))) { throw "Installed tree lacks: $f" }
        }
        # .pdb files stay out of installer/update packages by pack default;
        # the ordinary portable artifact still carries symbols.
        $ps1 = @(Get-ChildItem -LiteralPath $current -Recurse -Filter 'ghostty.ps1' -File)
        if ($ps1.Count -ne 1) { throw "Expected one installed ghostty.ps1, found $($ps1.Count)" }
        if ($motw -and (Get-Item -LiteralPath $ps1[0].FullName -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue)) {
            throw "Installed ghostty.ps1 carries Mark-of-the-Web from the marked Setup copy."
        }
        # RemoteSigned in a child process is the honest load test: an MOTW-
        # marked script would refuse to load without a signature.
        & pwsh -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -Command `
            ". '$($ps1[0].FullName)'; if (-not (Get-GhosttyPromptReport 'C:\smoke' 0)) { exit 1 }"
        if ($LASTEXITCODE -ne 0) { throw 'Installed ghostty.ps1 does not load under RemoteSigned.' }
        if ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $hash) {
            throw 'Settings marker outside the install root changed.'
        }
        Write-Host "install tree ok: version=$want ghostty.ps1 loads, marker untouched"
    }

try {
    $exe = Join-Path $PublishDir $MainExe
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Main exe not found: $exe" }

    function Assert-NoStagedProcess([string]$root, [string]$why) {
        $hits = @(Get-Process -Name 'Wintty', 'Update' -ErrorAction SilentlyContinue | ForEach-Object {
            try { $p = $_.Path } catch { $p = $null }
            if ($p -and (($p + '\') -like ((Resolve-Path -LiteralPath $root).Path.TrimEnd('\') + '\*'))) { $_ }
        })
        if ($hits.Count -gt 0) { throw "${why}: pid(s) $(($hits | ForEach-Object Id) -join ', ') still running from $root" }
    }

    function Invoke-HookProbe([string]$hook, [string]$version) {
        $since = Get-Date
        $p = Start-Process -FilePath $exe -ArgumentList @($hook, $version) `
            -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $work 'hook.out') -RedirectStandardError (Join-Path $work 'hook.err')
        # WaitForExit(ms) answers exit-vs-timeout as a bool; Wait-Process's
        # timeout reporting is not a contract to rely on here.
        if (-not $p.WaitForExit(60000)) {
            try { Stop-Process -InputObject $p -Force } catch { }
            throw "Hook probe '$hook $version' did not exit in 60s (killed own pid $($p.Id)): real install/apply would need a headless host API the product does not have."
        }
        $ms = [int]((Get-Date) - $since).TotalMilliseconds
        if ($p.ExitCode -ne 0) { throw "Hook probe '$hook $version' exited $($p.ExitCode) in ${ms}ms, expected fast 0." }
        Write-Host "hook ok: $hook $version exit=0 in ${ms}ms"
    }

    # Phase 0: the SDK fast hooks must exit, never show UI or touch the GPU.
    Invoke-HookProbe '--veloapp-install' $VersionA
    Invoke-HookProbe '--veloapp-obsolete' $VersionA
    Invoke-HookProbe '--veloapp-updated' $VersionB

    # Phase 1: pack version A first and install from exactly that Setup:
    # the Setup file name carries no version, so packing B afterwards
    # would overwrite it (same payload, but embedding B). A must be
    # installed and proven before B lands beside it for the delta.
    & $packScript -PublishDir $PublishDir -OutputDir $releases -Version $VersionA `
        -PackId $PackId -PackTitle $PackTitle -Channel $Channel -MainExe $MainExe
    if ($LASTEXITCODE -ne 0) { throw 'pack A failed' }
    $setup = @(Get-ChildItem -LiteralPath $releases -Filter '*-Setup.exe' -File)
    if ($setup.Count -ne 1) { throw "Expected one Setup after pack A, found $($setup.Count)." }
    Write-Host "setup for A: $([IO.Path]::GetFileName($setup[0].FullName))"

    # A marker outside the install root: install/apply must leave it alone.
    $marker = Join-Path $outside 'settings-marker.txt'
    'wintty-update-smoke settings marker, do not touch' | Set-Content -LiteralPath $marker -NoNewline
    $markerHash = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash

    # Phase 2: browser-mark OUR OWN setup copy (ZoneId=3) when the filesystem
    # allows it, then install silently from it. --silent is the documented
    # prevent-launch flag: the app must not start after install.
    $marked = Join-Path $work 'Setup.marked.exe'
    Copy-Item -LiteralPath $setup[0].FullName -Destination $marked
    Write-Host "setup copy ready: $marked"
    $motw = $false
    try {
        Set-Content -LiteralPath $marked -Stream 'Zone.Identifier' -Value "[ZoneTransfer]`nZoneId=3" -ErrorAction Stop
        $motw = $true
    } catch { Write-Host 'note: ADS unavailable, skipping MOTW simulation' }
    Write-Host 'probing for staged processes before install...'
    Assert-NoStagedProcess $installA 'refusing to install over a running staged app'
    Write-Host 'no staged processes; starting silent install...'
    # Start-Process -Wait, not the call operator: Setup.exe is a GUI
    # binary (it can show a splash), and & returns as soon as a GUI
    # process starts instead of when it finishes. Waiting on the call
    # operator would race the background install and assert on a
    # half-written tree.
    $p = Start-Process -FilePath $marked -ArgumentList @('--silent', '--installto', $installA) `
        -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(300000)) {
        try { Stop-Process -InputObject $p -Force } catch { }
        throw "silent install timed out after 300s (killed pid $($p.Id))"
    }
    if ($p.ExitCode -ne 0) { throw "silent install failed exit=$($p.ExitCode)" }
    # The exit above means Setup is done, but settle anyway: a slow disk
    # must never turn into a missing-tree failure here.
    $deadline = (Get-Date).AddSeconds(180)
    while (-not (Test-Path -LiteralPath (Join-Path $installA 'current\sq.version') -PathType Leaf)) {
        if ((Get-Date) -gt $deadline) { throw 'install tree never materialized current\sq.version' }
        Start-Sleep -Milliseconds 500
    }
    Assert-NoStagedProcess $installA 'install launched the app despite --silent'

    # Phase 3: installed A is complete and unmarked.
    Assert-InstalledVersion $installA $VersionA $markerHash

    # Phase 4: pack B beside A so the pack emits the A->B delta and a
    # merged feed, like a real patch release; then apply B onto installed
    # A with the real installed Update.exe, no restart, no instances.
    & $packScript -PublishDir $PublishDir -OutputDir $releases -Version $VersionB `
        -PackId $PackId -PackTitle $PackTitle -Channel $Channel -MainExe $MainExe
    if ($LASTEXITCODE -ne 0) { throw 'pack B failed' }
    # Feed-resolved, never hardcoded: vpk infixes the channel in package names.
    $feedB = Get-Content -LiteralPath (Join-Path $releases "releases.$Channel.json") -Raw | ConvertFrom-Json
    $fullB = @($feedB.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $VersionB })
    if ($fullB.Count -ne 1) { throw "Expected one Full asset for $VersionB, found $($fullB.Count)." }
    $packageB = Join-Path $releases $fullB[0].FileName
    if (-not (Test-Path -LiteralPath $packageB -PathType Leaf)) { throw "Missing B package: $packageB" }
    Assert-NoStagedProcess $installA 'refusing to apply over a running staged app'
    $updateExe = Join-Path $installA 'Update.exe'
    if (-not (Test-Path -LiteralPath $updateExe -PathType Leaf)) { throw "Missing installed updater: $updateExe" }
    # Start-Process with a hard timeout, not the call operator: an apply
    # that prompts (or otherwise blocks) on a headless runner must fail
    # loudly instead of wedging the step until the job timeout. --silent
    # keeps dialogs off here; the smoke proves extract/swap/verify
    # mechanics, not progress UI (the product handoff is the non-silent
    # one, on interactive machines).
    $p = Start-Process -FilePath $updateExe `
        -ArgumentList @('apply', '--silent', '--norestart', '--package', $packageB) `
        -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(300000)) {
        try { Stop-Process -InputObject $p -Force } catch { }
        Write-Host '--- install tree on apply timeout ---'
        Get-ChildItem -LiteralPath $installA -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 20 FullName | ForEach-Object { Write-Host $_.FullName }
        throw "Update.exe apply timed out after 300s (killed pid $($p.Id))"
    }
    if ($p.ExitCode -ne 0) { throw "Update.exe apply failed exit=$($p.ExitCode)" }
    Assert-NoStagedProcess $installA 'apply restarted the app despite --norestart'

    # Phase 5: B is live, whole, and still unmarked; outside settings intact.
    Assert-InstalledVersion $installA $VersionB $markerHash

    Write-Host "smoke ok: install $VersionA -> apply $VersionB, hooks fast, no GUI, marker untouched"
}

finally {
    # Only what this run staged: uninstall our temp install (drops its own
    # shortcuts), then remove the GUID workdir. Nothing else is touched.
    try {
        $u = Join-Path $installA 'Update.exe'
        if (Test-Path -LiteralPath $u -PathType Leaf) {
            $p = Start-Process -FilePath $u -ArgumentList @('uninstall') -PassThru -WindowStyle Hidden
            if (-not $p.WaitForExit(60000)) {
                try { Stop-Process -InputObject $p -Force } catch { }
            }
        }
    } catch { }
    try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
