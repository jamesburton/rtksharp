# Design: Single-File `.cs` Execution Support (RtkSharp)

**Date:** 2026-07-08
**Status:** Approved (spec review pending)
**Phase:** `docs/PLANS.md` Phase 6 (Best-in-Class C# and .NET Support), item 7 of §12

## Context

.NET 10 introduced *file-based apps*: `dotnet run app.cs` (and the shorthand
`dotnet app.cs`) compile and run a single C# file with no `.csproj`/project required.
This has **no Rust equivalent** — the Rust oracle (`src/cmds/dotnet/dotnet_cmd.rs`)
never touches this invocation shape, so it currently falls through RtkSharp's existing
passthrough path unfiltered. This is a genuine superset feature, not a parity item:
there is no oracle to byte-match, and RtkSharp is free to define its own filtering
contract.

`RtkSharp/Commands/Dotnet/DotnetCommand.cs` currently dispatches only `build`,
`restore`, `test`, and `format` to dedicated filters (`RunAsync`'s switch,
`DotnetCommand.cs:202-209`); everything else — including `run` — is raw passthrough
via `RunPassthroughAsync`.

## Empirical findings (SDK `10.0.301`, verified locally this session)

| Case | Output shape | Exit code |
|---|---|---|
| Success (`dotnet run ok.cs`) | **Only** the program's own stdout — no build noise at all | 0 |
| Genuine syntax error (`Console.WriteLine("hi")` missing `;`) | `<file>(<line>,<col>): error CS1002: ; expected` then `The build failed. Fix the build errors and run again.` — **identical shape** to `dotnet build`'s per-line diagnostics, already matched by `DotnetCommand.IssueRegex` | 1 |
| Project-config diagnostic (no source location) | `CSC : error EnableGenerateDocumentationFile: Set MSBuild property 'GenerateDocumentationFile' to 'true' in project file to enable IDE0005 ...` then `The build failed. Fix the build errors and run again.` — a rarer, no-location diagnostic shape `IssueRegex` cannot match (no `(line,col)`) | 1 |
| Runtime exception (unhandled) | Partial program stdout, then `Unhandled exception. System.InvalidOperationException: boom` and a real stack trace (`   at Program.<Main>$(String[] args) in <file>:line 2`) | observed 127 (see Open Risk below) |
| `dotnet app.cs` shorthand (no `run` word) | Behaves identically to `dotnet run app.cs` in all cases above | same as above |

**Key implication:** success needs zero filtering (already optimal as passthrough).
The only value-add is compacting compile-failure diagnostics and unhandled-exception
stack traces — and the common compile-failure case is *already* the exact format
`IssueRegex`/`FilterBuild` handle for `dotnet build`, so most of this is reuse, not
new parsing.

## Detection & Dispatch

Add two new match arms to `DotnetCommand.RunAsync`'s switch
(`DotnetCommand.cs:202-209`), before the passthrough default:

- `subcommand == "run"` **and** `rest`'s first non-option argument ends with `.cs`
  (case-insensitive) → `RunFileBasedAppAsync`.
- `subcommand` itself ends with `.cs` (case-insensitive) → `RunFileBasedAppAsync`
  (the `dotnet <file>.cs` shorthand — the "subcommand" slot holds the filename here).

All other `run` invocations (ordinary project-based `dotnet run`) are unaffected —
they keep falling through to the existing `RunPassthroughAsync`, unchanged.

## Success Path

Execute via `executor.ExecuteAsync` with `CaptureMode.Separate` (same pattern as
every other filter in this file). On exit code `0`: write `result.Stdout`/
`result.Stderr` through completely unmodified — no filtering, no verdict line, no
added output of any kind. This matches the empirical finding that a working
file-based app already prints nothing but its own program output.

## Failure Path — Three-Tier Detection

On non-zero exit, build `raw = result.Stdout + "\n" + result.Stderr` (same convention
as `RunTextFilteredAsync`), then try, in order, the cheapest/most-common match first:

1. **`IssueRegex` matches** (genuine compile error/warning with a source location) →
   reuse `FilterBuild`'s existing summary-rendering logic unmodified (same compact
   `Errors:`/`Warnings:` block, same `CapBuildErrors`/`CapBuildWarnings` caps). This is
   the common case and requires no new parsing.
