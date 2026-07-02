# TOML Filter Inventory

## Provenance

- Source: `src/filters/*.toml` in this repository (rtk Rust CLI, `.toml` filter DSL configs).
- File count via `ls src/filters/*.toml | wc -l`: **63**
- File count via extraction pass (reading every file and extracting `description` / `match_command` / `[[tests.` counts): **63**
- Counts match; no discrepancy to reconcile.
- `src/filters/README.md` is not a filter and is excluded per the task brief.
- Cross-referenced against `docs/parity/command-inventory.md` (available) to check for `match_command` overlap with commands Task 1 marked `Must port for MVP` that would otherwise have no coverage. No such gap was found — see Rationale column and note below the table.

## Disposition legend

- `Must port for MVP` — required for baseline parity.
- `Port after core` — needed, but only once the core module system exists.
- `Replace with better abstraction` — superseded by a different design (here: `docs/PLANS.md` Phase 4's plan to migrate TOML filters to declarative module descriptors).
- `Defer` — out of scope for now, revisit later.

## Method note

Per Global Constraints and the task brief: `docs/PLANS.md` Phase 4 explicitly names TOML-filter migration to declarative module descriptors as in-scope work. Because every filter in `src/filters/` is a TOML filter, **all 63 default to `Replace with better abstraction`**, unless a filter's `match_command` overlaps a command Task 1 marked `Must port for MVP` with no other filter/module covering that command — in which case it would be marked `Port after core` instead, to avoid losing behavior before the module system exists.

That override was checked against every row below. The only near-miss is `dotnet-build.toml` (`match_command = "^dotnet\s+build\b"`), which overlaps the `Dotnet` command that Task 1 marked `Must port for MVP` (`cmds::dotnet::dotnet_cmd`, description "compact output (build/test/restore/format)"). Because the `Dotnet` command module itself already covers `dotnet build` and is already slated as Must port for MVP, there is no coverage gap — the override does not apply here. No other filter's `match_command` overlaps an MVP command at all. Therefore no rows use `Port after core`.

## Filters

| Filter file | description | match_command | test case count | Disposition | Rationale |
|---|---|---|---|---|---|
| `ansible-playbook` | "Compact ansible-playbook output" | `"^ansible-playbook\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `basedpyright` | "Compact basedpyright type checker output — strip blank lines, keep errors" | `"^basedpyright\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `biome` | "Compact Biome lint/format output — strip blank lines, keep diagnostics" | `"^biome\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `brew-install` | "Compact brew install/upgrade output — strip downloads, short-circuit when already installed" | `"^brew\\s+(install\|upgrade)\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `bundle-install` | "Compact bundle install/update — strip 'Using' lines, keep installs and errors" | `"^bundle\\s+(install\|update)\\b"` | 4 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `composer-install` | "Compact composer install/update/require output — strip downloads, short-circuit when up-to-date" | `"^composer\\s+(install\|update\|require)\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `df` | "Compact df output — truncate wide columns, limit rows" | `"^df(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `dotnet-build` | "Compact dotnet build output — short-circuit on success, strip banners" | `"^dotnet\\s+build\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4; command is separately covered by the `Dotnet` module itself (Task 1: Must port for MVP), so no coverage gap exists and the override does not apply |
| `du` | "Compact du output" | `"^du\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `fail2ban-client` | "Compact fail2ban-client output" | `"^fail2ban-client\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `gcc` | "Compact gcc/g++ compiler output — strip notes, keep errors and warnings" | `"^g(cc\|\\+\\+)\\b"` | 4 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `gcloud` | "Compact gcloud output" | `"^gcloud\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `gradle` | "Compact Gradle build output — strip progress, keep tasks and errors" | `"^(gradle\|gradlew\|\\./)gradlew?\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `hadolint` | "Compact hadolint Dockerfile linting output" | `"^hadolint\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `helm` | "Compact helm output" | `"^helm\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `iptables` | "Compact iptables output" | `"^iptables\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `jira` | "Compact Jira CLI output — strip verbose metadata, keep essentials" | `"^jira\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `jj` | "Compact Jujutsu (jj) output — strip blank lines, truncate" | `"^jj\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `jq` | "Compact jq output — truncate large JSON results" | `"^jq\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `just` | "Compact just task runner output — strip recipe headers, keep command output" | `"^just\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `liquibase` | "Compact liquibase output — strip headers and generic info" | `"(?:^\|/)liquibase(?:\\s\|$)"` | 4 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `make` | "Compact make output" | `"^make\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `markdownlint` | "Compact markdownlint output — strip blank lines, limit rows" | `"^markdownlint\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `mise` | "Compact mise task runner output — strip status lines, keep task results" | `"^mise\\s+(run\|exec\|install\|upgrade)\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `mix-compile` | "Compact mix compile output" | `"^mix\\s+compile(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `mix-format` | "Compact mix format output" | `"^mix\\s+format(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `nx` | "Compact Nx monorepo output — strip task graph noise, keep results" | `"^(pnpm\\s+)?nx\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `ollama` | "Strip ANSI spinners and cursor control from ollama output, keep final text" | `"^ollama\\s+run\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `oxlint` | "Compact oxlint output — strip blank lines, keep diagnostics" | `"^oxlint\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `ping` | "Compact ping output — strip per-packet lines, keep summary" | `"^ping\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pio-run` | "Compact PlatformIO build output" | `"^pio\\s+run"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `poetry-install` | "Compact poetry install/lock/update output — strip downloads, short-circuit when up-to-date" | `"^poetry\\s+(install\|lock\|update)\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pre-commit` | "Compact pre-commit output" | `"^pre-commit\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `ps` | "Compact ps output — truncate wide lines, limit rows" | `"^ps(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pulumi-destroy` | "Compact Pulumi destroy output" | `"^pulumi\\s+destroy(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pulumi-preview` | "Compact Pulumi preview output" | `"^pulumi\\s+preview(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pulumi-refresh` | "Compact Pulumi refresh (state reconciliation) output" | `"^pulumi\\s+refresh(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pulumi-stack` | "Compact Pulumi stack (ls/output) output" | `"^pulumi\\s+stack(\\s+(ls\|output\|history\|select\|init\|rm\|rename\|tag\|unselect\|change-secrets-provider)\\b\|\\s*$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `pulumi-up` | "Compact Pulumi up (apply) output" | `"^pulumi\\s+up(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `quarto-render` | "Compact quarto render output" | `"^quarto\\s+render"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `rsync` | "Compact rsync output — short-circuit on success, strip progress" | `"^rsync\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `shellcheck` | "Compact shellcheck output — strip blank lines, keep caret indicators for error position" | `"^shellcheck\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `shopify-theme` | "Compact shopify theme push/pull output" | `"^shopify\\s+theme\\s+(push\|pull)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `skopeo` | "Compact skopeo output — truncate large manifests, strip verbosity" | `"^skopeo\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `sops` | "Compact sops output" | `"^sops\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `spring-boot` | "Compact Spring Boot output — strip banner and verbose startup logs, keep key events" | `"^(mvn\\s+spring-boot:run\|java\\s+-jar.*\\.jar\|gradle\\s+.*bootRun)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `ssh` | "Compact ssh output — strip connection banners, keep command output" | `"^ssh\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `stat` | "Compact stat output — strip device/inode/birth noise" | `"^stat\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `swift-build` | "Compact swift build output — short-circuit on success, strip Compiling/Linking" | `"^swift\\s+build\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `systemctl-status` | "Compact systemctl status output — strip blank lines, limit to 20 lines" | `"^systemctl\\s+status\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `task` | "Compact go-task output — strip task headers, keep command results" | `"^task\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `terraform-plan` | "Compact Terraform plan output" | `"^terraform\\s+plan"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `tofu-fmt` | "Compact OpenTofu fmt output" | `"^tofu\\s+fmt(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `tofu-init` | "Compact OpenTofu init output" | `"^tofu\\s+init(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `tofu-plan` | "Compact OpenTofu plan output" | `"^tofu\\s+plan(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `tofu-validate` | "Compact OpenTofu validate output" | `"^tofu\\s+validate(\\s\|$)"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `trunk-build` | "Compact trunk build output" | `"^trunk\\s+build"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `turbo` | "Compact Turborepo output — strip cache status noise, keep task results" | `"^turbo\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `ty` | "Compact ty type checker output — strip blank lines, keep errors" | `"^ty\\b"` | 3 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `uv-sync` | "Compact uv sync/pip install output — strip downloads, short-circuit when up-to-date" | `"^uv\\s+(sync\|pip\\s+install)\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `xcodebuild` | "Compact xcodebuild output — strip build phases, keep errors/warnings/summary" | `"^xcodebuild\\b"` | 4 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `yadm` | "Compact yadm (git wrapper) output — same filtering as git" | `"^yadm\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |
| `yamllint` | "Compact yamllint output — strip blank lines, limit rows" | `"^yamllint\\b"` | 2 | Replace with better abstraction | migrates to declarative module descriptor per docs/PLANS.md Phase 4 |

## Summary

- Total filters: 63
- `Must port for MVP`: 0
- `Port after core`: 0
- `Replace with better abstraction`: 63
- `Defer`: 0
