# RtkSharp.Filters Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract RtkSharp's existing *pure* filter logic (string-in/
string-out compression of already-captured command output — not process
execution) into a new `RtkSharp.Filters` class library, packaged as its own
NuGet artifact, so CodeSharp (which keeps running commands itself,
unchanged) can call `RtkFilters.Filter(...)` on output it already captured
instead of shelling out to a separately installed `rtk` binary.

**Architecture:** Per-file extraction, not a rewrite and not a directory
move: `docs/superpowers/specs/2026-07-10-rtksharp-filters-library-design.md`
(Revision 2) found ~46 of ~65 filter modules already expose a standalone
pure filter method (often in a matching `*Filters.cs` sibling), ~13 need a
shallow pull-out from a method that also does exec/Console work, 1
(`ErrCommand`) has no clean separation and is excluded from v1, and ~15 are
pure dispatchers with no filter logic of their own. `RtkSharp/Execution/**`,
`RtkSharp/Commands/**`'s `RunAsync`/dispatch methods, and
`RtkSharp/Rewrite/**` (rewrite/permission engine) all stay in `RtkSharp` —
none are pure, none belong in a filter-only library. `RtkSharp.csproj`
gains a `ProjectReference` to `RtkSharp.Filters` and each moved-from file's
`RunAsync` calls the extracted method from its new location instead of a
local one.

**Tech Stack:** .NET 10 (`net10.0`), xUnit, `Microsoft.CodeAnalysis.CSharp`
(Roslyn), `RtkSharp.TreeSitter.Trimmed`, `Acornima`.

## Global Constraints

- Every task must end with `dotnet build` succeeding for the whole solution
  (`RtkSharp.slnx`) and the full existing test suite still passing (3357
  passed / 1 skipped as of 2026-07-10, `dotnet test RtkSharp.Tests`) —
  extracting a method to a new project must not change CLI behavior.
- Each extracted method's **signature does not change** except visibility
  (`internal`/`private` → `public`) and, where noted, pulling logic out of
  an impure method into a new pure one. Callers in the original
  `*Command.cs` file are updated to call the moved method via a `using` of
  its new namespace — the call site itself is otherwise unchanged.
- CLI-only allowlist (never touched by this plan, stays exactly as-is):
  `Commands/Analytics/**`, `Commands/System/ConfigCommand.cs`,
  `Commands/System/SmartCommand.cs`, all of `RtkSharp/Hooks/**`,
  `RtkSharp/Rewrite/**` (except `ShellLexer.cs`, which moves — see Task 3),
  `RtkSharp/Execution/**`, `RtkSharp/Discover/**`, `RtkSharp/Learn/**`.
