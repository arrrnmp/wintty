# Unsigned builds of arrrnmp/wintty

This fork builds the public OSS source without changing application features
or pretending to be a paid edition. It is independent of the official Wintty
distribution and is not signed by its publisher.

## Download and run

Two flavors come out of every successful **build** job:

- **Installer + auto-updates (recommended).** Run
  `arrrnmp.wintty.oss-Setup.exe` for a per-user install that checks the
  stable GitHub Releases feed and updates itself in place. Tagged releases
  (`oss-vX.Y.Z`, see below) carry the installer durably on the release
  page; untagged CI runs attach the same installer to the run's
  `wintty-update-packages-<commit>` artifact (three-day retention, sign-in
  required).
- **Portable ZIP.** Download the run's `wintty-unsigned-win-x64-<commit>`
  artifact, extract the **entire** ZIP, and launch `Wintty.exe`; keep its
  DLLs, assets, licenses, and resource directories alongside it. GitHub
  requires sign-in to download Actions artifacts.

The ZIP is a one-time install and is never updater-managed: moving to
auto-updates means installing once from the Setup. Either way the app keeps
`%LOCALAPPDATA%\Wintty` state outside the install root, so reinstalling or
updating does not wipe settings.

Builds require Windows 11 x64 and a DirectX 12 feature-level-12.0 GPU.
The public renderer has no WARP software fallback. A successful hosted build
does not verify GUI operation on supported hardware. Windows may warn about
an unsigned application; verify the repository and run before choosing to run it.

Each artifact contains the source commit, CI run URL, and file SHA-256 hashes.
Hashes detect changes to the downloaded payload; they are not a publisher
signature. Third-party binaries retain their original signatures and licenses.
Symbols are included when present in the publish output.

## Feature survey

The public source has no payment or entitlement check controlling its existing
terminal features. Tabs, splits, search, quick terminal, settings, themes, and
the bundled shader gallery already work in an OSS build. No unlock patch is
needed, so no application-source changes are carried for this purpose.

Paid additions are not dormant implementations in this checkout:

- `windows/Ghostty.Core/Version/Edition.cs` contains only `Oss` and describes
  a separate release overlay adding editions. Changing a label adds no features.
- `windows/Ghostty.Core/Logging/LogEvents.cs` reserves space for Sponsor/Auth
  classes appended by tier patches; the authentication client is not here.
- `windows/Ghostty/licenses/README.md` explicitly excludes the Pro speech and
  compression dependencies supplied by release overlays.
- `windows/Ghostty/Ghostty.csproj` describes a separate Pro Legacy patch stack
  adding DX11 linking. `src/renderer/backend.zig` has no DX11 backend.
- The updater, Pro sessions/multiplexer, read-aloud, streamer/keycast, and agent
  detection implementations are not supplied by this public build. Host seams
  and configuration-key recognition are not feature implementations.

Those additions require their actual source and license terms, or separate
implementations. Enabling `Demo` or `TestSeam` does not unlock them; the latter
exposes a command-capable harness pipe and must stay out of distributed builds.

## Updates

