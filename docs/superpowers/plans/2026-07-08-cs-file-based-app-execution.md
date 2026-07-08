# Single-File `.cs` Execution Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect `dotnet run <file>.cs` / `dotnet <file>.cs` file-based-app invocations in RtkSharp's `DotnetCommand` and, on failure, render a compact diagnostic/exception summary instead of raw passthrough — a genuine superset feature with no Rust oracle.

**Architecture:** Two new dispatch arms in `DotnetCommand.RunAsync`'s switch route file-based-app invocations to a new `RunFileBasedAppAsync`. On success, output passes through completely unmodified. On failure, a new `FilterFileBasedApp` tries three detection tiers in order — reusing the existing `ParseBuildFromText`/`ParseRestoreIssuesFromText` diagnostic parsers for the first two tiers (near-zero new regex surface), then a new unhandled-exception detector for the third — and throws to trigger the same try/catch-based raw-fallback contract every other filter in this file already uses for anything unrecognized.

**Tech Stack:** C# / .NET 10, xUnit, existing `RtkSharp.Commands.Dotnet.DotnetCommand` filter conventions.

## Global Constraints

- Source of truth for behavior: `docs/superpowers/specs/2026-07-08-cs-file-based-app-execution-design.md` (approved). This is a superset feature — there is no Rust oracle, no parity-test entry, and no byte-exact target to match.
- Success path: zero filtering, zero added output — write `result.Stdout`/`result.Stderr` unmodified.
- Failure path never crashes and never hides output: any exception from the new filter function must be caught, printed as `rtk: filter warning: {ex.Message}`, followed by raw `stdout`/`stderr` dump and the real exit code — same fallback contract as `RunTextFilteredAsync`/`RunFormatAsync` in the same file.
- Maximize reuse of already-tested code: `ParseBuildFromText`, `ParseRestoreIssuesFromText`, `FormatIssueSection`, `JoinNonEmpty`, `BinlogIssue`, `CapBuildErrors`, `CapBuildWarnings` (all already `internal`/`private static` in `DotnetCommand` — same class, directly callable, no visibility changes needed).
- Do **not** call `FilterBuild`/`FormatBuildOutput` directly — their verdict line hardcodes `"{ok|fail} dotnet build: {N} projects, ..."`, which is factually wrong framing for a single-file run (see plan discovery notes below). Build a new, small verdict renderer instead, reusing only the lower-level pieces (`ParseBuildFromText`, `FormatIssueSection`).
- All new code lives in the existing `RtkSharp/Commands/Dotnet/DotnetCommand.cs` file (no new file), per this codebase's existing pattern of one file per ecosystem-command surface for `dotnet`.
- All new tests live in the existing `RtkSharp.Tests/Commands/DotnetCommandTests.cs` file, following its established fixture-constant + `[Fact]` pattern.
- Every fixture string used below was captured from **real local `dotnet run`/`dotnet <file>.cs` invocations** (SDK `10.0.301`) during this session's design work — not invented.

## Discovery Notes (read before implementing — corrects two details vs. the written design doc)

1. **Tier 1 must not call `FilterBuild` directly.** `FilterBuild` → `FormatBuildOutput` renders a verdict line hardcoded as `"{ok|fail} dotnet build: {N} projects, {E} errors, {W} warnings ({duration})"`. For a single `.cs` file there is no "project" and the command being run is `dotnet run`, not `dotnet build` — printing that verdict would actively mislabel the failure. Instead, call `DotnetCommand.ParseBuildFromText(raw)` directly (already `internal`) to get `BuildSummary.Errors`/`.Warnings` (this *is* the `IssueRegex`-driven parsing the design refers to), then render with a new small `FormatFileBasedAppIssues` helper using the existing `FormatIssueSection` primitive and a new file-based-app-specific verdict line.
2. **Tier 2 needs two branches, not one, and one of them requires zero new regex.** Empirical testing surfaced two distinct "no source location" diagnostic shapes:
   - `<file>.csproj : error NU1507: <message>` (an MSBuild/NuGet-level diagnostic with a real code) — this **already matches** the existing `RestoreDiagnosticRegex` used by `ParseRestoreIssuesFromText`, so tier 2's first branch is a **pure reuse call**, zero new regex.
   - `CSC : error EnableGenerateDocumentationFile: <message>` (a Roslyn analyzer-config diagnostic with **no** numeric code, so it does *not* match `RestoreDiagnosticRegex`, which requires `[A-Za-z]{2,}\d{3,}`) — this needs one small new regex, `FileBasedAppNoCodeDiagnosticRegex`.
   The design doc's tier 2 (a single `CSC :`-prefixed regex) undersold how much of this is already-tested reuse; this plan implements the fuller, more reuse-heavy version while preserving the design's intent exactly (compact no-location diagnostic summary).
