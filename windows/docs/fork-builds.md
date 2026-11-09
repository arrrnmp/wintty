# Unsigned builds of arrrnmp/wintty

This fork builds the public OSS source without changing application features
or pretending to be a paid edition. It is independent of the official Wintty
distribution and is not signed by its publisher.

## Download and run

Open this fork's [unsigned-windows workflow](https://github.com/arrrnmp/wintty/actions/workflows/unsigned-windows.yml),
choose a successful run, and download its `wintty-unsigned-win-x64-<commit>`
artifact. GitHub requires sign-in to download Actions artifacts. Extract the
**entire** ZIP and launch `Wintty.exe`; keep its DLLs, assets, licenses, and
resource directories alongside it. There is no installer or automatic updater.

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

## CI and costs

The workflow runs on pushes and PRs to `windows`, and by manual dispatch. It uses
one standard `windows-2025` runner, builds a baseline-CPU ReleaseFast native
library, runs the four existing C# test suites, and publishes the Release x64
self-contained NativeAOT app with locked NuGet dependencies. It uploads the
complete publish tree with three-day retention. No signing secrets, private
release checkout, or upstream release dispatch is needed.

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