- `ErrCommand.cs` is explicitly excluded from `RtkFilters` registration in
  this plan (per the design's non-goals) — do not attempt to extract it.
- `RtkSharp.ParityTests` needs no changes — verify this stays true at the
  end of Task 16.
- For every "NEEDS-EXTRACTION" task below, the exact boundary between pure
  and impure code was identified by a prior research pass but not
  pre-written as a diff — read the named method(s) in the named file before
  editing, since the described transformation is precise but the literal
  current code isn't reproduced in this plan.

## Global Constraints — per-command verdict reference

Compiled from a read-verified per-module survey; use this table instead of
re-deriving it. "Filter method(s)" is the exact existing name(s) to extract
or expose; `NEEDS-EXTRACTION` entries additionally describe what to pull
out.

| Command | Source file | Verdict | Filter method(s) |
|---|---|---|---|
| git | `Commands/Git/GitCommand.cs` | ALREADY-PURE | `FilterLogOutput`, `TruncateLine`, `CompactDiff`, `FormatStatusOutput`, `FormatStatusOutputDetached`, `FilterStatusWithArgs`, `FormatAddSummary`, `ParseCommitOutput`, `FilterPushOutput`, `FormatPullSummary`, `FilterBranchOutput`, `FormatFetchSummary`, `FormatStashMessage`, `FilterStashList`, `FilterWorktreeList` |
| diff | `Commands/Git/DiffCommand.cs` | ALREADY-PURE | `RenderFileDiff`, `FormatDiffChanges`, `ComputeDiff`, `Similarity`, `CondenseUnifiedDiff` |
| gt | `Commands/Git/GtCommand.cs` | ALREADY-PURE | `FilterGtLogEntries`, `FilterGtSubmit`, `FilterGtSync`, `FilterGtRestack`, `FilterGtCreate`, `FilterIdentity` |
| glab | `Commands/Git/GlabCommand.cs` | ALREADY-PURE | `FormatMrList`, `FormatMrView`, `FormatIssueList`, `FormatIssueView`, `FormatCiList`, `FormatCiStatus`, `FilterCiTrace`, `FormatReleaseList`, `FilterReleaseView`, `FilterMarkdownBody` |
| gh | `Commands/Gh/GhCommand.cs` | ALREADY-PURE | `FormatPrList`, `FormatPrView`, `FormatPrChecks`, `FormatPrStatus`, `FormatPrStatusEntry`, `FormatIssueList`, `FormatIssueView`, `FormatRunList`, `FormatRunView`, `FormatRepoView`, `FilterMarkdownBody` |
| dotnet | `Commands/Dotnet/DotnetCommand.cs` | ALREADY-PURE | `FilterBuild(string,bool)`, `FilterRestore(string,bool)`, `FormatTestOutput(TestSummary,...)`, `FormatDotnetFormatOutput`, `FilterFileBasedApp`, `ParseBuildFromText`, `ParseRestoreFromText`, `ParseTestFromText` |
| (dotnet format report) | `Commands/Dotnet/DotnetFormatReport.cs` | NEEDS-EXTRACTION | Split `Summarize(List<FormatReportEntryDto>)` out of `ParseFormatReport(string path)`, which currently fuses `File.OpenRead` + JSON parse + summarize in one method — the summarize half is what moves |
| (dotnet trx) | `Commands/Dotnet/DotnetTrx.cs` | ALREADY-PURE | `ParseTrxContent(string content)` |
| npm | `Commands/Js/NpmCommand.cs` | ALREADY-PURE | `FilterNpmOutput(string)` |
| pnpm | `Commands/Js/PnpmCommand.cs` | PARTIAL — install path ALREADY-PURE, list/outdated NEEDS-EXTRACTION | `FilterPnpmInstall(string)` pure as-is; `LogAndFormatList`/`LogAndFormatOutdated` mix `Console.Error.Write` into formatting — split the formatting-only part into new pure methods, e.g. `FormatPnpmList`/`FormatPnpmOutdated`, leaving the `Console.Error.Write` diagnostic call behind in the original file |
| tsc | `Commands/Js/TscCommand.cs` | ALREADY-PURE | `FilterTscOutput(string)` |
| vitest/jest | `Commands/Js/VitestCommand.cs` | NEEDS-EXTRACTION | `RenderTestOutputWithHints(...)` is genuinely pure and moves as-is; `FormatTestOutput(...)` writes Console diagnostics internally — separate the pure formatting from the diagnostic write before moving |
| playwright | `Commands/Js/PlaywrightCommand.cs` | NEEDS-EXTRACTION | `RenderOutput(...)` is pure and moves; filtering also happens inline via `PlaywrightParser` + `LogFullAndFormat`/`LogDegradedAndFormat` (Console side effects) — extract the formatting logic from those two methods into new pure methods, leaving the `Log*` Console calls behind |
| prisma | `Commands/Js/PrismaCommand.cs` | ALREADY-PURE | `FilterPrismaGenerate`, `FilterMigrateDev`, `FilterMigrateStatus`, `FilterMigrateDeploy`, `FilterDbPush` |
| lint | `Commands/Js/LintCommand.cs` | ALREADY-PURE | `FilterEslintJson`, `FilterPylintJson`, `FilterGenericLint` |
| next | `Commands/Js/NextCommand.cs` | ALREADY-PURE | `FilterNextBuild(string)` |
| prettier | `Commands/Js/PrettierCommand.cs` | ALREADY-PURE | `FilterPrettierOutput(string)` |
| cargo build/test | `Commands/Rust/CargoBuildTestHandlers.cs` | ALREADY-PURE | `CargoBuildTestFilters.FilterCargoBuild(string)`, `FilterCargoTest(string)` (nested static class) |
| cargo (other) | `Commands/Rust/CargoNonStreamingFilters.cs` | ALREADY-PURE | `FilterCargoInstall`, `FilterCargoClippy`, `FilterCargoNextest`, `FormatCrateInfo` |
| golangci-lint | `Commands/Go/GolangciLintCommand.cs` | ALREADY-PURE | `FilterGolangciJson(string,uint)` |
| go | `Commands/Go/GoFilters.cs` | ALREADY-PURE | `FilterGoTestJson`, `FilterGoBuild`, `FilterGoBuildWithExit`, `FilterGoVet`, `CompactPackageName` |
| ruff | `Commands/Python/RuffCommand.cs` + `RuffFilters.cs` | NEEDS-EXTRACTION (mild) | `RuffFilters.FilterRuffCheckJson`/`FilterRuffFormat` are already pure and move as-is; `RuffCommand.cs`'s mode-selection is an inline anonymous lambda — name it `FilterRuffOutput(string stdout, bool isCheck, bool isFormat)` in the new location instead of moving an anonymous lambda |
| pytest | `Commands/Python/PytestFilters.cs` | ALREADY-PURE | `FilterPytestOutput(string)` |
| mypy | `Commands/Python/MypyCommand.cs` + `MypyFilters.cs` | NEEDS-EXTRACTION (mild) | `MypyFilters.FilterMypyOutput(string)` is already pure and moves as-is; `MypyCommand.cs`'s call site is an inline lambda `raw => MypyFilters.FilterMypyOutput(Utils.StripAnsi(raw))` — no new method needed, just update the call site to reference the moved `FilterMypyOutput` from its new namespace |
| pip | `Commands/Python/PipFilters.cs` | ALREADY-PURE (call site NEEDS-EXTRACTION) | `FilterPipList`, `FilterPipOutdated` are pure and move as-is; `PipCommand.cs`'s `RunListAsync`/`RunOutdatedAsync` currently inline the filter call mixed with exec + Console.Write — no extraction of the filter itself needed, just update `PipCommand.cs`'s call sites to the new namespace |
| rake | `Commands/Ruby/RakeCommand.cs` | ALREADY-PURE | `FilterMinitestOutput(string)` |
| rubocop | `Commands/Ruby/RubocopFilters.cs` | ALREADY-PURE (note: `FilterRubocopJson` does `Console.Error.Write` on a JSON-parse-failure diagnostic path — move as-is, this is an acceptable disclosed side effect on an error path only, not a blocker) | `FilterRubocopJson`, `FilterRubocopText` |
| rspec | `Commands/Ruby/RspecFilters.cs` | ALREADY-PURE (same diagnostic-write caveat as rubocop) | `StripNoise`, `FilterRspecOutput`, `FilterRspecText` |
| gradlew | `Commands/Jvm/GradlewFilters.cs` | ALREADY-PURE | `FilterBuildLine(bool)`, `FilterTest`, `FilterConnected`, `FilterLint`, `FilterDependencies` |
| mvn (surefire) | `Commands/Jvm/MvnSurefireFilter.cs` | ALREADY-PURE | `FilterSurefire(string)`, `FilterSurefireWithCap`, `FilterPackage`, `FilterPackageWithCap` |
| mvn (compile/quiet) | `Commands/Jvm/MvnCompileQuietFilters.cs` | ALREADY-PURE | `FilterCompile(string)`, `FilterQuiet(string)` |
| mvn (shared helpers) | `Commands/Jvm/MvnSharedFilters.cs` | ALREADY-PURE (support library, moves alongside the two files above since they depend on it) | `IsFrameworkFrame`, `SurefireBlock`, `FailuresSummaryCap`, and other shared predicates/state-machine helpers |
| aws | `Commands/Cloud/AwsFilters.cs` | ALREADY-PURE | ~25 `FilterResult`-returning methods (`FilterStsIdentity`, `FilterS3Ls`, `FilterEc2Instances`, etc.), `FilterJsonCompact` |
| az | `Commands/Cloud/AzFilters.cs` | ALREADY-PURE | ~15 methods (`FilterAccountShow`, `FilterGroupList`, `FilterAcrRepositoryList`, etc.), `Redact` |
| docker | `Commands/Cloud/DockerCommand.cs` | NEEDS-EXTRACTION | `FormatContainerLine`, `FormatContainerLineFromParts`, `CompactPorts`, `FormatComposePs`, `FormatComposeLogs`, `FormatComposeBuild` are pure and move as-is; `docker ps`/`docker images` summary-building is inline inside impure `DockerPsAsync`/`RunImagesAsync` — extract that summary-building into new pure methods (e.g. `FormatPsSummary`/`FormatImagesSummary`) before moving |
| kubectl/oc | `Commands/Cloud/ContainerFilters.cs` | ALREADY-PURE (JSON-typed, needs a text wrapper) | `FormatKubectlPods(JsonElement)`, `FormatKubectlServices(JsonElement)` — these take `JsonElement`, not `string`; add a thin `string`-in wrapper (`JsonDocument.Parse(raw).RootElement`) when registering in `RtkFilters` (Task 15), don't change the existing methods' signatures |
| curl | `Commands/Cloud/CurlCommand.cs` | ALREADY-PURE (disclosed side effect) | `FilterCurlOutput(string,bool)` — internally calls `Tee.ForceTeeHint` which writes a file; move as-is, document this as a known side effect in the extracted method's XML doc, don't attempt to remove it in this pass |
| wget | `Commands/Cloud/WgetCommand.cs` | NEEDS-EXTRACTION | Pure sub-helpers `FormatSize`, `CompactUrl`, `ParseError`, `TruncateLine` move as-is; final message assembly is inline in `RunAsync`/`RunStdoutAsync` — extract into a new `FormatWgetOutput(...)` pure method (do not move `GetFileSize`, which does real file-stat I/O and stays behind) |
| psql | `Commands/Cloud/PsqlCommand.cs` | ALREADY-PURE | `FilterPsqlOutput`, `IsTableFormat`, `IsExpandedFormat`, `FilterTable`, `FilterExpanded` |
| ls | `Commands/System/LsCommand.cs` | ALREADY-PURE | `FilterLs(string,bool,bool)` |
| read | `Commands/System/ReadCommand.cs` | ALREADY-PURE (one caveat) | `Render(...)`, `ApplyLineWindow`, `FormatWithLineNumbers`, `SmartTruncate` — `Render` has one embedded `Console.Error.WriteLine` only when a `filePath` argument is supplied; move as-is and document the caveat, don't attempt to remove it in this pass |
| wc | `Commands/System/WcCommand.cs` | ALREADY-PURE | `FilterWcOutput(string,WcMode)` |
| tree | `Commands/System/TreeCommand.cs` | ALREADY-PURE | `FilterTreeOutput(string)` |
| find | `Commands/System/FindCommand.cs` | NEEDS-EXTRACTION (atypical — input is a file-path list, not process stdout) | Grouping/summary logic is inline inside `Run(FindArgs, TextWriter)` — extract a pure `FormatFindResults(IReadOnlyList<string> paths, FindArgs args)` method; document in `RtkFilters` registration that `find`'s "raw output" is actually a newline-joined path list from the caller's own directory walk, not literal command stdout |
| grep | `Commands/System/GrepCommand.cs` | ALREADY-PURE | `BuildGroupedOutput(string,string,int,int,bool)` |
| pipe | `Commands/System/PipeCommand.cs` | ALREADY-PURE | `GrepWrapper(string)`, `FindWrapper(string)`, `AutoDetectFilter(string)` (other aliases delegate to other already-moved modules' filters) |
| test | `Commands/System/TestCommand.cs` | ALREADY-PURE | `ExtractTestSummary(string,string)` |
| env | `Commands/System/EnvCommand.cs` | NEEDS-EXTRACTION (atypical — input is a `Dictionary`, not raw text) | Report-building is inline inside `Run(...)`, interleaved with Console writes — extract a pure `FormatEnvReport(IReadOnlyDictionary<string,string?> env, ...)` method; document that `env`'s "raw input" is the environment dictionary, not command stdout |
| format | `Commands/System/FormatCommand.cs` | ALREADY-PURE | `FilterBlackOutput(string)` (dispatch/selection is inline but each branch's filter is pure — `prettier`'s branch reuses `PrettierCommand.FilterPrettierOutput`, already moved in the Js task) |
| json | `Commands/System/JsonCommand.cs` | ALREADY-PURE | `FilterJsonCompact(string,int)`, `FilterJsonSchema(string,int)` |
| deps | `Commands/System/DepsCommand.cs` | ALREADY-PURE | `SummarizeCargo`, `SummarizePackageJson`, `SummarizeRequirements`, `SummarizePyproject`, `SummarizeGoMod` |
| log | `Commands/System/LogCommand.cs` | ALREADY-PURE | `AnalyzeLogs(string)` |
| summary | `Commands/System/SummaryCommand.cs` | ALREADY-PURE | `SummarizeOutput(string,string,bool)` |
| err | `Commands/System/ErrCommand.cs` | NO-CLEAN-SEPARATION | **Excluded from v1** — filtering happens via `RtkSharp.Core.ErrorStreamFilter` applied line-by-line during live streaming exec, no standalone method exists |
| run, proxy | `Commands/System/RunCommand.cs`, `ProxyCommand.cs` | N/A | No filtering by design (transparent passthrough) — not registered in `RtkFilters` |

---

### Task 1: Scaffold `RtkSharp.Filters` and `RtkSharp.Filters.Tests` projects

**Files:**
- Create: `RtkSharp.Filters/RtkSharp.Filters.csproj`
- Create: `RtkSharp.Filters/AssemblyInfo.cs`
- Create: `RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj`
- Modify: `RtkSharp.slnx`

**Interfaces:**
- Produces: an empty, buildable `RtkSharp.Filters` class library and
  `RtkSharp.Filters.Tests` test project, ready for later tasks.

- [ ] **Step 1: Create the class library project**

```xml
<!-- RtkSharp.Filters/RtkSharp.Filters.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackageId>RtkSharp.Filters</PackageId>
    <AssemblyName>RtkSharp.Filters</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="RtkSharp.Filters.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add a placeholder file so the project has something to compile**

```csharp
// RtkSharp.Filters/AssemblyInfo.cs
// Intentionally empty except for the InternalsVisibleTo wiring in the
// .csproj above; exists only so the initial empty project builds cleanly
// before later tasks populate it.
```

- [ ] **Step 3: Create the test project**

```xml
<!-- RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RtkSharp.Filters\RtkSharp.Filters.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Add both projects to the solution**

```xml
<!-- RtkSharp.slnx -->
<Solution>
  <Project Path="RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj" />
  <Project Path="RtkSharp.Filters/RtkSharp.Filters.csproj" />
  <Project Path="RtkSharp.ParityTests/RtkSharp.ParityTests.csproj" />
  <Project Path="RtkSharp.Tests/RtkSharp.Tests.csproj" />
  <Project Path="RtkSharp/RtkSharp.csproj" />
</Solution>
```

- [ ] **Step 5: Verify the solution restores and builds, then add the `ProjectReference` from `RtkSharp` to `RtkSharp.Filters`**

Run: `dotnet build RtkSharp.slnx`
Expected: Build succeeds, 5 projects, no errors.

Edit `RtkSharp/RtkSharp.csproj`, add:

```xml
<ItemGroup>
  <ProjectReference Include="..\RtkSharp.Filters\RtkSharp.Filters.csproj" />
</ItemGroup>
```

Run: `dotnet build RtkSharp.slnx` again.
Expected: still succeeds (nothing consumes `RtkSharp.Filters` yet, but the
reference resolves).

- [ ] **Step 6: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp.slnx RtkSharp/RtkSharp.csproj
git commit -m "chore: scaffold empty RtkSharp.Filters and RtkSharp.Filters.Tests projects"
```

---

### Task 2: Resolve the `RtkSharp.Filters` namespace collision

**Files:**
- Modify: `RtkSharp/Filters/TomlFilterEngine.cs`
- Modify: `RtkSharp/Filters/TomlFilterRegistry.cs`
- Modify: `RtkSharp.Tests/Filters/TomlFilterEngineTests.cs`
- Modify: `RtkSharp.Tests/Filters/TomlFilterRegistryTests.cs`
- Modify: `RtkSharp.Tests/Filters/RunFilterTestsTests.cs`

**Interfaces:**
- Produces: `TomlFilterEngine`/`TomlFilterRegistry` now live in
  `namespace RtkSharp.Filters.Toml`, freeing the bare `RtkSharp.Filters`
  namespace for the new library's public `RtkFilters` facade (Task 16).

This does **not** move these files into the new `RtkSharp.Filters` project
— they stay exactly where they are in `RtkSharp/Filters/`; only their
namespace changes, to avoid colliding with the new project/namespace of the
same name.

- [ ] **Step 1: Rename the namespace declaration in both source files**

```csharp
// RtkSharp/Filters/TomlFilterEngine.cs — change line 14
namespace RtkSharp.Filters.Toml;
```

```csharp
// RtkSharp/Filters/TomlFilterRegistry.cs — change line 7
namespace RtkSharp.Filters.Toml;
```

- [ ] **Step 2: Update every consumer's `using` directive**

Run: `grep -rl "using RtkSharp.Filters;" RtkSharp RtkSharp.Tests --include="*.cs"`

For each file returned that references `TomlFilterEngine` or
`TomlFilterRegistry`, change `using RtkSharp.Filters;` to
`using RtkSharp.Filters.Toml;`.

- [ ] **Step 3: Update the three test files**

```csharp
// RtkSharp.Tests/Filters/TomlFilterEngineTests.cs — top of file
using RtkSharp.Filters.Toml;
```

```csharp
// RtkSharp.Tests/Filters/TomlFilterRegistryTests.cs — top of file
using RtkSharp.Filters.Toml;
```

```csharp
// RtkSharp.Tests/Filters/RunFilterTestsTests.cs — top of file
using RtkSharp.Filters.Toml;
```

- [ ] **Step 4: Build and run the full existing test suite**

Run: `dotnet build RtkSharp.slnx`
Expected: Build succeeds, no `CS0246` namespace-not-found errors.

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release`
Expected: `Passed! - Failed: 0, Passed: 3357, Skipped: 1, Total: 3358`
(pure rename, no behavior change).

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Filters RtkSharp.Tests/Filters
git commit -m "refactor: rename TomlFilterEngine/Registry namespace to RtkSharp.Filters.Toml"
```

---

### Task 3: Move `Rewrite/ShellLexer.cs`, `Ast/**`, and `Parser/**`

**Files:**
- Move: `RtkSharp/Rewrite/ShellLexer.cs` → `RtkSharp.Filters/Rewrite/ShellLexer.cs`
- Move: `RtkSharp.Tests/Rewrite/ShellLexerTests.cs` → `RtkSharp.Filters.Tests/Rewrite/ShellLexerTests.cs`
- Move: `RtkSharp/Ast/*.cs` (14 files) → `RtkSharp.Filters/Ast/`
- Move: `RtkSharp/Parser/*.cs` (4 files) → `RtkSharp.Filters/Parser/`
- Move: `RtkSharp.Tests/Ast/*.cs` (11 files) → `RtkSharp.Filters.Tests/Ast/`
- Move: `RtkSharp.Tests/Parser/*.cs` (4 files) → `RtkSharp.Filters.Tests/Parser/`
- Modify: `RtkSharp.Filters/RtkSharp.Filters.csproj` (add package refs)
- Modify: `RtkSharp/RtkSharp.csproj` (remove package refs that moved)

**Interfaces:**
- Produces: `RtkSharp.Rewrite.ShellLexer` (used by `TryParseSingleCommand`,
  Task 17), `RtkSharp.Ast.*` (used by `ReadCommand`'s pure `Render` method,
  Task 15), `RtkSharp.Parser.*` (used by a few `Js/` filters, Task 6).

- [ ] **Step 1: Move the source directories**

```bash
mkdir -p RtkSharp.Filters/Rewrite
git mv RtkSharp/Rewrite/ShellLexer.cs RtkSharp.Filters/Rewrite/ShellLexer.cs
git mv RtkSharp/Ast RtkSharp.Filters/Ast
git mv RtkSharp/Parser RtkSharp.Filters/Parser
```

- [ ] **Step 2: Move the corresponding test files**

```bash
mkdir -p RtkSharp.Filters.Tests/Rewrite
git mv RtkSharp.Tests/Rewrite/ShellLexerTests.cs RtkSharp.Filters.Tests/Rewrite/ShellLexerTests.cs
git mv RtkSharp.Tests/Ast RtkSharp.Filters.Tests/Ast
git mv RtkSharp.Tests/Parser RtkSharp.Filters.Tests/Parser
```

- [ ] **Step 3: Move the Roslyn/TreeSitter/Acornima package references**

Edit `RtkSharp.Filters/RtkSharp.Filters.csproj`, add to the `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
<PackageReference Include="RtkSharp.TreeSitter.Trimmed" Version="1.3.0-rtksharp.1" />
<PackageReference Include="Acornima" Version="1.6.2" />
```

Remove the same three `<PackageReference>` lines from `RtkSharp/RtkSharp.csproj`
(available transitively via the `ProjectReference` from Task 1).

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx`
Expected: Build succeeds. Fix any `using RtkSharp.Ast;`/`using RtkSharp.Parser;`
compile errors in `RtkSharp/Commands/**` files — namespaces are unchanged,
so these should resolve via the `ProjectReference` without edits, but
verify.

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release`
Expected: All moved Ast/Parser/ShellLexer tests pass, 0 failed.

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release`
Expected: `Passed! - Failed: 0` at a reduced total (moved tests now run
under `RtkSharp.Filters.Tests`).

- [ ] **Step 5: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/RtkSharp.csproj
git commit -m "refactor: move ShellLexer, Ast/**, Parser/** to RtkSharp.Filters"
```

---

### Task 4: Extract Git filters (`git`, `diff`, `gt`, `glab`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Git/GitFilters.cs`,
  `DiffFilters.cs`, `GtFilters.cs`, `GlabFilters.cs`
- Modify: `RtkSharp/Commands/Git/GitCommand.cs`, `DiffCommand.cs`,
  `GtCommand.cs`, `GlabCommand.cs` (remove the moved methods, add a
  `using RtkSharp.Filters.Commands.Git;` and update call sites to the
  unqualified method names)
- Create: `RtkSharp.Filters.Tests/Commands/Git/GitFiltersTests.cs`,
  `DiffFiltersTests.cs`, `GtFiltersTests.cs`, `GlabFiltersTests.cs`

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Git.GitFilters.{FilterLogOutput,
  TruncateLine, CompactDiff, FormatStatusOutput,
  FormatStatusOutputDetached, FilterStatusWithArgs, FormatAddSummary,
  ParseCommitOutput, FilterPushOutput, FormatPullSummary,
  FilterBranchOutput, FormatFetchSummary, FormatStashMessage,
  FilterStashList, FilterWorktreeList}` (all `public static`, same
  signatures as today, just `internal` → `public` and relocated);
  `DiffFilters.{RenderFileDiff, FormatDiffChanges, ComputeDiff, Similarity,
  CondenseUnifiedDiff}`; `GtFilters.{FilterGtLogEntries, FilterGtSubmit,
  FilterGtSync, FilterGtRestack, FilterGtCreate, FilterIdentity}`;
  `GlabFilters.{FormatMrList, FormatMrView, FormatIssueList,
  FormatIssueView, FormatCiList, FormatCiStatus, FilterCiTrace,
  FormatReleaseList, FilterReleaseView, FilterMarkdownBody}`.
  Consumed by `FilterRegistry` in Task 16.

- [ ] **Step 1: For each of the four files, cut the listed methods into a new file**

Read `RtkSharp/Commands/Git/GitCommand.cs`, find each method named in the
Global Constraints verdict table above, and cut-and-paste it (unchanged
body) into a new file:

```csharp
// RtkSharp.Filters/Commands/Git/GitFilters.cs
namespace RtkSharp.Filters.Commands.Git;

public static class GitFilters
{
    // paste FilterLogOutput, TruncateLine, CompactDiff, FormatStatusOutput,
    // FormatStatusOutputDetached, FilterStatusWithArgs, FormatAddSummary,
    // ParseCommitOutput, FilterPushOutput, FormatPullSummary,
    // FilterBranchOutput, FormatFetchSummary, FormatStashMessage,
    // FilterStashList, FilterWorktreeList here verbatim, changing each
    // method's access modifier to `public static` if it was `internal
    // static`. Keep the exact signatures. If a method calls another method
    // being moved in this same step, no change needed (both now live in
    // GitFilters). If a method calls something that ISN'T moving (e.g. a
    // Core/ helper), add the appropriate `using` at the top of this file.
}
```

Repeat the same pattern for `DiffFilters.cs` (from `DiffCommand.cs`),
`GtFilters.cs` (from `GtCommand.cs`), and `GlabFilters.cs` (from
`GlabCommand.cs`), using the method lists from the verdict table.

- [ ] **Step 2: Update the original files' call sites**

In each of `GitCommand.cs`/`DiffCommand.cs`/`GtCommand.cs`/`GlabCommand.cs`,
add `using RtkSharp.Filters.Commands.Git;` at the top, and update every
call site that referenced a now-moved method by its old unqualified name —
these should resolve automatically via the `using` directive as long as no
other type in scope has a colliding member name; if the compiler reports an
ambiguity, qualify the call as `GitFilters.MethodName(...)` (or
`DiffFilters`/`GtFilters`/`GlabFilters` as appropriate) instead.

- [ ] **Step 3: Write regression tests comparing old vs. new behavior**

For each moved method, write a test that captures its current behavior via
a realistic fixture (reuse an existing fixture from
`RtkSharp.Tests/Commands/Git/*` test data if one exists for that method;
otherwise construct a minimal realistic input inline):

```csharp
// RtkSharp.Filters.Tests/Commands/Git/GitFiltersTests.cs
using RtkSharp.Filters.Commands.Git;

namespace RtkSharp.Filters.Tests.Commands.Git;

public class GitFiltersTests
{
    [Fact]
    public void FilterLogOutput_CondensesCommitLines()
    {
        var raw = "commit abc123\nAuthor: Test\nDate: ...\n\n    message\n";

        var filtered = GitFilters.FilterLogOutput(raw);

        Assert.Contains("abc123", filtered);
    }

    // Repeat for each of the other 13 GitFilters methods with a realistic
    // fixture per method — check the pre-move test file at
    // RtkSharp.Tests/Commands/GitCommandTests.cs first for existing
    // fixtures/expected outputs to reuse verbatim rather than inventing new
    // ones, so this is a genuine regression test against known-good output.
}
```

Repeat for `DiffFiltersTests.cs`, `GtFiltersTests.cs`, `GlabFiltersTests.cs`
— one test per moved method, using existing fixtures from
`RtkSharp.Tests/Commands/Git/DiffCommandTests.cs`,
`RtkSharp.Tests/Commands/Git/GtCommandTests.cs`,
`RtkSharp.Tests/Commands/Git/GlabCommandTests.cs` where available.

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: all new Git filter tests pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0` — the original `GitCommandTests.cs`/`DiffCommandTests.cs`/`GtCommandTests.cs`/`GlabCommandTests.cs` still pass unchanged, proving the extraction didn't alter CLI behavior.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Git
git commit -m "feat(filters): extract git/diff/gt/glab filter methods to RtkSharp.Filters"
```

---

### Task 5: Extract Gh filters

**Files:**
- Create: `RtkSharp.Filters/Commands/Gh/GhFilters.cs`
- Modify: `RtkSharp/Commands/Gh/GhCommand.cs`
- Create: `RtkSharp.Filters.Tests/Commands/Gh/GhFiltersTests.cs`

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Gh.GhFilters.{FormatPrList,
  FormatPrView, FormatPrChecks, FormatPrStatus, FormatPrStatusEntry,
  FormatIssueList, FormatIssueView, FormatRunList, FormatRunView,
  FormatRepoView, FilterMarkdownBody}`.

- [ ] **Step 1: Extract the methods (same pattern as Task 4 Step 1)**

Cut the 11 methods listed above from `GhCommand.cs` into
`RtkSharp.Filters/Commands/Gh/GhFilters.cs` (namespace
`RtkSharp.Filters.Commands.Gh`), changing visibility to `public static`.

- [ ] **Step 2: Update `GhCommand.cs`'s call sites (same pattern as Task 4 Step 2)**

- [ ] **Step 3: Write regression tests (same pattern as Task 4 Step 3)**

Reuse fixtures from `RtkSharp.Tests/Commands/GhCommandTests.cs` — the
`tests/parity/fixtures/gh-commands/*.json` fixtures already in this repo
(`gh_issue_list.json`, `gh_issue_view_2795.json`, `gh_pr_list.json`,
`gh_pr_view_2775.json`) are real captured `gh` output and make good test
inputs for `FormatIssueList`/`FormatIssueView`/`FormatPrList`/`FormatPrView`.

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: `GhFiltersTests` passes.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Gh
git commit -m "feat(filters): extract gh filter methods to RtkSharp.Filters"
```

---

### Task 6: Extract Dotnet filters

**Files:**
- Create: `RtkSharp.Filters/Commands/Dotnet/DotnetFilters.cs`
- Modify: `RtkSharp/Commands/Dotnet/DotnetCommand.cs`,
  `DotnetFormatReport.cs`, `DotnetTrx.cs`
- Create: `RtkSharp.Filters.Tests/Commands/Dotnet/DotnetFiltersTests.cs`

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Dotnet.DotnetFilters.{FilterBuild,
  FilterRestore, FormatTestOutput, FormatDotnetFormatOutput,
  FilterFileBasedApp, ParseBuildFromText, ParseRestoreFromText,
  ParseTestFromText, ParseTrxContent, Summarize}` — the last two come from
  `DotnetTrx.cs` and `DotnetFormatReport.cs` respectively.

- [ ] **Step 1: Extract the already-pure `DotnetCommand.cs` methods**

Cut `FilterBuild(string,bool)`, `FilterRestore(string,bool)`,
`FormatTestOutput(TestSummary,...)`, `FormatDotnetFormatOutput`,
`FilterFileBasedApp`, `ParseBuildFromText`, `ParseRestoreFromText`,
`ParseTestFromText` from `DotnetCommand.cs` into
`RtkSharp.Filters/Commands/Dotnet/DotnetFilters.cs` (namespace
`RtkSharp.Filters.Commands.Dotnet`), `public static`.

- [ ] **Step 2: Extract `DotnetTrx.cs`'s already-pure method**

Cut `public static TestSummary? ParseTrxContent(string content)` into the
same `DotnetFilters.cs` file. `DotnetTrx.cs`'s other members (if any
remain) stay behind in `RtkSharp/Commands/Dotnet/DotnetTrx.cs`; if
`ParseTrxContent` was the file's only member, delete the now-empty
`DotnetTrx.cs`.

- [ ] **Step 3: Split `DotnetFormatReport.cs`'s fused method**

Read `RtkSharp/Commands/Dotnet/DotnetFormatReport.cs`'s
`ParseFormatReport(string path)`. It currently does `File.OpenRead(path)` +
JSON deserialization + summarization in one method. Split it:

- Leave a `ParseFormatReport(string path)` behind in
  `RtkSharp/Commands/Dotnet/DotnetFormatReport.cs` that does the file I/O
  and JSON deserialization, then calls the new pure method below.
- Add to `DotnetFilters.cs`:

```csharp
// Add to RtkSharp.Filters/Commands/Dotnet/DotnetFilters.cs
public static string Summarize(List<FormatReportEntryDto> entries)
{
    // Move the summarization logic (whatever ParseFormatReport currently
    // does with `entries` after deserializing) here verbatim. `FormatReportEntryDto`
    // must also move to DotnetFilters.cs (or a new DotnetFormatReportModels.cs
    // in the same folder) if it isn't already a shared/public type — check
    // its current accessibility and namespace before moving.
}
```

- [ ] **Step 4: Update `DotnetCommand.cs`'s call sites**

Add `using RtkSharp.Filters.Commands.Dotnet;`, update call sites for the
moved methods (same pattern as Task 4 Step 2).

- [ ] **Step 5: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/DotnetCommandTests.cs`,
`DotnetFormatTests.cs`, `DotnetTrxTests.cs`. One test per moved method.

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: `DotnetFiltersTests` passes.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Dotnet
git commit -m "feat(filters): extract dotnet filter methods to RtkSharp.Filters"
```

---

### Task 7: Extract Js filters (`npm`, `pnpm`, `tsc`, `vitest`/`jest`, `playwright`, `prisma`, `lint`, `next`, `prettier`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Js/NpmFilters.cs`,
  `PnpmFilters.cs`, `TscFilters.cs`, `VitestFilters.cs`,
  `PlaywrightFilters.cs`, `PrismaFilters.cs`, `LintFilters.cs`,
  `NextFilters.cs`, `PrettierFilters.cs`
- Modify: `RtkSharp/Commands/Js/*.cs` (all 9 files)
- Create: `RtkSharp.Filters.Tests/Commands/Js/*FiltersTests.cs` (9 files)

**Interfaces:**
- Produces (already-pure, straightforward moves): `NpmFilters.FilterNpmOutput`,
  `TscFilters.FilterTscOutput`, `PrismaFilters.{FilterPrismaGenerate,
  FilterMigrateDev, FilterMigrateStatus, FilterMigrateDeploy, FilterDbPush}`,
  `LintFilters.{FilterEslintJson, FilterPylintJson, FilterGenericLint}`,
  `NextFilters.FilterNextBuild`, `PrettierFilters.FilterPrettierOutput`.
- Produces (needs-extraction, see steps below): `PnpmFilters.{FilterPnpmInstall,
  FormatPnpmList, FormatPnpmOutdated}`, `VitestFilters.{RenderTestOutputWithHints,
  <new formatting method extracted from FormatTestOutput>}`,
  `PlaywrightFilters.{RenderOutput, <new methods extracted from
  LogFullAndFormat/LogDegradedAndFormat>}`.

- [ ] **Step 1: Extract the six already-pure modules**

Following Task 4 Step 1's pattern, cut each already-pure method group into
its own `*Filters.cs` file under `RtkSharp.Filters/Commands/Js/`:
`NpmFilters.cs` (`FilterNpmOutput`), `TscFilters.cs` (`FilterTscOutput`),
`PrismaFilters.cs` (5 methods), `LintFilters.cs` (3 methods),
`NextFilters.cs` (`FilterNextBuild`), `PrettierFilters.cs`
(`FilterPrettierOutput`). Namespace `RtkSharp.Filters.Commands.Js` for all.

- [ ] **Step 2: Extract `PnpmCommand.cs`'s install filter and split its list/outdated formatting**

Cut `FilterPnpmInstall(string)` into `PnpmFilters.cs` as-is. For
`LogAndFormatList`/`LogAndFormatOutdated`, read both methods in
`PnpmCommand.cs`: each currently mixes a `Console.Error.Write` diagnostic
call with output formatting. Extract just the formatting half into two new
pure methods in `PnpmFilters.cs`:

```csharp
// Add to RtkSharp.Filters/Commands/Js/PnpmFilters.cs
public static string FormatPnpmList(string rawOutput /* + whatever other params LogAndFormatList's formatting half currently uses */)
{
    // Move the formatting logic from LogAndFormatList here, excluding the
    // Console.Error.Write call, which stays in PnpmCommand.cs's
    // LogAndFormatList (which now calls FormatPnpmList and then does its
    // own diagnostic write with the result).
}

public static string FormatPnpmOutdated(string rawOutput /* ditto */)
{
    // Same pattern for LogAndFormatOutdated.
}
```

- [ ] **Step 3: Split `VitestCommand.cs`'s `FormatTestOutput`**

`RenderTestOutputWithHints(...)` is already pure — cut it into
`VitestFilters.cs` as-is. For `FormatTestOutput(...)`: read the method,
separate the Console-writing diagnostic calls from the pure formatting
logic, and extract the pure part into a new method in `VitestFilters.cs`
(name it based on what it actually formats, e.g. `FormatVitestSummary` —
determine the exact name from what the extracted logic does once you've
read it). `VitestCommand.cs`'s `FormatTestOutput` keeps the Console calls
and delegates to the new pure method for the formatting.

- [ ] **Step 4: Split `PlaywrightCommand.cs`'s `LogFullAndFormat`/`LogDegradedAndFormat`**

`RenderOutput(...)` is already pure — cut it into `PlaywrightFilters.cs` as-is.
For `LogFullAndFormat`/`LogDegradedAndFormat`: read both, extract their
formatting logic (not the `Log*` Console calls) into two new pure methods
in `PlaywrightFilters.cs` (name them based on what they format, e.g.
`FormatFullResults`/`FormatDegradedResults` — confirm exact names once
read). `PlaywrightCommand.cs` keeps the `Log*` wrapper methods, which now
call the new pure methods and then write the Console diagnostic.

- [ ] **Step 5: Update all 9 files' call sites**

Add `using RtkSharp.Filters.Commands.Js;` to each of the 9
`RtkSharp/Commands/Js/*.cs` files, update call sites for moved methods.

- [ ] **Step 6: Write regression tests**

One test file per `*Filters.cs`, reusing fixtures from the corresponding
`RtkSharp.Tests/Commands/Js/*CommandTests.cs` file.

- [ ] **Step 7: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: all 9 new test files pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Js
git commit -m "feat(filters): extract npm/pnpm/tsc/vitest/playwright/prisma/lint/next/prettier filters to RtkSharp.Filters"
```

---

### Task 8: Extract Rust filters (`cargo`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Rust/CargoFilters.cs`
- Modify: `RtkSharp/Commands/Rust/CargoBuildTestHandlers.cs`,
  `CargoNonStreamingFilters.cs`
- Create: `RtkSharp.Filters.Tests/Commands/Rust/CargoFiltersTests.cs`

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Rust.CargoFilters.{FilterCargoBuild,
  FilterCargoTest, FilterCargoInstall, FilterCargoClippy,
  FilterCargoNextest, FormatCrateInfo}`.

- [ ] **Step 1: Extract from both source files**

Cut `CargoBuildTestFilters.FilterCargoBuild(string)`/`FilterCargoTest(string)`
(currently a nested static class in `CargoBuildTestHandlers.cs`) and
`FilterCargoInstall`/`FilterCargoClippy`/`FilterCargoNextest`/`FormatCrateInfo`
(from `CargoNonStreamingFilters.cs`) into one file,
`RtkSharp.Filters/Commands/Rust/CargoFilters.cs`, flattening the nested
class into top-level `public static` methods on `CargoFilters`.

- [ ] **Step 2: Update `CargoCommand.cs`'s call sites**

`CargoCommand.cs` is the dispatcher calling into both source files — add
`using RtkSharp.Filters.Commands.Rust;`, update call sites (including any
that referenced the old `CargoBuildTestFilters.` prefix — those become
`CargoFilters.` now that the nested class is flattened).

- [ ] **Step 3: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/Rust/CargoCommandTests.cs`.

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: `CargoFiltersTests` passes.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Rust
git commit -m "feat(filters): extract cargo filter methods to RtkSharp.Filters"
```

---

### Task 9: Extract Go filters (`go`, `golangci-lint`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Go/GoFilters.cs`
- Modify: `RtkSharp/Commands/Go/GolangciLintCommand.cs`, `GoFilters.cs`
  (the original, staying at `RtkSharp/Commands/Go/GoFilters.cs` until this
  task moves its contents)
- Create: `RtkSharp.Filters.Tests/Commands/Go/GoFiltersTests.cs`

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Go.GoFilters.{FilterGolangciJson,
  FilterGoTestJson, FilterGoBuild, FilterGoBuildWithExit, FilterGoVet,
  CompactPackageName}`.

- [ ] **Step 1: Move `RtkSharp/Commands/Go/GoFilters.cs`'s contents wholesale**

This file is already a pure filter layer. Move its entire contents
(`FilterGoTestJson`, `FilterGoBuild`, `FilterGoBuildWithExit`,
`FilterGoVet`, `CompactPackageName`) into
`RtkSharp.Filters/Commands/Go/GoFilters.cs`, namespace
`RtkSharp.Filters.Commands.Go`, `public static`.

- [ ] **Step 2: Add `GolangciLintCommand.cs`'s pure method**

Cut `FilterGolangciJson(string,uint)` from `GolangciLintCommand.cs` into the
same `RtkSharp.Filters/Commands/Go/GoFilters.cs` file.

- [ ] **Step 3: Update `GoCommand.cs`/`GolangciLintCommand.cs`'s call sites**

Add `using RtkSharp.Filters.Commands.Go;`, update call sites. Delete the
now-empty `RtkSharp/Commands/Go/GoFilters.cs` (its contents fully moved in
Step 1).

- [ ] **Step 4: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/Go/GoCommandTests.cs`,
`GolangciLintCommandTests.cs`.

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: `GoFiltersTests` passes.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Go
git commit -m "feat(filters): extract go/golangci-lint filter methods to RtkSharp.Filters"
```

---

### Task 10: Extract Python filters (`ruff`, `pytest`, `mypy`, `pip`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Python/RuffFilters.cs`,
  `PytestFilters.cs`, `MypyFilters.cs`, `PipFilters.cs`
- Modify: `RtkSharp/Commands/Python/RuffCommand.cs`, `RuffFilters.cs`,
  `PytestFilters.cs`, `MypyCommand.cs`, `MypyFilters.cs`, `PipCommand.cs`,
  `PipFilters.cs` (the originals, until this task moves their contents)
- Create: `RtkSharp.Filters.Tests/Commands/Python/*FiltersTests.cs` (4 files)

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Python.RuffFilters.{FilterRuffCheckJson,
  FilterRuffFormat, FilterRuffOutput}`, `PytestFilters.FilterPytestOutput`,
  `MypyFilters.FilterMypyOutput`, `PipFilters.{FilterPipList,
  FilterPipOutdated}`.

- [ ] **Step 1: Move the three already-pure `*Filters.cs` files' contents wholesale**

`RtkSharp/Commands/Python/RuffFilters.cs`, `PytestFilters.cs`,
`MypyFilters.cs`, `PipFilters.cs` are already pure filter layers — move
each one's full contents into a same-named file under
`RtkSharp.Filters/Commands/Python/`, namespace
`RtkSharp.Filters.Commands.Python`, `public static`.

- [ ] **Step 2: Add the named `FilterRuffOutput` method for `RuffCommand.cs`'s inline dispatch lambda**

Read `RuffCommand.cs`'s mode-selection anonymous lambda (dispatches between
check/format modes). Add a new named method to
`RtkSharp.Filters/Commands/Python/RuffFilters.cs`:

```csharp
public static string FilterRuffOutput(string stdout, bool isCheck, bool isFormat)
{
    // Move the lambda's dispatch logic here — it should just be calling
    // FilterRuffCheckJson or FilterRuffFormat based on isCheck/isFormat,
    // both already in this file from Step 1.
}
```

`RuffCommand.cs`'s call site changes from the inline lambda to calling
`RuffFilters.FilterRuffOutput(...)` directly.

- [ ] **Step 3: Update `MypyCommand.cs`'s and `PipCommand.cs`'s call sites**

`MypyCommand.cs`'s inline lambda `raw => MypyFilters.FilterMypyOutput(Utils.StripAnsi(raw))`
needs no new method — just update it to reference the moved
`RtkSharp.Filters.Commands.Python.MypyFilters.FilterMypyOutput` (add the
`using`). `PipCommand.cs`'s `RunListAsync`/`RunOutdatedAsync` similarly
just need their existing calls to `FilterPipList`/`FilterPipOutdated`
retargeted to the moved location via `using`.

- [ ] **Step 4: Delete the now-empty original `*Filters.cs` files**

`RtkSharp/Commands/Python/RuffFilters.cs`, `PytestFilters.cs`,
`MypyFilters.cs`, `PipFilters.cs` should be empty (or nearly empty) after
Step 1 — delete them.

- [ ] **Step 5: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/Python/RuffCommandTests.cs`,
`PytestCommandTests.cs`, `MypyCommandTests.cs`, `PipCommandTests.cs`.

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: all 4 new test files pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Python
git commit -m "feat(filters): extract ruff/pytest/mypy/pip filter methods to RtkSharp.Filters"
```

---

### Task 11: Extract Ruby filters (`rake`, `rubocop`, `rspec`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Ruby/RakeFilters.cs`,
  `RubocopFilters.cs`, `RspecFilters.cs`
- Modify: `RtkSharp/Commands/Ruby/RakeCommand.cs`, `RubocopFilters.cs`,
  `RspecFilters.cs` (originals, until moved)
- Create: `RtkSharp.Filters.Tests/Commands/Ruby/*FiltersTests.cs` (3 files)

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Ruby.RakeFilters.FilterMinitestOutput`,
  `RubocopFilters.{FilterRubocopJson, FilterRubocopText}`,
  `RspecFilters.{StripNoise, FilterRspecOutput, FilterRspecText}`.

- [ ] **Step 1: Extract `RakeCommand.cs`'s pure method**

Cut `FilterMinitestOutput(string)` into
`RtkSharp.Filters/Commands/Ruby/RakeFilters.cs`, namespace
`RtkSharp.Filters.Commands.Ruby`, `public static`.

- [ ] **Step 2: Move `RubocopFilters.cs` and `RspecFilters.cs` wholesale**

Both are already pure filter layers. Move each file's full contents into
same-named files under `RtkSharp.Filters/Commands/Ruby/`. Note:
`FilterRubocopJson` and `FilterRspecOutput`'s parse-failure diagnostic
`Console.Error.Write` calls move as-is (disclosed, acceptable per the
verdict table — do not remove them).

- [ ] **Step 3: Update `RakeCommand.cs`/`RubocopCommand.cs`/`RspecCommand.cs`'s call sites, delete emptied originals**

- [ ] **Step 4: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/Ruby/RakeCommandTests.cs`,
`RubocopCommandTests.cs`, `RspecCommandTests.cs`.

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: all 3 new test files pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Ruby
git commit -m "feat(filters): extract rake/rubocop/rspec filter methods to RtkSharp.Filters"
```

---

### Task 12: Extract Jvm filters (`gradlew`, `mvn`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Jvm/GradlewFilters.cs`,
  `MvnFilters.cs`, `MvnSharedFilters.cs`
- Modify: `RtkSharp/Commands/Jvm/GradlewFilters.cs`,
  `MvnSurefireFilter.cs`, `MvnCompileQuietFilters.cs`,
  `MvnSharedFilters.cs` (originals, until moved)
- Create: `RtkSharp.Filters.Tests/Commands/Jvm/GradlewFiltersTests.cs`,
  `MvnFiltersTests.cs`

**Interfaces:**
- Produces: `RtkSharp.Filters.Commands.Jvm.GradlewFilters.{FilterBuildLine,
  FilterTest, FilterConnected, FilterLint, FilterDependencies}`;
  `MvnFilters.{FilterSurefire, FilterSurefireWithCap, FilterPackage,
  FilterPackageWithCap, FilterCompile, FilterQuiet}`;
  `MvnSharedFilters.{IsFrameworkFrame, SurefireBlock, FailuresSummaryCap, ...}`
  (moved as internal support helpers `MvnFilters.cs` and the other Mvn
  files depend on — expose as `internal` within `RtkSharp.Filters` unless
  a test needs direct access, in which case `public`).

- [ ] **Step 1: Move `GradlewFilters.cs` wholesale**

Already a pure filter layer — move its full contents into
`RtkSharp.Filters/Commands/Jvm/GradlewFilters.cs`.

- [ ] **Step 2: Merge `MvnSurefireFilter.cs` and `MvnCompileQuietFilters.cs` into one `MvnFilters.cs`**

Both are already pure. Combine their methods
(`FilterSurefire`/`FilterSurefireWithCap`/`FilterPackage`/`FilterPackageWithCap`
from the former, `FilterCompile`/`FilterQuiet` from the latter) into one
new file, `RtkSharp.Filters/Commands/Jvm/MvnFilters.cs`, as a single
`public static class MvnFilters`.

- [ ] **Step 3: Move `MvnSharedFilters.cs` wholesale**

Move its full contents (`IsFrameworkFrame`, `SurefireBlock`,
`FailuresSummaryCap`, and other predicates/helpers) into
`RtkSharp.Filters/Commands/Jvm/MvnSharedFilters.cs` — `MvnFilters.cs`
depends on these, so this must land in the same project.

- [ ] **Step 4: Update `GradlewCommand.cs`/`MvnCommand.cs`'s call sites, delete emptied originals**

- [ ] **Step 5: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/Jvm/GradlewCommandTests.cs`,
`MvnCommandTests.cs`, `MvnSurefireFilterTests.cs`,
`MvnCompileQuietFilterTests.cs`, `JvmFixtures.cs` (shared fixture data —
move or reference as needed).

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: `GradlewFiltersTests`/`MvnFiltersTests` pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Jvm
git commit -m "feat(filters): extract gradlew/mvn filter methods to RtkSharp.Filters"
```

---

### Task 13: Extract Cloud filters (`aws`, `az`, `docker`, `kubectl`/`oc`, `curl`, `wget`, `psql`)

**Files:**
- Create: `RtkSharp.Filters/Commands/Cloud/AwsFilters.cs`,
  `AzFilters.cs`, `DockerFilters.cs`, `ContainerFilters.cs`,
  `CurlFilters.cs`, `WgetFilters.cs`, `PsqlFilters.cs`
- Modify: `RtkSharp/Commands/Cloud/*.cs` (all files listed in the verdict
  table's Cloud rows)
- Create: `RtkSharp.Filters.Tests/Commands/Cloud/*FiltersTests.cs` (7 files)

**Interfaces:**
- Produces (already-pure, straightforward moves): `AwsFilters` (~25
  methods), `AzFilters` (~15 methods + `Redact`), `PsqlFilters.{FilterPsqlOutput,
  IsTableFormat, IsExpandedFormat, FilterTable, FilterExpanded}`,
  `CurlFilters.FilterCurlOutput`, `ContainerFilters.{FormatKubectlPods,
  FormatKubectlServices}` (JSON-typed).
- Produces (needs-extraction): `DockerFilters.{FormatContainerLine,
  FormatContainerLineFromParts, CompactPorts, FormatComposePs,
  FormatComposeLogs, FormatComposeBuild, FormatPsSummary,
  FormatImagesSummary}`, `WgetFilters.{FormatSize, CompactUrl, ParseError,
  TruncateLine, FormatWgetOutput}`.

- [ ] **Step 1: Move the four already-pure files wholesale**

`AwsFilters.cs`, `AzFilters.cs`, `ContainerFilters.cs` move their full
contents into same-named files under
`RtkSharp.Filters/Commands/Cloud/`. `PsqlCommand.cs`'s 5 methods
(`FilterPsqlOutput`, `IsTableFormat`, `IsExpandedFormat`, `FilterTable`,
`FilterExpanded`) get cut into a new `PsqlFilters.cs`. `CurlCommand.cs`'s
`FilterCurlOutput(string,bool)` gets cut into a new `CurlFilters.cs` —
move as-is including its internal `Tee.ForceTeeHint` call (disclosed side
effect, don't remove).

- [ ] **Step 2: Extract `DockerCommand.cs`'s pure methods and split its impure summary-building**

Cut `FormatContainerLine`, `FormatContainerLineFromParts`, `CompactPorts`,
`FormatComposePs`, `FormatComposeLogs`, `FormatComposeBuild` into
`RtkSharp.Filters/Commands/Cloud/DockerFilters.cs` as-is. Then read
`DockerPsAsync`/`RunImagesAsync` in `DockerCommand.cs`: each mixes
process-exec/Console-write with summary-building. Extract the
summary-building half into two new pure methods added to
`DockerFilters.cs` (name them based on what they actually build once
read — e.g. `FormatPsSummary`/`FormatImagesSummary`).

- [ ] **Step 3: Extract `WgetCommand.cs`'s pure sub-helpers and split its message assembly**

Cut `FormatSize`, `CompactUrl`, `ParseError`, `TruncateLine` into
`RtkSharp.Filters/Commands/Cloud/WgetFilters.cs` as-is (do **not** move
`GetFileSize`, which does real file-stat I/O — it stays in
`WgetCommand.cs`). Read `RunAsync`/`RunStdoutAsync`'s final message
assembly and extract it into a new `FormatWgetOutput(...)` pure method in
`WgetFilters.cs`.

- [ ] **Step 4: Update all 7 source files' call sites, delete emptied originals**

For `kubectl`/`oc`, note in `KubectlCommand.cs`/`OcCommand.cs`'s updated
call sites that `ContainerFilters.FormatKubectlPods`/`FormatKubectlServices`
still take `JsonElement`, not `string` — the string→`JsonElement` wrapper
belongs in `FilterRegistry` (Task 16), not here; leave these two methods'
signatures unchanged in this task.

- [ ] **Step 5: Write regression tests**

Reuse fixtures from `RtkSharp.Tests/Commands/Cloud/AwsCommandTests.cs`,
`AzCommandTests.cs`, `DockerCommandTests.cs`, `KubectlCommandTests.cs`,
`OcCommandTests.cs`, `CurlCommandTests.cs`, `WgetCommandTests.cs`,
`PsqlCommandTests.cs`.

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: all 7 new test files pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/Cloud
git commit -m "feat(filters): extract aws/az/docker/kubectl/oc/curl/wget/psql filter methods to RtkSharp.Filters"
```

---

### Task 14: Extract System filters (`ls`, `read`, `wc`, `tree`, `find`, `grep`, `pipe`, `test`, `env`, `format`, `json`, `deps`, `log`, `summary`)

**Files:**
- Create: `RtkSharp.Filters/Commands/System/LsFilters.cs`, `ReadFilters.cs`,
  `WcFilters.cs`, `TreeFilters.cs`, `FindFilters.cs`, `GrepFilters.cs`,
  `PipeFilters.cs`, `TestFilters.cs`, `EnvFilters.cs`, `FormatFilters.cs`,
  `JsonFilters.cs`, `DepsFilters.cs`, `LogFilters.cs`, `SummaryFilters.cs`
- Modify: `RtkSharp/Commands/System/*.cs` (all files listed in the verdict
  table's System rows, except `ErrCommand.cs`/`RunCommand.cs`/`ProxyCommand.cs`
  — not touched by this task)
- Create: `RtkSharp.Filters.Tests/Commands/System/*FiltersTests.cs` (14 files)

**Interfaces:**
- Produces (already-pure, straightforward moves): `LsFilters.FilterLs`,
  `ReadFilters.{Render, ApplyLineWindow, FormatWithLineNumbers, SmartTruncate}`,
  `WcFilters.FilterWcOutput`, `TreeFilters.FilterTreeOutput`,
  `GrepFilters.BuildGroupedOutput`, `PipeFilters.{GrepWrapper, FindWrapper,
  AutoDetectFilter}`, `TestFilters.ExtractTestSummary`,
  `FormatFilters.FilterBlackOutput`, `JsonFilters.{FilterJsonCompact,
  FilterJsonSchema}`, `DepsFilters.{SummarizeCargo, SummarizePackageJson,
  SummarizeRequirements, SummarizePyproject, SummarizeGoMod}`,
  `LogFilters.AnalyzeLogs`, `SummaryFilters.SummarizeOutput`.
- Produces (needs-extraction, atypical input shapes): `FindFilters.FormatFindResults(IReadOnlyList<string> paths, FindArgs args)`,
  `EnvFilters.FormatEnvReport(IReadOnlyDictionary<string,string?> env, ...)`.

- [ ] **Step 1: Move the 11 already-pure modules**

Following the established pattern, cut each already-pure method group
into its own `*Filters.cs` file under `RtkSharp.Filters/Commands/System/`:
`LsFilters.cs`, `ReadFilters.cs` (note `Render`'s embedded
`Console.Error.WriteLine` when `filePath` is supplied — move as-is,
disclosed caveat), `WcFilters.cs`, `TreeFilters.cs`, `GrepFilters.cs`,
`PipeFilters.cs`, `TestFilters.cs`, `FormatFilters.cs`, `JsonFilters.cs`,
`DepsFilters.cs`, `LogFilters.cs`, `SummaryFilters.cs`. Namespace
`RtkSharp.Filters.Commands.System` for all.

- [ ] **Step 2: Extract `FindCommand.cs`'s summary logic**

Read `Run(FindArgs, TextWriter)` in `FindCommand.cs`. Its grouping/summary
logic operates on a file-path list (from the caller's own directory walk),
not process stdout — extract that logic into:

```csharp
// RtkSharp.Filters/Commands/System/FindFilters.cs
namespace RtkSharp.Filters.Commands.System;

public static class FindFilters
{
    public static string FormatFindResults(IReadOnlyList<string> paths, FindArgs args)
    {
        // Move the grouping/summary logic from Run(FindArgs, TextWriter)
        // here. FindArgs must also move (or be shared) if it isn't already
        // a public, dependency-free type — check its current location and
        // accessibility before moving.
    }
}
```

`FindCommand.cs`'s `Run` method keeps doing the actual directory walk, then
calls `FindFilters.FormatFindResults(paths, args)` and writes the result to
its `TextWriter`.

- [ ] **Step 3: Extract `EnvCommand.cs`'s report-building logic**

Same pattern: extract the report-building logic from `Run(...)` (which
currently interleaves it with Console writes) into:

```csharp
// RtkSharp.Filters/Commands/System/EnvFilters.cs
namespace RtkSharp.Filters.Commands.System;

public static class EnvFilters
{
    public static string FormatEnvReport(IReadOnlyDictionary<string, string?> env /* + whatever other params Run's report-building currently uses */)
    {
        // Move the report-building logic from Run(...) here, excluding the
        // Console write calls.
    }
}
```

- [ ] **Step 4: Update all 14 source files' call sites, delete emptied originals**

- [ ] **Step 5: Write regression tests**

Reuse fixtures from the corresponding `RtkSharp.Tests/Commands/*.cs` and
`RtkSharp.Tests/Commands/System/*.cs` test files (note some, like
`FindCommandTests.cs`/`LsCommandTests.cs`, are loose at `Commands/` root
rather than under `Commands/System/` — check both locations).

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build RtkSharp.slnx` — Expected: succeeds.
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release` — Expected: all 14 new test files pass.
Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release` — Expected: `Passed! - Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add RtkSharp.Filters RtkSharp.Filters.Tests RtkSharp/Commands/System
git commit -m "feat(filters): extract system command filter methods to RtkSharp.Filters"
```

---

### Task 15: Implement the `RtkFilters` public API facade (`Filter` + `IsRegistered`)

**Files:**
- Create: `RtkSharp.Filters/FilterRegistry.cs`
- Create: `RtkSharp.Filters/RtkFilters.cs`
- Create: `RtkSharp.Filters.Tests/RtkFiltersTests.cs`

**Interfaces:**
- Consumes: every `*Filters.cs` class created in Tasks 4–14.
- Produces: `RtkSharp.Filters.RtkFilters.IsRegistered(string)` and
  `RtkSharp.Filters.RtkFilters.Filter(string, string[], string, string, int)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// RtkSharp.Filters.Tests/RtkFiltersTests.cs
using RtkSharp.Filters;

namespace RtkSharp.Filters.Tests;

public class RtkFiltersTests
{
    [Theory]
    [InlineData("git")]
    [InlineData("npm")]
    [InlineData("ls")]
    [InlineData("aws")]
    public void IsRegistered_ReturnsTrue_ForKnownFilters(string command)
    {
        Assert.True(RtkFilters.IsRegistered(command));
    }

    [Theory]
    [InlineData("notacommand")]
    [InlineData("")]
    [InlineData("err")]   // explicitly excluded per the design's non-goals
    [InlineData("run")]   // no filtering by design
    [InlineData("proxy")] // no filtering by design
    public void IsRegistered_ReturnsFalse_ForUnknownOrExcludedCommands(string command)
    {
        Assert.False(RtkFilters.IsRegistered(command));
    }

    [Fact]
    public void Filter_ThrowsInvalidOperationException_ForUnregisteredCommand()
    {
        Assert.Throws<InvalidOperationException>(
            () => RtkFilters.Filter("notacommand", [], "", "", 0));
    }

    [Fact]
    public void Filter_DispatchesToRegisteredFilter()
    {
        var filtered = RtkFilters.Filter("git", ["log"], "commit abc123\nAuthor: Test\n\n    msg\n", "", 0);

        Assert.Contains("abc123", filtered);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj --filter "FullyQualifiedName~RtkFiltersTests"`
Expected: FAIL — `RtkFilters` doesn't exist yet (`CS0246`).

- [ ] **Step 3: Implement `FilterRegistry` — the internal dispatch table**

For every registered command, add an entry mapping the command name to a
`Func<string[], string, string, int, string>` (args, rawStdout, rawStderr,
exitCode → filtered string) that calls the appropriate `*Filters` method
from Tasks 4–14. Most entries are a direct one-line call; a few need small
adapters (documented inline below).

```csharp
// RtkSharp.Filters/FilterRegistry.cs
using RtkSharp.Filters.Commands.Cloud;
using RtkSharp.Filters.Commands.Dotnet;
using RtkSharp.Filters.Commands.Gh;
using RtkSharp.Filters.Commands.Git;
using RtkSharp.Filters.Commands.Go;
using RtkSharp.Filters.Commands.Js;
using RtkSharp.Filters.Commands.Jvm;
using RtkSharp.Filters.Commands.Python;
using RtkSharp.Filters.Commands.Ruby;
using RtkSharp.Filters.Commands.Rust;
using RtkSharp.Filters.Commands.System;
using System.Text.Json;

namespace RtkSharp.Filters;

internal static class FilterRegistry
{
    internal delegate string Handler(string[] args, string rawStdout, string rawStderr, int exitCode);

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.Ordinal)
    {
        // Each entry below must be verified against the actual method
        // signature from Tasks 4-14 before finalizing — some methods take
        // (string), some take (string, bool), some take additional args
        // parsed from `args`. Adjust each lambda to match the real
        // signature; this is a first-pass mapping based on the verdict
        // table's method names, not a guarantee every parameter list is
        // exactly right.
        ["git"] = (args, stdout, stderr, exitCode) => GitFilters.FilterStatusWithArgs(stdout, args),
        ["diff"] = (args, stdout, stderr, exitCode) => DiffFilters.RenderFileDiff(stdout),
        ["gt"] = (args, stdout, stderr, exitCode) => GtFilters.FilterGtLogEntries(stdout),
        ["glab"] = (args, stdout, stderr, exitCode) => GlabFilters.FormatMrList(stdout),
        ["gh"] = (args, stdout, stderr, exitCode) => GhFilters.FormatPrList(stdout),
        ["dotnet"] = (args, stdout, stderr, exitCode) => DotnetFilters.FilterBuild(stdout, exitCode == 0),
        ["npm"] = (args, stdout, stderr, exitCode) => NpmFilters.FilterNpmOutput(stdout),
        ["pnpm"] = (args, stdout, stderr, exitCode) => PnpmFilters.FilterPnpmInstall(stdout),
        ["tsc"] = (args, stdout, stderr, exitCode) => TscFilters.FilterTscOutput(stdout),
        ["vitest"] = (args, stdout, stderr, exitCode) => VitestFilters.RenderTestOutputWithHints(stdout),
        ["jest"] = (args, stdout, stderr, exitCode) => VitestFilters.RenderTestOutputWithHints(stdout),
        ["playwright"] = (args, stdout, stderr, exitCode) => PlaywrightFilters.RenderOutput(stdout),
        ["prisma"] = (args, stdout, stderr, exitCode) => PrismaFilters.FilterPrismaGenerate(stdout),
        ["cargo"] = (args, stdout, stderr, exitCode) => CargoFilters.FilterCargoBuild(stdout),
        ["go"] = (args, stdout, stderr, exitCode) => GoFilters.FilterGoBuild(stdout),
        ["golangci-lint"] = (args, stdout, stderr, exitCode) => GoFilters.FilterGolangciJson(stdout, 0),
        ["ruff"] = (args, stdout, stderr, exitCode) => RuffFilters.FilterRuffOutput(stdout, isCheck: true, isFormat: false),
        ["pytest"] = (args, stdout, stderr, exitCode) => PytestFilters.FilterPytestOutput(stdout),
        ["mypy"] = (args, stdout, stderr, exitCode) => MypyFilters.FilterMypyOutput(stdout),
        ["pip"] = (args, stdout, stderr, exitCode) => PipFilters.FilterPipList(stdout),
        ["rake"] = (args, stdout, stderr, exitCode) => RakeFilters.FilterMinitestOutput(stdout),
        ["rubocop"] = (args, stdout, stderr, exitCode) => RubocopFilters.FilterRubocopText(stdout),
        ["rspec"] = (args, stdout, stderr, exitCode) => RspecFilters.FilterRspecText(stdout),
        ["gradlew"] = (args, stdout, stderr, exitCode) => GradlewFilters.FilterTest(stdout),
        ["mvn"] = (args, stdout, stderr, exitCode) => MvnFilters.FilterSurefire(stdout),
        ["aws"] = (args, stdout, stderr, exitCode) => AwsFilters.FilterJsonCompact(stdout),
        ["az"] = (args, stdout, stderr, exitCode) => AzFilters.Redact(stdout),
        ["docker"] = (args, stdout, stderr, exitCode) => DockerFilters.FormatPsSummary(stdout),
        // kubectl/oc take JsonElement, not string — wrap with JsonDocument.Parse
        ["kubectl"] = (args, stdout, stderr, exitCode) => ContainerFilters.FormatKubectlPods(JsonDocument.Parse(stdout).RootElement),
        ["oc"] = (args, stdout, stderr, exitCode) => ContainerFilters.FormatKubectlPods(JsonDocument.Parse(stdout).RootElement),
        ["curl"] = (args, stdout, stderr, exitCode) => CurlFilters.FilterCurlOutput(stdout, exitCode == 0),
        ["wget"] = (args, stdout, stderr, exitCode) => WgetFilters.FormatWgetOutput(stdout),
        ["psql"] = (args, stdout, stderr, exitCode) => PsqlFilters.FilterPsqlOutput(stdout),
        ["ls"] = (args, stdout, stderr, exitCode) => LsFilters.FilterLs(stdout, false, false),
        ["read"] = (args, stdout, stderr, exitCode) => ReadFilters.Render(stdout),
        ["wc"] = (args, stdout, stderr, exitCode) => WcFilters.FilterWcOutput(stdout, default),
        ["tree"] = (args, stdout, stderr, exitCode) => TreeFilters.FilterTreeOutput(stdout),
        // find's "raw input" is a newline-joined path list, not literal
        // command stdout — document this in RtkFilters.Filter's XML doc
        ["find"] = (args, stdout, stderr, exitCode) => FindFilters.FormatFindResults(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), default!),
        ["grep"] = (args, stdout, stderr, exitCode) => GrepFilters.BuildGroupedOutput(stdout, "", 0, 0, false),
        ["pipe"] = (args, stdout, stderr, exitCode) => PipeFilters.AutoDetectFilter(stdout),
        ["test"] = (args, stdout, stderr, exitCode) => TestFilters.ExtractTestSummary(stdout, stderr),
        // env's "raw input" is the environment dictionary, not stdout —
        // this entry is a placeholder pending Task 14's exact
        // FormatEnvReport signature; do not finalize FilterRegistry until
        // Task 14 is complete and this can be wired for real
        ["format"] = (args, stdout, stderr, exitCode) => FormatFilters.FilterBlackOutput(stdout),
        ["json"] = (args, stdout, stderr, exitCode) => JsonFilters.FilterJsonCompact(stdout, 2),
        ["deps"] = (args, stdout, stderr, exitCode) => DepsFilters.SummarizeCargo(stdout),
        ["log"] = (args, stdout, stderr, exitCode) => LogFilters.AnalyzeLogs(stdout),
        ["summary"] = (args, stdout, stderr, exitCode) => SummaryFilters.SummarizeOutput(stdout, stderr, exitCode == 0),
    };

    internal static bool TryGet(string command, out Handler handler) => Handlers.TryGetValue(command, out handler!);

    internal static bool IsRegistered(string command) => Handlers.ContainsKey(command);
}
```

**This mapping table must be reconciled against the real method signatures
once Tasks 4–14 are complete** — several entries above use placeholder
argument values (e.g. `default`, `""`, `0`, `false`) where the real
signature needs a value derived from `args` or another source. Before
finalizing this file: re-open every `*Filters.cs` file created in Tasks
4–14, confirm each method's actual signature, and replace every
placeholder argument with real logic (parsing relevant flags out of
`args`, or removing parameters the real signature doesn't have). Do not
merge this task with a placeholder mapping table left in place — this
step's own "Step 4" build/test checkpoint will fail to compile with the
placeholders above as literally written for several signature mismatches
(e.g. `GitFilters.FilterStatusWithArgs`'s real parameter list), which is
the intended forcing function to make this reconciliation happen.

- [ ] **Step 4: Implement the public `RtkFilters` facade**

```csharp
// RtkSharp.Filters/RtkFilters.cs
namespace RtkSharp.Filters;

/// <summary>
/// Public entry point for calling RtkSharp's command filters in-process,
/// on output the caller already captured — no process execution happens
/// in this library. See
/// <c>docs/superpowers/specs/2026-07-10-rtksharp-filters-library-design.md</c>
/// (Revision 2) for the design this implements.
/// </summary>
public static class RtkFilters
{
    /// <summary>True if a filter is registered for <paramref name="command"/>.</summary>
    public static bool IsRegistered(string command) => FilterRegistry.IsRegistered(command);

    /// <summary>
    /// Filters already-captured output for <paramref name="command"/>.
    /// </summary>
    /// <param name="command">The command name (e.g. <c>"git"</c>).</param>
    /// <param name="args">The command's arguments (e.g. <c>["status"]</c>).</param>
    /// <param name="rawStdout">The command's captured stdout.</param>
    /// <param name="rawStderr">The command's captured stderr.</param>
    /// <param name="exitCode">The command's exit code.</param>
    /// <returns>The filtered output.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no filter is registered for <paramref name="command"/>.
    /// Check <see cref="IsRegistered"/> first, or catch this and fall back
    /// to raw output.
    /// </exception>
    public static string Filter(string command, string[] args, string rawStdout, string rawStderr, int exitCode)
    {
        if (!FilterRegistry.TryGet(command, out var handler))
        {
            throw new InvalidOperationException($"No filter registered for command '{command}'.");
        }

        return handler(args, rawStdout, rawStderr, exitCode);
    }
}
```

- [ ] **Step 5: Reconcile `FilterRegistry`'s placeholder arguments against real signatures, then build and test**

Run: `dotnet build RtkSharp.filters.csproj` iteratively, fixing each
signature mismatch reported by the compiler until it builds clean.

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj --filter "FullyQualifiedName~RtkFiltersTests"`
Expected: `Passed! - Failed: 0`.

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release`
Expected: `Passed! - Failed: 0` (full suite, all tasks so far).

- [ ] **Step 6: Commit**

```bash
git add RtkSharp.Filters/FilterRegistry.cs RtkSharp.Filters/RtkFilters.cs RtkSharp.Filters.Tests/RtkFiltersTests.cs
git commit -m "feat(filters): add RtkFilters public API facade"
```

---

### Task 16: Implement `RtkFilters.TryParseSingleCommand`

**Files:**
- Modify: `RtkSharp.Filters/RtkFilters.cs`
- Modify: `RtkSharp.Filters.Tests/RtkFiltersTests.cs`

**Interfaces:**
- Consumes: `RtkSharp.Rewrite.ShellLexer.Tokenize(string)` (Task 3).
- Produces: `RtkSharp.Filters.RtkFilters.TryParseSingleCommand(string, out string, out string[])`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Append to RtkSharp.Filters.Tests/RtkFiltersTests.cs

public class RtkFiltersTryParseSingleCommandTests
{
    [Fact]
    public void TryParseSingleCommand_SplitsSimpleCommand()
    {
        var result = RtkFilters.TryParseSingleCommand("git status", out var command, out var args);

        Assert.True(result);
        Assert.Equal("git", command);
        Assert.Equal(["status"], args);
    }

    [Fact]
    public void TryParseSingleCommand_HandlesQuotedArguments()
    {
        var result = RtkFilters.TryParseSingleCommand("git commit -m \"fix bug\"", out var command, out var args);

        Assert.True(result);
        Assert.Equal("git", command);
        Assert.Equal(["commit", "-m", "fix bug"], args);
    }

    [Theory]
    [InlineData("git status && echo done")]
    [InlineData("git status || echo failed")]
    [InlineData("git status; echo done")]
    [InlineData("git log | head -5")]
    [InlineData("git log > out.txt")]
    public void TryParseSingleCommand_ReturnsFalse_ForCompoundCommands(string commandLine)
    {
        var result = RtkFilters.TryParseSingleCommand(commandLine, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseSingleCommand_ReturnsFalse_ForEmptyInput()
    {
        var result = RtkFilters.TryParseSingleCommand("", out _, out _);

        Assert.False(result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj --filter "FullyQualifiedName~RtkFiltersTryParseSingleCommandTests"`
Expected: FAIL — `TryParseSingleCommand` doesn't exist yet.

- [ ] **Step 3: Implement `TryParseSingleCommand`**

```csharp
// Add to RtkSharp.Filters/RtkFilters.cs, inside the RtkFilters class
using RtkSharp.Rewrite;

// ... (existing IsRegistered/Filter methods above) ...

    /// <summary>
    /// Tokenizes <paramref name="commandLine"/> and splits it into a
    /// command name and argv, but only if it's a single, non-compound
    /// command — no pipes, <c>&amp;&amp;</c>/<c>||</c>/<c>;</c> operators,
    /// or redirects.
    /// </summary>
    public static bool TryParseSingleCommand(string commandLine, out string command, out string[] args)
    {
        command = "";
        args = [];

        var tokens = ShellLexer.Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens.Any(t => t.Kind is TokenKind.Operator or TokenKind.Pipe or TokenKind.Redirect))
        {
            return false;
        }

        var argTokens = tokens.Where(t => t.Kind == TokenKind.Arg).ToList();
        if (argTokens.Count == 0)
        {
            return false;
        }

        command = argTokens[0].Value;
        args = argTokens.Skip(1).Select(t => t.Value).ToArray();
        return true;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release`
Expected: `Passed! - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp.Filters/RtkFilters.cs RtkSharp.Filters.Tests/RtkFiltersTests.cs
git commit -m "feat(filters): add RtkFilters.TryParseSingleCommand"
```

---

### Task 17: Full-solution checkpoint — confirm the CLI and ParityTests are unaffected

**Files:** No file changes expected unless Steps 1–4 find a problem.

- [ ] **Step 1: Full solution build**

Run: `dotnet build RtkSharp.slnx -c Release`
Expected: All 5 projects build with 0 errors.

- [ ] **Step 2: Full test suite across both projects**

Run: `dotnet test RtkSharp.Tests/RtkSharp.Tests.csproj -c Release`
Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release`
Expected: both `Passed! - Failed: 0`; combined total equals the
pre-refactor baseline of 3357 passed / 1 skipped plus the new
`RtkFilters`/`TryParseSingleCommand` tests from Tasks 15–16 (verify the
arithmetic explicitly).

- [ ] **Step 3: Confirm `RtkSharp.ParityTests` needs no changes**

Run: `dotnet build RtkSharp.ParityTests/RtkSharp.ParityTests.csproj -c Release`
Expected: builds with 0 errors, 0 changes needed.

- [ ] **Step 4: Manual CLI smoke test**

Run: `dotnet run --project RtkSharp -- git status`
Expected: filtered git status output, identical to pre-refactor behavior —
confirms `GitCommand.RunAsync` still produces correct output after its
filter methods moved to `RtkSharp.Filters.Commands.Git.GitFilters`.

- [ ] **Step 5: Commit (only if fixes were needed)**

```bash
git add -A
git commit -m "fix: resolve remaining build/test issues after RtkSharp.Filters extraction"
```

---

### Task 18: Package `RtkSharp.Filters` as a NuGet artifact

**Files:**
- Modify: `RtkSharp.Filters/RtkSharp.Filters.csproj`

- [ ] **Step 1: Add packaging metadata**

```xml
<!-- RtkSharp.Filters/RtkSharp.Filters.csproj — add inside the existing PropertyGroup -->
<Version>1.0.0</Version>
<Description>In-process pure output filters extracted from the RtkSharp CLI (git, gh, dotnet, npm/pnpm, python, go, ruby, jvm, cloud, system, az, etc.), for consumers that already run commands themselves and just want to filter the captured output without a separate rtk install.</Description>
```

- [ ] **Step 2: Pack and verify**

Run: `dotnet pack RtkSharp.Filters/RtkSharp.Filters.csproj -o .artifacts/packages -c Release`
Expected: `Successfully created package '.../.artifacts/packages/RtkSharp.Filters.1.0.0.nupkg'.`

- [ ] **Step 3: Re-verify the CLI tool still packs and runs correctly**

Run: `dotnet pack RtkSharp/RtkSharp.csproj -o .artifacts/packages -c Release`
Expected: `Successfully created package '.../.artifacts/packages/RtkSharp.1.0.0.nupkg'.`

On Windows, via `pwsh` (`dnx` resolves as `dnx.cmd`, invisible to Git Bash):

```powershell
Push-Location ".artifacts/packages"
dnx RtkSharp --add-source . -- --version
dnx RtkSharp --add-source . -- git status
Pop-Location
```

Expected: `RtkSharp 1.0.0`, then filtered `git status` output — identical
to the baseline verified earlier this session.

- [ ] **Step 4: Commit**

```bash
git add RtkSharp.Filters/RtkSharp.Filters.csproj
git commit -m "feat(filters): package RtkSharp.Filters as a versioned NuGet artifact"
```

---

### Task 19: Extend CI to build, test, and pack `RtkSharp.Filters`

**Files:**
- Modify: `.github/workflows/dotnet-ci.yml`

- [ ] **Step 1: Add `RtkSharp.Filters` to the paths trigger and build-and-test job**

```yaml
# .github/workflows/dotnet-ci.yml — update the `paths:` list under `pull_request:`
    paths:
      - "RtkSharp/**"
      - "RtkSharp.Tests/**"
      - "RtkSharp.Filters/**"
      - "RtkSharp.Filters.Tests/**"
      - ".github/workflows/dotnet-ci.yml"
```

```yaml
# Add to the existing build-and-test job, after the existing "dotnet test" step
      - name: dotnet test (RtkSharp.Filters.Tests)
        run: dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release
```

- [ ] **Step 2: Add an `RtkSharp.Filters` pack step to the package-smoke-test job**

```yaml
# Add to the existing package-smoke-test job, after the existing "dotnet pack" step
      - name: dotnet pack (RtkSharp.Filters)
        run: dotnet pack RtkSharp.Filters/RtkSharp.Filters.csproj -o .artifacts/packages -c Release
```

- [ ] **Step 3: Validate YAML syntax**

Run: `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/dotnet-ci.yml')); print('YAML OK')"`
Expected: `YAML OK`

- [ ] **Step 4: Local dry-run**

Run: `dotnet test RtkSharp.Filters.Tests/RtkSharp.Filters.Tests.csproj -c Release`
Expected: `Passed! - Failed: 0`.

Run: `dotnet pack RtkSharp.Filters/RtkSharp.Filters.csproj -o .artifacts/packages -c Release`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/dotnet-ci.yml
git commit -m "ci: build, test, and pack RtkSharp.Filters"
```

---

## Self-Review Notes

- **Spec coverage:** every architectural element of the design doc's
  Revision 2 (pure filter extraction, no `Execution`/`IProcessExecutor`,
  `RtkFilters.Filter`/`IsRegistered`/`TryParseSingleCommand`, packaging,
  test split) has a task.
- **Known risk flagged inline (Task 15, Step 3):** `FilterRegistry`'s first
  draft uses placeholder arguments for several entries where the real
  method signature needs a value derived from `args` — explicitly called
  out as needing reconciliation against the real signatures from Tasks
  4–14 before Task 15 can be considered done, with the build failure as
  the forcing function rather than silent placeholder code shipping.
- **`ErrCommand` exclusion is deliberate**, not a gap — covered in the
  Global Constraints table and tested for in Task 15's
  `IsRegistered_ReturnsFalse_ForUnknownOrExcludedCommands`.
- **Out of scope, correctly excluded:** wiring into CodeSharp's `ShellTool`
  itself — a separate CodeSharp-repo design/plan per the design doc's
  non-goals.
