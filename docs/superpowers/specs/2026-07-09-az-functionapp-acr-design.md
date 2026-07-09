# Design: `az functionapp` and `az acr` Filters

**Date:** 2026-07-09
**Status:** Approved (spec review pending)
**Phase:** `docs/PLANS.md` Phase 8 (Azure CLI Demonstration Module) — first post-MVP deferral slice

## Context

The `az` MVP (`docs/superpowers/specs/2026-07-08-az-command-module-design.md`, merged
`dcb2b34`) explicitly deferred `bicep`, `acr`, `aks`, `functionapp`, `monitor`, `az rest`,
subscription-level `az deployment sub`, and deployment-failure summarization. This design
picks up the next slice: **`functionapp`** and **`acr`**, chosen because both have real,
fixture-backable data in the same Azure subscription used for the MVP (`FNZ Q-Hub Azure
subscription`); `aks` (no clusters in this subscription — `az aks list` returns `[]`) and
`bicep` (CLI installed mid-session, but no real `.bicep` template file available to build
fixtures from) remain deferred to a future pass.

## Empirical findings (this session, real subscription)

| Command | Real output shape | Notes |
|---|---|---|
| `az functionapp list` | Bare JSON array of `Microsoft.Web/sites` objects — **identical top-level shape to `az webapp list`**, confirmed against 3 real function apps (`terraform-backup-functions`, `qhub-teams-transcripts`, `qhub-ticket-relay`) | `kind` is `"functionapp,linux"` or `"functionapp"`; `siteConfig.linuxFxVersion` carries the runtime stack (`"DOTNET-ISOLATED|10.0"`, `"PYTHON|3.9"`, or `""` when unset) |
| `az functionapp show` | Single object, same shape as one `list` element | Captured for `terraform-backup-functions` |
| `az acr list` | Bare JSON array of `Microsoft.ContainerRegistry/registries` objects: `name`, `loginServer`, `sku.{name,tier}`, `location`, `resourceGroup`, `adminUserEnabled`, `provisioningState`, plus verbose nested `policies`/`networkRuleSet`/etc. | Captured 2 real registries (`icppi`, `icppipdn`) |
| `az acr repository list --name X` | **Bare JSON array of strings** (repository names), not objects — e.g. `["ifp.portal.bnl.demo", "ifp.reportingengine.web.linux", ...]` | Captured against `icppi` (5 repositories) |
| `az acr repository show-tags --name X --repository Y` | **Bare JSON array of strings** (tag names) | Captured against `icppi`/`ifp.valuationengine.web` (8 tags) |

**Key implication:** `functionapp` needs no new JSON-shape handling — it is a second
consumer of the exact `Microsoft.Web/sites` shape `webapp` already parses. `acr repository
list`/`show-tags` are the first `az` filters in this codebase to receive a bare array of
*strings* rather than objects, requiring new formatting logic distinct from the existing
`JoinWithOverflow` (which newline-joins pre-formatted object strings).

## Architecture

All new filters live in the existing `RtkSharp/Commands/Cloud/AzFilters.cs`; all new
dispatch entries live in the existing `RtkSharp/Commands/Cloud/AzCommand.cs`. No new files,
no changes to the six-phase execution contract, no changes to `JsonCompaction`.

### `functionapp` — mirrors `webapp` plus one runtime field

- `FormatFunctionApp(JsonElement w)`: identical to `FormatWebapp` (`AzFilters.cs:314-325`)
  — name, state, kind, location, sku, https, rg, host — plus one appended field, giving:
  `$"{name} {state} {kind} {location} sku:{sku} https:{https} rg:{rg} host:{host} runtime:{stack}"`
  where `{stack}` is `JNestedStr(w, "siteConfig", "linuxFxVersion", "")` (nested lookup,
  already defined at `AzFilters.cs:497-498`) if non-empty, else
  `JNestedStr(w, "siteConfig", "windowsFxVersion", "?")` — i.e. try Linux stack first, fall
  back to Windows stack, fall back to `"?"` if both are empty/absent.
  - Deliberately **not** extracted into a shared helper with `FormatWebapp`: the two
    functions are one field apart, and per this codebase's established DRY threshold
    ("introduce abstractions when two or more instances of similar logic exist"), two near-
    identical 9-field formatters is exactly the borderline case — but `webapp` has no
    runtime-stack concept (it's not meaningful for arbitrary App Service sites the way it is
    for Functions), so forcing a shared helper would mean threading an "is this a function
    app" flag through `FormatWebapp` for no benefit to `webapp` callers. Two small, single-
    purpose functions stay clearer than one parameterized one here.
