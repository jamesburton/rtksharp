# RtkSharp .NET 10 Port and Extension Plan

## 1. Goal and Operating Assumptions

RtkSharp should rework this RTK fork into a .NET 10-first CLI that preserves the upstream value proposition: transparently reduce agent-visible command output while preserving command semantics, exit codes, and recoverability.

The target user experience is:

```sh
dnx -y RtkSharp -- <command> [args]
```

or, where the installed SDK exposes `--yes` rather than `-y`:

```sh
dnx RtkSharp --yes -- <command> [args]
```

The exact final invocation syntax must be verified against the .NET 10 SDK used in CI. Microsoft documents `dnx` as the .NET 10 SDK one-shot tool execution shortcut, backed by `dotnet tool exec`, which downloads/caches a NuGet tool without permanently installing it or modifying `PATH`.

### Dependency Position

Treat a Rust dependency as equivalent in cost and friction to a .NET 10 dependency.

That means the project should not justify keeping Rust solely on "dependency minimization" grounds. The decision should be based on:

- Correctness and parity risk.
- Cross-platform behavior.
- Distribution and update path.
- Developer velocity.
- Extension model quality.
- Runtime/toolchain availability for the target users.

Given the desired `dnx` model, .NET 10 is the strategic dependency. Rust can remain as a reference implementation and parity oracle until RtkSharp reaches functional equivalence.

### Project Naming and Packaging

The CLI project and NuGet package should be named `RtkSharp`.

Recommended package properties:

- `PackageId`: `RtkSharp`
- `ToolCommandName`: `rtk`
- `TargetFramework`: `net10.0`
- `PackAsTool`: `true`
- `RuntimeIdentifiers` or `ToolPackageRuntimeIdentifiers`: `win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64;any`

The direct `dnx` path uses the package ID, not the command name:

```sh
dnx -y RtkSharp -- git status
```

Permanent install remains optional:

```sh
dotnet tool install --global RtkSharp
rtk git status
```

## 2. Current Project Re-Review

### Architecture Scorecard

| Area | Current Score | Target Score | Review |
|---|---:|---:|---|
| Core CLI coverage | 8/10 | 9/10 | Existing Rust CLI has broad command support and a mature routing model. Port should preserve breadth but reduce central `main.rs` coupling. |
| .NET command support | 7/10 | 10/10 | Existing `dotnet build/test/restore/format` support is stronger than many tools, including binlog/TRX/MTP logic. Missing first-class file-based `.cs`, C# source filtering, and broader .NET workflows. |
| Cross-platform execution | 6/10 | 9/10 | PATHEXT handling exists for `.cmd/.bat/.ps1` wrappers. Windows shell built-ins and PowerShell aliases are not first-class. |
| Hook/injection model | 7/10 | 9/10 | Thin hook delegates are the right pattern. Windows-specific scripts and robust fallback modes need design. |
| Extensibility | 4/10 | 9/10 | TOML filters are useful, but rewrite rules and modules are static. RtkSharp needs a plugin/module contract from the start. |
| DRY/modularity | 5/10 | 9/10 | Current command modules are useful but require repeated registration and centralized enum edits. Port should use registry-driven discovery. |
| Testing posture | 7/10 | 10/10 | Existing fixtures and tests are a strong baseline. Port needs golden parity, shell matrix, package execution, and extension contract tests. |
| Distribution | 5/10 | 10/10 | Current Rust distribution is conventional. Target is `dnx` zero permanent install, optional global install, RID packages, and auto-update through NuGet resolution. |

### Key Findings to Preserve

- The rewrite engine is the strategic core: shell tokenization, compound splitting, rewrite guards, and command-specific safety rules are more important than any individual filter.
- The output contract is correct: preserve exit codes, fall back to raw output on filter failure, and provide tee/recovery hints when content is truncated.
- The existing `.NET` support is worth porting carefully rather than rewriting casually.
- `gh` already exists and should become a reference module.
- `az` is a good demonstration module because it exercises JSON/text modes, cloud command breadth, and extension scalability.

### Key Gaps to Fix

- No C# language entry in source filtering.
- No `.cs` file-based app execution path.
- No first-class `.ps1`, `.bat`, `.cmd`, `.sh` script parity layer.
- Static rewrite rule registration.
- Static command registration.
- Insufficient Windows command alias support.
- Hook artifacts lean Unix/shell-first.
- TOML filters are declarative but not a complete extension system.

## 3. Target Architecture

### Solution Layout

Use one publishable CLI project named `RtkSharp`, with internal folders or optional library projects depending on complexity. Start with one project until boundaries become real; split only when the abstractions are stable.

Recommended initial structure:

```text
src/
  RtkSharp/
    RtkSharp.csproj
    Program.cs
    Cli/
    Core/
    Execution/
    Rewrite/
    Filtering/
    Commands/
    Modules/
    Templates/
    Hooks/
    Telemetry/
    Testing/
tests/
  RtkSharp.Tests/
  RtkSharp.GoldenTests/
  RtkSharp.PackageTests/
```

If library separation becomes useful:

- `RtkSharp.Core`
- `RtkSharp.Abstractions`
- `RtkSharp.Modules.Builtin`
- `RtkSharp.Tests`

Keep `RtkSharp` as the only published CLI tool.

### Design Principles

- Registry-driven over central switch statements.
- Immutable value objects for parsed commands, rewrite results, filter results, and execution results.
- Functional pipelines for parse → classify → execute → filter → render → track.
- Fluent builders only where they clarify module registration and tests.
- Explicit safety gates for rewrite, filtering, truncation, hook mutation, and script execution.
- Prefer composition over inheritance.
- Keep extension contracts small and stable.
- Preserve transparent command behavior over aggressive token reduction.

### Core Pipeline

```text
Raw Args / Hook Command
  -> Shell Parse
  -> Command Normalization
  -> Rewrite Classification
  -> Execution Plan
  -> Process Execution
  -> Output Capture / Streaming
  -> Filter Selection
  -> Filter Application
  -> Recovery Hint / Tee
  -> Render
  -> Tracking
  -> Exit Code
```

### Core Abstractions

```csharp
public interface IRtkModule
{
    string Name { get; }
    ModuleMetadata Metadata { get; }
    void Configure(IRtkModuleBuilder builder);
}

public interface IRtkCommandHandler
{
    ValueTask<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}

public interface IOutputFilter
{
    bool CanFilter(FilterContext context);
    ValueTask<FilterResult> FilterAsync(FilterContext context, CancellationToken cancellationToken);
}

public interface IRewriteRuleProvider
{
    IEnumerable<RewriteRule> GetRules();
}

public interface IScriptRunner
{
    bool CanRun(ScriptExecutionRequest request);
    ValueTask<CommandResult> RunAsync(ScriptExecutionRequest request, CancellationToken cancellationToken);
}
```

### Fluent Module Registration

Example shape:

```csharp
builder.Command("gh")
    .Category("GitHub")
    .Rewrite(@"^gh\s+(pr|issue|run|repo|api|release)")
    .Prefixes("gh")
    .Savings(82)
    .Handler<GhCommandHandler>();

builder.Filter("az-deployment")
    .Matches(@"^az\s+deployment\s+")
    .StripAnsi()
    .PreferJson()
    .Handler<AzureDeploymentFilter>();
```

The fluent API should compile to immutable descriptors. Runtime dispatch should not depend on mutable builder state.

