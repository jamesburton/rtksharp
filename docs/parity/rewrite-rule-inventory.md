# Rewrite Rule Inventory — `src/discover/rules.rs`

This document inventories every entry of the `RULES` array in the Rust reference
implementation (`src/discover/rules.rs`), which the `rtk discover`/`rtk learn`/hook
rewrite pipeline uses to detect raw shell invocations that have an `rtk`-wrapped
equivalent, and assigns each an MVP disposition for the RtkSharp (.NET) port. It
complements `docs/parity/command-inventory.md` (Task 1) at the rewrite-rule level and
is the reference for Phase 3's rewrite-engine planning (tokenization is already
scaffolded via `ShellLexer.cs`; the rewrite *rules* themselves are not yet ported).

## Scope

- **In scope**: every `RtkRule { .. }` struct literal in the `RULES` constant
  (`src/discover/rules.rs:13`–`905`).
- **Out of scope**: the `IGNORED_PREFIXES` (`:907`–955) and `IGNORED_EXACT`
  (`:957`–959) constants — these are commands rtk deliberately does not rewrite
  (shell builtins, no-op wrappers), not rewrite rules, and are out of scope for this
  rule-level inventory.

## Provenance

- **Extracted rule count**: 75, extracted by reading `src/discover/rules.rs:13-905`
  in full and recording every `RtkRule` entry's `pattern`, `rtk_cmd`,
  `rewrite_prefixes`, `category`, and `savings_pct` fields.
- **Grep sanity count**: `grep -c "^    RtkRule {" src/discover/rules.rs` = **75**.
  Matches the extracted count exactly.
- Of the 75 rules, exactly **one** (`rtk cargo`, category `Cargo`) has a non-empty
  `subcmd_status`: `&[("fmt", RtkStatus::Passthrough)]`. This is noted in that row's
  Notes/hint but not copied verbatim as ground truth for the disposition — it merely
  documents that the Rust source already treats `cargo fmt` as a raw passthrough
  (no compaction) rather than a filtered subcommand, which is orthogonal to whether
  the whole `cargo` rewrite rule is MVP scope.
- Cross-referenced against `docs/parity/command-inventory.md` (Task 1, committed at
  `bf6d726`, available at authoring time). Disposition matching is done by mapping
  each rule's `rtk_cmd` (e.g. `rtk git` → `Git`, `rtk gh` → `Gh`) to its corresponding
  `Commands` enum row in Task 1's inventory, rather than by the rule's broader
  `category` field (e.g. `Build`, `Infra`, `System`) — `category` in `rules.rs` groups
  many unrelated tools together (e.g. `dotnet`, `tsc`, `make`, and `mvn` are all
  `Build`) and using it directly would produce false-consistency across unrelated
  commands. Where a rule's `rtk_cmd` has no corresponding `Commands` variant in Task
  1's inventory (i.e. the rewrite rule points at an `rtk <cmd>` that isn't a
  top-level Rust CLI command — e.g. `rtk make`, `rtk terraform`, `rtk ps`), the
  Method section's fallback applies: default to `Port after core` and note "no
  matching command-inventory entry found" in the Rationale.

## Disposition legend

Same four values as Task 1, applied at the rewrite-rule level:

- `Must port for MVP` — the target `rtk_cmd` is itself `Must port for MVP` in Task 1,
  so its rewrite-detection rule (enabling raw-command → `rtk` suggestions) is needed
  for baseline parity.
- `Port after core` — the target command is `Port after core` in Task 1, or no
  matching `Commands` variant exists at all (aspirational/未-implemented rewrite
  target), so the rule is valuable but secondary.
- `Replace with better abstraction` — the target command is itself flagged
  `Replace with better abstraction` in Task 1 (thin single-tool ecosystem wrapper
  expected to move to the Phase 4 declarative filter/module system); only applied
  where Task 1 already reached that conclusion for the underlying command — never
  assigned freshly at the rule level without that cross-reference, per the Task 1
  review-cycle lesson (Phase 4's actual scope, PLANS.md:434-486, only names
  TOML-filter migration and a `gh`/`az`-based module system, not JS-ecosystem or
  other language tooling).
- `Defer` — the target command is `Defer` in Task 1 (rarely-used, low-priority
  passthrough).

## Inventory