- `FilterFunctionAppList(string jsonStr)`: identical structure to `FilterWebappList`
  (`AzFilters.cs:328-360`) — bare top-level array, `MaxItems`-capped, `JoinWithOverflow`
  with label `"function apps"`.
- `FilterFunctionAppShow(string jsonStr)`: identical structure to `FilterWebappShow`
  (`AzFilters.cs:363-380`) — single object.

### `acr` — three filters

- `FormatAcrRegistry(JsonElement r)`: name, loginServer, sku (via `JNestedStr(r, "sku",
  "name", "?")`), location, resourceGroup, adminUserEnabled (via `JBoolStr`), provisioningState
  — mirrors `FormatStorageAccount`'s (`AzFilters.cs:384-395`) field-count and style.
- `FilterAcrList(string jsonStr)`: identical structure to `FilterStorageAccountList`
  (`AzFilters.cs:398-430`) — bare top-level array, `MaxItems`-capped, label `"registries"`.
- `FilterAcrRepositoryList(string jsonStr)` / `FilterAcrRepositoryShowTags(string jsonStr)`:
  new shape — bare top-level array of **strings**, not objects. New private helper
  `FormatStringArrayWithOverflow(JsonElement arr, int max, string label)`:
  - Validates `arr.ValueKind == JsonValueKind.Array`; returns `null` (fallback-to-raw) if not,
    or if any element is not a `JsonValueKind.String` (defensive — real captures are 100%
    strings, but a filter must never assume an unvalidated shape).
  - Takes up to `MaxItems` string values, comma-joins them on a **single line** (deliberately
    not `JoinWithOverflow`'s newline convention — a flat list of short names reads better
    joined than stacked, and this halves the printed line count for a typical repository/tag
    listing), prefixed with `"{total} {label}: "`, e.g.
    `"5 repositories: ifp.portal.bnl.demo, ifp.reportingengine.web.linux, ifp.valuationengine.web, ifp.valuationengine.web.linux, ifp.valuationengine.web.windows"`.
  - Appends `" (+{total - max} more)"` when `total > max`, matching the overflow-signaling
    convention (distinct wording, since `JoinWithOverflow`'s `"… +{N} more {label}"` is
    newline-prefixed and reads oddly appended inline).
  - `FilterAcrRepositoryList` calls this with `label: "repositories"`;
    `FilterAcrRepositoryShowTags` calls it with `label: "tags"`.

### Dispatch (`AzCommand.cs`)

Four new arms added to the existing `(subcommand, op)` switch (`AzCommand.cs:68-91`):

```csharp
("functionapp", "list") => await RunAzFilteredAsync(
    ["functionapp", "list"], opArgs, verbose, executor, AzFilters.FilterFunctionAppList)...,
("functionapp", "show") => await RunAzFilteredAsync(
    ["functionapp", "show"], opArgs, verbose, executor, AzFilters.FilterFunctionAppShow)...,
("acr", "list") => await RunAzFilteredAsync(
    ["acr", "list"], opArgs, verbose, executor, AzFilters.FilterAcrList)...,
("acr", "repository") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
    ["acr", "repository", "list"], opArgs[1..], verbose, executor, AzFilters.FilterAcrRepositoryList)...,
("acr", "repository") when opArgs.Length > 0 && opArgs[0] == "show-tags" => await RunAzFilteredAsync(
    ["acr", "repository", "show-tags"], opArgs[1..], verbose, executor, AzFilters.FilterAcrRepositoryShowTags)...,
```