## 4. Detailed Phase Plan

## Phase 0: Baseline, Scope, and Parity Harness

### Tasks

- Inventory every command currently exposed in `src/main.rs`.
- Inventory every rewrite rule in `src/discover/rules.rs`.
- Inventory every TOML filter in `src/filters/`.
- Inventory hook behavior across Claude, Cursor, Copilot, Gemini, Codex, OpenCode, Pi, Hermes, Cline, and Windsurf.
- Mark each feature as one of:
  - Must port for MVP.
  - Port after core.
  - Replace with better abstraction.
  - Defer.
- Capture golden inputs/outputs for:
  - Rewrite cases.
  - Compound shell commands.
  - Pipe safety.
  - `gh` commands.
  - `dotnet build/test/restore/format`.
  - `ls/read/find/grep`.
  - TOML filter application.
- Build a parity test runner that can execute Rust RTK and RtkSharp against the same fixtures.
- Define acceptable intentional differences and document them.

### Acceptance Criteria

- `docs/PLANS.md` has an inventory-derived parity checklist.
- Golden test harness can compare Rust RTK output to RtkSharp output.
- MVP and non-MVP scope are explicitly separated.
- Every current rewrite rule has a tracked disposition.
- Every current command has a tracked disposition.

### Testing

- Golden fixture tests for rewrite output.
- Snapshot tests for rendered filtered output.
- Exit code preservation tests.
- Cross-platform path normalization tests.

### Risks and Mitigations

- Risk: port scope balloons.
  - Mitigation: freeze MVP around rewrite, execution, system, git/gh, dotnet, script basics, and extension model.
- Risk: tests encode Rust bugs.
  - Mitigation: mark intentional deviations in a compatibility ledger.

## Phase 1: .NET 10 CLI Skeleton and NuGet Tool Packaging

### Tasks

- Create `src/RtkSharp/RtkSharp.csproj`.
- Set `TargetFramework` to `net10.0`.
- Configure tool packaging:
  - `PackAsTool=true`
  - `ToolCommandName=rtk`
  - `PackageId=RtkSharp`
  - `AssemblyName=RtkSharp`
- Add CLI parser. Prefer `System.CommandLine` if stable for the required UX; otherwise use a minimal parser with explicit tests.
- Implement global flags:
  - `--verbose` / `-v`
  - `--ultra-compact`
  - `--no-color`
  - `--version`
  - `--help`
- Implement fallback pass-through for unknown commands.
- Ensure direct source execution works during development:
  - `dotnet run --project src/RtkSharp -- git status`
- Ensure packed execution works:
  - `dotnet pack`
  - `dnx RtkSharp --add-source <local_nupkg> -- --help`
- Verify final non-interactive syntax:
  - `dnx -y RtkSharp -- --help`
  - `dnx RtkSharp --yes -- --help`

### Acceptance Criteria

- `RtkSharp` package executes through `dnx` from a local package source.
- The package name is exactly `RtkSharp`.
- The installed command name is `rtk`.
- Unknown commands pass through with preserved exit codes.
- Help and version output are deterministic.

### Testing

- Unit tests for CLI parse behavior.
- Package tests that pack locally and execute through `dnx`.
- Windows, Linux, macOS CI jobs.
- Tests that ensure argument forwarding after `--` is unchanged.

### Risks and Mitigations

- Risk: `dnx -y` syntax differs across SDK versions.
  - Mitigation: CI validates both documented `--yes` and desired `-y`; docs use the verified form.
- Risk: `System.CommandLine` introduces unstable behavior.
  - Mitigation: isolate parser behind `IArgumentParser`.

## Phase 2: Core Execution and Output Infrastructure

### Tasks

- Implement `ProcessExecutor`.
- Implement `ResolvedCommand` behavior equivalent to Rust `resolved_command()`:
  - Windows `PATHEXT` support.
  - `.cmd`, `.bat`, `.ps1`, `.exe` awareness.
  - Direct executable path support.
  - Shell built-in detection.
- Implement `ExecutionResult`:
  - `Stdout`
  - `Stderr`
  - `ExitCode`
  - `TimedDuration`
  - `WasStarted`
  - `Failure`
- Implement output capture modes:
  - Capture stdout and stderr separately.
  - Merge stdout/stderr.
  - Inherit streaming.
  - Capture with timeout.
- Implement ANSI stripping.
- Implement truncation caps:
  - Errors.
  - Warnings.
  - Lists.
  - Inventory.
  - Logs.
- Implement tee/recovery store:
  - Per-command raw output.
  - Tail hints.
  - Multi-line block hints.
  - Safe cleanup policy.
- Implement token tracking abstraction with a no-op default.

### Acceptance Criteria

- Commands execute on Windows, Linux, and macOS.
- Exit codes are preserved for success, failure, and command-not-found cases.
- `.cmd` and `.bat` wrappers execute correctly on Windows.
- `.ps1` execution is explicit and policy-controlled, not accidental.
- Filter failures fall back to raw output.
- Truncated output always has a recovery path.

### Testing

- Unit tests for path resolution.
- Integration tests with temporary `.cmd`, `.bat`, `.ps1`, and Unix executable files.
- Timeout tests.
- Large output tests.
- ANSI stripping tests.
- Tee recovery tests.

### Risks and Mitigations

- Risk: shell built-ins cannot be executed directly.
  - Mitigation: route built-ins through shell-specific execution plans.
- Risk: PowerShell execution policy blocks `.ps1`.
  - Mitigation: use `pwsh -NoProfile -ExecutionPolicy Bypass -File` only when explicitly executing a script path.

## Phase 3: Rewrite Engine and Shell Normalization

### Tasks

- Port shell lexer:
  - Quotes.
  - Escapes.
  - Environment prefixes.
  - Redirections.
  - Pipes.
  - `&&`, `||`, `;`, background `&`.
- Port compound segment splitting.
- Port rewrite guards:
  - Already `rtk`.
  - `RTK_DISABLED=1`.
  - Explicit excluded commands.
  - Structured output guards such as `gh --json`.
  - Pipe consumer safety.
  - Write redirection safety for read commands.
- Implement platform-aware normalization:
  - Unix aliases.
  - CMD built-ins.
  - PowerShell aliases and command names.
- Add Windows equivalents:
  - `dir` → `rtk ls`
  - `type` → `rtk read` where safe.
  - `where` → `rtk find` only where semantics are compatible; otherwise create `rtk where`.
  - `findstr` → `rtk grep` where safe.
  - `Get-ChildItem`, `gci`, `ls`, `dir` → `rtk ls`
  - `Get-Content`, `gc`, `cat`, `type` → `rtk read`
  - `Select-String`, `sls` → `rtk grep`
- Move rewrite rules to descriptors provided by modules.
- Support project/user override rules.

### Acceptance Criteria

- Existing Rust rewrite fixture parity reaches at least 95% for MVP commands.
- No unsafe rewrite occurs for pipes or redirections.
- Windows command aliases rewrite only when semantics are proven safe.
- `rtk rewrite "<command>"` explains no-rewrite decisions with a debug flag.
- Rules can be added by a module without editing central CLI code.

### Testing

- Golden rewrite tests.
- Shell syntax fuzz/property tests.
- Windows CMD fixture tests.
- PowerShell fixture tests.
- Pipe/redirection safety tests.
- Exclusion config tests.

