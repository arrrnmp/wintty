#requires -Version 7
<#
    Packs the published unsigned app into Velopack installer + update packages.

    Thin wrapper over `dotnet vpk pack` (vpk 1.2.161, pinned in
    .config/dotnet-tools.json) with this fork's identity baked in, followed by
    a fail-closed check of everything pack produced: the Setup installer, the
    full nupkg for -Version, the optional delta, and a releases feed whose
    entries name files that exist with matching size and hash.

    The app's own hook probes (Phase 0 of the sibling smoke script, plus the
    explicit CI step) are the proof that the VelopackApp.Run() wiring works
    before any GPU code runs: vpk 1.2.161 pack has no in-pack bootstrap
    check and no --aumid flag, so neither is passed here. The process AUMID
    comes from the app's own startup wiring (BuildInfo AUMID, baked per
    build with _WinttyAumId), not the pack step. No signing flags, no MSI,
    no private overlays: this is the unsigned per-user build.
#>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$PackId = 'arrrnmp.wintty.oss',
    [string]$PackTitle = 'Wintty OSS',
    [string]$Channel = 'stable',
    [string]$MainExe = 'Wintty.exe'
)
$ErrorActionPreference = 'Stop'

# Stable releases only: plain X.Y.Z, no prerelease or build metadata, so the
# feed sorts and the tag name maps to it exactly (oss-vX.Y.Z).
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Version '$Version' is not a stable X.Y.Z semver (oss-vX.Y.Z without the prefix)."
}
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish dir not found: $PublishDir"
}
$mainPath = Join-Path $PublishDir $MainExe
if (-not (Test-Path -LiteralPath $mainPath -PathType Leaf)) {
    throw "Main exe not found in publish dir: $mainPath"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

# The app's own hook probes (Phase 0 of the smoke script, plus the
# explicit CI bootstrap step) are the proof that the SDK wiring works:
# vpk 1.2.161 pack runs no in-pack check of its own.
& dotnet vpk pack `
    --packId $PackId `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe $MainExe `
    --packTitle $PackTitle `
    --channel $Channel `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed exit=$LASTEXITCODE" }

# --- fail-closed output check ---
function Assert-PackageFile([string]$name) {
    $p = Join-Path $OutputDir $name
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing package output: $name" }
    return $p
}

$setups = @(Get-ChildItem -LiteralPath $OutputDir -Filter '*-Setup.exe' -File)
if ($setups.Count -ne 1) { throw "Expected exactly one *-Setup.exe, found $($setups.Count)" }

$feedName = "releases.$Channel.json"
$feedPath = Assert-PackageFile $feedName
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
if ($null -eq $feed.Assets) { throw "$feedName has no Assets list" }
# The feed is authoritative for file names: vpk infixes the channel
# (arrrnmp.wintty.oss-stable-Setup.exe, *-stable-full.nupkg, ...), so a
# hardcoded name would rot the moment the channel does. Resolve the Full
# asset for -Version from the feed, then prove every listed file exists
# with a matching size and hash.
$full = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version })
if ($full.Count -ne 1) { throw "$feedName has no unique Full asset for $Version (found $($full.Count))" }
$fullName = $full[0].FileName
$fullPath = Assert-PackageFile $fullName
foreach ($asset in $feed.Assets) {
    $file = Join-Path $OutputDir $asset.FileName
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Feed lists missing file: $($asset.FileName)" }
    $actual = Get-Item -LiteralPath $file
    if ([long]$asset.Size -ne $actual.Length) {
        throw "Size mismatch for $($asset.FileName): feed=$($asset.Size) disk=$($actual.Length)"
    }
    $expected = if ($asset.SHA256) { $asset.SHA256 } else { $asset.SHA1 }
    if (-not $expected) { throw "Feed asset has no hash: $($asset.FileName)" }
    $algo = if ($asset.SHA256) { 'SHA256' } else { 'SHA1' }
    $digest = (Get-FileHash -LiteralPath $file -Algorithm $algo).Hash
    if ($digest -ne $expected.ToUpperInvariant()) { throw "Hash mismatch for $($asset.FileName) ($algo)" }
}
$deltas = @($feed.Assets | Where-Object { $_.Type -eq 'Delta' -and $_.Version -eq $Version })
Write-Host "pack ok: $([IO.Path]::GetFileName($setups[0].FullName)) + $fullName + $feedName ($($deltas.Count) delta(s) for $Version)"