`functionapp` is two-level (matches `webapp`/`group`'s existing pattern exactly). `acr list`
is two-level; `acr repository list`/`show-tags` are three-level, using the exact `when
opArgs[0] == "..."` guard pattern already established for `storage account` and `deployment
group` (`AzCommand.cs:78-89`) — no new dispatch mechanism required.

## Redaction

**No new redaction rules needed.** None of the five captured commands expose secret-shaped
values:
- `functionapp list`/`show` share `webapp`'s existing shape, already covered by
  `AzFilters.Redact` (applied uniformly to all `RunAzFilteredAsync` output — no per-filter
  opt-out exists, so this is automatic, not a new decision).
- `acr list` exposes `adminUserEnabled` (a boolean flag, not a credential) — the actual admin
  password/username require the separate, out-of-scope `az acr credential show`.
- `acr repository list`/`show-tags` are bare string arrays of repository/tag names — no
  object fields for a key-name regex to match against at all.

`AzFilters.Redact` still runs against all new filters' output (it's applied unconditionally
in `RunAzFilteredAsync`/`RunGenericAsync`, not opted into per-filter), so if a future
registry/function app naming convention happened to embed something secret-shaped, it would
still be caught — this is existing behavior, not a new mechanism.

## Testing

New tests added to the existing `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` (no new
test file), following the established pattern: real captured fixtures frozen as constants,
one snapshot-style assertion per filter plus a ≥60%-savings assertion, and dispatch tests
extending the existing `RecordingExecutor`-based table to cover the 5 new op combinations
(`functionapp list`, `functionapp show`, `acr list`, `acr repository list`, `acr repository
show-tags`) plus confirming the explicit-`--output table` passthrough policy still holds for
all of them (already-generic logic, but each new op needs its own dispatch-routing test per
this codebase's existing per-op-family convention).

Fixtures to capture fresh during implementation (real subscription, same session-verified
approach as the MVP):
- `FunctionAppListRaw` / `FunctionAppShowRaw` — `terraform-backup-functions` (already
  captured this session, values above; implementer re-captures via `az functionapp
  list`/`show --name terraform-backup-functions --resource-group terraform-backup-rg` to get
  a clean, current copy for the fixture file).
- `AcrListRaw` — `icppi`/`icppipdn` (already captured; implementer re-captures via `az acr
  list`).
- `AcrRepositoryListRaw` — `icppi` (already captured; `az acr repository list --name icppi`).
- `AcrRepositoryShowTagsRaw` — `icppi`/`ifp.valuationengine.web` (already captured; `az acr
  repository show-tags --name icppi --repository ifp.valuationengine.web`).

No parity-test entry — same treatment as every other `az` filter, no Rust oracle exists.

## Non-Goals (this design)

- `az aks` (list/show/get-credentials) — no AKS clusters exist in the verified subscription
  (`az aks list` → `[]`); deferred until a real cluster is available to capture against.
- `az bicep` (build/decompile/version) — CLI became available mid-session
  (`0.44.1`), but no real `.bicep` template file was found to build/decompile fixtures from;
  deferred to its own design pass once a real template is available. Unlike the other
  deferrals, this is purely a fixture-availability gap, not a scope decision — bicep's
  output shape (ARM JSON for `build`, `.bicep` source text for `decompile`, plain version
  string for `version`) is structurally unlike anything else in this module and deserves its
  own design once real input is available, rather than being force-fit into this pass.
- `az functionapp` beyond `list`/`show` (e.g. `log`, `deployment list-publishing-
  credentials`, `config appsettings list`) — out of scope, matches the MVP's own
  `list`/`show`-only scoping for every other family.
- `az acr` beyond `list`/`repository list`/`repository show-tags` (e.g. `acr build`, `acr
  credential show`, `acr task`) — out of scope; `credential show` in particular is
  deliberately excluded since it returns actual admin passwords, which would need dedicated
  redaction design rather than the "no redaction needed" conclusion this design reaches for
  the in-scope commands.
