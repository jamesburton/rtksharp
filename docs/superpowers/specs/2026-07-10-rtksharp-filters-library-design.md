# RtkSharp.Filters: in-process filter library for CodeSharp

**Date:** 2026-07-10
**Status:** Approved design, revised twice same day after inventory research
surfaced increasingly deep contradictions with the codebase as it actually
exists (see "Revision 1" and "Revision 2" below); pending implementation plan.
**Backlog source:** `docs/PLANS.md` §11a — "Expose RTK as a library for direct usage within a coding agent"

## Revision 2 2026-07-10 — boundary changed to pure filter functions

A read-verified, per-module survey (not grep-based guessing, after three
earlier grep-based guesses each turned out wrong in different ways) found
that **zero** of RtkSharp's CLI-registered command entry points
(`CommandRegistry.cs`'s exact wired expressions) support external
`IProcessExecutor`/`TextWriter` injection. Most modules spawn their own
`new ProcessExecutor()` internally and write straight to `Console.Out`/
`Console.Error`, with no reachable seam for a caller to substitute its own
sandboxed executor or capture output into a string. The ~19 modules that
route through `RtkSharp/Execution/CommandRunner.cs` are equally
non-injectable — `CommandRunner.RunFilteredWithExitAsync` hardcodes
`new ProcessExecutor()` and writes to `Console` directly (verified by
reading the method body, not inferred).

Building `RtkFilters.Run(command, args, executor, stdout, stderr)` on top of
this, as Revision 1 assumed, would require threading executor/writer
injection through all ~70 modules' actual public entry points *and*
`CommandRunner` — a refactor comparable in size to (arguably larger than)
the "true pure filter functions" option this design rejected in its first
draft as too costly.

**Decision: the library boundary reverts to pure filter functions.**
CodeSharp keeps running commands itself (unchanged `ShellTool` execution —
its own sandboxing, timeout, process-tree kill, all untouched) and calls a
pure `Filter(command, args, rawStdout, rawStderr, exitCode) -> string` on
the output it already captured. A second read-verified survey found this is
now the *more* tractable option: of ~65 filter modules, ~46 already expose a
standalone pure string-in/string-out filter method (often in a matching
`*Filters.cs` sibling file), ~13 need a shallow extraction (pulling a filter
call out of a method that also does exec/Console work), 1
(`ErrCommand.cs`) has no clean separation at all (filtering happens inline
during live line-by-line streaming) and is excluded from v1, and ~15 are
pure dispatchers with no filter logic of their own (their sub-modules are
covered separately).

**Consequences:**
- `RtkSharp/Execution/**` (`IProcessExecutor`, `ProcessExecutor`,
  `CommandRunner`, etc.) **does not move** — the library never touches
  process execution. Revision 1's Task 3 (moving `Execution/**`) is
  cancelled.
- The "RtkSharp taking over process execution" non-goal is now **trivially
  true** rather than a caveat requiring an injection seam — there's no
  process execution in the library at all.
- The `IProcessExecutor executor` parameter is dropped from the public API.
  `RtkFilters.Run` becomes `RtkFilters.Filter` and takes already-captured
  text, not a way to run anything.
- `TryParseSingleCommand` is unchanged in purpose (CodeSharp still needs the
  command name to know which filter to call) but its result is now used
  purely for filter dispatch, not also for constructing an executor call.
- `Core/Tracking/**` still moves (Revision 1's point 1 stands — many pure
  filter call sites are adjacent to `TimedExecution` calls in the same
  file, and separating those cleanly is out of scope for this pass; the
  moved *pure filter methods* themselves don't call `Tracking`, only their
  non-moving `RunAsync` callers do — confirm per-file during extraction and
  leave `Tracking` behind if a given file's filter method turns out clean).
- `Ast/**`/`Parser/**` still move (Revision 1's point 2 stands — `ReadCommand`'s
  pure `Render`/etc. methods call into `RtkSharp.Ast` directly).
- The TOML namespace collision fix (Revision 1's point 3) still applies.
- The `Commands/**` allowlist (Revision 1's point 4) still applies.

## Revision 1 2026-07-10 (superseded in part by Revision 2 above — kept for history)

A pre-plan inventory pass found the original non-goals below don't match the
current codebase. Corrections, with rationale:

1. **Tracking is NOT isolated from filter handlers.** 31 of the ~70 filter
   modules call `TimedExecution.Start()` directly (e.g. `TestCommand.cs:99`),
   which constructs a `Tracker` and writes to the SQLite tracking DB with no
   intervening abstraction. **Decision (still stands under Revision 2):**
   `RtkSharp.Core.Tracking/**` moves into `RtkSharp.Filters`.
2. **AST/Parser dependencies are already load-bearing in core filters.**
   `ReadCommand.cs` uses `RtkSharp.Ast` directly; `PipeCommand.cs`,
   `PnpmCommand.cs`, `PlaywrightCommand.cs`, `VitestCommand.cs` use
   `RtkSharp.Parser`. **Decision (still stands):** `RtkSharp/Ast/**` and
   `RtkSharp/Parser/**` move into `RtkSharp.Filters` wholesale.
3. **Namespace collision.** `RtkSharp/Filters/TomlFilterEngine.cs`/
   `TomlFilterRegistry.cs` already use `namespace RtkSharp.Filters;`.
   **Decision (still stands):** rename to `RtkSharp.Filters.Toml`.
4. **`Commands/**` is not a clean "filter module" boundary.**
   `Commands/Analytics/**` and `Commands/System/ConfigCommand.cs`/
   `SmartCommand.cs` are CLI-only despite the shared folder/namespace
   convention. **Decision (still stands):** explicit per-file allowlist.

(Revision 1's assumption that `IProcessExecutor`/`Execution/**` should move
and that `RtkFilters.Run` should take an injectable executor is
**superseded** by Revision 2 above.)

## Problem

RtkSharp's command filters (git, gh, dotnet, npm/pnpm, python, go, ruby, system,
az, etc.) only exist behind the `rtk` CLI process boundary. CodeSharp — the
user's own C# agent CLI, living in a separate repo (`c:/Development/codesharp`)
— has its own `ShellTool` that executes arbitrary shell commands and returns
raw, unfiltered stdout/stderr as tool-call output to the model. That raw
output is exactly the kind of noisy, token-heavy text RtkSharp already knows
how to compress, but today the only way to get it would be for CodeSharp to
shell out to a separately-installed `rtk` binary — a packaging/installation
dependency the user wants to avoid.

## Goal

Extract RtkSharp's existing pure filter logic into a plain .NET class library
(`RtkSharp.Filters`), packaged as its own NuGet artifact, so CodeSharp can
call filtering in-process on output it already captured — no subprocess
round-trip, no separate `rtk` install.

## Non-goals

- **Whole-conversation-history compaction.** Out of scope entirely; this is
  per-tool-call output filtering only, matching CodeSharp's existing
  per-call `ShellTool` granularity.
- **Exposing the rewrite/permission engine.** `RewriteEngine`/`PermissionRules`
  stay CLI-only; irrelevant to a pure filter library.
- **RtkSharp taking over process execution.** The library contains no
  process-execution code at all (Revision 2) — CodeSharp's `ShellTool` runs
  commands exactly as it does today, unchanged.
- **`ErrCommand`'s streaming filter.** `RtkSharp.Core.ErrorStreamFilter` is
  applied line-by-line during live streaming exec, with no standalone
  raw-text-in/filtered-text-out method — excluded from v1's `RtkFilters`
  registrations; revisit if a consumer needs `err`'s specific behavior.
- **Actually wiring `RtkSharp.Filters` into CodeSharp's `ShellTool`.** That's
  a CodeSharp-repo change and gets its own design/plan over there. This
  design's deliverable ends at "the package exists, is correct, and is
  documented for a consumer to use."

## Why this doesn't conflict with the `dnx` zero-install CLI thesis

The CLI tool package (`RtkSharp`, `PackAsTool=true`) and the new library
package (`RtkSharp.Filters`, plain class library) are two separate NuGet
artifacts built from two separate projects. `RtkSharp.csproj` references
`RtkSharp.Filters.csproj` for the pure-filter logic its own `Commands/**`
modules call, same relationship as today just moved to a project boundary.
Nothing about `dnx -y RtkSharp -- <command>` changes.

## Architecture

### New project: `RtkSharp.Filters`

A new class library (`net10.0`, `PackAsTool` absent) containing:
- Every filter module's **pure filter method(s)** — not the whole
  `*Command.cs` file, only the string-in/string-out (or
  string+exitcode-in/string-out) logic, per the per-file allowlist/verdict
  table the implementation plan carries.
- `RtkSharp/Ast/**`, `RtkSharp/Parser/**` (load-bearing for `ReadCommand`'s
  pure rendering path and a few `Js/` filters).
- `RtkSharp/Core/Tracking/**` (Revision 1, point 1).
- The parts of `RtkSharp/Core/**` the moved pure methods actually call
  (string/text utilities — `Utils.cs`, `TruncationCaps.cs`,
  `JsonCompaction.cs`, etc. — determined per-file during extraction, not a
  wholesale `Core/**` move like Revision 1 assumed, since pure filter
  methods don't need `Config.cs`/`Tee.cs`/execution-adjacent helpers the way
  the full `RunAsync` methods did).
- `RtkSharp/Filters/TomlFilterEngine.cs`/`TomlFilterRegistry.cs`, renamed to
  `namespace RtkSharp.Filters.Toml` (Revision 1, point 3).

`RtkSharp/Execution/**` (including `CommandRunner.cs`), `RtkSharp/Commands/**`'s
`RunAsync`/exec/dispatch methods, and `RtkSharp/Rewrite/**` (rewrite/permission
engine) all **stay in `RtkSharp`** — none of them are pure, none are needed by
a filter-only library.

### Public API surface

```csharp
namespace RtkSharp.Filters;

public static class RtkFilters
{
    /// <summary>True if a filter is registered for this command name.</summary>
    public static bool IsRegistered(string command);

    /// <summary>
    /// Filters already-captured output for <paramref name="command"/>.
    /// Throws if no filter is registered — callers should check
    /// <see cref="IsRegistered"/> first, or catch and fall back to raw
    /// output themselves.
    /// </summary>
    public static string Filter(
        string command, string[] args,
        string rawStdout, string rawStderr, int exitCode);

    /// <summary>
    /// Tokenizes a shell command line (via the existing <see cref="ShellLexer"/>)
    /// and splits it into a command name + argv, but only if it is a single,
    /// non-compound command — no pipes, <c>&amp;&amp;</c>/<c>||</c>/<c>;</c>
    /// operators, or redirects. Returns false for anything compound, since
    /// there is no single filter target for a pipeline.
    /// </summary>
    public static bool TryParseSingleCommand(
        string commandLine, out string command, out string[] args);
}
```

### Consumer usage sketch (CodeSharp side, illustrative only — not built here)

```csharp
// CodeSharp.Tools/ShellTool.cs — execution is completely unchanged
var raw = await RunProcessAsSandboxedToday(...); // existing code, untouched

if (RtkFilters.TryParseSingleCommand(command, out var name, out var argv)
    && RtkFilters.IsRegistered(name))
{
    var filtered = RtkFilters.Filter(name, argv, raw.Stdout, raw.Stderr, raw.ExitCode);
    return new ShellResult(raw.ExitCode, filtered, "", false);
}
// else: raw.Stdout/Stderr used unfiltered (compound command or unregistered filter)
```

### Packaging

- New `RtkSharp.Filters/RtkSharp.Filters.csproj`, added to `RtkSharp.slnx`.
- `PackageId=RtkSharp.Filters`, versioned independently from `RtkSharp`
  (starts at `1.0.0`).
- `dotnet pack RtkSharp.Filters/RtkSharp.Filters.csproj -o .artifacts/packages`
  produces the local-feed `.nupkg`, same pattern already proven for
  `RtkSharp` itself (`docs/PLANS.md` §10).
- Published to whatever feed CodeSharp already resolves packages from
  (local feed for now).

### Testing

- New tests specific to each extracted filter method (regression: old
  behavior via the CLI vs. new `RtkFilters.Filter` call, same input →
  same output).
- `TryParseSingleCommand`/`IsRegistered` unit tests (unchanged from
  Revision 1's plan).
- `RtkSharp.Tests` and `RtkSharp.ParityTests` are far less disrupted than
  under Revision 1: since `RunAsync`/`Execution/**`/`CommandRunner` don't
  move, most existing CLI-level tests stay exactly where they are; only
  tests that specifically exercise a filter method in isolation move to
  `RtkSharp.Filters.Tests`.

## Error handling

- `RtkFilters.Filter` for an unregistered command: throws
  `InvalidOperationException`. No silent fallback — the caller decides.
- `TryParseSingleCommand` never throws — malformed/unparseable input returns
  `false`.
