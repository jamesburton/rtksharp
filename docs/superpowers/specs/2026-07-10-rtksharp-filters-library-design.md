# RtkSharp.Filters: in-process filter library for CodeSharp

**Date:** 2026-07-10
**Status:** Approved design, pending implementation plan
**Backlog source:** `docs/PLANS.md` §11a — "Expose RTK as a library for direct usage within a coding agent"

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

Extract RtkSharp's existing filter dispatch into a plain .NET class library
(`RtkSharp.Filters`), packaged as its own NuGet artifact, so CodeSharp can
reference it directly and call filtering in-process — no subprocess
round-trip, no separate `rtk` install.

## Non-goals

- **Whole-conversation-history compaction.** Out of scope entirely; this is
  per-tool-call output filtering only, matching CodeSharp's existing
  per-call `ShellTool` granularity.
- **Exposing the rewrite/permission engine.** `RewriteEngine`/`PermissionRules`
  govern whether a command is *safe to auto-run* under Claude Code's hook
  model — irrelevant to CodeSharp, which has its own permission gateway
  (`ToolRiskClass.Destructive` + its own sandboxing). Not part of this
  library's public surface.
- **RtkSharp taking over process execution.** CodeSharp keeps its own
  `ShellTool` execution model (Docker sandbox, timeout, process-tree kill).
  The library never spawns a process itself except through a caller-supplied
  `IProcessExecutor`.
- **AST-level (`--level ast`) semantic filtering.** Real feature, but a
  separate, larger surface (Roslyn + 9 TreeSitter grammars); not needed to
  solve the ShellTool-output problem and deferred to a future extension of
  this same library if wanted.
