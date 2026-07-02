# Hook Behavior Inventory — Agent Integrations

This document inventories per-agent hook/integration behavior across the 10 named
coding agents listed in `docs/PLANS.md` Phase 0 ("Inventory hook behavior across
Claude, Cursor, Copilot, Gemini, Codex, OpenCode, Pi, Hermes, Cline, and Windsurf"),
as implemented in the Rust reference implementation. It complements
`docs/parity/command-inventory.md` (Task 1) and `docs/parity/rewrite-rule-inventory.md`
(Task 2/3) and is the reference for Phase 9's hook/agent-integration planning.

## Scope

- **In scope**: for each of the 10 named agents, the hook artifact format it installs,
  the install mode(s) `rtk init` supports for it, and whether it has a dedicated code
  path (hook processor and/or install logic) or falls back to a generic/instruction-only
  path.
- **Row count is fixed at exactly 10** (one per named agent), unlike Tasks 1-3 where
  the count is derived from source array/enum sizes. Every named agent gets a row
  regardless of how much dedicated code exists for it.
- **Out of scope**: agents not named in `docs/PLANS.md`'s Phase 0 list even though the
  Rust source has code for them (`AgentTarget::Kilocode`, `AgentTarget::Antigravity`).
  These are noted here for completeness but excluded from the 10-row table.

## Provenance — search method and actual output

`src/hooks/` is organized by *function* (hook checking, rewriting, trust, integrity),
not by *agent name*, so this task first determined where agent-specific logic actually
lives before inventorying it.

**Step 1a — narrow grep in `src/hooks/` only:**

```
grep -rn "Claude\|Cursor\|Copilot\|Gemini\|Codex\|OpenCode\|Pi\b\|Hermes\|Cline\|Windsurf" src/hooks/
```

Unlike the brief's note (which recorded zero hits against `src/hooks/mod.rs` alone
during planning), running this grep against the full `src/hooks/` directory returned
**302 matching lines**, concentrated in `src/hooks/hook_cmd.rs` (per-agent hook
processors and JSON payload handling), `src/hooks/init.rs` (per-agent install/uninstall
logic — the majority of hits), `src/hooks/permissions.rs` (a `Host` enum: `Claude`,
`Cursor`, `Gemini`), and `src/hooks/README.md` (an existing agent/install-mode summary
table). `src/hooks/mod.rs` itself (a thin re-export module) indeed contains no agent
names, which is consistent with the brief's note.

**Step 1b — broad grep across `src/`:**

```
grep -rln "Claude\|Cursor\|Copilot\|Gemini\|Codex\|OpenCode\|Hermes\|Cline\|Windsurf" src/
```

Returned 22 files. Besides the `src/hooks/*` files above, agent names also appear in
`src/analytics/*` (usage/economics tracking, not install logic), `src/cmds/git/gh_cmd.rs`
/ `glab_cmd.rs` (unrelated — `gh`/`glab` CLI output containing the word "Codex" or
similar in fixtures/output, not agent-hook logic), `src/core/telemetry.rs`,
`src/core/tracking.rs`, `src/discover/*` (rewrite-rule detection, covered by Tasks 2-3),
`src/learn/*`, and `src/main.rs` (CLI arg definitions: `AgentTarget` enum, `HookCommands`
enum, and per-flag dispatch in the `init` subcommand handler). None of these introduce
*additional* per-agent hook/install logic beyond what's in `src/hooks/hook_cmd.rs` and
`src/hooks/init.rs`; they reference agent names only in passing (telemetry event names,
gh/glab CLI passthrough fixtures, discover-registry rule strings already covered by
Tasks 2-3).

**Conclusion of Step 1**: per-agent dispatch logic lives in three files:
- `src/main.rs` — `AgentTarget` enum (lines 34-52: `Claude, Cursor, Windsurf, Cline,
  Kilocode, Antigravity, Pi, Hermes`) plus separate boolean flags for `--gemini`,
  `--codex`, `--copilot`, `--opencode` (special modes not modeled as `AgentTarget`
  variants) on the `init` subcommand (~line 334-376), and the `HookCommands` enum
  (lines 774-793: `Claude, Cursor, Gemini, Copilot, Check`) for the `hook` subcommand.
- `src/hooks/init.rs` — all install/uninstall logic per agent (hook files, config
  patches, instruction files), ~4000+ lines, organized in per-agent sections (e.g.
  `─── Windsurf support ───`, `─── Hermes support ───`, `─── Cursor Agent support ───`,
  `─── Pi coding agent support ───`, `─── Copilot integration ───`, `─── Gemini CLI
  support ───`).