### Risks and Mitigations

- Risk: PowerShell syntax is not bash syntax.
  - Mitigation: treat PowerShell as a separate parser mode; do not overfit bash lexer.
- Risk: `dir` and `ls` flags differ.
  - Mitigation: only map safe subsets initially; fallback raw otherwise.

## Phase 4: Filter and Module System

### Tasks

- Define `IRtkModule`, `IRtkModuleBuilder`, `IOutputFilter`, `IRtkCommandHandler`, and `IRewriteRuleProvider`.
- Implement built-in module loading.
- Implement declarative module loading from:
  - `.rtk/modules/*.json`
  - `.rtk/modules/*.toml`
  - user config directory.
- Implement trust model for local modules:
  - Project hash.
  - Explicit trust command.
  - Warning on untrusted modules.
  - Fail-safe passthrough.
- Implement module metadata:
  - Name.
  - Version.
  - Category.
  - Minimum RtkSharp version.
  - Capabilities.
- Implement template support:
  - Command templates.
  - Hook templates.
  - Agent instruction templates.
  - Filter templates.
- Migrate simple TOML filters to declarative module descriptors.
- Keep existing TOML compatibility if feasible.

### Acceptance Criteria

- A new simple line filter can be added without recompiling.
- A new rewrite rule can be added without editing central code.
- A new built-in module can be added without editing parser switch statements.
- Untrusted project-local modules do not execute arbitrary code.
- Declarative filters can be tested inline.

### Testing

- Module load tests.
- Trust/untrust tests.
- Shadowing precedence tests.
- Inline filter tests.
- Invalid module schema tests.
- Compatibility tests for existing TOML filters.

### Risks and Mitigations

- Risk: compiled plugins create security risk.
  - Mitigation: MVP supports declarative extensions only; compiled NuGet modules come later with signing/trust rules.
- Risk: too much abstraction early.
  - Mitigation: build abstractions from two real modules: `gh` and `az`.

## Phase 5: System Commands and Script Parity

### Tasks

- Implement system module:
  - `ls`
  - `dir`
  - `tree`
  - `read`
  - `grep`
  - `find`
  - `where`
  - `wc`
  - `ps`
  - `env`
- Implement script runner module:
  - `.sh` via `bash`/`sh`.
  - `.bat` and `.cmd` via `cmd /d /s /c`.
  - `.ps1` via `pwsh` first, Windows PowerShell fallback only by config.
  - `.cs` via .NET 10 file-based app support.
- Implement script filtering parity:
  - Same output capture.
  - Same exit code preservation.
  - Same tee hints.
  - Same truncation caps.
- Add shell detection:
  - Direct CLI invocation.
  - Hook-provided shell name.
  - Environment hints.
- Add command equivalents table and safety rules.

### Acceptance Criteria

- `.bat`, `.cmd`, `.ps1`, `.sh`, and `.cs` scripts execute through RtkSharp on supported platforms.
- Script execution preserves arguments exactly.
- `.cs` scripts work with .NET 10 file-based app rules.
- Shell built-ins are handled through shell execution plans.
- Unsafe alias rewrites fall back raw.

### Testing

- Temporary script execution tests for each script type.
- Argument quoting tests.
- Spaces-in-path tests.
- Unicode path tests.
- Non-zero script exit tests.
- Missing interpreter tests.
- PowerShell Core vs Windows PowerShell tests.

### Risks and Mitigations

- Risk: `.ps1` behavior differs by host.
  - Mitigation: prefer `pwsh`; document fallback.
- Risk: `.cs` file-based app behavior differs inside project directories.
  - Mitigation: prefer `dotnet run --file file.cs` when ambiguity exists.

## Phase 6: Best-in-Class C# and .NET Support

### Tasks

- Add C# language support:
  - `.cs`
  - `.csx` if supported as script/read filtering only.
  - `.csproj`, `.props`, `.targets` as XML/MSBuild-aware data/code hybrid.
- Implement C# source filtering:
  - `using` directives.
  - Namespace declarations.
  - Class/record/struct/interface signatures.
  - Method/property signatures.
  - Attributes.
  - XML doc comments policy.
  - Top-level statements.
- Expand `dotnet` command module:
  - `build`
  - `test`
  - `restore`
  - `format`
  - `publish`
  - `pack`
  - `clean`
  - `run`
  - `tool`
  - `new`
  - `workload`
  - `sln`
  - `ef` where available.
- Port existing binlog parsing.
- Port existing TRX parsing.
- Port MTP detection and reporting.
- Add file-based app handling:
  - `dotnet file.cs`
  - `dotnet run --file file.cs`
  - `dotnet publish file.cs`
  - `dotnet pack file.cs`
- Add solution/project discovery:
  - `.sln`
  - `.slnx`
  - `.csproj`
  - `global.json`
  - `Directory.Build.props`
  - `Directory.Packages.props`
- Add NuGet/tool execution support:
  - `dnx`
  - `dotnet tool exec`
  - `dotnet tool restore`
  - `dotnet tool run`

### Acceptance Criteria

- `dotnet build` summaries identify project count, errors, warnings, and primary diagnostics.
- `dotnet test` summaries identify total/passed/failed/skipped, failed tests, duration, and project count.
- `dotnet format` summaries identify files needing formatting and recovery hints.
- `.cs` file-based apps run and publish through supported .NET 10 syntax.
- C# read filtering is at parity with or better than Rust/Python/JS filtering.
- RtkSharp itself can dogfood `rtk dotnet test`.

### Testing

- Fixture tests from existing Rust `.NET` fixtures.
- Real minimal solution tests.
- Multi-project solution tests.
- Failing build tests.
- Failing test tests.
- MTP native tests.
- VSTest bridge tests.
- File-based `.cs` tests.
- C# source filter snapshot tests.

### Risks and Mitigations

- Risk: binlog parsing in C# takes too long to port.
  - Mitigation: begin with text/TRX parsing, then add binlog.
- Risk: .NET SDK behavior changes by patch.
  - Mitigation: CI pins one SDK and has floating latest advisory job.

## Phase 7: GitHub (`gh`) Reference Module

### Tasks

- Port existing `gh` support into module form.
- Support:
  - `gh pr list/view/diff/checks/status`
  - `gh issue list/view`
  - `gh run list/view/watch`
  - `gh repo view`
  - `gh release list/view`
  - `gh api`
- Preserve guards:
  - Do not rewrite when `--json`, `--jq`, or `--template` would be corrupted.
  - Do not hide URLs or identifiers needed for follow-up.
- Prefer `gh --json` internally when safe and hidden from user semantics.
- Provide compact summaries and tee recovery.
- Use `gh` as the reference for module registration and command-specific filtering.

### Acceptance Criteria

- `gh` module can be disabled independently.
- Existing `gh` rewrite behavior is preserved.
- `gh` output summaries are deterministic.
- Structured user-requested output is not corrupted.
- Module demonstrates command handler, rewrite rules, filters, and tests.

### Testing

- Fixture tests for PRs, issues, runs, releases, API responses.
- Guard tests for JSON/template/jq.
- Passthrough tests.
- Exit code tests.

### Risks and Mitigations

- Risk: `gh` output changes frequently.
  - Mitigation: prefer JSON APIs internally where safe.

## Phase 8: Azure CLI (`az`) Demonstration Module

### Tasks

