# Command Inventory — Rust `rtk` top-level `Commands` enum

This document inventories every variant of the top-level `Commands` enum in the Rust
reference implementation (`src/main.rs`) and assigns each an MVP disposition for the
RtkSharp (.NET) port. It is the source of truth later Phase 0+ tasks and Phase 1+
planning reference for "is command X already scoped and what's its priority."

## Scope

- **In scope**: every variant of `enum Commands { ... }` declared at `src/main.rs:79`
  (with the `#[derive(Debug, Subcommand)]` attribute at line 78), spanning lines
  79–772 (closing brace at line 772, immediately followed by the next
  `#[derive(Debug, Subcommand)]` block for `HookCommands` at line 774).
- **Out of scope**: nested subcommand enums referenced from `Commands` variants
  (e.g. `GitCommands`, `DotnetCommands`, `DockerCommands`, `KubectlCommands`,
  `OcCommands`, `PnpmCommands`, `CargoCommands`, `GoCommands`, `GtCommands`,
  `PrismaCommands`, `HookCommands`, etc.). These are implementation detail of their
  parent command's disposition, not separate top-level commands, and are deferred to
  a future, more granular inventory task if needed.

## Provenance

- **Extracted variant count**: 68, extracted by reading `src/main.rs:79-772` in full
  and recording every variant name, its doc comment, and its dispatch arm in the
  `match cli.command { ... }` block (found via `grep -n "Commands::" src/main.rs`).
- **Grep sanity count**: `grep -c "^    [A-Z][a-zA-Z]* {" src/main.rs` = **123**
  (whole-file count). This count is intentionally much larger than 68 because it
  matches variant-shaped lines across the *entire* 3438-line file, including all of
  the nested subcommand enums listed above (`GitCommands`, `DotnetCommands`,
  `PnpmCommands`, `CargoCommands`, `HookCommands`, etc.) which are out of scope here.
  Restricting the same grep pattern to just the `Commands` enum span
  (`sed -n '79,772p' src/main.rs | grep -c "^    [A-Z][a-zA-Z]* {"`) yields **67**,
  one short of the true 68 because the pattern requires a line-ending `{` and the
  `Session {}` variant closes its (empty) brace group on the same line
  (`    Session {},`), which the simple pattern does not match. The manual
  line-by-line read confirms 68 is the correct, ground-truth count.

## Disposition legend

- `Must port for MVP` — required for baseline parity; core execution/argument
  infra, explicitly named in Global Constraints, or an explicit Phase 5–8 target.
- `Port after core` — valuable but secondary; cloud CLIs beyond the `gh`/`az`
  reference modules, niche ecosystem wrappers, or analytics/discovery features.
- `Replace with better abstraction` — a thin single-tool passthrough-with-filter
  wrapper whose behavior the Phase 4 declarative module/filter system is expected
  to replace wholesale rather than have a bespoke hand-ported C# module.
- `Defer` — rarely-used, single-purpose passthrough wrapper with low expected
  usage; not worth MVP effort.

## Inventory