2. **No `IssueRegex` match, but a new `FileBasedAppDiagnosticRegex` matches** — a
   no-location diagnostic line of the form
   `^\s*CSC\s*:\s*error\s+(?<name>\S+):\s*(?<msg>.*)$` (covers the
   `EnableGenerateDocumentationFile`-style project-config error). Render one compact
   `error <name>: <msg>` line per match, capped the same way as build errors
   (`CapBuildErrors`).
3. **`Unhandled exception.` found in stdout** — extract the exception type + message
   from the line immediately following the marker, count stack-trace frames (lines
   starting with `   at `), and render a compact summary:
   `exception: <Type>: <message> (N frames, first: <frame>)`. Any program output
   printed *before* the exception (e.g. partial `Console.WriteLine` calls) is preserved
   verbatim above this summary line — it is user content, not proxy noise, and must
   never be hidden or altered.
4. **None of the above match** (unrecognized failure shape) — fall back to raw,
   unfiltered passthrough of `stdout`/`stderr`, per this codebase's mandatory fallback
   contract (a filter must never hide output it doesn't recognize).

## Error Handling / Fallback Contract

Wrap the filtering attempt in the same try/catch pattern already used by
`RunTextFilteredAsync`/`RunFormatAsync` in this file: if the new filter function
throws for any reason, catch it, print `rtk: filter warning: {ex.Message}` to stderr,
dump `result.Stdout`/`result.Stderr` raw, and return the real exit code. Never crash,
never swallow output — this is the same fallback rule already enforced project-wide
(`.claude/rules/rust-patterns.md`'s "fallback pattern").

## Open Risk (flag for implementation, not blocking design approval)

The observed exit code `127` for an unhandled exception is unusual (.NET's typical
unhandled-exception exit code is a large HRESULT-style value, not the conventional
"command not found" 127) and was only observed once, through a Git Bash `timeout`
wrapper that could itself be a confound. **Detection must key off stderr/stdout
content markers (`Unhandled exception.`), never off a specific exit-code value**,
so this doesn't matter for correctness — but the exact exit code should be
re-verified empirically during implementation and noted in the code comment/test,
since RtkSharp still needs to propagate whatever the real exit code is.

## Testing Plan

- New unit tests in `RtkSharp.Tests/Commands/Dotnet/DotnetCommandTests.cs` (existing
  file, fixture-based pattern already used for `FilterBuild`), covering:
  - Success/passthrough (no filtering applied).
  - Genuine syntax error via `IssueRegex` reuse (tier 1).
  - No-location `CSC :` project-config diagnostic (tier 2).
  - Unhandled-exception stack trace compaction (tier 3), including the case where
    partial program stdout precedes the exception.
  - Unrecognized-failure-shape fallback (tier 4) — asserts raw passthrough, no crash.
- Dispatch-level tests confirming both `dotnet run <file>.cs` and `dotnet <file>.cs`
  route to the new handler, and that ordinary project-based `dotnet run` (no `.cs`
  argument) is untouched.
- Fixtures are captured from real local `dotnet run`/`dotnet <file>.cs` invocations
  (the cases already captured this session), not derived from Rust source — there is
  no oracle for this feature.
- **No parity-test entry** — excluded from `RtkSharp.ParityTests` by design, same
  treatment as the existing `--level ast` superset feature (no Rust oracle exists to
  compare against).

## Non-Goals (this design)

- Expanded `dotnet` subcommands (`publish`/`pack`/`clean`/`run` for *project*-based
  apps/`tool`/`new`/`workload`/`sln`/`ef`) — separate, larger Phase 6 item, not in
  scope here.
- `dotnet publish file.cs` / `dotnet pack file.cs` filtering — same family as this
  feature but a distinct invocation shape with its own output; deliberately deferred
  to a follow-up design once this lands and its patterns are proven.