- Add `az` module with rewrite rule:
  - `^az\s+`
- Implement command families:
  - `az account show/list`
  - `az group list/show`
  - `az deployment group/sub/mg/tenant create/what-if/show`
  - `az bicep build/decompile/version`
  - `az acr repository/list/show-tags`
  - `az aks list/show/get-credentials`
  - `az webapp list/show/log`
  - `az functionapp list/show`
  - `az storage account/container/blob list/show`
  - `az monitor activity-log list`
  - `az rest`
- Prefer JSON where safe:
  - If user already requested `-o json`, preserve.
  - If user requested `-o table`, avoid corrupting.
  - Internally inject JSON only when output semantics remain acceptable.
- Add cloud-safe redaction:
  - Subscription IDs configurable.
  - Tenant IDs configurable.
  - Secrets and connection strings always redacted.
- Add Azure-specific summaries:
  - Resource counts.
  - Names/resource groups/locations.
  - Deployment status and failed operations.
  - Bicep diagnostics.

### Acceptance Criteria

- `az` module is implemented without changing core dispatch code.
- `az` filters demonstrate declarative and compiled filter styles.
- Sensitive values are redacted by default.
- Failed deployments surface actionable error summaries.
- Raw output recovery exists for all truncation.

### Testing

- JSON fixture tests.
- Table/text fixture tests.
- Redaction tests.
- Deployment failure fixture tests.
- Large resource list tests.
- User output format guard tests.

### Risks and Mitigations

- Risk: Azure output is too broad.
  - Mitigation: ship scoped high-value subcommands first, fallback raw for the rest.
- Risk: accidental secret exposure.
  - Mitigation: redaction pipeline runs before rendering and tracking.

## Phase 9: Hooks, Agent Integration, and Windows Injection

### Tasks

- Port hook processors:
  - Claude.
  - Cursor.
  - Copilot.
  - Gemini.
  - Codex instruction mode.
  - OpenCode/Pi/Hermes if APIs remain viable.
- Generate hook artifacts:
  - `.sh`
  - `.ps1`
  - `.cmd`
  - JSON config snippets.
  - Agent instruction templates.
- Implement `rtk init`:
  - Global install.
  - Project install.
  - Hook-only.
  - Instruction-only.
  - Agent-specific.
  - Windows mode: `powershell`, `cmd`, `instructions`.
- Implement integrity:
  - Hash installed hook artifacts.
  - Verify current artifact.
  - Non-blocking runtime behavior.
- Implement hook dry-run:
  - `rtk hook check --agent <agent> <command>`
- Add instruction fallback for agents without mutation APIs.

### Acceptance Criteria

- Hook errors never block command execution.
- Hooks can rewrite commands on supported agents.
- Codex has reliable instruction-mode support.
- Windows hook artifacts work without requiring Unix shell.
- `rtk verify` reports hook status.

### Testing

- Hook JSON input/output tests.
- Invalid JSON tests.
- Missing `rtk` tests.
- Windows `.ps1` and `.cmd` hook tests.
- Integrity hash tests.
- Idempotent install/uninstall tests.

### Risks and Mitigations

- Risk: agent APIs change.
  - Mitigation: isolate each agent protocol and degrade to instructions.
- Risk: Windows hook execution differs by host.
  - Mitigation: provide both `.ps1` and `.cmd` artifacts plus explicit mode selection.

## Phase 10: Packaging, Release, and Auto-Update Behavior

### Tasks

- Configure NuGet publishing for `RtkSharp`.
- Validate `dnx` behavior:
  - Latest stable resolution.
  - Version pinning via `RtkSharp@x.y.z`.
  - Local source testing.
  - Prerelease testing.
- Add RID-specific packaging evaluation:
  - Framework-dependent `any`.
  - Self-contained RIDs.
  - Native AOT RIDs if compatible.
- Add release workflow:
  - Build.
  - Test.
  - Pack.
  - Sign if required.
  - Publish.
  - Smoke test with `dnx`.
- Add compatibility docs.
- Add cache/update docs:
  - `dnx` uses NuGet cache.
  - Users can pin versions.
  - Users can clear NuGet caches if needed.

### Acceptance Criteria

- `dnx -y RtkSharp -- --version` works after publish.
- Version pinning works.
- Local package source tests work before publish.
- CI proves Windows/Linux/macOS package execution.
- Published package does not require Rust.

### Testing

- Local NuGet feed package tests.
- Real `dnx` smoke tests.
- Global install smoke tests.
- RID package smoke tests.
- AOT smoke tests if enabled.

### Risks and Mitigations

- Risk: Native AOT breaks reflection-heavy libraries.
  - Mitigation: ship CoreCLR first; AOT as an optimization track.
- Risk: NuGet cache makes update behavior look stale.
  - Mitigation: document version pinning and cache behavior; test latest resolution.

## Phase 11: Documentation and Developer Experience

### Tasks

- Write user docs:
  - Quick start.
  - `dnx` usage.
  - Global install.
  - Cross-platform command equivalents.
  - Script execution.
  - Dotnet/C# support.
  - `gh` support.
  - `az` support.
  - Hook setup.
  - Extension authoring.
- Write maintainer docs:
  - Architecture.
  - Module contract.
  - Filter authoring.
  - Rewrite safety.
  - Test strategy.
  - Release process.
- Add examples:
  - Project-local module.
  - Custom filter.
  - Custom rewrite rule.
  - Custom template.
- Add migration docs from Rust RTK.

### Acceptance Criteria

- A new user can run via `dnx` from docs only.
- A contributor can add a module from docs only.
- A maintainer can release from docs only.
- Windows examples use CMD and PowerShell where relevant.

### Testing

- Docs command snippets tested in CI where practical.
- Link checks.
- Generated help output comparison.

## 5. Cross-Platform Command Equivalence Policy

### Initial Mapping

| Intent | Unix | CMD | PowerShell | RtkSharp |
|---|---|---|---|---|
| List files | `ls` | `dir` | `Get-ChildItem`, `gci`, `ls`, `dir` | `rtk ls` |
| Read file | `cat`, `head`, `tail` | `type` | `Get-Content`, `gc`, `cat`, `type` | `rtk read` |
| Search text | `grep`, `rg` | `findstr` | `Select-String`, `sls` | `rtk grep` |
| Find executable | `which` | `where` | `Get-Command` | `rtk where` or raw |
| Find files | `find`, `fd` | `dir /s`, `where /r` | `Get-ChildItem -Recurse` | `rtk find` |
| Print directory | `pwd` | `cd` | `Get-Location`, `pwd` | raw or `rtk pwd` if added |

### Safety Rules

- Rewrite only safe subsets initially.
- Do not rewrite if flags imply incompatible semantics.
- Do not rewrite commands that feed strict downstream pipe consumers unless output shape is preserved.
- Do not rewrite write operations.
- Do not rewrite structured output requested by the user.
- Provide a debug explanation for skipped rewrites.

## 6. Acceptance Criteria for the Whole Program

### MVP Acceptance

- `RtkSharp` packs and runs through `dnx`.
- Core pipeline preserves exit codes.
- Rewrite engine handles common shell commands and compounds.
- Windows aliases work for safe cases.
- `dotnet`, `gh`, system commands, and scripts have MVP support.
- Declarative modules can add rewrite rules and filters.
- C# read filtering exists.
- `.cs` file-based app execution exists.
- Test matrix runs on Windows, Linux, and macOS.