3. **The unhandled-exception line is single-line, not two-line.** Real captured output is
   `Unhandled exception. System.InvalidOperationException: boom` — type and message are on
   the **same** line as the `Unhandled exception.` marker (design doc's prose said "the
   line immediately following the marker", which is imprecise; its own empirical-findings
   table had the correct single-line text). The regex below matches the real single-line
   shape.
4. **"Preceding program output" is preserved from the combined `raw` buffer, not a specific stream.** A later attempt to empirically confirm the stdout/stderr split for the exception case was inconclusive (an unrelated ambient NuGet-config diagnostic fired instead in the retry directory). Rather than depend on an unconfirmed stream-split assumption, the implementation takes everything in the combined `raw = stdout + "\n" + stderr` buffer *before* the regex match index, trims it, and prepends it verbatim above the compact exception summary — satisfying the design's "preserve verbatim" requirement without depending on which literal stream carried it.

---

### Task 1: Detect file-based-app invocations and pass success straight through

**Files:**
- Modify: `RtkSharp/Commands/Dotnet/DotnetCommand.cs:202-209` (the `RunAsync` switch) and a new region near `RunPassthroughAsync` (`DotnetCommand.cs:258-277`)
- Test: `RtkSharp.Tests/Commands/DotnetCommandTests.cs`

**Interfaces:**
- Produces: `internal static Task<int> RunAsync(string[] args, IProcessExecutor executor)` gains two new dispatch arms (no signature change — same public entry point).
- Produces: `private static string? FindFileBasedAppArg(string[] rest)` — returns the first non-option, non-post-`--` argument ending in `.cs` (case-insensitive), or `null`.
- Produces: `private static Task<int> RunFileBasedAppAsync(string[] args, string fileDisplayName, IProcessExecutor executor)` — new handler. This task implements only the success path and dispatch wiring; Task 2-4 add the failure-path filtering.
- Consumes: existing `ExecutionRequest`, `ExecutionResult`, `IProcessExecutor`, `DotnetCliUiLanguage`/`DotnetCliUiLanguageValue` constants (all already in this file).

- [ ] **Step 1: Write the failing dispatch + success-path tests**

Add to `RtkSharp.Tests/Commands/DotnetCommandTests.cs` (inside the existing `DotnetCommandTests` class, after the last `FilterRestore` test — check the file tail with `Read` first to append correctly, do not guess the insertion line):

```csharp
    // ---- File-based app dispatch (dotnet run <file>.cs / dotnet <file>.cs) ----

    private sealed class RecordingExecutor : IProcessExecutor
    {
        private readonly ExecutionResult _result;

        public RecordingExecutor(ExecutionResult result) => _result = result;

        public List<ExecutionRequest> Requests { get; } = new();

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_result);
        }
    }

    [Fact]
    public async Task RunAsync_RunWithCsFile_DispatchesToFileBasedAppHandler()
    {
        var executor = new RecordingExecutor(new ExecutionResult("hello from file-based app\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "run", "app.cs" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(executor.Requests);
        Assert.Equal(new[] { "run", "app.cs" }, executor.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_BareCsFileShorthand_DispatchesToFileBasedAppHandler()
    {
        var executor = new RecordingExecutor(new ExecutionResult("shorthand works\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "app.cs" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(executor.Requests);
        Assert.Equal(new[] { "app.cs" }, executor.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_FileBasedAppSuccess_PassesOutputThroughUnmodified()
    {
        var executor = new RecordingExecutor(new ExecutionResult("hello from file-based app\n", "", 0, TimeSpan.Zero, true, null, false));
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await DotnetCommand.RunAsync(new[] { "run", "app.cs" }, executor);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal("hello from file-based app\n", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_OrdinaryProjectRun_StillPassesThroughRaw()
    {
        // No .cs argument anywhere -> must NOT be treated as a file-based app.
        var executor = new RecordingExecutor(new ExecutionResult("normal project run output\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "run", "--project", "MyApp.csproj" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "run", "--project", "MyApp.csproj" }, executor.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_RunWithCsFileAfterDoubleDash_NotTreatedAsFileBasedApp()
    {
        // "somearg.cs" is the invoked program's own argument, not the file-based app itself.
        var executor = new RecordingExecutor(new ExecutionResult("ran\n", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await DotnetCommand.RunAsync(new[] { "run", "--project", "MyApp.csproj", "--", "somearg.cs" }, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "run", "--project", "MyApp.csproj", "--", "somearg.cs" }, executor.Requests[0].Arguments);
    }
```