- `src/hooks/hook_cmd.rs` — native runtime hook *processors* (stdin JSON → rewrite
  decision → stdout JSON/exit code) for the subset of agents with a live hook protocol:
  Claude (`run_claude`), Cursor (`run_cursor`), Gemini (`run_gemini`), Copilot
  (`run_copilot`, auto-detecting VS Code vs. Copilot CLI JSON shapes).

`src/hooks/README.md` (lines 20-34, 83-91) independently documents an install-mode
table and a per-tool permission-support table that corroborate the above; both were
cross-checked against `src/main.rs` and `src/hooks/init.rs` rather than trusted as
sole source, since a stale README is possible.

## Step 4 verification

```
grep -c "^|" docs/parity/hook-inventory.md
```
Expected and actual: row count minus 2 (header + separator) = **10**.

## Inventory

| Agent | Artifact format | Install modes supported | Source location | Disposition | Rationale |
|---|---|---|---|---|---|
| Claude | Native JSON hook (PreToolUse stdin/stdout protocol) + `settings.json` patch + optional `CLAUDE.md`/RTK.md instruction block | Default (global, `rtk init -g`), hook-only (`--hook-only`), legacy Claude-MD block (`--claude-md`) | `src/hooks/hook_cmd.rs` (`run_claude`, `HookCommands::Claude`), `src/hooks/init.rs` (default/hook-only/claude-md install paths), `src/hooks/permissions.rs` (`Host::Claude`) | Must port for MVP | Named as one of the 4 primary agents; has the most complete dedicated code path (native hook processor + full ask/deny/allow permission support) and `docs/PLANS.md` frames Claude Code as the primary consumer. |
| Cursor | Native JSON hook registered in `hooks.json` (global-only; legacy shell-script mode also supported for migration) | Global-only (`rtk init -g --agent cursor`) | `src/hooks/hook_cmd.rs` (`run_cursor`, `HookCommands::Cursor`), `src/hooks/init.rs` (`─── Cursor Agent support ───`, ~line 2979 onward: install/patch/remove `hooks.json`), `src/hooks/permissions.rs` (`Host::Cursor`), `src/main.rs` (`AgentTarget::Cursor`) | Must port for MVP | One of the 4 primary agents named in the brief; has a dedicated native hook processor plus full ask-support permission handling (per `src/hooks/README.md`'s per-tool support table). |
| Copilot | JSON hook config, dual-format auto-detected (VS Code Copilot Chat: snake_case `tool_name`/`tool_input`/`updatedInput`; Copilot CLI: camelCase `toolName`/`toolArgs`/`modifiedArgs`) | Project-scoped (`rtk init --copilot`) and global/user-scoped (`rtk init -g --copilot`) | `src/hooks/hook_cmd.rs` (`run_copilot`, `detect_format`, `handle_copilot_cli`, `HookCommands::Copilot`), `src/hooks/init.rs` (`─── Copilot integration ───`, ~line 3870 onward: `run_copilot`, `run_copilot_global`, uninstall variants), `src/main.rs` (`--copilot` flag, ~line 374-376) | Must port for MVP | One of the 4 primary agents named in the brief; dedicated native hook processor handling two distinct JSON schemas, plus `updatedInput`-based ask support in the VS Code path. |
| Gemini | Shell wrapper script (delegates to `rtk hook gemini`) + `settings.json` patch (`BeforeTool` protocol) + `GEMINI.md` instruction file | Global-only (`rtk init -g --gemini`) | `src/hooks/hook_cmd.rs` (`run_gemini`, `HookCommands::Gemini`), `src/hooks/init.rs` (`─── Gemini CLI support ───`, ~line 3599 onward), `src/hooks/permissions.rs` (`Host::Gemini`, `load_gemini_rules`) | Must port for MVP | One of the 4 primary agents named in the brief; dedicated native hook processor, though limited to allow/deny only (no native `ask` mode support in Gemini's `BeforeTool` protocol — documented limitation in `src/hooks/README.md`'s per-tool support table, not a porting gap). |
| Codex | Instruction files only: `RTK.md` written to `$CODEX_HOME`/`~/.codex`, patches `AGENTS.md` | Project and global (`rtk init --codex`) | `src/hooks/init.rs` (`─── Codex config directory / RTK.md install ───`, ~line 2281-2320, uninstall ~line 861-866), `src/main.rs` (`--codex` flag, ~line 370-372: "no Claude hook patching") | Port after core | Has dedicated install logic (RTK.md + AGENTS.md patching) but explicitly **no native hook processor** in `hook_cmd.rs`/`HookCommands` — `src/hooks/README.md`'s permission table notes "ask parsed but no-op → allow (limitation — fails open)", i.e. Codex is instruction-only with no live command interception, a materially thinner integration than the 4 primary agents. |
| OpenCode | Embedded TypeScript plugin (`~/.config/opencode/plugins/rtk.ts`), auto-rewrite (no JSON hook protocol/stdin-stdout exchange) | Global-only (`rtk init -g --opencode`) | `src/hooks/init.rs` (`─── OpenCode plugin support ───`, ~line 2926-2970: `opencode_plugin_path`, `write_opencode_plugin`, `remove_opencode_plugin`), `src/main.rs` (`--opencode` flag, ~line 334-336) | Port after core | Has dedicated install logic (embedded plugin template written/removed by RTK) but no `hook_cmd.rs` processor and no permission `Host` variant — the plugin is a self-contained rewrite mechanism outside RTK's native hook/permission pipeline, so porting it is lower-complexity than the 4 primary agents but still a known, working code path (not net-new design). |
| Pi | Extension file (`.pi/extensions/rtk.ts`, hook-only; no `AGENTS.md`-equivalent instruction injection) | Project and global (`rtk init --agent pi`, uninstall via `--uninstall --agent pi`) | `src/hooks/init.rs` (`─── Pi coding agent support ───`, ~line 2788-2925: `resolve_pi_config_dir`, `write_pi_extension`, `uninstall_pi`), `src/main.rs` (`AgentTarget::Pi`) | Port after core | Has dedicated install logic (extension file template, global/local scoping, idempotent write) but no `hook_cmd.rs` processor and no permission `Host` variant, same category as OpenCode — working code path, not primary-tier. |
| Hermes | Python plugin directory (`~/.hermes/plugins/rtk-rewrite/`) + `config.yaml` patch (`plugins.enabled` list) | Project and global (`rtk init --agent hermes`, dedicated uninstall path) | `src/hooks/init.rs` (`─── Hermes support ───`, ~line 1760-1900: `run_hermes_mode`, plugin init + manifest write, YAML config patch, uninstall), `src/main.rs` (`AgentTarget::Hermes`, dedicated `uninstall_hermes` dispatch branch at ~line 1459-1460) | Port after core | Has the most substantial non-primary-agent code path found (Python plugin templating + YAML config patch, idempotency-tested per `src/hooks/init.rs` test comments) but no `hook_cmd.rs` processor and no permission `Host` variant — same tier as OpenCode/Pi. |
| Cline | Instruction file only: `.clinerules` written to project root (workspace-scoped) | Project-scoped (`rtk init --agent cline`) | `src/hooks/init.rs` (`─── Cline / Roo Code support ───`, ~line 1554-1600), `src/main.rs` (`AgentTarget::Cline`) | Defer | Dedicated code exists but is instruction-text-only (no hook artifact, no `hook_cmd.rs` processor, no permission model) — the entire integration is "write a rules file"; porting it is trivial relative to the other 8 but there is no rewrite/interception behavior to port, only a static template, so it is deprioritized behind agents with actual hook logic. |
| Windsurf | Instruction file only: `.windsurfrules` written to project root (workspace-scoped) | Project-scoped, but note `src/hooks/init.rs` also gates one variant as global-only (`"Windsurf support is global-only"` bail message at ~line 294) — the two references are inconsistent in the source between a per-project rules file and a global-only guard; recorded as observed, not reconciled here | `src/hooks/init.rs` (`─── Windsurf support ───`, ~line 1546-1640, plus the global-only guard at ~line 290-294), `src/main.rs` (`AgentTarget::Windsurf`) | Defer | Same tier as Cline: dedicated code exists but is instruction-text-only with no hook artifact or permission model. The install-mode inconsistency noted above (project-scoped rules file vs. a global-only validation guard elsewhere in `init.rs`) is itself a reason to defer rather than port as-is — it should be resolved by design, not carried over. |

## Notes on agents outside the fixed 10-row scope

`src/main.rs`'s `AgentTarget` enum also defines `Kilocode` and `Antigravity` (project-
scoped variants per the `--agent kilocode`/`--agent antigravity` bail-message hints at
`src/main.rs` ~line 1932-1938), which are not named in `docs/PLANS.md` Phase 0's list of
10 agents and are therefore excluded from the table above per this task's fixed scope.
They are noted here for Phase 9 awareness only.