### Best-in-Class Acceptance

- `.NET` support is better than upstream:
  - MTP-aware test summaries.
  - TRX support.
  - Binlog support.
  - File-based apps.
  - C# source intelligence.
  - NuGet tool/dnx awareness.
- Extension model supports parallel development:
  - Independent modules.
  - No central parser edits for simple modules.
  - Inline tests for declarative filters.
  - Trust model for local project extensions.
- Windows is not a compatibility afterthought:
  - CMD equivalents.
  - PowerShell equivalents.
  - `.bat/.cmd/.ps1` parity.
  - PATHEXT handling.
  - Windows hook artifacts.

## 7. Testing Strategy

### Test Layers

- Unit tests:
  - Parser.
  - Rewrite rules.
  - Filters.
  - Redaction.
  - Path resolution.
  - Module registration.
- Golden tests:
  - Rust RTK parity.
  - Snapshot outputs.
  - Known tricky shell commands.
- Integration tests:
  - Real process execution.
  - Script execution.
  - Dotnet sample projects.
  - GitHub/Azure fixtures.
- Package tests:
  - `dotnet pack`.
  - `dnx` local source.
  - Global install.
- Cross-platform tests:
  - Windows CMD.
  - Windows PowerShell.
  - Linux bash.
  - macOS zsh/bash.
- Security tests:
  - Untrusted module blocking.
  - Redaction.
  - Hook non-blocking behavior.
  - Path traversal.

### Required CI Matrix

- Windows latest, .NET 10 SDK.
- Ubuntu latest LTS, .NET 10 SDK.
- macOS latest, .NET 10 SDK.
- Optional:
  - Windows ARM64.
  - Linux ARM64.
  - macOS ARM64.

### Minimum Gates

- All unit tests pass.
- All package smoke tests pass.
- Golden parity threshold met for MVP command set.
- No command handler may swallow non-zero exit codes.
- No filter may truncate without recovery hint.
- No untrusted local module may execute code.

## 8. Parallel Workstreams

The work can be parallelized once Phase 1 and Phase 2 define stable contracts.

### Workstream A: Core and CLI

- CLI parser.
- Process execution.
- Output model.
- Packaging.

### Workstream B: Rewrite

- Lexer.
- Rules.
- Windows normalization.
- Safety guards.

### Workstream C: .NET/C#

- Dotnet module.
- C# filtering.
- File-based apps.
- Sample projects.

### Workstream D: Modules

- Module abstractions.
- Declarative filters.
- Trust model.
- Template model.

### Workstream E: Tools

- `gh`.
- `az`.
- System commands.
- Scripts.

### Workstream F: Hooks

- Hook processors.
- Installers.
- Windows artifacts.
- Agent instructions.

## 9. Scoring Model and Scored Backlog

### Scoring Rubric

Each task gets a 1-10 delivery priority score.

The score is intentionally pragmatic rather than theoretical:

- Impact: user-visible value and architectural leverage.
- Dependency: how much later work is blocked by this task.
- Risk reduction: how much uncertainty this removes.
- Testability: how clearly TDD can prove the work.
- Parallelism: whether others can build against it safely.
- Effort penalty: large or ambiguous work scores lower until decomposed.

Interpretation:

- 10: critical path, start immediately.
- 8-9: high-leverage core work.
- 6-7: important, usually after dependencies land.
- 4-5: useful but not MVP-blocking.
- 1-3: defer unless it unblocks a specific decision.

### Phase Scores

