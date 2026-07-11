# rust-original/

This is the original Rust `rtk` codebase this fork started from
([rtk-ai/rtk](https://github.com/rtk-ai/rtk)), kept here for two reasons:

1. **Parity oracle.** `RtkSharp.ParityTests` builds this crate and diffs its
   output against the RtkSharp (.NET) port, command-by-command, to verify the
   port behaves identically to the original implementation.
2. **Reference for tracking upstream.** The `upstream-main` branch tracks
   `upstream/master` directly (`git fetch upstream && git log
   upstream-main..upstream/master`), so changes made to the original project
   can be compared against this snapshot to spot features/fixes worth
   porting.

## This fork does not release from this code

Releases for the Rust binary are the upstream project's responsibility
([rtk-ai/rtk](https://github.com/rtk-ai/rtk)). The Rust-specific CI/CD
pipelines that used to live in `.github/workflows/` (`cd.yml`, `ci.yml`,
`release.yml`, `next-release.yml`, `pr-target-check.yml`) were removed when
this code moved here — see `CICD.md` in this folder for what they used to do.
Active development and NuGet publishing now happens for the .NET port
(`RtkSharp`/`RtkSharp.Filters`) via `.github/workflows/nuget-publish.yml`.

## Building the oracle binary

```powershell
cd rust-original
cargo build --release
```

Must `cd` into this folder first — Cargo resolves `.cargo/config.toml` from
the current working directory, not from `--manifest-path`, so running
`cargo build --release --manifest-path rust-original/Cargo.toml` from the
repo root does **not** pick up the redirect below and puts output in the
wrong place. This folder's `.cargo/config.toml` redirects Cargo's output
back to `<repo-root>/target/release/rtk.exe` (not `rust-original/target/...`),
since `RtkSharp.ParityTests` hardcodes that repo-root-relative path as the
oracle binary location.

## Path references from RtkSharp.ParityTests

A handful of parity tests use real files as literal test inputs (e.g. `rtk
ls`/`read`/`find`/`grep` against real source files). When this code moved
here, those references were updated to `rust-original/src/...` — see
`SystemParityTests.cs`'s `Battery` array. `RtkSharp.slnx` (not this folder's
`Cargo.toml`) is now the repo-root marker every `FindRepoRoot()` helper
across the parity test suite walks up to find.