Add `using System.IO;` and `using System.Threading;` and `using System.Threading.Tasks;` and `using RtkSharp.Execution;` at the top of `DotnetCommandTests.cs` if not already present — check the existing `using` block first with `Read` before editing.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj --filter "FullyQualifiedName~DotnetCommandTests.RunAsync_RunWithCsFile_DispatchesToFileBasedAppHandler|FullyQualifiedName~DotnetCommandTests.RunAsync_BareCsFileShorthand_DispatchesToFileBasedAppHandler|FullyQualifiedName~DotnetCommandTests.RunAsync_FileBasedAppSuccess_PassesOutputThroughUnmodified|FullyQualifiedName~DotnetCommandTests.RunAsync_OrdinaryProjectRun_StillPassesThroughRaw|FullyQualifiedName~DotnetCommandTests.RunAsync_RunWithCsFileAfterDoubleDash_NotTreatedAsFileBasedApp"`

Expected: the first two new tests FAIL (dispatch not yet implemented — `run`/bare-`.cs` currently fall through to plain passthrough, so `Requests[0].Arguments` assertions will actually still pass since passthrough forwards `args` unchanged too — the real distinguishing behavior comes in Task 2+ once filtering diverges; if these first two pass trivially at this stage, that's expected and fine, since Task 1 only adds routing, not behavior change on success). The `RunAsync_FileBasedAppSuccess_PassesOutputThroughUnmodified` test should already PASS even before any code change, since passthrough already does this — this is intentional: it's a regression guard for Task 1's refactor, not a new-behavior test. Confirm this by running it now and observing PASS.

- [ ] **Step 3: Implement dispatch + success-path handler**

In `RtkSharp/Commands/Dotnet/DotnetCommand.cs`, modify the `RunAsync` switch (lines 202-209):

```csharp
        return subcommand switch
        {
            "build" => await RunTextFilteredAsync("build", rest, executor).ConfigureAwait(false),
            "restore" => await RunTextFilteredAsync("restore", rest, executor).ConfigureAwait(false),
            "test" => await RunTestAsync(rest, executor).ConfigureAwait(false),
            "format" => await RunFormatAsync(rest, executor).ConfigureAwait(false),
            "run" when FindFileBasedAppArg(rest) is { } csFile =>
                await RunFileBasedAppAsync(args, csFile, executor).ConfigureAwait(false),
            _ when subcommand.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) =>
                await RunFileBasedAppAsync(args, subcommand, executor).ConfigureAwait(false),
            _ => await RunPassthroughAsync(args, executor).ConfigureAwait(false),
        };