Status column updated 2026-07-08 against the actual codebase (see "Implementation
Progress" below for how each was verified) — the phase numbering/scope here predates
`docs/superpowers/plans/`, which is where the real per-phase execution plans and
acceptance records live once a phase starts.

| Phase | Score | Status | Rationale |
|---|---:|---|---|
| Phase 0: Baseline, Scope, and Parity Harness | 10 | **Done** | Prevents blind porting and gives every later task objective tests. |
| Phase 1: .NET 10 CLI Skeleton and NuGet Tool Packaging | 10 | **Done** | Proves the core product thesis: `dnx` execution through package ID `RtkSharp`. |
| Phase 2: Core Execution and Output Infrastructure | 10 | **Done** | All command modules depend on reliable process execution, capture, exit codes, and tee behavior. |
| Phase 3: Rewrite Engine and Shell Normalization | 10 | **Done** | Auto-rewrite is the main value path and the highest semantic-risk component. |
| Phase 4: Filter and Module System | 9 | **Done** | Enables DRY parallel module development and avoids recreating upstream central coupling. |
| Phase 5: System Commands and Script Parity | 8 | **Partial** — system commands ported (`ls`/`read`/`find`/`grep`/etc., full parity); PATHEXT/`.cmd`/`.ps1`/`.bat` *wrapper resolution* exists (`PathResolver.cs`), but no dedicated `.ps1`/`.bat`/`.sh` *content-filtering* layer as this phase originally envisioned. | Required for cross-platform agent usability, especially Windows parity. |
| Phase 6: Best-in-Class C# and .NET Support | 9 | **Partial** — `dotnet build/test/restore/format` at full Rust parity plus Windows-drive-letter and binlog-fallback improvements (ledgered as disclosed superset gains); 10-language `--level ast` semantic filtering shipped as a genuine no-Rust-equivalent superset feature (C# via Roslyn, 9 others via TreeSitter); file-based `.cs` app execution (`dotnet run <file>.cs` / `dotnet <file>.cs`) shipped 2026-07-08 as a genuine no-Rust-equivalent superset feature (compile-failure and unhandled-exception summaries, success is pure passthrough). Not yet started: expanded `dotnet` subcommands (`publish`/`pack`/`clean`/`run` for *project*-based apps/`tool`/`new`/`workload`/`sln`/`ef`). | Strategic differentiator for a .NET 10-first fork. |
| Phase 7: GitHub (`gh`) Reference Module | 7 | **Done** | Existing upstream support makes this a low-risk reference implementation for modules. |
| Phase 8: Azure CLI (`az`) Demonstration Module | 7 | **Not started** — no `az` command exists anywhere in `CommandRegistry.cs`; queued after file-based `.cs` execution, see §12. | Strong extension proof, but should wait until module contracts stabilize. |
| Phase 9: Hooks, Agent Integration, and Windows Injection | 8 | **Done** | High value, but depends on rewrite and packaging stability. |
| Phase 10: Packaging, Release, and Auto-Update Behavior | 9 | **Unverified this session** — not re-checked; earlier `dnx`/`dotnet pack` smoke tests are the last recorded evidence (see "Implementation Progress"). | Product cannot meet the zero-install/auto-update requirement without this. |
| Phase 11: Documentation and Developer Experience | 7 | **Ongoing** | Must be continuous; final polish depends on actual CLI behavior. |

### Phase 0 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Inventory current commands | 9 | Generated inventory matches current CLI command list. |
| Inventory rewrite rules | 10 | Generated inventory matches all static rewrite rules. |
| Inventory TOML filters | 8 | Generated inventory includes every built-in TOML filter and inline test status. |
| Inventory hook behavior | 8 | Agent hook matrix documents mutation support and fallback behavior. |
| Classify MVP/defer/replace | 9 | Every inventory item has one disposition and rationale. |
| Capture golden rewrite fixtures | 10 | Fixture runner verifies current Rust outputs. |
| Capture golden command output fixtures | 9 | Fixtures cover `dotnet`, `gh`, system commands, scripts, and failure modes. |
| Build parity test runner | 10 | Same fixture can run against Rust RTK and RtkSharp. |
| Document intentional deviations | 8 | Compatibility ledger distinguishes bugs from desired behavior. |

### Phase 1 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Create `src/RtkSharp/RtkSharp.csproj` | 10 | `dotnet build` succeeds for `net10.0`. |
| Configure `PackageId=RtkSharp` | 10 | Packed `.nupkg` has exact package ID. |
| Configure `ToolCommandName=rtk` | 9 | Global install exposes `rtk`. |
| Implement global flags | 8 | Parser tests cover help/version/verbosity/color modes. |
| Implement command passthrough | 9 | Unknown command preserves args and exit code. |
| Prove `dotnet run --project` path | 8 | Local dev command smoke test passes. |
| Prove local `dnx` package execution | 10 | Local package source can run `dnx RtkSharp`. |
| Verify `-y` versus `--yes` syntax | 9 | CI records supported syntax for the pinned SDK. |

### Phase 2 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Implement `ProcessExecutor` | 10 | Executes success, failure, missing command, timeout, and large output cases. |
| Implement Windows command resolution | 10 | Resolves `.exe`, `.cmd`, `.bat`, and safe `.ps1` scenarios. |
| Implement immutable `ExecutionResult` | 9 | Result model preserves stdout, stderr, exit code, duration, and failure reason. |
| Implement capture modes | 9 | Separate, merged, inherited, and timeout modes have integration tests. |
| Implement ANSI stripping | 7 | Unit tests cover common ANSI sequences. |
| Implement truncation caps | 8 | Cap tests prove no unbounded output in summaries. |
| Implement tee/recovery store | 10 | Every truncating filter produces a recoverable raw/tail hint. |
| Implement tracking abstraction | 6 | No-op tracking is default; real tracking can be added without changing handlers. |

### Phase 3 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Port shell lexer | 10 | Golden tests cover quotes, escapes, redirects, pipes, and operators. |
| Port compound splitting | 10 | `&&`, `||`, `;`, `|`, and `&` rewrite safely. |
| Port rewrite guards | 10 | Guard tests prove no corruption of structured or write-oriented commands. |
| Implement Unix normalization | 8 | Existing upstream-style commands rewrite with parity. |
| Implement CMD normalization | 9 | `dir`, `type`, `findstr`, and `where` safe subsets tested. |
| Implement PowerShell normalization | 9 | `Get-ChildItem`, `Get-Content`, `Select-String`, aliases tested. |
| Move rules to module descriptors | 9 | Adding a rule through a module requires no central parser edit. |
| Add project/user rewrite overrides | 7 | Override precedence tests pass. |

### Phase 4 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Define module interfaces | 10 | `gh` and a fake test module register through the same contract. |
| Implement built-in module loading | 9 | Built-ins load deterministically and can be disabled. |
| Implement declarative module loading | 9 | Project-local JSON/TOML module adds a filter and rewrite rule. |
| Implement trust model | 10 | Untrusted local modules cannot execute code or silently alter behavior. |
| Implement module metadata | 7 | Version/capability compatibility checks reject invalid modules. |
| Implement templates | 6 | Template rendering is deterministic and path-safe. |
| Migrate simple TOML filters | 8 | Existing simple filters pass compatibility tests. |
| Preserve TOML compatibility | 7 | Existing filter files load or have documented migration paths. |

### Phase 5 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Implement `ls`/`dir` | 8 | Cross-platform directory summaries match expected safe output. |
| Implement `read`/`type`/`Get-Content` | 9 | Read filters preserve content semantics and truncation recovery. |
| Implement `grep`/`findstr`/`Select-String` | 8 | Search summaries preserve file/line matches. |
| Implement `find`/`where` safe subsets | 7 | Ambiguous forms fall back raw. |
| Implement `.sh` runner | 7 | Arguments, spaces, exit codes, and missing shell tested. |
| Implement `.bat/.cmd` runner | 9 | CMD script execution tested on Windows. |
| Implement `.ps1` runner | 9 | `pwsh` first, explicit fallback policy tested. |
| Implement `.cs` runner | 10 | .NET 10 file-based app execution smoke tests pass. |
| Add shell detection | 8 | Hook and direct invocation modes identify intended shell. |
| Add equivalence safety table | 7 | Every alias has safe/unsafe classification. |

### Phase 6 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Add C# language detection | 10 | `.cs` maps to C# in source filtering. |
| Implement C# source filtering | 10 | Snapshot tests cover using, namespace, types, methods, attributes, XML docs, top-level statements. |
| Port `dotnet build` | 10 | Failing and passing builds summarize diagnostics and preserve exit codes. |
| Port `dotnet test` | 10 | TRX/MTP fixtures summarize failed tests accurately. |
| Port `dotnet restore` | 8 | Restore noise reduced while errors remain actionable. |
| Port `dotnet format` | 9 | Report JSON fixtures produce deterministic summaries. |
| Add `publish`/`pack` | 7 | Common success/failure output summarized. |
| Add `run` and file-based `.cs` paths | 9 | `dotnet file.cs` and `dotnet run --file file.cs` behavior tested. |
| Add solution/project discovery | 8 | `.sln`, `.slnx`, `.csproj`, `global.json`, props files detected. |
| Add `dnx`/tool command awareness | 8 | Tool execution commands rewrite/filter safely. |

### Phase 7 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Port `gh` module registration | 8 | `gh` loads as an independent module. |
| Support PR commands | 8 | PR fixtures summarize status, checks, and diffs. |
| Support issue commands | 7 | Issue fixtures summarize labels, state, and URLs. |
| Support run commands | 8 | Run/check fixtures identify failures. |
| Support repo/release/API commands | 6 | Common outputs summarized; uncommon forms pass raw. |
| Preserve JSON/template guards | 10 | `--json`, `--jq`, and `--template` are never corrupted. |
| Add tee recovery | 8 | Truncated PR/issue/run lists include recovery hints. |

### Phase 8 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Add `az` module shell | 8 | `az` module adds rules without core edits. |
| Account/group filters | 7 | JSON fixtures produce concise resource summaries. |
| Deployment/Bicep filters | 9 | Failed deployment and Bicep diagnostic fixtures are actionable. |
| ACR/AKS/WebApp/Function filters | 7 | Large resource lists summarize counts and key identifiers. |
| Storage/monitor/rest filters | 6 | Safe subsets handled; unknown forms pass raw. |
| Output format guards | 9 | User-requested table/json/tsv behavior is preserved. |
| Redaction pipeline | 10 | Secrets, connection strings, and configured IDs are redacted before render/tracking. |

### Phase 9 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Port hook processors | 8 | Agent JSON protocol fixtures pass. |
| Generate `.sh` hooks | 7 | Unix hook smoke tests pass. |
| Generate `.ps1` hooks | 9 | PowerShell hook tests pass without Unix shell. |
| Generate `.cmd` hooks | 8 | CMD hook tests pass. |
| Implement `rtk init` modes | 8 | Idempotent install/uninstall tests pass. |
| Implement integrity verification | 8 | Tamper/no-baseline/orphan states tested. |
| Implement hook dry-run | 9 | `rtk hook check` reports rewrite/no-rewrite decisions. |
| Add instruction fallback | 7 | Codex/instruction-only output generated deterministically. |

### Phase 10 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Configure NuGet publishing | 9 | CI produces valid signed/unsigned package artifacts. |
| Validate latest `dnx` resolution | 10 | Published or local feed package runs through `dnx`. |
| Validate version pinning | 8 | Pinned version smoke test runs. |
| Evaluate RID packaging | 7 | RID packages smoke tested or explicitly deferred. |
| Evaluate Native AOT | 5 | AOT compatibility report exists; not MVP-blocking. |
| Add release workflow | 8 | Build/test/pack/publish dry run succeeds. |
| Add cache/update docs | 7 | Docs explain NuGet cache and update behavior. |

### Phase 11 Task Scores

| Task | Score | TDD Gate |
|---|---:|---|
| Quick-start docs | 8 | Commands are copy/paste tested. |
| `dnx` docs | 10 | Verified syntax and package naming are documented. |
| Cross-platform equivalent docs | 8 | CMD/PowerShell/bash examples exist. |
| Script execution docs | 8 | `.cs`, `.ps1`, `.bat/.cmd`, `.sh` examples exist. |
| Dotnet/C# docs | 8 | Feature examples map to tests. |
| `gh` and `az` docs | 7 | Module examples map to fixtures. |
| Hook setup docs | 7 | Per-agent setup and fallback behavior documented. |
| Extension authoring docs | 9 | New module/filter/rewrite tutorial is test-backed. |
| Release docs | 7 | Maintainer can release from docs alone. |

## 10. Autoresearcher/Ralph + TDD Scorer Loop

### Loop Definition

Run short evidence-driven iterations where the autoresearcher/Ralph role proposes the highest-leverage next experiment and the TDD scorer role converts it into measurable red/green/refactor gates.

This is not currently a Hive task directory, so the loop is represented locally in this plan rather than submitted to a Hive server.

### Roles

- Autoresearcher/Ralph:
  - Inspect the current plan, upstream fork, and latest .NET 10 constraints.
  - Identify the highest-risk assumption.
  - Propose one bounded experiment.
  - State what evidence would change the plan.
- TDD Scorer:
  - Convert the experiment into failing tests first.
  - Define pass/fail thresholds.
  - Score the experiment by impact, dependency, risk reduction, and effort.
  - Reject work that cannot be objectively tested yet.

### Iteration Contract

Each loop iteration produces:

- Hypothesis.
- Task score.
- Red tests to write first.
- Green implementation target.
- Refactor criterion.
- Evidence captured.
- Decision: keep, revise, defer, or split.

### Iteration 1: Prove `dnx` Package Execution

| Field | Value |
|---|---|
| Hypothesis | A minimal `RtkSharp` .NET 10 tool package can be run via `dnx` with package ID `RtkSharp` and command arguments forwarded after `--`. |
| Score | 10 |
| Red Test | Package smoke test fails until `dotnet pack` produces `RtkSharp` and local-feed `dnx` can run `--help`. |
| Green Target | `dnx -y RtkSharp -- --help` or documented equivalent works from local package feed in CI. |
| Refactor Criterion | CLI parser remains behind an abstraction so command routing can evolve without package test changes. |
| Evidence | CI log captures SDK version, exact `dnx` syntax, package ID, command name, and forwarded args. |
| Decision Gate | If `-y` is unsupported but `--yes` works, docs and tests standardize on `--yes` while preserving user-requested shorthand as an open compatibility item. |

### Iteration 2: Prove Execution Semantics

| Field | Value |
|---|---|
| Hypothesis | RtkSharp can execute commands cross-platform while preserving stdout, stderr, exit code, and Windows wrapper resolution. |
| Score | 10 |
| Red Test | Temporary executable fixtures fail until process execution handles success, failure, missing commands, `.cmd`, `.bat`, and timeout. |
| Green Target | `ProcessExecutor` returns an immutable `ExecutionResult` with exact streams and exit code. |
| Refactor Criterion | Command handlers depend on `IProcessExecutor`, not `System.Diagnostics.Process` directly. |
| Evidence | Windows/Linux/macOS integration tests pass. |
| Decision Gate | If `.ps1` direct execution is unsafe or inconsistent, require explicit script-runner path through `pwsh`. |

### Iteration 3: Prove Rewrite Safety

| Field | Value |
|---|---|
| Hypothesis | The rewrite engine can port high-value upstream behavior without corrupting pipes, redirects, structured output, or Windows aliases. |
| Score | 10 |
| Red Test | Golden fixtures for `git status`, `gh pr list --json`, `dir`, `type`, `Get-ChildItem`, pipes, and redirects fail before implementation. |
| Green Target | Safe commands rewrite; guarded commands explain no-rewrite in debug mode. |
| Refactor Criterion | Rewrite rules are descriptors loaded from modules, not hard-coded switch arms. |
| Evidence | Golden parity threshold met for MVP rewrite set. |
| Decision Gate | Any alias with ambiguous semantics is downgraded to raw passthrough until a safe subset is proven. |

### Iteration 4: Prove Module Extensibility

| Field | Value |
|---|---|
| Hypothesis | `gh` and `az` can be implemented through the same module contract without editing central dispatch. |
| Score | 9 |
| Red Test | Fake module and `gh` module registration tests fail until descriptors add commands, filters, and rewrite rules dynamically. |
| Green Target | `gh` module and one `az` filter load through `IRtkModule`. |
| Refactor Criterion | Module descriptors are immutable and testable without invoking the CLI. |
| Evidence | A project-local declarative module adds a filter in tests. |
| Decision Gate | If compiled plugin trust is unresolved, keep MVP extensions declarative only. |

### Iteration 5: Prove .NET/C# Differentiation

| Field | Value |
|---|---|
| Hypothesis | RtkSharp can exceed upstream .NET support by combining C# source filtering, `.cs` file-based execution, and `dotnet test` summaries. |
| Score | 9 |
| Red Test | C# filter snapshots, `.cs` execution smoke test, and TRX/MTP fixtures fail before implementation. |
| Green Target | `.cs` files are detected, filtered, run, and test output is summarized with failed-test detail. |
| Refactor Criterion | Dotnet-specific parsers remain pure and fixture-driven. |
| Evidence | Minimal file-based app and test project fixtures pass. |
| Decision Gate | If binlog parsing delays delivery, ship TRX/text first and track binlog as a follow-up. |

### Loop Start Status

Started locally with two reviewer agents:

- Autoresearcher/Ralph reviewer: plan critique, missing research questions, and scoring dimensions.
- TDD Scorer reviewer: independent scoring rubric and red/green/refactor recommendations.

Their outputs should be merged into this section after the first reviewer pass.

### Implementation Progress

**This section was stale for most of the project's life** — it stopped being updated
after early Phase 1/2 smoke tests even though the port has since gone far past that
point. Rewritten 2026-07-08 to reflect actual verified state; the old Phase 1/2 notes
below are kept as a historical record of the first working `dnx`/execution proofs, not
as the current status.

**Current status (2026-07-08, re-verified this session):**

- **Full Rust command-inventory coverage: 68/68**, mechanically diffed against
  `docs/parity/command-inventory.md` — every MVP-priority Rust command has a
  registered RtkSharp counterpart in `CommandRegistry.cs`.
- **Full parity-test suite: 27/27 passing** (`RtkSharp.ParityTests`, unfiltered, last
  full run ~1h53m) — every category in `docs/parity/*.md` is at confirmed byte-level
  parity with the live Rust oracle binary (`target/release/rtk.exe`), modulo the
  disclosed differences in `docs/parity/compatibility-ledger.md`.
- **Unit test suite: 3275 passed, 1 pre-existing Unix-only skip, 0 failed** — reverified
  this session (`dotnet test RtkSharp.Tests`, Release).
- **Rust oracle suite: reverified this session** — must be run from **PowerShell**, not
  Git Bash; a Git-Bash invocation shadows MSVC's `link.exe` with MSYS's own `link`
  (a coreutils tool, not a linker) and fails to link with a confusing `extra operand`
  error that looks like a code regression but is a shell/PATH artifact only.
- **One genuine superset feature already shipped**: `--level ast` semantic-structure
  filtering for 10 languages (C#, Rust, Python, JavaScript, TypeScript, Go, C, C++,
  Java, Ruby) — no Rust equivalent exists at all. See the compatibility ledger's
  `--level ast` entry for full detail (analyzers, AOT footprint, trimmed TreeSitter
  redistribution).
- **`rtk init`'s 9 previously-deferred AI-CLI agent targets** (Gemini, Copilot,
  OpenCode, Cursor, Pi, Hermes, Cline, Windsurf, Kilocode, Antigravity) are ported and
  wired; every disclosed deviation claim for this area has been independently
  re-derived from `src/hooks/init.rs`/`src/main.rs` twice now and confirmed accurate.
