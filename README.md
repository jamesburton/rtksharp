<p align="center">
  <img src="https://avatars.githubusercontent.com/u/258253854?v=4" alt="RTK - Rust Token Killer" width="500">
</p>

<p align="center">
  <strong>High-performance CLI proxy that reduces LLM token consumption by 60-90%</strong>
</p>

<p align="center">
  <a href="https://github.com/rtk-ai/rtk/actions"><img src="https://github.com/rtk-ai/rtk/workflows/Security%20Check/badge.svg" alt="CI"></a>
  <a href="https://github.com/rtk-ai/rtk/releases"><img src="https://img.shields.io/github/v/release/rtk-ai/rtk" alt="Release"></a>
  <a href="https://opensource.org/licenses/Apache-2.0"><img src="https://img.shields.io/badge/License-Apache_2.0-blue.svg" alt="License: Apache 2.0"></a>
  <a href="https://discord.gg/RySmvNF5kF"><img src="https://img.shields.io/discord/1470188214710046894?label=Discord&logo=discord" alt="Discord"></a>
  <a href="https://formulae.brew.sh/formula/rtk"><img src="https://img.shields.io/homebrew/v/rtk" alt="Homebrew"></a>
  <a href="https://www.nuget.org/packages/RtkSharp"><img src="https://img.shields.io/nuget/v/RtkSharp?label=NuGet%3A%20RtkSharp" alt="NuGet: RtkSharp"></a>
  <a href="https://www.nuget.org/packages/RtkSharp.Filters"><img src="https://img.shields.io/nuget/v/RtkSharp.Filters?label=NuGet%3A%20RtkSharp.Filters" alt="NuGet: RtkSharp.Filters"></a>
</p>

<p align="center">
  <a href="#installation">Install</a> &bull;
  <a href="#commands">Commands</a> &bull;
  <a href="RESULTS.md">Measured Results</a> &bull;
  <a href="#rtksharpfilters-embeddable-library">RtkSharp.Filters</a> &bull;
  <a href="docs/parity/">Parity Docs</a> &bull;
  <a href="https://discord.gg/RySmvNF5kF">Discord</a>
</p>

<p align="center">
  <a href="README.md">English</a> &bull;
  <a href="README_fr.md">Francais</a> &bull;
  <a href="README_zh.md">中文</a> &bull;
  <a href="README_ja.md">日本語</a> &bull;
  <a href="README_ko.md">한국어</a> &bull;
  <a href="README_es.md">Espanol</a> &bull;
  <a href="README_pt.md">Português</a>
</p>

---