| Command | One-line description | Routed module | Disposition | Rationale |
|---|---|---|---|---|
| `Ls` | List directory contents with token-optimized output (proxy to native ls) | `cmds::system::ls` | Must port for MVP | Named explicitly in Global Constraints; Phase 5 system-module scope. |
| `Tree` | Directory tree with token-optimized output (proxy to native tree) | `cmds::system::tree` | Must port for MVP | Phase 5 system-module scope lists `tree`. |
| `Read` | Read file with intelligent filtering | `cmds::system::read` | Must port for MVP | Named explicitly in Global Constraints; core file-read primitive. |
| `Smart` | Generate 2-line technical summary (heuristic-based) | `cmds::system::local_llm` | Port after core | Single heuristic-model summary feature; not named in Phase 0-8 scope, secondary to core proxying. |
| `Git` | Git commands with compact output | `cmds::git::git` (+ nested `GitCommands`) | Must port for MVP | Named explicitly in Global Constraints. |
| `Gh` | GitHub CLI (gh) commands with token-optimized output | `cmds::git::gh_cmd` | Must port for MVP | Named explicitly in Global Constraints; explicit Phase 7 reference module target. |
| `Glab` | GitLab CLI (glab) commands with token-optimized output | `cmds::git::glab_cmd` | Port after core | Not named as a Phase 7/8 reference module; lower priority than `gh`. |
| `Aws` | AWS CLI with compact output (force JSON, compress) | `cmds::cloud::aws_cmd` | Port after core | Cloud CLI beyond the `gh`/`az` reference modules named in Phase 7/8. |
| `Psql` | PostgreSQL client with compact output (strip borders, compress tables) | `cmds::cloud::psql_cmd` | Defer | Single-purpose DB client passthrough wrapper, low expected usage vs. core dev loop. |
| `Pnpm` | pnpm commands with ultra-compact output | `cmds::js::pnpm_cmd` (+ nested `PnpmCommands`) | Must port for MVP | Core JS package manager; fork's CLAUDE.md names pnpm as a headline "modern JS stack" differentiator. |
| `Err` | Run command and show only errors/warnings | `cmds::rust::runner` | Must port for MVP | Generic process-execution + output-filtering wrapper; core Phase 1/2 execution infra. |
| `Test` | Run tests and show only failures | `cmds::rust::runner` | Must port for MVP | Generic process-execution + output-filtering wrapper; core Phase 1/2 execution infra. |
| `Json` | Show JSON (compact values by default, or keys-only with --keys-only) | `cmds::system::json_cmd` | Port after core | Utility viewer, secondary to core command proxying. |
| `Deps` | Summarize project dependencies | `cmds::system::deps` | Port after core | Dependency summary utility, secondary priority. |
| `Env` | Show environment variables (filtered, sensitive masked) | `cmds::system::env_cmd` | Must port for MVP | Phase 5 system-module scope lists `env` explicitly. |
| `Find` | Find files with compact tree output (accepts native find flags) | `cmds::system::find_cmd` | Must port for MVP | Named explicitly in Global Constraints; Phase 5 scope lists `find`. |
| `Diff` | Ultra-condensed diff (only changed lines) | `cmds::git::diff_cmd` | Port after core | Standalone diff utility overlapping with `git diff`; secondary priority. |
| `Log` | Filter and deduplicate log output | `cmds::system::log_cmd` | Port after core | Generic log-filter utility, secondary priority. |
| `Dotnet` | .NET commands with compact output (build/test/restore/format) | `cmds::dotnet::dotnet_cmd` (+ nested `DotnetCommands`) | Must port for MVP | Phase 6 explicitly expands the `dotnet` command module; core dogfooding target for a .NET-native port. |
| `Docker` | Docker commands with compact output | `cmds::cloud::container` (+ nested `DockerCommands`) | Port after core | Container orchestration passthrough, beyond `gh`/`az` reference scope. |
| `Kubectl` | Kubectl commands with compact output | `cmds::cloud::container` (+ nested `KubectlCommands`) | Port after core | Same rationale as `Docker`. |
| `Oc` | OpenShift CLI (oc) commands with compact output | `cmds::cloud::container` (+ nested `OcCommands`) | Port after core | Niche cloud CLI beyond `gh`/`az` reference scope. |
| `Summary` | Run command and show heuristic summary | `cmds::system::summary` | Port after core | Generic heuristic-summary wrapper, secondary utility. |
| `Grep` | Compact grep - strips whitespace, truncates, groups by file | `cmds::system::grep_cmd` | Must port for MVP | Named explicitly in Global Constraints. |
| `Init` | Initialize rtk instructions for assistant CLI usage | `hooks::init` | Must port for MVP | Required for RTK hook installation/onboarding; core UX and prerequisite for tracked usage. |
| `Wget` | Download with compact output (strips progress bars) | `cmds::cloud::wget_cmd` | Defer | Single-purpose download wrapper, largely superseded by `Curl` in practice. |
| `Wc` | Word/line/byte count with compact output | `cmds::system::wc_cmd` | Must port for MVP | Phase 5 system-module scope lists `wc` explicitly. |
| `Gain` | Show token savings summary and history | `analytics::gain` | Must port for MVP | Core token-savings metric display; central to RTK's value proposition and stated Phase 0 parity goal. |
| `CcEconomics` | Claude Code economics: spending (ccusage) vs savings (rtk) analysis | `analytics::cc_economics` | Port after core | Claude-specific economics analytics; valuable but secondary to core proxying/tracking. |
| `Config` | Show or create configuration file | `core::config` | Must port for MVP | Core configuration management; prerequisite for any filter/module behavior. |
| `Jest` | Jest commands with compact output | `cmds::js::vitest_cmd` (shared arm with `Vitest`) | Must port for MVP | CLAUDE.md names "modern JavaScript stack support (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma)" as this fork's headline differentiator; Phase 4's actual text only targets migrating simple TOML filters and building the module abstraction from `gh`/`az`, and never names JS-ecosystem tools for wholesale redesign. Per the disposition-default rule, commands central to the project's stated identity default to MVP scope. |
| `Vitest` | Vitest commands with compact output | `cmds::js::vitest_cmd` | Must port for MVP | Explicitly named in CLAUDE.md's "modern JavaScript stack support (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma)" differentiator list; Phase 4's text does not authorize wholesale replacement of `vitest` specifically, and the Rust implementation does non-trivial bespoke work (grouped error output), not thin passthrough. |
| `Prisma` | Prisma commands with compact output (no ASCII art) | `cmds::js::prisma_cmd` (+ nested `PrismaCommands`/`PrismaMigrateCommands`) | Must port for MVP | Explicitly named in CLAUDE.md's "modern JavaScript stack support (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma)" differentiator list; Phase 4's text never names `prisma` for wholesale redesign, and it does structured filtering, not thin passthrough. |
| `Tsc` | TypeScript compiler with grouped error output | `cmds::js::tsc_cmd` | Must port for MVP | Explicitly named in CLAUDE.md's "modern JavaScript stack support (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma)" differentiator list; performs non-trivial grouped compiler-error output, not thin passthrough, and Phase 4's text doesn't authorize replacing it wholesale. |
| `Next` | Next.js build with compact output | `cmds::js::next_cmd` | Port after core | Niche/ecosystem-tool passthrough+filter wrapper not named among CLAUDE.md's JS-stack differentiators (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma — note "Next.js" there refers to framework awareness generally, not this specific `next` build-wrapper command) or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Lint` | ESLint with grouped rule violations | `cmds::js::lint_cmd` | Port after core | Niche/ecosystem-tool passthrough+filter wrapper not named among CLAUDE.md's JS-stack differentiators (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma) or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Prettier` | Prettier format checker with compact output | `cmds::js::prettier_cmd` | Port after core | Simple passthrough+filter wrapper; not named among CLAUDE.md's JS-stack differentiators and Phase 4's text does not name it as a wholesale-replacement target, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Format` | Universal format checker (prettier, black, ruff format) | `cmds::system::format_cmd` | Port after core | Multi-tool dispatcher over thin filters; not named among CLAUDE.md's stated differentiators or as an explicit Phase 4 target, so it defaults to a secondary/niche utility ported after core rather than an unsupported Phase-4-wholesale-replacement claim. |
| `Playwright` | Playwright E2E tests with compact output | `cmds::js::playwright_cmd` | Must port for MVP | Explicitly named in CLAUDE.md's "modern JavaScript stack support (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma)" differentiator list; Phase 4's text never names `playwright` for wholesale redesign, and its intelligent sub-tool routing is non-trivial bespoke behavior. |
| `Cargo` | Cargo commands with compact output | `cmds::rust::cargo_cmd` (+ nested `CargoCommands`) | Must port for MVP | Named explicitly in Global Constraints ("cargo/rust commands"); RtkSharp itself is Rust-adjacent tooling used during development. |
| `Npm` | npm run with filtered output (strip boilerplate) | `cmds::js::npm_cmd` | Must port for MVP | Core JS package manager/script runner; fork's CLAUDE.md names JS stack support as a headline differentiator. |
| `Npx` | npx with intelligent routing (tsc, eslint, prisma -> specialized filters) | `cmds::js::npm_cmd` (routes onward to `tsc_cmd`/others) | Must port for MVP | Part of CLAUDE.md's "modern JavaScript stack support (pnpm, vitest, Next.js, TypeScript, Playwright, Prisma)" differentiator; performs non-trivial intelligent sub-tool routing to `tsc`/`prisma`/etc., not thin passthrough, and Phase 4's text doesn't authorize wholesale replacement of this router or its JS-ecosystem targets. |
| `Curl` | Curl with auto-JSON detection and schema output | `cmds::cloud::curl_cmd` | Port after core | Cloud/network utility beyond `gh`/`az` reference scope; valuable but secondary. |
| `Discover` | Discover missed RTK savings from Claude Code history | `discover` | Port after core | Analytics/discovery feature, secondary to core proxying. |
| `Session` | Show RTK adoption across Claude Code sessions | `analytics::session_cmd` | Port after core | Analytics feature, secondary to core proxying. |
| `Telemetry` | Manage telemetry consent and data (RGPD/GDPR) | `core::telemetry_cmd` | Port after core | Compliance/consent management; important but not required for MVP command parity. |
| `Learn` | Learn CLI corrections from Claude Code error history | `learn` | Port after core | Analytics/learning feature, secondary to core proxying. |
| `Run` | Execute a shell command via sh -c (raw, no filtering or tracking) | core execution path (no dedicated `cmds` module) | Must port for MVP | Generic raw process execution; already-ported Phase 1/2 execution infra. |
| `Proxy` | Execute command without filtering but track usage | core execution + `core::tracking` | Must port for MVP | Generic execution + tracking passthrough; already-ported Phase 1/2 execution infra, explicitly documented in CLAUDE.md as a key workaround/metrics mode. |
| `Pipe` | Read stdin, apply filter, print filtered output (Unix pipe mode) | `cmds::system::pipe_cmd` | Must port for MVP | Core filter-invocation primitive underpinning the hook/rewrite pipeline. |
| `Trust` | Trust project-local TOML filters in current directory | `hooks::trust` | Must port for MVP | Required for the trust model that gates project-local filter/module execution (see Phase 4 trust model). |
| `Untrust` | Revoke trust for project-local TOML filters | `hooks::trust` | Must port for MVP | Companion to `Trust`; same trust-model requirement. |
| `Verify` | Verify hook integrity and run TOML filter inline tests | `hooks::verify_cmd` | Must port for MVP | Core hook-integrity verification; required for safe hook installation (Phase 9 depends on this). |
| `Ruff` | Ruff linter/formatter with compact output | `cmds::python::ruff_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Pytest` | Pytest test runner with compact output | `cmds::python::pytest_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Mypy` | Mypy type checker with grouped error output | `cmds::python::mypy_cmd` | Port after core | Niche/rarely-used ecosystem tool wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Rake` | Rake/Rails test with compact Minitest output (Ruby) | `cmds::ruby::rake_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Rubocop` | RuboCop linter with compact output (Ruby) | `cmds::ruby::rubocop_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Rspec` | RSpec test runner with compact output (Rails/Ruby) | `cmds::ruby::rspec_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Pip` | Pip package manager with compact output (auto-detects uv) | `cmds::python::pip_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Go` | Go commands with compact output | `cmds::go::go_cmd` (+ nested `GoCommands`) | Port after core | Niche ecosystem support beyond the fork's stated JS/dotnet/git focus; valuable but secondary. |
| `Gt` | Graphite (gt) stacked PR commands with compact output | `cmds::git::gt_cmd` (+ nested `GtCommands`) | Port after core | Niche stacked-PR workflow tool, secondary to core `git`/`gh` support. |
| `GolangciLint` | golangci-lint wrapper with compact `run` support and passthrough for other invocations | `cmds::go::golangci_cmd` | Port after core | Niche/rarely-used ecosystem tool wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target; `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Gradlew` | Android Gradle wrapper with compact output (build, test, lint) | `cmds::jvm::gradlew_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target. `filters/gradle.toml` does exist and match `gradlew`, but that TOML filter is a separate rewrite-path artifact already correctly marked `Replace` in `filter-inventory.md` — the `rtk gradlew` *command module* inventoried here is a distinct bespoke Rust implementation, not the TOML filter, so it isn't rescued by that tie-breaker. `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not this bespoke command module, so it defaults to ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `Mvn` | Apache Maven wrapper with compact output (test, integration-test, compile, package, install, verify, deploy) | `cmds::jvm::mvn_cmd` | Port after core | Niche/rarely-used ecosystem tool passthrough+filter wrapper, not named among CLAUDE.md's stated JS/.NET-first differentiators or as an explicit Phase 4 target. No corresponding TOML filter exists for it either (`spring-boot.toml` only matches a different subcommand surface, not `mvn`), so there is no tie-breaker rescuing a `Replace` reading. `docs/PLANS.md` Phase 4 (lines 434-486) only authorizes wholesale replacement for TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction, not bespoke language-ecosystem command modules like this one, so it defaults to a niche ecosystem tool ported after core per Global Constraints rather than resting on an unsupported "Phase 4 will replace this" claim. |
| `HookAudit` | Show hook rewrite audit metrics (requires RTK_HOOK_AUDIT=1) | `hooks::hook_audit_cmd` | Must port for MVP | Diagnostics for the hook/rewrite pipeline that Phase 3/9 depend on. |
| `Rewrite` | Rewrite a raw command to its RTK equivalent (single source of truth for hooks) | `hooks::rewrite_cmd` | Must port for MVP | Explicitly named as "single source of truth for hooks"; core to Phase 3 rewrite engine and Phase 9 hook integration. |
| `Hook` | Hook processors for LLM CLI tools (Gemini CLI, Copilot, etc.) | `hooks::hook_cmd` (+ nested `HookCommands`) | Must port for MVP | Core hook-processing entry point required for Phase 9 agent integration. |