- **Not yet started**: Phase 6's expanded `dotnet` subcommands (`publish`/`pack`/
  `clean`/`run`/`tool`/`new`/`workload`/`sln`/`ef`) and file-based `.cs` app handling;
  Phase 5's `.ps1`/`.bat`/`.sh` content-filtering layer; Phase 8's `az` module in its
  entirety. See §12 for the immediate next two items being picked up now.
- **Phase 10 (packaging/release/auto-update) has not been re-verified this session** —
  the `dnx`/`dotnet pack` proofs below are the last recorded evidence; treat as
  unconfirmed until re-run against the current codebase.

**Historical: first Phase 1/2 proofs (SDK `10.0.301`, early in the project)**

- Phase 1 skeleton exists in `RtkSharp/` and targets `net10.0`.
- Package metadata is configured with `PackageId=RtkSharp`, `ToolCommandName=rtk`, and `PackAsTool=true`.
- `Program.cs` now implements deterministic `--help`, `--version`, and unknown-command passthrough through the process executor.
- `dotnet run --project RtkSharp -- -- cmd /d /s /c echo passthrough-ok` preserves command execution and prints `passthrough-ok`.
- `dotnet pack RtkSharp/RtkSharp.csproj -o .artifacts/packages` creates `.artifacts/packages/RtkSharp.1.0.0.nupkg`.
- Local `dnx` package execution is verified with:

