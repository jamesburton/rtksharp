# Design: `az` (Azure CLI) Command Module (MVP)

**Date:** 2026-07-08
**Status:** Approved (spec review pending)
**Phase:** `docs/PLANS.md` Phase 8 (Azure CLI Demonstration Module) — MVP slice, not full scope

## Context

`docs/PLANS.md`'s Phase 8 describes an ambitious `az` module covering account/group/
deployment/bicep/acr/aks/webapp/functionapp/storage/monitor/`az rest` — a large surface
never started (no `az` verb exists anywhere in `RtkSharp/Cli/CommandRegistry.cs` today).
Like the `.cs` file-based-app feature, this is a pure superset feature: the Rust oracle
(`src/`) has no `az` support at all, so there is no byte-exact target — RtkSharp is free to
define its own filtering behavior.

`az` CLI 2.84.0 is installed and authenticated to a real subscription (`FNZ Q-Hub Azure
subscription`, matching this repo's own `CLAUDE.md` Azure DevOps context) — every claim
below is grounded in real captures against that subscription this session, not invented.

## Scope (MVP)

Five subcommand families get dedicated compact filters, each `list`/`show` pair:

- `az account show` / `az account list`
- `az group list` / `az group show`
- `az deployment group list` / `az deployment group show`
- `az webapp list` / `az webapp show`
- `az storage account list` / `az storage account show`

Every other `az` subcommand falls through a **generic JSON-compaction filter** (mirroring
`AwsCommand.RunGenericAsync`) rather than running fully raw, so the whole `az` surface gets
*some* token-savings benefit, not just the five named families.

**Explicitly out of scope for this MVP** (deferred, not silently dropped):
bicep, acr, aks, functionapp, monitor, `az rest`, subscription-level `az deployment sub`,
and deployment-failure-specific summarization (a failed deployment/op just takes the
existing raw-stderr-plus-tee-hint fallback path like every other filtered op — no dedicated
"why did this fail" summarizer in this pass).

## Empirical findings (az CLI 2.84.0, this session, real subscription)

| Command | Real output shape | Size |
|---|---|---|
| `az account show` | Single JSON object: `environmentName`, `homeTenantId`, `id` (subscription ID), `isDefault`, `name`, `state`, `tenantId`, `user.{name,type}` | 12 lines |
| `az account list` | Array of the above shape, one per subscription | small, scales with subscription count |
| `az group list` | Array of `{id, location, managedBy, name, properties.provisioningState, tags, type}` | 662 lines for ~9 groups in this subscription |
| `az group show` | Single object, same shape as one `list` element | ~9 lines |
| `az deployment group list` | Array of deployment objects — each has `properties.{correlationId, debugSetting, dependencies, duration, error, mode, outputResources[], outputs, parameters{...}, provisioningState, timestamp, ...}` | 119 lines for a **single** deployment — extremely verbose per-item |
| `az webapp list` | Array of full App Service objects (dozens of fields: `hostNameSslStates[]`, `dnsConfiguration`, `siteConfig`-adjacent flags, etc.) | **7253 lines for the apps in this subscription** — the single richest compaction target found |
| `az storage account list` | Array of storage account objects (`encryption.services.{blob,file,queue,table}`, `networkRuleSet`, `creationTime`, `accessTier`, etc.) | Similarly verbose per-item, structurally nested |
| (all of the above) | Default output format is **already JSON** with no `--output` flag needed (confirmed via `az account show` and `az config get core.output` returning "not set") | — |

**Key implications:**
- No `--output json` injection needed for the default case (unlike `aws`, which defaults to
  a human-readable format) — `az` already defaults to JSON. Injection/format-checking is
  still needed to detect and respect an *explicit* non-JSON request.
- `webapp`/`storage`/`deployment` list operations are each individually large enough (100s–
  1000s of lines) that even a single real-world invocation demonstrates meaningful token
  savings — these are not synthetic/contrived examples.
- One real-world quirk found and worth flagging for implementation: `az webapp list` in this
  environment emitted a stray Python `UserWarning` line from the CLI's bundled
  `cryptography` library. Confirmed this lands on **stderr**, not mixed into stdout's JSON —
  it does not threaten JSON parsing, but fixture capture for `webapp list` should capture
  stdout and stderr separately to avoid accidentally embedding this line in a frozen fixture.

## Architecture

New `RtkSharp/Commands/Cloud/AzCommand.cs` (dispatch) + new
`RtkSharp/Commands/Cloud/AzFilters.cs` (pure filter functions), mirroring the existing
`AwsCommand.cs`/`AwsFilters.cs` split exactly:

- `AzCommand.RunAsync` switches on `(subcommand, op)` tuples exactly like `AwsCommand`, e.g.
  `("account", "show")`, `("group", "list")`, `("webapp", "show")`. Unmatched pairs route to
  a generic JSON-compaction handler (`RunGenericAsync`-equivalent).
- `AzFilters` gets one pure function per named op, all returning the existing
  `FilterResult`-shaped type (text + `IsTruncated`) — reuse the exact same result type
  `AwsFilters.FilterResult` already defines, or an identical az-local copy if visibility
  constraints make cross-file reuse awkward (decide at implementation time; either is fine,
  this is an internal detail with no external contract).
- Execution follows `AwsCommand`'s established six-phase contract exactly: timer start →
  execute via `IProcessExecutor`/`PathResolver.Resolve("az")` → filter (mandatory
  fallback-to-raw on any exception, project-wide contract) → tee-and-hint on
  truncation/failure → `TimedExecution.Track` → exit code.
- Verbose flag (`RuntimeOptions.Verbosity`) prints `Running: az <cmd>` the same way `aws`
  does.
- Zero-args `rtk az` (no subcommand) has no Rust oracle to match — takes the simplest
  sensible behavior: pass through raw to the real `az` binary (which prints its own
  help/banner), rather than inventing an RTK-specific required-argument error the way
  `AwsCommand` does (that error is a faithful port of a real Rust/clap constraint that has
  no `az` equivalent).

### Shared JSON-compaction extraction (the one change to existing code)

`AwsFilters.FilterJsonCompact` is a generic, values-preserving JSON-compaction algorithm
with no `aws`-specific logic in its body. `az` becomes the second real user of an identical
algorithm, so per this project's DRY rule (`CLAUDE.md`: "introduce abstractions when two or
more instances of similar logic exist"), it is extracted into a new shared
`RtkSharp/Core/JsonCompaction.cs` (pure static method, e.g.
`JsonCompaction.Compact(string json, int maxDepth)`), and both `AwsFilters` and `AzFilters`
call it. `AwsFilters.FilterJsonCompact` becomes a thin wrapper (or its call sites switch
directly to `JsonCompaction.Compact` — decide at implementation time) preserving `aws`'s
existing byte-exact behavior, verified by a regression test.

This is the only change to pre-existing code in this feature; everything else is additive.

## Output-Format & Dispatch Policy

Since `az`'s factory default is already JSON, filtering applies whenever the effective
output format is JSON (the default, or an explicit `--output json`/`-o json`). If the user
explicitly passes `--output table`/`--output tsv`/`--output yaml`/a short-form equivalent,
the command passes through completely unfiltered — mirrors `AwsCommand`'s policy of never
corrupting a deliberately-requested format. Unlike `aws`, this MVP's five named op families
are all read/list/show operations, so there is no `AwsCommand.IsStructuredOperation`-style
distinction needed between "structured" and "transfer/mutating" ops — that distinction only
matters once mutating ops (e.g. a future `deployment group create`) enter scope, which this
MVP does not include.

## Redaction

New `AzFilters` redaction helper, modeled directly on
`DotnetCommand.ScrubSensitiveEnvVars`/`SensitiveEnvRegex`: a key-name-keyed regex matching
known secret-shaped keys — starting list: `connectionString`, `key`, `keys`, `password`,
`secret`, `token`, `sasToken`, `primaryKey`, `secondaryKey`, `accessKey` (finalize the exact
key list against real captured fixtures during implementation, especially `storage account
show`'s key-listing shape and `deployment`'s `properties.parameters` shape — both realistic
places secrets could surface) — replacing the matched value with `[REDACTED]`. Applied to
filtered output before printing, for every named-op filter and the generic fallback alike.

**Subscription/tenant IDs are explicitly NOT redacted** (deliberate scope decision, differs
from `PLANS.md`'s original wishlist): real ARM resource IDs embed the subscription ID inline
(e.g. `/subscriptions/c83a19df-.../resourceGroups/...`, confirmed in every captured
fixture), so redacting them would break resource IDs users need for follow-up `az` commands
— the same "don't hide identifiers needed for follow-up" principle this codebase already
applies to the `gh` module. IDs are visible to anyone with portal/CLI access to begin with,
so they are not secrets in the same sense as keys/tokens/connection-strings. Revisit
configurable ID redaction later only if a real need surfaces.

## Testing

New `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` (check first whether an existing
`Commands/Cloud` test folder/file convention should be extended instead of creating a new
one), following `DotnetCommandTests`' fixture-constant pattern established in the `.cs`
feature: real captured `az` output (this subscription, captured this session where shown
above; additional `show`-variant fixtures for `webapp`/`storage account`/`deployment group`
to be captured fresh during implementation using real resource names already identified this
session — `qhub-mg-prufund-prod-uks-v3` in `qhub-mg-prufund-v3-prod-rg` for webapp,
`csb1003200244ccb05c` in `cloud-shell-storage-westeurope` for storage account) frozen as
fixture strings, testing each `AzFilters.Filter*` function directly against real data — no
live Azure dependency in the test suite itself.

- A small `RecordingExecutor`-based dispatch test (same pattern used in
  `DotnetCommandTests`) confirms `AzCommand.RunAsync` routes correctly for all 10 named ops
  plus the generic-fallback default case, and that the explicit-`--output table` passthrough
  policy holds (no filtering attempted when a non-JSON format was explicitly requested).
- One shared-extraction regression test confirming `AwsFilters.FilterJsonCompact` (now
  calling into `JsonCompaction.Compact`, or replaced by it) still produces byte-identical
  output to its pre-extraction behavior against `AwsCommandTests`'/`AwsFilters`'s existing
  fixtures — this is the one place existing test coverage must be re-verified, not just
  extended.
- Redaction-specific tests: at least one fixture per named family containing a secret-shaped
  value (or a synthetically-injected one into an otherwise-real fixture, if no real captured
  example happens to contain one) confirming it becomes `[REDACTED]`, and confirming
  subscription/tenant IDs in the same fixture remain untouched.
- No parity-test entry — excluded from `RtkSharp.ParityTests` by design, same treatment as
  every other superset feature (no Rust oracle exists to compare against).

## Non-Goals (this design)

- bicep, acr, aks, functionapp, monitor, `az rest` — deferred to a future design/plan, not
  silently dropped. `PLANS.md`'s Phase 8 status should be updated to "Partial" (mirroring how
  Phase 6 was updated for the `.cs` feature), not "Done", once this MVP lands.
- Subscription-level `az deployment sub` (only `deployment group` is in scope) — no real test
  data was captured for the `sub`-level variant this session; add it in a follow-up once a
  real need or test subscription-level deployment surfaces.
- Deployment-failure-specific summarization (e.g. parsing `properties.error` into an
  actionable one-line summary) — failures currently take the generic raw-stderr-plus-tee-hint
  fallback path, same as every other filtered op's failure path. A dedicated summarizer is
  plausible future work but requires a real failed-deployment fixture this session didn't
  have access to.
- Configurable subscription/tenant ID redaction — explicitly deferred per the Redaction
  section above; the MVP hardcodes "never redact IDs."
- Bicep diagnostics compaction (`PLANS.md` listed this under the `bicep` family, itself
  out of scope).