```

Immediately after `RunPassthroughAsync` (after line 277), add:

```csharp
    // --- file-based app path (dotnet run <file>.cs / dotnet <file>.cs; .NET 10+ only, no Rust equivalent) ---

    /// <summary>
    /// Finds the first non-option argument ending in <c>.cs</c> before any bare <c>--</c>
    /// separator, identifying a <c>dotnet run &lt;file&gt;.cs</c> file-based-app invocation.
    /// Returns null for ordinary project-based <c>dotnet run</c> (no <c>.cs</c> argument, or
    /// a <c>.cs</c>-looking token that is actually a post-<c>--</c> program argument).
    /// </summary>
    private static string? FindFileBasedAppArg(string[] rest)
    {
        foreach (var arg in rest)
        {
            if (arg == "--")
            {
                return null;
            }

            if (arg.Length > 0 && arg[0] != '-' && arg.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs a .NET 10 file-based app (<c>dotnet run &lt;file&gt;.cs</c> or the <c>dotnet
    /// &lt;file&gt;.cs</c> shorthand). A superset feature with no Rust equivalent: success is
    /// pure passthrough (a working file-based app already prints nothing but its own program
    /// output), failure is filtered by <see cref="FilterFileBasedApp"/>.
    /// </summary>
    /// <remarks>
    /// Detection tiers in <see cref="FilterFileBasedApp"/> key off stdout/stderr content
    /// markers, never off a specific exit-code value — an unhandled runtime exception was
    /// observed to exit 127 during design-phase testing, which is unusual for .NET (typically
    /// a large HRESULT-style code) and was not independently reconfirmed. This method always
    /// propagates <c>result.ExitCode</c> verbatim regardless of which tier matched, so that
    /// uncertainty has no effect on correctness here.
    /// </remarks>
    private static async Task<int> RunFileBasedAppAsync(string[] args, string fileDisplayName, IProcessExecutor executor)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DotnetCliUiLanguage] = DotnetCliUiLanguageValue,
        };

        var result = await executor
            .ExecuteAsync(new ExecutionRequest("dotnet", args, Environment: environment, CaptureMode: ExecutionCaptureMode.Separate))
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
            return result.ExitCode;
        }

        var raw = result.Stdout + "\n" + result.Stderr;
        try
        {
            var filtered = FilterFileBasedApp(raw, fileDisplayName);
            Console.Out.Write(filtered + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.Write($"rtk: filter warning: {ex.Message}\n");
            Console.Out.Write(result.Stdout);
            Console.Error.Write(result.Stderr);
        }

        return result.ExitCode;
    }
```

This will not compile yet — `FilterFileBasedApp` doesn't exist. Add a temporary stub directly below `RunFileBasedAppAsync` so Task 1 compiles and its tests (all success-path, never reach the filter) can run:

```csharp
    private static string FilterFileBasedApp(string raw, string fileDisplayName) =>
        throw new NotImplementedException("implemented in Task 2");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj --filter "FullyQualifiedName~DotnetCommandTests.RunAsync_"`

Expected: all 5 new tests PASS (none of them exercise the failure path yet, so the `NotImplementedException` stub is never hit).

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Dotnet/DotnetCommand.cs RtkSharp.Tests/Commands/DotnetCommandTests.cs
git commit -m "feat(dotnet): detect file-based .cs app invocations, passthrough on success"
```

---

### Task 2: Tier 1 + Tier 2 — diagnostic-based failure summaries (build/restore-parser reuse)

**Files:**
- Modify: `RtkSharp/Commands/Dotnet/DotnetCommand.cs` (replace the `FilterFileBasedApp` stub; add one new regex, two new private helpers)
- Test: `RtkSharp.Tests/Commands/DotnetCommandTests.cs`

**Interfaces:**
- Consumes: `internal static BuildSummary ParseBuildFromText(string text)` (existing, `DotnetCommand.cs:1394`), `internal static (List<BinlogIssue> Errors, List<BinlogIssue> Warnings) ParseRestoreIssuesFromText(string text)` (existing, `DotnetCommand.cs:1538`), `private static string FormatIssueSection(List<BinlogIssue> issues, string kind, string header, int cap, string teeLabel)` (existing, `DotnetCommand.cs:1680`), `private static string JoinNonEmpty(params string[] parts)` (existing, `DotnetCommand.cs:1709`), `internal sealed record BinlogIssue(string Code, string File, int Line, int Column, string Message)` (existing, `DotnetCommand.cs:1763`), `private const int CapBuildErrors = 20`, `private const int CapBuildWarnings = 10` (existing).
- Produces: `internal static string FilterFileBasedApp(string raw, string fileDisplayName)` (replaces the Task 1 stub; tier 3/4 added in Task 3).
- Produces: `private static string FormatFileBasedAppIssues(List<BinlogIssue> errors, List<BinlogIssue> warnings, string fileDisplayName)`.

- [ ] **Step 1: Write the failing tier 1/2 filter tests**

Add to `RtkSharp.Tests/Commands/DotnetCommandTests.cs`:

```csharp
    // ---- FilterFileBasedApp: tier 1 (IssueRegex-shaped) + tier 2 (no-location diagnostics) ----

    // Captured from a real `dotnet run syntax.cs` with a genuine missing-semicolon syntax error.
    private const string FileBasedAppSyntaxErrorRaw =
        "C:\\scratch\\syntax.cs(1,24): error CS1002: ; expected\n" +
        "\n" +
        "The build failed. Fix the build errors and run again.\n";

    // Captured from a real `dotnet run boom2.cs` where an ambient ~/NuGet.Config with two
    // package sources triggered a central-package-management source-mapping error — a
    // no-location MSBuild/NuGet diagnostic that already matches the existing
    // RestoreDiagnosticRegex used by ParseRestoreIssuesFromText (file + numeric code, no line/col).
    private const string FileBasedAppNuGetDiagnosticRaw =
        "C:\\scratch\\boom2.cs.csproj : error NU1507: Warning As Error: There are 2 package sources defined in your configuration. When using central package management, please map your package sources with package source mapping (https://aka.ms/nuget-package-source-mapping) or specify a single package source. The following sources are defined: nuget.org, QHubPackages\n" +
        "\n" +
        "The build failed. Fix the build errors and run again.\n";

    // Captured from a real `dotnet run ok.cs` on a trivial one-line program on this SDK, which
    // fails a Roslyn analyzer-config check with no source location and no numeric diagnostic code.
    private const string FileBasedAppNoCodeDiagnosticRaw =
        "CSC : error EnableGenerateDocumentationFile: Set MSBuild property 'GenerateDocumentationFile' to 'true' in project file to enable IDE0005 (Remove unnecessary usings/imports) on build (https://github.com/dotnet/roslyn/issues/41640)\n" +
        "\n" +
        "The build failed. Fix the build errors and run again.\n";

    [Fact]
    public void FilterFileBasedApp_SyntaxError_ReusesIssueRegexPath()
    {
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppSyntaxErrorRaw, "syntax.cs");

        Assert.Equal(
            "Errors:\n" +
            "  C:\\scratch\\syntax.cs(1,24) error CS1002: ; expected\n" +
            "\n" +
            "fail dotnet run: syntax.cs (1 errors, 0 warnings)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_NuGetDiagnostic_ReusesRestoreDiagnosticPath()
    {
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppNuGetDiagnosticRaw, "boom2.cs");

        Assert.Equal(
            "Errors:\n" +
            "  error NU1507: Warning As Error: There are 2 package sources defined in your configuration. When using central package management, please map your package sources with package source mapping (https://aka.ms/nuget-package-source-mapping) or specify...\n" +
            "\n" +
            "fail dotnet run: boom2.cs (1 errors, 0 warnings)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_NoCodeDiagnostic_UsesNewFallbackRegex()
    {
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppNoCodeDiagnosticRaw, "ok.cs");

        Assert.Equal(
            "Errors:\n" +
            "  error EnableGenerateDocumentationFile: Set MSBuild property 'GenerateDocumentationFile' to 'true' in project file to enable IDE0005 (Remove unnecessary usings/imports) on build (https://github.com/dotnet/roslyn/issues/41640)\n" +
            "\n" +
            "fail dotnet run: ok.cs (1 errors, 0 warnings)",
            output);
    }
```

Note on the `NuGetDiagnostic` expected string: `FormatIssueSection`/`FormatIssue` truncates messages to 180 characters via `Truncate(issue.Message, 180)`. Do not hand-compute the truncation boundary by eye — Step 2 will surface the exact expected string from the actual test failure output, then Step 1's assertion should be corrected to match precisely before proceeding (this is the one string in this task worth confirming empirically rather than trusting a manual count).

- [ ] **Step 2: Run tests to verify they fail, and capture the exact truncated string**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj --filter "FullyQualifiedName~DotnetCommandTests.FilterFileBasedApp_"`

Expected: all three FAIL with `NotImplementedException: implemented in Task 2` (from the Task 1 stub). This confirms the stub is reachable and correctly failing before real implementation exists. The exact truncated-message assertion for `FilterFileBasedApp_NuGetDiagnostic_ReusesRestoreDiagnosticPath` will be finalized in Step 4 below, once real output exists to compare against.

- [ ] **Step 3: Implement tier 1 + tier 2**

In `RtkSharp/Commands/Dotnet/DotnetCommand.cs`, add one new regex next to the other `lazy_static`-equivalent regex block (near `RestoreDiagnosticRegex`, around line 129):

```csharp
    // New regex, not ported from Rust (no oracle for file-based apps): matches a no-location
    // Roslyn/MSBuild diagnostic of the shape "CSC : error <NonNumericName>: <msg>" — narrower
    // than RestoreDiagnosticRegex (which requires a numeric code like NU1507 and already covers
    // that shape for free) because this one has no digits in its "code" position at all.
    private static readonly Regex FileBasedAppNoCodeDiagnosticRegex = new(
        @"^\s*CSC\s*:\s*error\s+(?<name>\S+):\s*(?<msg>.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);
```

Replace the Task 1 stub:

```csharp
    private static string FilterFileBasedApp(string raw, string fileDisplayName) =>
        throw new NotImplementedException("implemented in Task 2");
```

with:

```csharp
    internal static string FilterFileBasedApp(string raw, string fileDisplayName)
    {
        // Tier 1: same IssueRegex-driven parsing dotnet build/restore already use — covers
        // genuine source-level compile errors/warnings for free (identical output shape).
        var buildSummary = ParseBuildFromText(raw);
        if (buildSummary.Errors.Count > 0 || buildSummary.Warnings.Count > 0)
        {
            return FormatFileBasedAppIssues(buildSummary.Errors, buildSummary.Warnings, fileDisplayName);
        }

        // Tier 2a: no-location MSBuild/NuGet diagnostics with a numeric code (e.g. NU1507) —
        // already matched by the existing restore-diagnostic parser, zero new regex needed.
        var (restoreErrors, restoreWarnings) = ParseRestoreIssuesFromText(raw);
        if (restoreErrors.Count > 0 || restoreWarnings.Count > 0)
        {
            return FormatFileBasedAppIssues(restoreErrors, restoreWarnings, fileDisplayName);
        }

        // Tier 2b: no-location diagnostics with no numeric code at all (e.g. Roslyn
        // analyzer-config errors like EnableGenerateDocumentationFile).
        var noCodeMatches = FileBasedAppNoCodeDiagnosticRegex.Matches(raw);
        if (noCodeMatches.Count > 0)
        {
            var errors = noCodeMatches
                .Select(m => new BinlogIssue(string.Empty, string.Empty, 0, 0, $"{m.Groups["name"].Value}: {m.Groups["msg"].Value.Trim()}"))
                .ToList();
            return FormatFileBasedAppIssues(errors, new List<BinlogIssue>(), fileDisplayName);
        }

        // Tier 3 (unhandled exception) and tier 4 (unrecognized -> raw fallback) added in Task 3.
        throw new InvalidOperationException("unrecognized dotnet run failure output shape");
    }

    private static string FormatFileBasedAppIssues(List<BinlogIssue> errors, List<BinlogIssue> warnings, string fileDisplayName)
    {
        var errorsSection = FormatIssueSection(errors, "error", "Errors:", CapBuildErrors, "dotnet-run-errors");
        var warningsSection = FormatIssueSection(warnings, "warning", "Warnings:", CapBuildWarnings, "dotnet-run-warnings");
        var verdict = $"fail dotnet run: {fileDisplayName} ({errors.Count} errors, {warnings.Count} warnings)";

        return JoinNonEmpty(warningsSection, errorsSection, verdict);
    }
```

Add `using System.Linq;` at the top of `DotnetCommand.cs` if not already present (check first — `FormatBuildOutput`/etc. already use LINQ elsewhere in this file via `.Take`/`.Select`, so it is almost certainly already imported).

- [ ] **Step 4: Run tests, fix the truncation-dependent assertion, verify all pass**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj --filter "FullyQualifiedName~DotnetCommandTests.FilterFileBasedApp_"`

Expected: `FilterFileBasedApp_SyntaxError_ReusesIssueRegexPath` and `FilterFileBasedApp_NoCodeDiagnostic_UsesNewFallbackRegex` PASS immediately. `FilterFileBasedApp_NuGetDiagnostic_ReusesRestoreDiagnosticPath` will likely FAIL on the exact truncation point of the long NU1507 message — read the actual/expected diff from the test failure output, update that one string literal in the test to match the real 180-character-truncated output exactly (do not adjust the production code to fit the test — `Truncate` is existing, already-tested shared logic), then re-run until all three PASS.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Dotnet/DotnetCommand.cs RtkSharp.Tests/Commands/DotnetCommandTests.cs
git commit -m "feat(dotnet): file-based app tier 1/2 diagnostic summaries (reuses build/restore parsers)"
```

---

### Task 3: Tier 3 — unhandled-exception summary, and tier 4 fallback wiring

**Files:**
- Modify: `RtkSharp/Commands/Dotnet/DotnetCommand.cs` (add two new regexes, one new formatter, extend `FilterFileBasedApp`)
- Test: `RtkSharp.Tests/Commands/DotnetCommandTests.cs`

**Interfaces:**
- Consumes: `internal static string FilterFileBasedApp(string raw, string fileDisplayName)` (from Task 2, extended here, not replaced).
- Produces: `private static string FormatFileBasedAppException(string raw, Match exceptionMatch)`.

- [ ] **Step 1: Write the failing tier 3/4 tests**

Add to `RtkSharp.Tests/Commands/DotnetCommandTests.cs`:

```csharp
    // ---- FilterFileBasedApp: tier 3 (unhandled exception) + tier 4 (unrecognized -> throws) ----

    // Captured from a real `dotnet run boom.cs` (Console.WriteLine then `throw new
    // InvalidOperationException("boom")`). stdout/stderr were captured combined (2>&1); the
    // implementation operates on the same combined `raw` convention this file already uses
    // for every other filter, so this fixture models that combined stream faithfully.
    private const string FileBasedAppExceptionRaw =
        "before crash\n" +
        "Unhandled exception. System.InvalidOperationException: boom\n" +
        "   at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2\n";

    [Fact]
    public void FilterFileBasedApp_UnhandledException_SummarizesWithFrameCountAndPreservesPrecedingOutput()
    {
        var output = DotnetCommand.FilterFileBasedApp(FileBasedAppExceptionRaw, "boom.cs");

        Assert.Equal(
            "before crash\n" +
            "\n" +
            "exception: System.InvalidOperationException: boom (1 frames, first: at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_UnhandledExceptionWithNoPrecedingOutput_OmitsBlankPrefix()
    {
        const string raw =
            "Unhandled exception. System.InvalidOperationException: boom\n" +
            "   at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2\n";

        var output = DotnetCommand.FilterFileBasedApp(raw, "boom.cs");

        Assert.Equal(
            "exception: System.InvalidOperationException: boom (1 frames, first: at Program.<Main>$(String[] args) in C:\\scratch\\boom.cs:line 2)",
            output);
    }

    [Fact]
    public void FilterFileBasedApp_UnrecognizedFailureShape_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DotnetCommand.FilterFileBasedApp("some completely unrecognized failure text\n", "mystery.cs"));
    }

    [Fact]
    public async Task RunFileBasedAppAsync_UnrecognizedFailureShape_FallsBackToRawPassthrough()
    {
        var executor = new RecordingExecutor(new ExecutionResult(
            "some completely unrecognized failure text\n", "", 1, TimeSpan.Zero, true, null, false));
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        int exitCode;
        try
        {
            exitCode = await DotnetCommand.RunAsync(new[] { "run", "mystery.cs" }, executor);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.Equal(1, exitCode);
        Assert.Contains("some completely unrecognized failure text", outWriter.ToString());
        Assert.Contains("rtk: filter warning:", errWriter.ToString());
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj --filter "FullyQualifiedName~DotnetCommandTests.FilterFileBasedApp_Unhandled|FullyQualifiedName~DotnetCommandTests.FilterFileBasedApp_UnrecognizedFailureShape_Throws|FullyQualifiedName~DotnetCommandTests.RunFileBasedAppAsync_UnrecognizedFailureShape_FallsBackToRawPassthrough"`

Expected: the two exception-summary tests FAIL with `InvalidOperationException: unrecognized dotnet run failure output shape` (tier 3 not implemented yet, so it falls through to the tier-4 throw already in place from Task 2 — confirms Task 2's fallback throw is wired correctly). `FilterFileBasedApp_UnrecognizedFailureShape_Throws` should already PASS (tier 4 behavior already exists from Task 2). `RunFileBasedAppAsync_UnrecognizedFailureShape_FallsBackToRawPassthrough` should already PASS too (the try/catch wiring was added in Task 1). Confirm this split explicitly before proceeding — it validates Task 1/2's fallback plumbing independently of Task 3's new tier.

- [ ] **Step 3: Implement tier 3**

In `RtkSharp/Commands/Dotnet/DotnetCommand.cs`, add two new regexes next to `FileBasedAppNoCodeDiagnosticRegex`:

```csharp
    // New regexes, not ported from Rust (no oracle for file-based apps). Matches .NET's
    // single-line unhandled-exception header ("Unhandled exception. <Type>: <message>") and
    // VSTest/CLR-style stack-trace frame lines ("   at <frame>").
    private static readonly Regex FileBasedAppExceptionRegex = new(
        @"^Unhandled exception\.\s*(?<type>[^\r\n:]+):\s*(?<message>.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex FileBasedAppStackFrameRegex = new(
        @"^\s+at\s+\S.*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
```

In `FilterFileBasedApp`, replace the final `throw` line:

```csharp
        // Tier 3 (unhandled exception) and tier 4 (unrecognized -> raw fallback) added in Task 3.
        throw new InvalidOperationException("unrecognized dotnet run failure output shape");
```

with:

```csharp
        // Tier 3: an unhandled runtime exception (not a compile-time failure at all).
        var exceptionMatch = FileBasedAppExceptionRegex.Match(raw);
        if (exceptionMatch.Success)
        {
            return FormatFileBasedAppException(raw, exceptionMatch);
        }

        // Tier 4: unrecognized failure shape -> throw, caught by RunFileBasedAppAsync's
        // try/catch, which falls back to raw, unfiltered passthrough (mandatory fallback
        // contract: a filter must never hide output it doesn't recognize).
        throw new InvalidOperationException("unrecognized dotnet run failure output shape");
    }

    private static string FormatFileBasedAppException(string raw, Match exceptionMatch)
    {
        var type = exceptionMatch.Groups["type"].Value.Trim();
        var message = exceptionMatch.Groups["message"].Value.Trim();

        var frames = FileBasedAppStackFrameRegex.Matches(raw);
        var firstFrame = frames.Count > 0 ? frames[0].Value.Trim() : "unknown";
        var summary = $"exception: {type}: {message} ({frames.Count} frames, first: {firstFrame})";

        var preceding = raw[..exceptionMatch.Index].Trim();
        return preceding.Length > 0 ? $"{preceding}\n\n{summary}" : summary;
    }
```

Add `using System.Text.RegularExpressions;`'s `Match` type is already imported via the existing `using System.Text.RegularExpressions;` at the top of the file (line 3) — no new using needed.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj --filter "FullyQualifiedName~DotnetCommandTests.FilterFileBasedApp_|FullyQualifiedName~DotnetCommandTests.RunFileBasedAppAsync_|FullyQualifiedName~DotnetCommandTests.RunAsync_"`

Expected: all tests from Tasks 1-3 PASS.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Dotnet/DotnetCommand.cs RtkSharp.Tests/Commands/DotnetCommandTests.cs
git commit -m "feat(dotnet): file-based app tier 3 exception summary + tier 4 fallback"
```

---

### Task 4: Class-remarks documentation update and full-suite verification

**Files:**
- Modify: `RtkSharp/Commands/Dotnet/DotnetCommand.cs:11-42` (class-level `<remarks>` doc comment)
- Modify: `docs/PLANS.md` (mark this item done in the Phase 6 status row and §12 backlog entry)

**Interfaces:**
- None new — this task is documentation-only plus full-suite regression verification.

- [ ] **Step 1: Update the class-level doc comment**

Read `RtkSharp/Commands/Dotnet/DotnetCommand.cs:11-42` first to see the current exact text, then add a new `<para>` after the existing "Windows drive-letter paths" paragraph (before the closing `</remarks>`):

```csharp
/// <para>
/// <b>File-based app support (superset feature, no Rust oracle).</b> <c>dotnet run
/// &lt;file&gt;.cs</c> and the <c>dotnet &lt;file&gt;.cs</c> shorthand (.NET 10+) are detected
/// and routed to <see cref="RunFileBasedAppAsync"/>. Success is pure passthrough (a working
/// file-based app already prints nothing but its own program output). Failure is filtered by
/// <see cref="FilterFileBasedApp"/>, which tries, in order: the same <see cref="IssueRegex"/>-
/// driven parsing <c>build</c>/<c>restore</c> already use (covers genuine compile errors for
/// free), a no-location MSBuild/NuGet-diagnostic path (reusing <see
/// cref="ParseRestoreIssuesFromText"/> plus one narrow new regex for diagnostics with no
/// numeric code), an unhandled-runtime-exception summary, and finally a raw-passthrough
/// fallback for anything unrecognized. See
/// <c>docs/superpowers/specs/2026-07-08-cs-file-based-app-execution-design.md</c> for the full
/// design rationale and empirical findings this is based on.
/// </para>
```

- [ ] **Step 2: Run the full unit test suite**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release --nologo`

Expected: `Passed! - Failed: 0, Passed: 3287, Skipped: 1, Total: 3288` (3275 existing + 13 new tests from Tasks 1-3: 5 dispatch/success + 3 tier-1/2 + 4 tier-3/4 + 1 no-preceding-output variant — recount exact new-test total from the actual `[Fact]` methods added across Tasks 1-3 and confirm the total matches; if it doesn't, some test was missed, not silently dropped).

- [ ] **Step 3: Update `docs/PLANS.md` status**

In the Phase Scores table (Phase 6 row), change:

```
| Phase 6: Best-in-Class C# and .NET Support | 9 | **Partial** — ... Not yet started: expanded `dotnet` subcommands (...) and file-based `.cs` app handling — **file-based `.cs` execution is next up, see §12**. | Strategic differentiator for a .NET 10-first fork. |
```

to:

```
| Phase 6: Best-in-Class C# and .NET Support | 9 | **Partial** — ... file-based `.cs` app execution (`dotnet run <file>.cs` / `dotnet <file>.cs`) shipped 2026-07-08 as a genuine no-Rust-equivalent superset feature (compile-failure and unhandled-exception summaries, success is pure passthrough). Not yet started: expanded `dotnet` subcommands (`publish`/`pack`/`clean`/`run` for *project*-based apps/`tool`/`new`/`workload`/`sln`/`ef`). | Strategic differentiator for a .NET 10-first fork. |
```

In §12's item 7, change:

```
7. Add `.cs`, `.ps1`, `.bat/.cmd`, and `.sh` script execution. **Partial** — ... **In progress now: single-file `.cs` execution support** (...) — picked as the quick win of this group since it reuses the existing `dotnet build`-diagnostics text-parsing path rather than needing new infrastructure.
```

to:

```
7. Add `.cs`, `.ps1`, `.bat/.cmd`, and `.sh` script execution. **Partial** — single-file `.cs`
   execution (`dotnet run <file>.cs` / `dotnet <file>.cs`) **done** 2026-07-08 (see
   `docs/superpowers/specs/2026-07-08-cs-file-based-app-execution-design.md` and
   `docs/superpowers/plans/2026-07-08-cs-file-based-app-execution.md`). Still not started:
   `.ps1`/`.bat`/`.sh` content-filtering layer (only wrapper *resolution* exists), and `dotnet
   publish file.cs`/`dotnet pack file.cs` filtering (explicitly deferred as a non-goal of the
   `.cs` design, see that spec's "Non-Goals" section).
```

- [ ] **Step 4: Verify the doc edits render sensibly and commit**

No automated check for markdown table formatting — visually confirm the two edited table rows/list items in `docs/PLANS.md` still parse as valid Markdown tables/lists (correct pipe alignment isn't required, just correct pipe *count* per row) by reading the file back.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Dotnet/DotnetCommand.cs docs/PLANS.md
git commit -m "docs: document file-based .cs app support, mark PLANS.md item done"
```