```sh
dnx RtkSharp --add-source .artifacts/packages -- --version
```

The verified output is:

```text
RtkSharp 1.0.0
```

For SDK `10.0.301`, `dnx --help` does not list `-y` or `--yes`. The currently verified local-feed syntax is package ID first, followed by `--add-source`, then `--` and tool arguments.

Phase 2 baseline now includes:

- Immutable `ExecutionRequest` and `ExecutionResult` models.
- `IProcessExecutor` and `ProcessExecutor`.
- Separate stdout/stderr capture by default.
- Exit-code preservation for non-zero commands.
- Command-not-found result with exit code `127`.
- Timeout reporting with `TimedOut=true` and exit code `-1`.
- Focused executor tests for stdout, non-zero exit, missing command, and timeout.
- `PathResolver` integrated into `ProcessExecutor`.
- Deterministic command resolution tests with injected `PATH` and `PATHEXT`.
- Windows `.cmd` wrapper resolution and execution through request-level `PATH`.

## 11. Open Questions

- Does the target .NET 10 SDK support both `dnx -y RtkSharp -- ...` and `dnx RtkSharp --yes -- ...`, or only `--yes`?
- Should the first public package reserve the `rtk` command name, or use `rtksharp` until parity is proven?
- Should Native AOT be part of MVP, or a post-MVP performance track?
- Should compiled third-party modules be supported at all, or should extensions stay declarative for security?
- Should `az` use internal `--output json` injection where possible, or only filter user-selected output?
- Should telemetry/tracking be ported initially or stubbed until core behavior is stable?

## 11a. Backlog / Potential Future Superset Features

Not scoped or scheduled — captured here so they aren't lost, not yet designed.

- **Expose RTK as a library for direct usage within a coding agent.** Instead of only
  shelling out to `rtk <command>` as a CLI proxy, package RtkSharp's filtering engine
  (rewrite, TOML filters, AST analyzers, per-ecosystem output filters) as a referenceable
  .NET library/API so an agent host (or another .NET process) could call filtering
  functions in-process rather than via a subprocess round-trip. Raised 2026-07-08;
  needs its own brainstorming pass before scoping (open questions: which surface is the
  library boundary — just the filter functions, or the whole execute+filter pipeline;
  packaging as a second NuGet artifact alongside the `RtkSharp` tool package vs. a
  shared internal project; whether this conflicts with or reinforces the `dnx`
  zero-install CLI thesis in §1).

## 12. Immediate Next Steps

Original steps 1-6 and 8 below are **done** (kept for history, not re-numbered so past
references stay valid). Steps 7, 9, and 10 are the real remaining backlog.

1. ~~Create a parity inventory from the Rust fork.~~ **Done** — `docs/parity/command-inventory.md`, 68/68.
2. ~~Scaffold `src/RtkSharp/RtkSharp.csproj`.~~ **Done.**
3. ~~Prove local package execution with `dnx`.~~ **Done** (see §10 "Historical" proofs); not re-verified this session.
4. ~~Implement the core execution model.~~ **Done.**
5. ~~Port rewrite lexer and a small MVP rule set.~~ **Done** — full rewrite engine at 100% parity.
6. ~~Add system, `gh`, and `dotnet` MVP modules.~~ **Done**, all at confirmed parity.
7. Add `.cs`, `.ps1`, `.bat/.cmd`, and `.sh` script execution. **Partial** — single-file `.cs`
   execution (`dotnet run <file>.cs` / `dotnet <file>.cs`) **done** 2026-07-08 (see
   `docs/superpowers/specs/2026-07-08-cs-file-based-app-execution-design.md` and
   `docs/superpowers/plans/2026-07-08-cs-file-based-app-execution.md`). Still not started:
   `.ps1`/`.bat`/`.sh` content-filtering layer (only wrapper *resolution* exists), and `dotnet
   publish file.cs`/`dotnet pack file.cs` filtering (explicitly deferred as a non-goal of the
   `.cs` design, see that spec's "Non-Goals" section).
8. ~~Add the declarative module loader.~~ **Done** — TOML filter engine (`TomlFilterEngine`/`TomlFilterCompiler`), 63 built-in filters + project/global tiers.
9. Add `az` as the first new module. **Not started.** Queued immediately after item 7
   above.
10. Build CI package smoke tests across Windows, Linux, and macOS. **Not verified this
    session** — no CI matrix re-check was performed; status unknown until checked.