| Category | rtk_cmd | rewrite_prefixes | savings_pct | Disposition | Rationale |
|---|---|---|---|---|---|
| Git | `rtk git` | `git`, `yadm` | 70.0 | Must port for MVP | Task 1: `Git` = Must port for MVP (named explicitly in Global Constraints). |
| GitHub | `rtk gh` | `gh` | 82.0 | Must port for MVP | Task 1: `Gh` = Must port for MVP (Phase 7 reference module target). |
| GitLab | `rtk glab` | `glab` | 82.0 | Port after core | Task 1: `Glab` = Port after core (not a Phase 7/8 reference module). |
| Cargo | `rtk cargo` | `cargo` | 80.0 | Must port for MVP | Task 1: `Cargo` = Must port for MVP. `subcmd_status` hints `fmt` is already raw passthrough in Rust — consistent with MVP scope, not a signal for demotion. |
| PackageManager | `rtk pnpm` | `pnpm` | 80.0 | Must port for MVP | Task 1: `Pnpm` = Must port for MVP (headline "modern JS stack" differentiator per root CLAUDE.md). |
| PackageManager | `rtk npm` | `npm` | 70.0 | Must port for MVP | Task 1: `Npm` = Must port for MVP. |
| PackageManager | `rtk npx` | `npx` | 70.0 | Must port for MVP | Task 1: `Npx` = Must port for MVP (intelligent sub-tool routing). |
| Files | `rtk read` | `cat`, `head`, `tail` | 60.0 | Must port for MVP | Task 1: `Read` = Must port for MVP (named in Global Constraints). |
| Files | `rtk grep` | `rg`, `grep` | 75.0 | Must port for MVP | Task 1: `Grep` = Must port for MVP (named in Global Constraints). |
| Files | `rtk ls` | `ls` | 65.0 | Must port for MVP | Task 1: `Ls` = Must port for MVP. |
| Files | `rtk find` | `find` | 70.0 | Must port for MVP | Task 1: `Find` = Must port for MVP. |
| Build | `rtk tsc` | `npm exec tsc`, `npm rum tsc`, `npm run tsc`, `npm run-script tsc`, `npm tsc`, `npm urn tsc`, `npm x tsc`, `npx tsc`, `pnpm dlx tsc`, `pnpm exec tsc`, `pnpm run tsc`, `pnpm run-script tsc`, `pnpm tsc`, `pnpx tsc`, `tsc` | 83.0 | Must port for MVP | Task 1: `Tsc` = Must port for MVP (JS-stack differentiator, non-trivial grouped error output). |
| Build | `rtk lint` | `biome`, `eslint`, `lint`, + npm/pnpm/npx variants | 84.0 | Port after core | Task 1: `Lint` = Port after core (not named in JS-stack differentiator list; `docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Build | `rtk prettier` | `prettier` + npm/pnpm/npx variants | 70.0 | Port after core | Task 1: `Prettier` = Port after core (not named among JS-stack differentiators; no explicit Phase 4 wholesale-replacement claim). |
| Build | `rtk next` | `next build` + npm/pnpm/npx variants | 87.0 | Port after core | Task 1: `Next` = Port after core (CLAUDE.md's "Next.js" differentiator refers to framework awareness generally, not this specific build-wrapper command; `docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module). |
| Tests | `rtk jest` | `jest`, `jest run` + npm/pnpm/npx variants | 99.0 | Must port for MVP | Task 1: `Jest` = Must port for MVP (shares `vitest_cmd` dispatch arm; JS-stack differentiator). |
| Tests | `rtk vitest` | `vitest`, `vitest run` + npm/pnpm/npx variants | 99.0 | Must port for MVP | Task 1: `Vitest` = Must port for MVP (explicit JS-stack differentiator; bespoke grouped error output). |
| Tests | `rtk playwright` | `playwright` + npm/pnpm/npx variants | 94.0 | Must port for MVP | Task 1: `Playwright` = Must port for MVP (explicit JS-stack differentiator; intelligent sub-tool routing). |
| Build | `rtk prisma` | `prisma` + npm/pnpm/npx variants | 88.0 | Must port for MVP | Task 1: `Prisma` = Must port for MVP (explicit JS-stack differentiator; structured filtering, not thin passthrough). |
| Infra | `rtk docker` | `docker` | 85.0 | Port after core | Task 1: `Docker` = Port after core (container orchestration beyond `gh`/`az` reference scope). |
| Infra | `rtk kubectl` | `kubectl` | 85.0 | Port after core | Task 1: `Kubectl` = Port after core (same rationale as `Docker`). |
| Infra | `rtk oc` | `oc` | 85.0 | Port after core | Task 1: `Oc` = Port after core (niche cloud CLI beyond `gh`/`az` scope). |
| Files | `rtk tree` | `tree` | 70.0 | Must port for MVP | Task 1: `Tree` = Must port for MVP. |
| Files | `rtk diff` | `diff` | 60.0 | Port after core | Task 1: `Diff` = Port after core (overlaps with `git diff`; secondary priority). |
| Network | `rtk curl` | `curl` | 70.0 | Port after core | Task 1: `Curl` = Port after core (cloud/network utility beyond `gh`/`az` scope). |
| Network | `rtk wget` | `wget` | 65.0 | Defer | Task 1: `Wget` = Defer (largely superseded by `Curl` in practice). |
| Build | `rtk mypy` | `python3 -m mypy`, `python -m mypy`, `mypy` | 80.0 | Port after core | Task 1: `Mypy` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Python | `rtk ruff` | `ruff` | 80.0 | Port after core | Task 1: `Ruff` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Python | `rtk pytest` | `python3 -m pytest`, `python -m pytest`, `pytest` | 90.0 | Port after core | Task 1: `Pytest` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Python | `rtk pip` | `pip3`, `pip`, `uv pip` | 75.0 | Port after core | Task 1: `Pip` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Go | `rtk go` | `go` | 85.0 | Port after core | Task 1: `Go` = Port after core (niche ecosystem beyond fork's stated JS/dotnet/git focus). |
| Go | `rtk golangci-lint run` | `golangci-lint run`, `golangci run` | 85.0 | Port after core | Task 1: `GolangciLint` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Ruby | `rtk bundle` | `bundle` | 70.0 | Port after core | No matching `Commands` variant in Task 1's inventory (`bundle install`/`update` has no dedicated `rtk bundle` top-level command; only `Rake`, `Rubocop`, `Rspec` exist for Ruby). Defaulting to Port after core per Method fallback. |
| Ruby | `rtk rake` | `bundle exec rails`, `bundle exec rake`, `bin/rails`, `rails`, `rake` | 85.0 | Port after core | Task 1: `Rake` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Tests | `rtk rspec` | `bundle exec rspec`, `bin/rspec`, `rspec` | 65.0 | Port after core | Task 1: `Rspec` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Build | `rtk rubocop` | `bundle exec rubocop`, `rubocop` | 65.0 | Port after core | Task 1: `Rubocop` = Port after core (`docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module, only TOML-filter-to-declarative-descriptor migration and the `gh`/`az` module abstraction). |
| Infra | `rtk aws` | `aws` | 80.0 | Port after core | Task 1: `Aws` = Port after core (cloud CLI beyond `gh`/`az` reference scope). |
| Infra | `rtk psql` | `psql` | 75.0 | Defer | Task 1: `Psql` = Defer (single-purpose DB client, low expected usage). |
| Infra | `rtk ansible-playbook` | `ansible-playbook` | 70.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| PackageManager | `rtk brew` | `brew` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| PackageManager | `rtk composer` | `composer` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| System | `rtk df` | `df` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk dotnet` | `dotnet` | 70.0 | Must port for MVP | Task 1: `Dotnet` = Must port for MVP (Phase 6 explicitly expands this module; core dogfooding target). |
| System | `rtk du` | `du` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk fail2ban-client` | `fail2ban-client` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk gcloud` | `gcloud` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk gradlew` | `./gradlew`, `gradlew.bat`, `gradlew`, `gradle` | 75.0 | Port after core | Task 1: `Gradlew` = Port after core. `filters/gradle.toml` exists and matches `gradlew`, but it is a separate rewrite-path filter already correctly marked `Replace` in `filter-inventory.md`; the `rtk gradlew` command module is a distinct bespoke Rust implementation, not the TOML filter, so it is not rescued by that tie-breaker. `docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module. |
| Build | `rtk hadolint` | `hadolint` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk helm` | `helm` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk iptables` | `iptables` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk make` | `make` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk markdownlint` | `markdownlint` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk mix` | `mix` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk mvn` | `./mvnw`, `mvnw.cmd`, `mvnw`, `mvn` | 82.0 | Port after core | Task 1: `Mvn` = Port after core. No corresponding TOML filter exists (`spring-boot.toml` only matches a different subcommand surface, not `mvn`), so there is no tie-breaker rescuing a `Replace` reading. `docs/PLANS.md` Phase 4, lines 434-486, does not authorize wholesale replacement of this bespoke command module. |
| Network | `rtk ping` | `ping` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk pio` | `pio` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Python | `rtk poetry` | `poetry` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk pre-commit` | `pre-commit` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| System | `rtk ps` | `ps` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk pulumi` | `pulumi` | 45.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk quarto` | `quarto` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Network | `rtk rsync` | `rsync` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk shellcheck` | `shellcheck` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk shopify` | `shopify` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk sops` | `sops` | 60.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk swift` | `swift` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| System | `rtk systemctl` | `systemctl` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk terraform` | `terraform` | 70.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Infra | `rtk tofu` | `tofu` | 70.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk trunk` | `trunk` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Python | `rtk uv` | `uv` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Build | `rtk yamllint` | `yamllint` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |
| Files | `rtk wc` | `wc` | 60.0 | Must port for MVP | Task 1: `Wc` = Must port for MVP (Phase 5 system-module scope). |
| Git | `rtk gt` | `gt` | 70.0 | Port after core | Task 1: `Gt` = Port after core (niche stacked-PR workflow, secondary to `git`/`gh`). |
| Infra | `rtk liquibase` | `liquibase` | 65.0 | Port after core | No matching `Commands` variant in Task 1's inventory. Defaulting to Port after core per Method fallback. |

## Concerns / uncertain classifications

- 33 of the 75 rules (`bundle`, `ansible-playbook`, `brew`, `composer`, `df`, `du`,
  `fail2ban-client`, `gcloud`, `hadolint`, `helm`, `iptables`, `make`,
  `markdownlint`, `mix`, `ping`, `pio`, `poetry`, `pre-commit`, `ps`, `pulumi`,
  `quarto`, `rsync`, `shellcheck`, `shopify`, `sops`, `swift`, `systemctl`,
  `terraform`, `tofu`, `trunk`, `uv`, `yamllint`, `liquibase`) have no corresponding
  variant in the Rust `Commands` enum inventoried
  by Task 1. These rewrite rules exist purely in the discover/rewrite-detection
  pipeline (`src/discover/rules.rs`) pointing at `rtk <cmd>` targets that are not
  (yet, or ever) implemented as top-level CLI commands in `src/main.rs`. This is
  plausible: `rules.rs` appears to be a broader, more speculative/aspirational
  catalog of "commands rtk could usefully wrap" used for discovery/analytics (e.g.
  `rtk discover`, `rtk learn` reporting "you could have saved N% by using `rtk
  terraform`") rather than a 1:1 mirror of implemented commands. All 32 default to
  `Port after core` per the Method section's fallback rather than `Defer`, since
  they still represent legitimate future scope, not confirmed-low-priority targets
  the way `Wget`/`Psql` are (which map to actual `Defer` `Commands` variants). All
  33 no-match rules are, by construction, also outside the `Replace with better
  abstraction` bucket — that disposition is only assigned via direct cross-reference
  to a Task 1 `Commands` row already carrying it, per the Task 1 review-cycle
  lesson, and no-match rules have no such row to inherit from.
- `rtk bundle` was mapped to "no match" rather than lumped in with `Rake` even
  though both are Ruby/`bundle`-adjacent, because Task 1's `Rake` disposition is
  specifically about `rake`/`rails test` output filtering, not `bundle
  install`/`update` — a genuinely different command surface with no corresponding
  `Commands` variant.
- The `category` field in `rules.rs` (`Git`, `GitHub`, `GitLab`, `Cargo`,
  `PackageManager`, `Files`, `Build`, `Tests`, `Infra`, `Network`, `Python`, `Go`,
  `Ruby`, `System`) is much coarser than Task 1's per-command dispositions — e.g.
  `Build` alone spans `tsc` (Must port for MVP), `lint` (Replace with better
  abstraction), `prettier` (Port after core), `dotnet` (Must port for MVP), and a
  dozen no-match tools. Per the Method section's guidance, disposition was assigned
  by matching each rule's `rtk_cmd` target to its specific Task 1 `Commands` row,
  not by treating `category` as a single disposition bucket — a literal reading of
  "rules whose `category` matches a command already marked X" would have produced
  inconsistent, category-wide dispositions that don't reflect Task 1's actual
  per-command judgment calls (e.g. it would have forced `dotnet` and `lint` to the
  same disposition solely because both are tagged `Build`).