- **Analytics/tracking side effects.** `Tracker`/token-savings recording
  lives in `RtkSharp/Execution/CommandRunner.cs`, one layer above
  `CommandRegistry` dispatch — not inside the filter handlers themselves.
  The library exposes the handlers directly, so calling it never writes to
  the SQLite tracking DB. (CodeSharp can add its own metrics later if it
  wants; not this design's concern.)
- **Actually wiring `RtkSharp.Filters` into CodeSharp's `ShellTool`.** That's
  a CodeSharp-repo change (implementing `IProcessExecutor`, calling
  `TryParseSingleCommand` + `Run`) and gets its own design/plan over there.
  This design's deliverable ends at "the package exists, is correct, and is
  documented for a consumer to use."

## Why this doesn't conflict with the `dnx` zero-install CLI thesis

The CLI tool package (`RtkSharp`, `PackAsTool=true`) and the new library
package (`RtkSharp.Filters`, plain class library) are two separate NuGet
artifacts built from two separate projects. `RtkSharp.csproj` references
`RtkSharp.Filters.csproj` and re-exports its `CommandRegistry` dispatch for
CLI use, same as today. Nothing about `dnx -y RtkSharp -- <command>` changes.
The library package additionally gives a second, install-free (for .NET
consumers) distribution path — reinforcing the "no separate install"
philosophy rather than conflicting with it.

## Architecture

### New project: `RtkSharp.Filters`

A new class library (`net10.0`, `PackAsTool` absent) that most of the
existing `RtkSharp/Commands/**`, `RtkSharp/Execution/**` (`IProcessExecutor`,
`ExecutionRequest`/`ExecutionResult`, `ProcessExecutor`),
`RtkSharp/Rewrite/ShellLexer.cs`, and the parts of `RtkSharp/Core/**` those
depend on (config/filter-loading, **not** `Tracking/**`) move into.
`RtkSharp/RtkSharp.csproj` (the CLI tool) becomes a thin `Program.cs` +
`Cli/CommandRegistry.cs` + `Execution/CommandRunner.cs` (tracking wrapper)
+ `Hooks/**` + `Analytics/**` shell that references `RtkSharp.Filters` as a
`ProjectReference`.

This is a **move + re-reference** operation, not a rewrite: the ~30 command
modules keep their existing `RunAsync(string[] args, IProcessExecutor
executor, TextWriter stdout, TextWriter stderr)` shape unchanged. `git`,
`gh`, `dotnet`, `npm`/`pnpm`/`npx`, `tsc`, `vitest`/`jest`, `playwright`,
`prisma`, `cargo`, `go`, `python`/`ruff`/`pytest`/`mypy`/`pip`,
`rspec`/`rubocop`/`rake`, `ls`/`read`/`wc`/`tree`/`find`/`grep`, `docker`,
`az`, and the rest of the currently-registered filters all move as-is.

### Public API surface

```csharp
namespace RtkSharp.Filters;

public static class RtkFilters
{
    /// <summary>True if a filter is registered for this command name.</summary>
    public static bool IsRegistered(string command);

    /// <summary>
    /// Runs a registered filter for <paramref name="command"/>, using
    /// <paramref name="executor"/> to actually spawn the process. Throws
    /// if no filter is registered for <paramref name="command"/> —
    /// callers should check <see cref="IsRegistered"/> or catch and fall
    /// back to raw output themselves.
    /// </summary>
    public static Task<int> Run(
        string command, string[] args,
        IProcessExecutor executor, TextWriter stdout, TextWriter stderr);

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

`IProcessExecutor`, `ExecutionRequest`, `ExecutionResult` move into
`RtkSharp.Filters` unchanged (namespace `RtkSharp.Filters.Execution` or kept
as `RtkSharp.Execution` — decide at implementation time based on how much
`using` churn each costs across the moved modules; not a design-level
decision).

### Consumer usage sketch (CodeSharp side, illustrative only — not built here)

```csharp
// CodeSharp.Tools/ShellTool.cs
if (RtkFilters.TryParseSingleCommand(command, out var name, out var argv)
    && RtkFilters.IsRegistered(name))
{
    var executor = new CodeSharpProcessExecutor(_scope, _config); // wraps existing sandboxed run
    var sw = new StringWriter();
    var exitCode = await RtkFilters.Run(name, argv, executor, sw, sw);
    return new ShellResult(exitCode, sw.ToString(), "", false);
}
// else: existing raw ProcessStartInfo path, unfiltered (compound command or unregistered filter)
```

### Packaging

- New `RtkSharp.Filters/RtkSharp.Filters.csproj`, added to `RtkSharp.slnx`.
- `PackageId=RtkSharp.Filters`, versioned independently from `RtkSharp`
  (starts at `1.0.0`, bumped whenever the filter surface changes) but built
  from the same repo/release pipeline.
- `dotnet pack RtkSharp.Filters/RtkSharp.Filters.csproj -o .artifacts/packages`
  produces the local-feed `.nupkg`, same pattern already proven for
  `RtkSharp` itself (`docs/PLANS.md` §10).
- Published to whatever feed CodeSharp already resolves packages from
  (local feed for now, matching `RtkSharp`'s current unpublished state; a
  real feed is a later, separate concern shared with `RtkSharp`'s own
  release story).

### Testing

- `RtkSharp.Tests` splits: tests for moved modules (command filter tests)
  move to a new `RtkSharp.Filters.Tests` project alongside the library
  (also added to `RtkSharp.slnx`); tests for what stays in `RtkSharp`
  (hooks, init, tracking, CLI parsing) stay in `RtkSharp.Tests`, which
  references `RtkSharp.Filters` transitively through its existing
  `RtkSharp` reference.
- `RtkSharp.ParityTests` is unaffected — it already tests through the
  `RtkSharp` CLI tool's public dispatch, which still works identically
  post-move (`RtkSharp.csproj` → `RtkSharp.Filters.csproj` is an internal
  restructuring, not a behavior change).
- New tests specific to the library boundary: `TryParseSingleCommand`
  (single command, compound with `&&`/`|`/`;`, redirects, quoting edge
  cases already covered by existing `ShellLexer` tests), `IsRegistered`
  (hit/miss), and one integration-style test per handful of command
  families confirming `RtkFilters.Run` produces the same output as calling
  the moved handler directly (regression guard against the move itself).

## Error handling

- `RtkFilters.Run` for an unregistered command: throws
  `InvalidOperationException` (fail fast — callers are expected to check
  `IsRegistered` first, matching the `TryParseSingleCommand` sketch above).
  No silent fallback inside the library; the caller decides what "no filter"
  means for it (CodeSharp: use raw output).
- `TryParseSingleCommand` never throws — malformed/unparseable input returns
  `false`, same fail-open-to-raw-output philosophy as the CLI's own
  fallback pattern (`.claude/rules/rust-patterns.md`'s "fallback pattern",
  ported as a design principle even though this is a library, not the CLI).
- Executor exceptions (e.g. `IProcessExecutor` implementation throws)
  propagate to the caller unchanged — the library does not swallow them,
  since only the caller (CodeSharp) knows whether that should surface as a
  tool error or a fallback.

## Open item deferred to implementation

Exact namespace placement for `IProcessExecutor`/`ExecutionRequest`/
`ExecutionResult` (keep `RtkSharp.Execution` vs. rename to
`RtkSharp.Filters.Execution`) is left to whoever writes the implementation
plan — it's a mechanical choice with no architectural consequence, and
deciding it now would just be guessing at file-move ergonomics rather than
anything that changes behavior.