> **The upstream Rust codebase has moved into [`rust-original/`](rust-original/README.md).**
> For that Rust CLI's own installation and usage docs, go to the upstream project directly:
> **[rtk-ai/rtk](https://github.com/rtk-ai/rtk)**. This fork does not build or release the Rust
> binary — the code under `rust-original/` is kept here and actively maintained, but only to
> **track and verify feature parity** with upstream: `RtkSharp.ParityTests` builds it as an
> oracle and diffs its output against this fork's own rebuild command-by-command, and the
> `upstream-main` branch tracks `upstream/master` so upstream changes can be spotted and ported.
> It is not a release target of this repository.
>
> **Everything below is this fork's own from-scratch .NET rebuild** — `RtkSharp` (the `rtk` CLI,
> published as a .NET global tool) and `RtkSharp.Filters` (the same filtering logic as an
> embeddable library) — which *is* the actively developed and released artifact of this repo,
> published to [NuGet.org](https://www.nuget.org/profiles/jamesburton). It targets behavioral
> parity with the Rust original (same commands, same filtering strategies, same config file
> shape) while being a native .NET tool: no Rust toolchain, no separate binary download, install
> via `dotnet tool install`.

rtk filters and compresses command outputs before they reach your LLM context. This fork's rebuild targets full parity with the upstream Rust CLI: 60+ supported commands, single `dotnet tool install`, no Rust toolchain required.

## Token Savings (30-min Claude Code Session)

| Operation | Frequency | Standard | rtk | Savings |
|-----------|-----------|----------|-----|---------|
| `ls` / `tree` | 10x | 2,000 | 400 | -80% |
| `cat` / `read` | 20x | 40,000 | 12,000 | -70% |
| `grep` / `rg` | 8x | 16,000 | 3,200 | -80% |
| `git status` | 10x | 3,000 | 600 | -80% |
| `git diff` | 5x | 10,000 | 2,500 | -75% |
| `git log` | 5x | 2,500 | 500 | -80% |
| `git add/commit/push` | 8x | 1,600 | 120 | -92% |
| `cargo test` / `npm test` | 5x | 25,000 | 2,500 | -90% |
| `ruff check` | 3x | 3,000 | 600 | -80% |
| `pytest` | 4x | 8,000 | 800 | -90% |
| `go test` | 3x | 6,000 | 600 | -90% |
| `docker ps` | 3x | 900 | 180 | -80% |
| **Total** | | **~118,000** | **~23,900** | **-80%** |

> Estimates based on medium-sized TypeScript/Rust projects. Actual savings vary by project size.
> For real measured numbers (not estimates) from this fork's `RtkSharp` rebuild, run against this
> repository's own codebase, see **[RESULTS.md](RESULTS.md)**.

## Installation

### .NET Global Tool (all platforms)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or a runtime capable of running a global tool). Works identically on Windows, macOS, and Linux:

```bash
dotnet tool install -g RtkSharp
```

This installs the `rtk` command onto your PATH (via `dotnet tool`'s standard global-tool shim — no separate binary download, no Rust toolchain).

### Verify Installation

```bash
rtk --version   # Prints the installed RtkSharp version
rtk gain        # Should show token savings stats
```

### Update / Uninstall

```bash
dotnet tool update -g RtkSharp     # Update to the latest published version
dotnet tool uninstall -g RtkSharp  # Remove
```

### Prerelease builds

Every push to this fork's `develop` branch publishes a prerelease (`X.Y.Z-dev.N`); `main` publishes stable. To try the latest prerelease:

```bash
dotnet tool install -g RtkSharp --prerelease
```

## Quick Start

```bash
# 1. Install for your AI tool
rtk init -g                     # Claude Code / Copilot (default)
rtk init -g --gemini            # Gemini CLI
rtk init -g --codex             # Codex (OpenAI)
rtk init -g --agent cursor      # Cursor
rtk init -g --agent windsurf    # Windsurf
rtk init --agent cline          # Cline / Roo Code
rtk init --agent kilocode       # Kilo Code
rtk init --agent antigravity    # Google Antigravity
rtk init -g --agent pi          # Pi
rtk init --agent hermes         # Hermes

# 2. Restart your AI tool, then test
git status  # Automatically rewritten to rtk git status
```

Hook-based agents rewrite Bash commands (e.g., `git status` -> `rtk git status`) before execution. Plugin-based agents, including Hermes, use their plugin API to rewrite commands before execution. The agent receives compact output without needing to call `rtk` explicitly.

**Important:** the hook only runs on Bash tool calls. Claude Code built-in tools like `Read`, `Grep`, and `Glob` do not pass through the Bash hook, so they are not auto-rewritten. To get RTK's compact output for those workflows, use shell commands (`cat`/`head`/`tail`, `rg`/`grep`, `find`) or call `rtk read`, `rtk grep`, or `rtk find` directly.

## How It Works

```
  Without rtk:                                    With rtk:

  Claude  --git status-->  shell  -->  git         Claude  --git status-->  RTK  -->  git
    ^                                   |            ^                      |          |
    |        ~2,000 tokens (raw)        |            |   ~200 tokens        | filter   |
    +-----------------------------------+            +------- (filtered) ---+----------+
```

Four strategies applied per command type:

1. **Smart Filtering** - Removes noise (comments, whitespace, boilerplate)
2. **Grouping** - Aggregates similar items (files by directory, errors by type)
3. **Truncation** - Keeps relevant context, cuts redundancy
4. **Deduplication** - Collapses repeated log lines with counts

## Commands

### Files
```bash
rtk ls .                        # Token-optimized directory tree
rtk read file.rs                # Smart file reading
rtk read file.rs -l aggressive  # Signatures only (strips bodies)
rtk smart file.rs               # 2-line heuristic code summary
rtk find "*.rs" .               # Compact find results
rtk grep "pattern" .            # Grouped search results
rtk diff file1 file2            # Condensed diff (exit 1 if files differ)
```

> **C#/.NET codebases**: `rtk read file.cs -l aggressive` has C#-specific signature/import
> recognition (modifiers, `using` directives, `class`/`struct`/`record`/etc., methods,
> constructors) — measured at 90%+ byte savings on this repo's own `.cs` files, see
> [RESULTS.md](RESULTS.md).

### Git
```bash
rtk git status                  # Compact status
rtk git log -n 10               # One-line commits
rtk git diff                    # Condensed diff
rtk git add                     # -> "ok"
rtk git commit -m "msg"         # -> "ok abc1234"
rtk git push                    # -> "ok main"
rtk git pull                    # -> "ok 3 files +10 -2"
```

### GitHub CLI
```bash
rtk gh pr list                  # Compact PR listing
rtk gh pr view 42               # PR details + checks
rtk gh issue list               # Compact issue listing
rtk gh run list                 # Workflow run status
```

### Test Runners
```bash
rtk jest                        # Jest compact (failures only)
rtk vitest                      # Vitest compact (failures only)
rtk playwright test             # E2E results (failures only)
rtk pytest                      # Python tests (-90%)
rtk go test                     # Go tests (NDJSON, -90%)
rtk cargo test                  # Cargo tests (-90%)
rtk rake test                   # Ruby minitest (-90%)
rtk rspec                       # RSpec tests (JSON, -60%+)
rtk err <cmd>                   # Filter errors only from any command
rtk test <cmd>                  # Generic test wrapper - failures only (-90%)
```

### Build & Lint
```bash
rtk lint                        # ESLint grouped by rule/file
rtk lint biome                  # Supports other linters
rtk tsc                         # TypeScript errors grouped by file
rtk next build                  # Next.js build compact
rtk prettier --check .          # Files needing formatting
rtk cargo build                 # Cargo build (-80%)
rtk cargo clippy                # Cargo clippy (-80%)
rtk ruff check                  # Python linting (JSON, -80%)
rtk golangci-lint run           # Go linting (JSON, -85%)
rtk rubocop                     # Ruby linting (JSON, -60%+)
```

### Package Managers
```bash
rtk pnpm list                   # Compact dependency tree
rtk pip list                    # Python packages (auto-detect uv)
rtk pip outdated                # Outdated packages
rtk prisma generate             # Schema generation (no ASCII art)
```

> Ruby gems: `rtk rake`/`rtk rspec`/`rtk rubocop` automatically wrap with `bundle exec` when a
> `Gemfile` is present — there is no separate standalone `rtk bundle` command.

### AWS
```bash
rtk aws sts get-caller-identity # One-line identity
rtk aws ec2 describe-instances  # Compact instance list
rtk aws lambda list-functions   # Name/runtime/memory (strips secrets)
rtk aws logs get-log-events     # Timestamped messages only
rtk aws cloudformation describe-stack-events  # Failures first
rtk aws dynamodb scan           # Unwraps type annotations
rtk aws iam list-roles          # Strips policy documents
rtk aws s3 ls                   # Truncated with tee recovery
```

### Containers
```bash
rtk docker ps                   # Compact container list
rtk docker images               # Compact image list
rtk docker logs <container>     # Deduplicated logs
rtk docker compose ps           # Compose services
rtk kubectl pods                # Compact pod list
rtk kubectl logs <pod>          # Deduplicated logs
rtk kubectl services            # Compact service list
rtk oc get pods                 # OpenShift pod summary
rtk oc get services             # OpenShift service list
rtk oc logs <pod>               # Deduplicated logs
```

### Data & Analytics
```bash
rtk json config.json            # Structure without values
rtk deps                        # Dependencies summary
rtk env -f AWS                  # Filtered env vars
rtk log app.log                 # Deduplicated logs
rtk curl <url>                  # Truncate + save full output
rtk wget <url>                  # Download, strip progress bars
rtk summary <long command>      # Heuristic summary
rtk proxy <command>             # Raw passthrough + tracking
```

### Token Savings Analytics
```bash
rtk gain                        # Summary stats
rtk gain --graph                # ASCII graph (last 30 days)
rtk gain --history              # Recent command history
rtk gain --daily                # Day-by-day breakdown
rtk gain --all --format json    # JSON export for dashboards

rtk discover                    # Find missed savings opportunities
rtk discover --all --since 7    # All projects, last 7 days

rtk session                     # Show RTK adoption across recent sessions
```

## Global Flags

```bash
-u, --ultra-compact    # ASCII icons, inline format (extra token savings)
-v, --verbose          # Increase verbosity (-v, -vv, -vvv)
```

## Examples

**Directory listing:**
```
# ls -la (45 lines, ~800 tokens)        # rtk ls (12 lines, ~150 tokens)
drwxr-xr-x  15 user staff 480 ...       my-project/
-rw-r--r--   1 user staff 1234 ...       +-- src/ (8 files)
...                                      |   +-- main.rs
                                         +-- Cargo.toml
```

**Git operations:**
```
# git push (15 lines, ~200 tokens)       # rtk git push (1 line, ~10 tokens)
Enumerating objects: 5, done.             ok main
Counting objects: 100% (5/5), done.
Delta compression using up to 8 threads
...
```

**Test output:**
```
# cargo test (200+ lines on failure)     # rtk test cargo test (~20 lines)
running 15 tests                          FAILED: 2/15 tests
test utils::test_parse ... ok               test_edge_case: assertion failed
test utils::test_format ... ok              test_overflow: panic at utils.rs:18
...
```

## Auto-Rewrite Hook

The most effective way to use rtk. The hook transparently intercepts Bash commands and rewrites them to rtk equivalents before execution.

**Result**: 100% rtk adoption across all conversations and subagents, zero token overhead.

**Scope note:** this only applies to Bash tool calls. Claude Code built-in tools such as `Read`, `Grep`, and `Glob` bypass the hook, so use shell commands or explicit `rtk` commands when you want RTK filtering there.

### Setup

```bash
rtk init -g                 # Install hook + RTK.md (recommended)
rtk init -g --opencode      # OpenCode plugin (instead of Claude Code)
rtk init -g --auto-patch    # Non-interactive (CI/CD)
rtk init -g --hook-only     # Hook only, no RTK.md
rtk init --show             # Verify installation
```

After install, **restart Claude Code**.

## Windows

RtkSharp is itself a native .NET tool (built and tested on Windows first — this fork's primary
dev machine), so filters, `rtk init`, and `rtk gain`/analytics all work fully on native Windows.
The one limitation carried over from the Rust original: the Claude Code/Cursor auto-rewrite hook
is still a `rtk-rewrite.sh` **Bash** script (same mechanism as upstream, for compatibility with
Claude Code's hook system), so it needs a Unix shell to actually execute. On native Windows,
`rtk init -g` falls back to **CLAUDE.md injection mode** — your AI assistant receives RTK
instructions but commands are not rewritten automatically.

### Recommended: WSL (full auto-rewrite support)

Inside [WSL](https://learn.microsoft.com/en-us/windows/wsl/install), the hook runs like Linux —
full auto-rewrite:

```bash
# Inside WSL, once .NET is installed
dotnet tool install -g RtkSharp
rtk init -g
```

### Native Windows (filters + CLI fully work; hook falls back)

```powershell
dotnet tool install -g RtkSharp
rtk init -g          # Falls back to CLAUDE.md injection (no auto-rewrite)
rtk cargo test        # Use rtk explicitly instead
rtk git status
```

| Feature | WSL | Native Windows |
|---------|-----|----------------|
| Filters (cargo, git, etc.) | Full | Full |
| Auto-rewrite hook | Yes | No (CLAUDE.md fallback) |
| `rtk init -g` | Hook mode | CLAUDE.md mode |
| `rtk gain` / analytics | Full | Full |

## Supported AI Tools

This rebuild's `rtk init` ports all 14 of upstream's AI-tool integrations. Each rewrites shell commands to `rtk` equivalents for 60-90% token savings where the agent supports command interception.

| Tool | Install | Method |
|------|---------|--------|
| **Claude Code** | `rtk init -g` | PreToolUse hook (bash) |
| **GitHub Copilot (VS Code)** | `rtk init -g --copilot` | PreToolUse hook — transparent rewrite |
| **GitHub Copilot CLI** | `rtk init -g --copilot` | PreToolUse deny-with-suggestion (CLI limitation) |
| **Cursor** | `rtk init -g --agent cursor` | preToolUse hook (hooks.json) |
| **Gemini CLI** | `rtk init -g --gemini` | BeforeTool hook |
| **Codex** | `rtk init -g --codex` | AGENTS.md + RTK.md instructions |
| **Windsurf** | `rtk init -g --agent windsurf` | .windsurfrules (project-scoped) |
| **Cline / Roo Code** | `rtk init --agent cline` | .clinerules (project-scoped) |
| **OpenCode** | `rtk init -g --opencode` | Plugin TS (tool.execute.before) |
| **OpenClaw** | `openclaw plugins install ./openclaw` | Plugin TS (before_tool_call) |
| **Pi** | `rtk init -g --agent pi` (global) | TypeScript extension (tool_call) |
| **Hermes** | `rtk init --agent hermes` | Python plugin adapter (terminal command mutation via `rtk rewrite`) |
| **Mistral Vibe** | Planned ([#800](https://github.com/rtk-ai/rtk/issues/800)) | Blocked on upstream |
| **Kilo Code** | `rtk init --agent kilocode` | .kilocode/rules/rtk-rules.md (project-scoped) |
| **Google Antigravity** | `rtk init --agent antigravity` | .agents/rules/antigravity-rtk-rules.md (project-scoped) |

For per-agent setup details, override controls, and graceful degradation, see the [Supported Agents guide](https://www.rtk-ai.app/guide/getting-started/supported-agents). The Hermes plugin source and tests live in `hooks/hermes/`; installed Hermes runtime files still live under `~/.hermes/plugins/rtk-rewrite/`.

## Configuration

Single-tier `config.toml` (no per-project `.rtk/config.toml` tier — same as upstream), managed
via `rtk config`. Location follows OS convention (overridable with `RTK_CONFIG_DIR_OVERRIDE`):

- **Windows**: `%APPDATA%\rtk\config.toml`
- **macOS**: `~/Library/Application Support/rtk/config.toml`
- **Linux**: `$XDG_CONFIG_HOME/rtk/config.toml`, falling back to `~/.config/rtk/config.toml`

```toml
[tracking]
enabled = true              # command tracking / token-savings metrics
history_days = 90
# database_path = "..."     # override the SQLite tracking DB location

[display]
colors = true
emoji = true
max_width = 120

[filters]
ignore_dirs = [".git", "node_modules", "target", "__pycache__", ".venv", "vendor"]
ignore_files = ["*.lock", "*.min.js", "*.min.css"]

[telemetry]
enabled = false              # opt-in only, see Privacy & Telemetry below
# consent_given = true
# consent_date = "..."

[hooks]
exclude_commands = ["curl", "playwright"]  # skip auto-rewrite for these
transparent_prefixes = []    # e.g. "docker exec mycontainer", "poetry run", "bundle exec"

[limits]
grep_max_results = 200
grep_max_per_file = 25
status_max_files = 15
status_max_untracked = 10
passthrough_max_chars = 2000
```

```bash
rtk config             # Show current config (creates the file with defaults if missing)
rtk trust              # Trust a project's .rtk/filters.toml (custom filter overrides)
rtk untrust            # Revoke trust
```

When a command fails (or, for some filters like `curl`, unconditionally on large output), RTK
saves the full unfiltered output to disk so the LLM can inspect it without re-running the command:

```
FAILED: 2/15 tests
[full output: ~/AppData/Local/rtk/tee/1707753600_cargo_test.log]
```

> **Known gap vs. upstream:** tee-to-disk itself is fully implemented (`Enabled`/`Mode`/
> `MaxFiles`/`MaxFileSize`/`Directory`, same defaults as Rust: on-failure, 1MB cap, 20 files
> retained), but it is not yet wired to `config.toml` — there is no `[tee]` table to configure it
> with. Control it via environment variables instead: `RTK_TEE=0` disables teeing entirely;
> `RTK_TEE_DIR` overrides the output directory.

### Uninstall

```bash
rtk init -g --uninstall        # Remove hook, RTK.md, settings.json entry
dotnet tool uninstall -g RtkSharp   # Remove the CLI itself
```

## RtkSharp.Filters (embeddable library)

Everything above is the `rtk` CLI (`RtkSharp` package). If you're building a .NET agent runtime
that already executes commands and captures `stdout`/`stderr`/exit code itself, shelling out to a
separate `rtk` process is unnecessary — `RtkSharp.Filters` exposes the exact same filters as an
in-process library instead:

```bash
dotnet add package RtkSharp.Filters
```

```csharp
using RtkSharp.Filters;

// You already ran the command and captured its output somewhere:
string command = "git";
string[] args = ["status"];
string stdout = /* captured stdout */;
string stderr = /* captured stderr */;
int exitCode = /* captured exit code */;

if (RtkFilters.IsRegistered(command))
{
    string filtered = RtkFilters.Filter(command, args, stdout, stderr, exitCode);
    // pass `filtered` to the LLM instead of the raw output
}
```

`RtkFilters.TryParseSingleCommand(commandLine, out command, out args)` is also available to split
a raw shell command line (e.g. `"git status"`) into `command`/`args`, but only for a single,
non-compound command — it returns `false` for anything with pipes, `&&`/`||`/`;`, or redirects,
so compound commands should be filtered per-segment or left unfiltered.

Supported command names (dispatch table in `RtkSharp.Filters/FilterRegistry.cs`): `git`, `diff`,
`gt`, `glab`, `gh`, `dotnet`, `npm`, `pnpm`, `tsc`, `vitest`, `jest`, `playwright`, `prisma`,
`cargo`, `go`, `golangci-lint`, `ruff`, `pytest`, `mypy`, `pip`, `rake`, `rubocop`, `rspec`,
`gradlew`, `mvn`, `aws`, `az`, `docker`, `kubectl`, `oc`, `curl`, `wget`, `psql`, `ls`, `read`,
`wc`, `tree`, `find`, `grep`, `pipe`, `test`, `format`, `json`, `deps`, `log`, `summary`.
`RtkFilters.IsRegistered(command)` is the source of truth — check it before calling `Filter`, or
catch the `InvalidOperationException` it throws for unregistered commands and fall back to raw
output.

**Disclosed side effect:** unlike every other filter, `Filter("curl", ...)` may write a copy of
large/non-JSON output to disk (the same tee-to-disk behavior described above), unless disabled via
`RTK_TEE=0`. See `RtkFilters`'s XML doc for details.

See [`third-party/treesitter-dotnet-trimmed/README.md`](third-party/treesitter-dotnet-trimmed/README.md)
for how the `RtkSharp.TreeSitter.Trimmed` transitive dependency (used by `RtkSharp.Filters`'s AST
analyzers) is built and published.

## Documentation

Docs specific to this fork's .NET rebuild:

- **[`docs/parity/`](docs/parity/)** — the authoritative parity-tracking area: `command-inventory.md`
  (every command from the Rust `Commands` enum and its port disposition), `filter-inventory.md`,
  `hook-inventory.md`, `rewrite-rule-inventory.md`, plus a dedicated `*-parity-report.md` per
  command family, documenting confirmed-identical behavior and any deliberate deviations from the
  Rust oracle.
- **[`rust-original/README.md`](rust-original/README.md)** — why the Rust code is kept, how to
  build it as the parity oracle, and how it's cross-referenced from the test suite.
- **[`third-party/treesitter-dotnet-trimmed/README.md`](third-party/treesitter-dotnet-trimmed/README.md)** —
  how the trimmed `RtkSharp.TreeSitter.Trimmed` NuGet dependency is built and published.

Docs describing the **upstream Rust CLI** specifically (installation/build/architecture for the
Rust binary this fork does not release) — useful if you're working inside `rust-original/` or
comparing against upstream, not for using this fork's `RtkSharp` tool:

- **[rtk-ai.app/guide](https://www.rtk-ai.app/guide)** — upstream's full user guide
- **[INSTALL.md](INSTALL.md)** — upstream Rust binary installation reference
- **[ARCHITECTURE.md](docs/contributing/ARCHITECTURE.md)** — upstream Rust system design
- **[SECURITY.md](SECURITY.md)** / **[CONTRIBUTING.md](CONTRIBUTING.md)** — general/upstream policy docs

## Privacy & Telemetry

Upstream's Rust CLI collects anonymous, opt-in aggregate usage metrics (see
**[docs/TELEMETRY.md](docs/TELEMETRY.md)** for the full field list and rationale, if you're using
the Rust binary from `rust-original/` or upstream directly).

**This .NET rebuild currently ports only the consent-management flow, not the data-collection
pipeline itself** — there is no telemetry ping sent anywhere yet. `rtk telemetry` manages the same
consent state the Rust CLI would read (so config stays forward-compatible if/when collection is
ported), but as of today, running any of the commands below changes local consent flags only:

```bash
rtk telemetry status     # Check current consent state
rtk telemetry enable     # Give consent (interactive prompt) — no data is actually sent yet
rtk telemetry disable    # Withdraw consent
rtk telemetry forget     # Withdraw consent + delete local consent state
```

If this changes (data collection gets ported), this section and `docs/parity/` will be updated
together — check `docs/parity/telemetry-parity-report.md` for the current, authoritative status.

## Star History

<a href="https://www.star-history.com/?repos=rtk-ai%2Frtk&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=rtk-ai/rtk&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=rtk-ai/rtk&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=rtk-ai/rtk&type=date&legend=top-left" />
 </picture>
</a>

## StarMapper

<a href="https://starmapper.bruniaux.com/rtk-ai/rtk">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://starmapper.bruniaux.com/api/map-image/rtk-ai/rtk?theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://starmapper.bruniaux.com/api/map-image/rtk-ai/rtk?theme=light" />
    <img alt="StarMapper" src="https://starmapper.bruniaux.com/api/map-image/rtk-ai/rtk" />
  </picture>
</a>

## Core team

- **Patrick Szymkowiak** — Founder
  [GitHub](https://github.com/pszymkowiak) · [LinkedIn](https://www.linkedin.com/in/patrick-szymkowiak/)
- **Florian Bruniaux** — Core contributor
  [GitHub](https://github.com/FlorianBruniaux) · [LinkedIn](https://www.linkedin.com/in/florian-bruniaux-43408b83/)
- **Adrien Eppling** — Core contributor
  [GitHub](https://github.com/aeppling) · [LinkedIn](https://www.linkedin.com/in/adrien-eppling/)

## Contributing

For the upstream Rust CLI: open an issue or PR on [rtk-ai/rtk](https://github.com/rtk-ai/rtk) and join the community on [Discord](https://discord.gg/RySmvNF5kF).

For this fork's .NET rebuild specifically (`RtkSharp`/`RtkSharp.Filters`): open an issue or PR on [this repository](https://github.com/jamesburton/rtksharp).

## License

Apache License 2.0 - see [LICENSE](LICENSE) for details.

## Disclaimer

See [DISCLAIMER.md](DISCLAIMER.md).