## Concerns / uncertain classifications

- `Jest` shares its dispatch arm with `Vitest` (`Commands::Jest { ref args } | Commands::Vitest { ref args } => vitest_cmd::run(...)`)
  in the Rust source — both route to the same `vitest_cmd` module. This is recorded
  as-is; it is not a modeling error in this inventory.
- No command rows carry `Replace with better abstraction` after review: an earlier
  draft assigned it to ~20 single-ecosystem wrapper commands (`Ruff`, `Pytest`,
  `Rake`, `Gradlew`, `Mvn`, etc.), but two review rounds established that
  `docs/PLANS.md` Phase 4 only authorizes wholesale replacement for TOML-filter
  migration and the `gh`/`az` module abstraction — never bespoke language-ecosystem
  command modules. JS-stack differentiators (Jest/Vitest/Prisma/Tsc/Playwright/Npx)
  were moved to `Must port for MVP` per CLAUDE.md's headline framing; the remaining
  ecosystem wrappers were moved to `Port after core`. `Replace with better
  abstraction` is reserved for the TOML filter inventory
  (`docs/parity/filter-inventory.md`), where it is genuinely Phase 4's scope.
- `Run` and `Proxy` have no dedicated `cmds::` module (they execute directly in
  `main.rs`), so "Routed module" for these two rows names the core execution path
  rather than a `src/cmds/<ecosystem>/` module — flagged since the Method section's
  routed-module guidance assumes a `cmds` module exists for every variant.
