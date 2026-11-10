# Tooling & CI

## Why Just

This project uses [Just](https://github.com/casey/just) as a command runner for the Windows
development inner loop.

Just is like Make but simpler and more natural on Windows. No tabs-vs-spaces footguns,
no implicit rules, just named recipes that run commands. It's common in the Zig and Rust
ecosystems and installs with `winget install Casey.Just`.

The `justfile` at the repo root has recipes for testing, building the DLL, and syncing
with upstream. Run `just --list` to see them all.

## How CI works

This fork uses the [unsigned-windows workflow](../../.github/workflows/unsigned-windows.yml)
for public Windows builds and the four C# test suites. It runs on pushes and PRs
to `windows`, and by manual dispatch. Download and build details are in
[Unsigned builds](fork-builds.md). Inherited upstream workflows are not this
fork's release pipeline.

This is a solo passion project developed on a cross-platform homelab - Windows, macOS,
and Linux machines are all available. The primary development loop happens on Windows
since that's where the port runs.

**Inner loop (every change):**

```bash
just test-lib-vt   # ~30s, catches most regressions
just build-dll     # verify the DLL builds
```

**Before merging a PR:**

```bash
just test           # full Zig test suite on Windows
```

**Cross-platform testing (on demand):**

Tests run natively on all three platforms via SSH. This happens before merging
anything that touches shared code (Zig, build system, renderer abstractions).
It's skipped for Windows-only changes like C# code.

Upstream keeps Linux and macOS CI healthy. Since the fork rebases daily, any
cross-platform regression from upstream gets caught during the next sync.

## GitHub Actions costs

Standard GitHub-hosted runner compute is free for public repositories, including
Windows; private-repository included-compute budgets do not apply here. Larger
runners are billed, and runtime, concurrency, artifact-storage, and cache limits
still apply. See [GitHub's billing documentation](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

Local Windows hardware is still needed for GPU/desktop harness verification;
the hosted build is not a GUI smoke test.