Published releases live on [GitHub Releases](https://github.com/arrrnmp/wintty/releases)
under tags named `oss-vX.Y.Z` (stable semver, e.g. `oss-v0.1.0`), cut only
with explicit approval — never automatically, and never from upstream
`v*`/`w*` tags. Each release carries the per-user Setup installer, the full
(and, from the second release on, delta) update packages, and the
`releases.stable.json` feed the installed app polls. There is exactly one
channel, `stable`; the app ID is `arrrnmp.wintty.oss`. The installed app
runs under the AUMID `io.github.arrrnmp.wintty.oss` (set by its own startup
wiring, matching shell grouping, jump lists, and toasts), so the OSS
install never merges with other Wintty flavors in the shell.

How it behaves: an installed build checks the feed at most once a day,
downloads a newer release in the background, and shows a sticky
`Update ready` notice. Nothing applies without approval — the
`Close Wintty and update` action closes windows through the normal path
(a vetoed close rescinds the approval), and only after the UI thread has
joined and teardown is complete does the updater run, silently and without
restarting the app. Set `windows-update-check = false` to opt out and stay
on fully manual downloads. Portable builds never check: there is no
updater beside the binary.

The updater manages only files inside its own per-user install root; user
settings live outside it and survive updates. The first install from the
Setup is per-user with no elevation. Installed scripts ship with no
Mark-of-the-Web, so they load under RemoteSigned; a browser-marked Setup
download does not propagate the mark into the installed tree (the CI smoke
test proves this on every build).

Unsigned builds stay unsigned all the way down: no signing flags, no MSI,
no private overlays. Binaries may trip Defender SmartScreen warnings;
those warnings are about the absence of a signature, not a claim of
authenticity — verify the repository and the release hashes.

## PowerShell shell integration (unsigned ZIPs)

The portable build's `ghostty.ps1` shell integration is unsigned. If the
downloaded ZIP is extracted with Explorer, every extracted file carries
Mark-of-the-Web (ZoneId 3), and the tab's process-scoped `RemoteSigned`
policy refuses to load the script ("not digitally signed"). An
installer-written tree carries no such stamp and loads fine. This is the
delivery, not the script: the policy is working as documented.

After reviewing the script and deciding you trust this build, unblock
what you received, then open a new tab:

```powershell
# On the ZIP, BEFORE extracting (preferred: nothing extracted carries the stamp):
Unblock-File -LiteralPath 'C:\path\to\wintty-unsigned-win-x64-<commit>.zip'

# Or, if already extracted, on the exact script:
Unblock-File -LiteralPath 'C:\path\to\ghostty.ps1'
```

Unblock only the file you reviewed (`-LiteralPath`, no wildcards, no
directory sweeps); tabs already started keep the failed load. Details --
including how to check the stamp, why a process `RemoteSigned` overrides
a locally set `CurrentUser`/`LocalMachine` policy but not Group Policy,
and why Group-Policy-enforced `AllSigned` cannot be satisfied by
unblocking (it needs a trusted Authenticode signature these builds do
not carry; do not lower any policy to work around it) -- are in
`src/shell-integration/powershell/README.md`. The behaviour is pinned by
`windows/scripts/powershell-integration-trust-test.ps1`, which reproduces
the stamp refusal, the load after unblocking, and the `AllSigned`
refusal without changing any persisted policy.

OSS features are ungated: everything in this checkout works without payment,
and there is no Pro code to unlock (see the feature survey above). The
installer and update feed distribute the same OSS application as the
portable ZIP, not Sponsor/Pro packages or access to hosted services.

## CI and costs

The workflow runs on pushes and PRs to `windows`, on `oss-v*` tags, and by
manual dispatch. Its **build** job uses a standard `windows-2025` runner, builds a baseline-CPU
ReleaseFast native library, and publishes the Release x64 self-contained NativeAOT
app with locked NuGet dependencies. It uploads the complete portable publish tree with
three-day retention, then — with no release-write privilege — validates the
update packaging on every run: `vpk pack` with the pinned CLI, explicit
headless probes of the app's Velopack fast hooks (vpk 1.2.161 pack runs no
bootstrap check of its own), the PowerShell trust test against the published
integration script, plus a headless
A→B smoke that installs version A per-user into temp space, applies version
B with the installed updater, and checks the installed tree, the shell
integration script, and untouched outside settings without ever launching
the GUI. Non-tag builds pack rehearsal version `0.0.<run_number>`, strictly
internal and never published; tag builds pack the tag's semver. An
independent **tests** job runs all four existing C# test
suites. Test failures
remain failing CI checks, but do not block artifact creation. An available
artifact does not mean every test passed; inspect both jobs before using it.
No signing secrets, private release checkout, or upstream release dispatch
is needed.

A separate **release** job is the only one allowed to publish. It runs
solely on pushed `oss-vX.Y.Z` tags in `arrrnmp/wintty`, after the build job,
with `contents: write`; every other job keeps `contents: read`. Before the
privileged upload it fails closed: the tag must be exact stable semver, its
commit must be reachable from `origin/windows`, and every feed asset must
exist with a matching size and hash. Uploads go to GitHub Releases via
`vpk upload github` with the workflow's own `GITHUB_TOKEN` (never a personal
token on a command line). The release on GitHub is durable; the CI
artifacts that fed it keep the short three-day retention to bound storage.

The initial hosted core-test run passed 5,587 of 5,590 tests. Two pipe-ACL checks
found the Administrators SID rather than the creating user SID on the elevated
runner account; one toast-registration test expected an absent registry parent
to be available. These failures are preserved, not skipped or weakened. The
application and test source are unchanged by this build-pipeline work.

The pinned translate-c dependency is prefetched through Codeberg's Git transport
to avoid archive-generation timeouts. Its content hash must match the existing
source manifest exactly; neither the dependency revision nor its hash is changed.

Standard GitHub-hosted runner compute is free for public repositories, including
Windows. This is not an unlimited-resource guarantee: hosted jobs have runtime
and concurrency limits; larger runners are billed even for public repositories;
artifact and cache storage have separate limits and billing rules. Short
retention reduces accumulated storage but does not guarantee a zero bill for
all account usage. See [GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)
and [Actions limits](https://docs.github.com/en/actions/reference/limits).

The MIT copyright/permission notice and bundled dependency notices travel with
the application. This workflow distributes the OSS application, not official
signed Sponsor/Pro packages or access to their hosted services.
