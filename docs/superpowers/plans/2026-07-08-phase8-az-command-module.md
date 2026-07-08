# `az` (Azure CLI) Command Module (MVP) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `rtk az` command verb that compresses Azure CLI JSON output via five dedicated
list/show filter families (account, group, deployment group, webapp, storage account) plus a
generic values-preserving JSON-compaction fallback for every other `az` subcommand, with secret
redaction applied throughout.

**Architecture:** New `RtkSharp/Commands/Cloud/AzCommand.cs` (dispatch, six-phase execution
contract) + `RtkSharp/Commands/Cloud/AzFilters.cs` (pure filter + redaction functions), mirroring
the existing `AwsCommand.cs`/`AwsFilters.cs` split exactly. A new shared
`RtkSharp/Core/JsonCompaction.cs` extracts the generic JSON-compaction algorithm out of
`AwsFilters.FilterJsonCompact` so both `aws` and `az` call the same implementation — the only
change to pre-existing code in this feature.

**Tech Stack:** C#/.NET, `System.Text.Json`, xUnit, the existing `IProcessExecutor`/
`ExecutionRequest`/`ExecutionResult` execution abstraction.

## Global Constraints

- Spec source of truth: `docs/superpowers/specs/2026-07-08-az-command-module-design.md` — every
  task below implements one part of that approved design; do not deviate from its scope decisions
  (MVP's 5 named families only; ID redaction explicitly OFF; no deployment-failure summarizer).
- `AwsFilters.FilterResult` is reused directly (not duplicated) as the shared filter-result type —
  it is already `internal`, same assembly, so `AzFilters`/`AzCommand` can reference
  `AwsFilters.FilterResult` with no visibility change needed.
- No parity test — `az` has no Rust oracle, matching `aws`'s existing treatment (no
  `AwsParityTests` file exists either).
- After every task's code changes: `cargo`-equivalent for this repo is `dotnet build` and
  `dotnet test` — run both before committing (see per-task Step "Run full test suite").
- Real captured Azure output (this session, `FNZ Q-Hub Azure subscription`,
  `c83a19df-6be1-4eba-9505-9ab469177af5`) is used verbatim or as a trimmed-but-real subset for
  every fixture below — no synthetic data, matching this repo's established testing convention
  (`AwsCommandTests`, `DotnetCommandTests`).

---

## File Structure

- Create: `RtkSharp/Core/JsonCompaction.cs` — shared generic JSON-compaction algorithm (`Compact`
  static method), extracted from `AwsFilters`.
- Modify: `RtkSharp/Commands/Cloud/AwsFilters.cs` — `FilterJsonCompact` becomes a one-line
  delegating wrapper around `JsonCompaction.Compact`; its private `CompactJson`/`IsSimpleJson`
  bodies are deleted (now live in `JsonCompaction`).
- Create: `RtkSharp/Commands/Cloud/AzFilters.cs` — 10 named filter functions (account
  show/list, group show/list, deployment group show/list, webapp show/list, storage account
  show/list), their private JSON-value helpers (a local copy, per spec — not shared with
  `AwsFilters`), and the redaction helper (`Redact`, two `SensitiveKeyRegex*` fields, one
  `StorageAccountKeyValueRegex` field).
- Create: `RtkSharp/Commands/Cloud/AzCommand.cs` — `RunAsync` dispatch, `RunAzFilteredAsync`
  (six-phase shared runner), `RunGenericAsync` (fallback), `RunPassthroughAsync` (zero-args and
  explicit-non-JSON-format cases), `RequestsNonJsonOutput` gate.
- Modify: `RtkSharp/Cli/CommandRegistry.cs` — register `"az"` → `AzCommand.RunAsync`.
- Create: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` — filter unit tests (fixture +
  token-savings), redaction tests, and a `RecordingExecutor`-based dispatch test covering all 10
  named ops, the generic fallback, zero-args passthrough, and the `--output table` passthrough
  policy.
- Modify: `docs/PLANS.md` — Phase 8 status updated to "Partial" once this MVP lands (Task 9).

---

### Task 1: Extract shared `JsonCompaction.Compact`

**Files:**
- Create: `RtkSharp/Core/JsonCompaction.cs`
- Modify: `RtkSharp/Commands/Cloud/AwsFilters.cs:1591-1703` (the `FilterJsonCompact`/`CompactJson`/`IsSimpleJson` block)
- Test: `RtkSharp.Tests/Commands/Cloud/AwsCommandTests.cs` (existing test, unmodified — used as the regression check)

**Interfaces:**
- Produces: `RtkSharp.Core.JsonCompaction.Compact(string jsonStr, int maxDepth) : string` — used by
  Task 8's `AzCommand.RunGenericAsync` and by `AwsFilters.FilterJsonCompact`.

- [ ] **Step 1: Confirm the existing regression test passes before touching anything**

Run: `dotnet test --filter FullyQualifiedName~AwsCommandTests.FilterJsonCompact_UnsupportedSubcommandJson_PreservesValues`
Expected: PASS (baseline, before refactor)

- [ ] **Step 2: Create `RtkSharp/Core/JsonCompaction.cs`**

```csharp
using System;
using System.Linq;
using System.Text.Json;

namespace RtkSharp.Core;

/// <summary>
/// Generic, values-preserving JSON-compaction algorithm: long strings are truncated, large arrays
/// are summarized, and the result renders each key on its own line for readability. Faithful port
/// of <c>json_cmd::filter_json_compact</c>/<c>compact_json</c> (<c>src/cmds/system/json_cmd.rs</c>:
/// 91-178). Shared by <c>rtk aws</c>'s and <c>rtk az</c>'s generic (no-dedicated-filter) fallback
/// paths — extracted from <see cref="RtkSharp.Commands.Cloud.AwsFilters.FilterJsonCompact"/>, which
/// is now a one-line delegating wrapper preserving byte-identical output.
/// </summary>
public static class JsonCompaction
{
    /// <summary>Parses <paramref name="jsonStr"/> and returns its compact, values-preserving rendering.</summary>
    /// <param name="jsonStr">The raw JSON text to compact.</param>
    /// <param name="maxDepth">The maximum nesting depth to render before truncating with <c>...</c>.</param>
    /// <returns>The compact rendering.</returns>
    public static string Compact(string jsonStr, int maxDepth)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        return CompactJson(doc.RootElement, 0, maxDepth);
    }

    private static string CompactJson(JsonElement value, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);

        if (depth > maxDepth)
        {
            return $"{indent}...";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return $"{indent}null";
            case JsonValueKind.True:
                return $"{indent}true";
            case JsonValueKind.False:
                return $"{indent}false";
            case JsonValueKind.Number:
                return $"{indent}{value.GetRawText()}";
            case JsonValueKind.String:
                {
                    var s = value.GetString() ?? string.Empty;
                    if (s.Length > 80)
                    {
                        var end = Math.Min(77, s.Length);
                        return $"{indent}\"{s[..end]}...\"";
                    }

                    return $"{indent}\"{s}\"";
                }

            case JsonValueKind.Array:
                {
                    var arr = value.EnumerateArray().ToList();
                    if (arr.Count == 0)
                    {
                        return $"{indent}[]";
                    }

                    if (arr.Count > 5)
                    {
                        var first = CompactJson(arr[0], depth + 1, maxDepth).Trim();
                        return $"{indent}[{first}, ... +{arr.Count - 1} more]";
                    }

                    if (arr.All(IsSimpleJson))
                    {
                        var inline = arr.Select(x => CompactJson(x, 0, maxDepth).Trim());
                        return $"{indent}[{string.Join(", ", inline)}]";
                    }

                    var arrLines = new System.Collections.Generic.List<string> { $"{indent}[" };
                    foreach (var item in arr)
                    {
                        arrLines.Add($"{CompactJson(item, depth + 1, maxDepth)},");
                    }

                    arrLines.Add($"{indent}]");
                    return string.Join('\n', arrLines);
                }

            case JsonValueKind.Object:
                {
                    var props = value.EnumerateObject().ToList();
                    if (props.Count == 0)
                    {
                        return $"{indent}{{}}";
                    }

                    var keys = props.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
                    var map = props.ToDictionary(p => p.Name, p => p.Value);
                    var objLines = new System.Collections.Generic.List<string> { $"{indent}{{" };

                    for (var i = 0; i < keys.Count; i++)
                    {
                        var key = keys[i];
                        var val = map[key];
                        if (IsSimpleJson(val))
                        {
                            var valStr = CompactJson(val, 0, maxDepth).Trim();
                            objLines.Add($"{indent}  {key}: {valStr}");
                        }
                        else
                        {
                            objLines.Add($"{indent}  {key}:");
                            objLines.Add(CompactJson(val, depth + 1, maxDepth));
                        }

                        if (i >= 20)
                        {
                            objLines.Add($"{indent}  ... +{keys.Count - i - 1} more keys");
                            break;
                        }
                    }

                    objLines.Add($"{indent}}}");
                    return string.Join('\n', objLines);
                }

            default:
                return $"{indent}null";
        }
    }

    private static bool IsSimpleJson(JsonElement v) =>
        v.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;
}
```

- [ ] **Step 3: Replace `AwsFilters.FilterJsonCompact` with a delegating wrapper and delete its now-dead private helpers**

In `RtkSharp/Commands/Cloud/AwsFilters.cs`, replace the entire block from the
`FilterJsonCompact` doc comment (line ~1585) through the end of `IsSimpleJson` (line ~1703) with:

```csharp
    // ===================== Generic fallback: values-preserving JSON compaction =====================

    /// <summary>
    /// Parse a JSON string and return compact representation with values preserved. Faithful port
    /// of <c>json_cmd::filter_json_compact</c> (<c>src/cmds/system/json_cmd.rs</c>:91-178), used by
    /// <c>run_generic</c> for any AWS subcommand without a specialized filter. Delegates to the
    /// shared <see cref="RtkSharp.Core.JsonCompaction.Compact"/> (extracted so <c>rtk az</c>'s
    /// generic fallback shares the identical algorithm) — preserves this method's pre-extraction,
    /// byte-identical output, verified by <c>AwsCommandTests.FilterJsonCompact_UnsupportedSubcommandJson_PreservesValues</c>.
    /// </summary>
    public static string FilterJsonCompact(string jsonStr, int maxDepth) =>
        RtkSharp.Core.JsonCompaction.Compact(jsonStr, maxDepth);
```

Remove the now-unused `using System.Text.Json;`/`using System.Text.Json.Nodes;` only if no other
method in the file still needs them (it does — `AwsFilters` uses `JsonElement`/`JsonDocument`
extensively elsewhere — leave the usings untouched).

- [ ] **Step 4: Run the regression test again to confirm byte-identical output post-extraction**

Run: `dotnet test --filter FullyQualifiedName~AwsCommandTests.FilterJsonCompact_UnsupportedSubcommandJson_PreservesValues`
Expected: PASS (unchanged test, now exercising the delegating wrapper — this is the regression proof required by the spec's Testing section)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: All tests PASS (no other test references `AwsFilters`' now-deleted private `CompactJson`/`IsSimpleJson`, since they were private)

- [ ] **Step 6: Commit**

```bash
git add RtkSharp/Core/JsonCompaction.cs RtkSharp/Commands/Cloud/AwsFilters.cs
git commit -m "refactor(cloud): extract JsonCompaction.Compact from AwsFilters for az reuse"
```

---

### Task 2: `AzFilters` skeleton + `account show`/`account list` filters

**Files:**
- Create: `RtkSharp/Commands/Cloud/AzFilters.cs`
- Test: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Consumes: `RtkSharp.Commands.Cloud.AwsFilters.FilterResult` (reused type — `New(string)`/`Truncated(string)` factory methods).
- Produces: `AzFilters.FilterAccountShow(string) : AwsFilters.FilterResult?`,
  `AzFilters.FilterAccountList(string) : AwsFilters.FilterResult?`, and the shared private JSON
  helpers (`TryParse`, `TryGetProp`, `JStr`, `JNestedStr`, `JBoolStr`, `IsSimpleJson`,
  `JoinWithOverflow`) that Tasks 3-6 build on. (`JStrOr` was listed here in an earlier draft but
  is not needed by any az filter — none require an OR-fallback between two field names — and was
  correctly omitted from Task 2's own Step 3 code and every later task's code.)

- [ ] **Step 1: Write the failing tests**

Add to a new `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`:

```csharp
using System;
using System.Linq;
using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="AzFilters"/> and <see cref="AzCommand"/>. Unlike <c>AwsCommandTests</c>, there
/// is no Rust oracle to port from — <c>az</c> is a pure RtkSharp superset feature per
/// <c>docs/superpowers/specs/2026-07-08-az-command-module-design.md</c>. Every fixture below is a
/// real (or trimmed-but-real-derived) capture from the <c>FNZ Q-Hub Azure subscription</c>
/// (<c>c83a19df-6be1-4eba-9505-9ab469177af5</c>), not synthetic data.
/// </summary>
public sealed class AzCommandTests
{
    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ===================== account show =====================

    private const string AccountShowRaw = """
        {
          "environmentName": "AzureCloud",
          "homeTenantId": "7cdbc4de-cf13-4b46-aeea-483d6fed189a",
          "id": "c83a19df-6be1-4eba-9505-9ab469177af5",
          "isDefault": true,
          "managedByTenants": [],
          "name": "FNZ Q-Hub Azure subscription",
          "state": "Enabled",
          "tenantId": "7cdbc4de-cf13-4b46-aeea-483d6fed189a",
          "user": {
            "name": "james.burton@fnz-qhub.com",
            "type": "user"
          }
        }
        """;

    [Fact]
    public void FilterAccountShow_RealCapture_FormatsNameIdTenantStateUser()
    {
        var result = AzFilters.FilterAccountShow(AccountShowRaw)!;
        Assert.Equal(
            "FNZ Q-Hub Azure subscription (c83a19df-6be1-4eba-9505-9ab469177af5) tenant:7cdbc4de-cf13-4b46-aeea-483d6fed189a state:Enabled user:james.burton@fnz-qhub.com(user)",
            result.Text);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterAccountShow_MissingFields_UsesQuestionMarks()
    {
        var result = AzFilters.FilterAccountShow("{}")!;
        Assert.Equal("? (?) tenant:? state:? user:?(?)", result.Text);
    }

    [Fact]
    public void FilterAccountShow_InvalidJson_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterAccountShow("not json"));
    }

    [Fact]
    public void FilterAccountShow_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterAccountShow(AccountShowRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(AccountShowRaw) * 100.0);
        Assert.True(savings >= 60.0, $"account show filter: expected >=60% savings, got {savings:F1}%");
    }

    // ===================== account list =====================

    private const string AccountListRaw = """
        [
          {
            "environmentName": "AzureCloud",
            "homeTenantId": "7cdbc4de-cf13-4b46-aeea-483d6fed189a",
            "id": "c83a19df-6be1-4eba-9505-9ab469177af5",
            "isDefault": true,
            "name": "FNZ Q-Hub Azure subscription",
            "state": "Enabled",
            "tenantId": "7cdbc4de-cf13-4b46-aeea-483d6fed189a",
            "user": { "name": "james.burton@fnz-qhub.com", "type": "user" }
          },
          {
            "environmentName": "AzureCloud",
            "homeTenantId": "7cdbc4de-cf13-4b46-aeea-483d6fed189a",
            "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "isDefault": false,
            "name": "Other Test Subscription",
            "state": "Enabled",
            "tenantId": "7cdbc4de-cf13-4b46-aeea-483d6fed189a",
            "user": { "name": "james.burton@fnz-qhub.com", "type": "user" }
          }
        ]
        """;

    [Fact]
    public void FilterAccountList_RealCapture_MarksDefaultAccount()
    {
        var result = AzFilters.FilterAccountList(AccountListRaw)!;
        Assert.Contains("*FNZ Q-Hub Azure subscription (c83a19df-6be1-4eba-9505-9ab469177af5)", result.Text, StringComparison.Ordinal);
        Assert.Contains(" Other Test Subscription (aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee)", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterAccountList_Overflow_TruncatesAfterTwenty()
    {
        var accounts = Enumerable.Range(1, 25).Select(i => $$"""
            {"id": "id-{{i}}", "name": "sub-{{i}}", "tenantId": "t", "state": "Enabled", "isDefault": false}
            """);
        var input = $"[{string.Join(',', accounts)}]";
        var result = AzFilters.FilterAccountList(input)!;
        Assert.Contains("… +5 more accounts", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void FilterAccountList_NotAnArray_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterAccountList("{}"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (no `AzFilters` type exists yet)**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL with a compile error — `AzFilters` does not exist

- [ ] **Step 3: Create `RtkSharp/Commands/Cloud/AzFilters.cs` with the skeleton, JSON helpers, and the two account filters**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Pure filter and redaction functions for <c>rtk az</c>. A pure RtkSharp superset feature — no
/// Rust oracle exists for <c>az</c>, so these functions define their own compact, token-optimized
/// rendering rather than porting one. Design source of truth:
/// <c>docs/superpowers/specs/2026-07-08-az-command-module-design.md</c>. Mirrors the
/// <c>AwsCommand</c>/<see cref="AwsFilters"/> split; reuses <see cref="AwsFilters.FilterResult"/>
/// rather than duplicating an identical type.
/// </summary>
internal static class AzFilters
{
    // Matches AwsFilters.MaxItems (core/truncate.rs's CAP_LIST equivalent) for consistency.
    private const int MaxItems = 20;

    // ===================== account show / account list =====================

    /// <summary>Formats <c>az account show</c>'s single-object JSON.</summary>
    public static AwsFilters.FilterResult? FilterAccountShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var name = JStr(v, "name", "?");
            var id = JStr(v, "id", "?");
            var tenantId = JStr(v, "tenantId", "?");
            var state = JStr(v, "state", "?");
            var userName = JNestedStr(v, "user", "name", "?");
            var userType = JNestedStr(v, "user", "type", "?");
            return AwsFilters.FilterResult.New($"{name} ({id}) tenant:{tenantId} state:{state} user:{userName}({userType})");
        }
    }

    /// <summary>Formats <c>az account list</c>'s bare top-level array of the same object shape as <c>show</c>, marking the default subscription with <c>*</c>.</summary>
    public static AwsFilters.FilterResult? FilterAccountList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var acct in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var marker = JBoolStr(acct, "isDefault", "false") == "true" ? "*" : " ";
                var name = JStr(acct, "name", "?");
                var id = JStr(acct, "id", "?");
                var tenantId = JStr(acct, "tenantId", "?");
                var state = JStr(acct, "state", "?");
                result.Add($"{marker}{name} ({id}) tenant:{tenantId} state:{state}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "accounts");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    // ===================== shared JSON helpers (local copy — not shared with AwsFilters, per spec) =====================

    private static bool TryParse(string jsonStr, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(jsonStr);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static bool TryGetProp(JsonElement el, string prop, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string JStr(JsonElement el, string prop, string dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : dflt;

    private static string JNestedStr(JsonElement el, string prop, string inner, string dflt) =>
        TryGetProp(el, prop, out var v) ? JStr(v, inner, dflt) : dflt;

    private static string JBoolStr(JsonElement el, string prop, string dflt) =>
        TryGetProp(el, prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? (v.GetBoolean() ? "true" : "false")
            : dflt;

    private static bool IsSimpleJson(JsonElement v) =>
        v.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;

    /// <summary>Faithful port of <c>join_with_overflow</c> (<c>core/utils.rs</c>:147-153), same as <c>AwsFilters</c>'s copy.</summary>
    private static string JoinWithOverflow(List<string> items, int total, int max, string label)
    {
        var text = string.Join('\n', items);
        if (total > max)
        {
            text += $"\n… +{total - max} more {label}";
        }

        return text;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS (all `FilterAccountShow`/`FilterAccountList` tests)

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add AzFilters skeleton with account show/list filters"
```

---

### Task 3: `group show`/`group list` filters

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs`
- Modify: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Produces: `AzFilters.FilterGroupList(string) : AwsFilters.FilterResult?`,
  `AzFilters.FilterGroupShow(string) : AwsFilters.FilterResult?`, private `FormatGroup(JsonElement) : string`.

- [ ] **Step 1: Write the failing tests**

Add to `AzCommandTests.cs`:

```csharp
    // ===================== group list / group show =====================

    private const string GroupListRaw = """
        [
          {
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/fnz-qhub-test",
            "location": "uksouth",
            "managedBy": null,
            "name": "fnz-qhub-test",
            "properties": { "provisioningState": "Succeeded" },
            "tags": null,
            "type": "Microsoft.Resources/resourceGroups"
          },
          {
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/fnz-qhub-qhub",
            "location": "uksouth",
            "managedBy": null,
            "name": "fnz-qhub-qhub",
            "properties": { "provisioningState": "Succeeded" },
            "tags": {},
            "type": "Microsoft.Resources/resourceGroups"
          }
        ]
        """;

    private const string GroupShowRaw = """
        {
          "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/fnz-qhub-test",
          "location": "uksouth",
          "managedBy": null,
          "name": "fnz-qhub-test",
          "properties": { "provisioningState": "Succeeded" },
          "tags": null,
          "type": "Microsoft.Resources/resourceGroups"
        }
        """;

    [Fact]
    public void FilterGroupList_RealCapture_FormatsNameLocationState()
    {
        var result = AzFilters.FilterGroupList(GroupListRaw)!;
        Assert.Contains("fnz-qhub-test uksouth Succeeded", result.Text, StringComparison.Ordinal);
        Assert.Contains("fnz-qhub-qhub uksouth Succeeded", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterGroupShow_RealCapture_FormatsNameLocationState()
    {
        var result = AzFilters.FilterGroupShow(GroupShowRaw)!;
        Assert.Equal("fnz-qhub-test uksouth Succeeded", result.Text);
    }

    [Fact]
    public void FilterGroupList_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterGroupList(GroupListRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(GroupListRaw) * 100.0);
        Assert.True(savings >= 60.0, $"group list filter: expected >=60% savings, got {savings:F1}%");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL — `AzFilters.FilterGroupList`/`FilterGroupShow` do not exist

- [ ] **Step 3: Add the group filters to `AzFilters.cs`** (insert after the account-list method, before the shared JSON helpers section)

```csharp
    // ===================== group list / group show =====================

    private static string FormatGroup(JsonElement g)
    {
        var name = JStr(g, "name", "?");
        var location = JStr(g, "location", "?");
        var state = JNestedStr(g, "properties", "provisioningState", "?");

        var tagKeys = new List<string>();
        if (TryGetProp(g, "tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var t in tags.EnumerateObject())
            {
                tagKeys.Add(t.Name);
            }
        }

        var tagsSuffix = tagKeys.Count == 0 ? string.Empty : $" tags:[{string.Join(',', tagKeys)}]";
        return $"{name} {location} {state}{tagsSuffix}";
    }

    /// <summary>Formats <c>az group list</c>'s bare top-level array of resource-group objects.</summary>
    public static AwsFilters.FilterResult? FilterGroupList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var g in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatGroup(g));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "resource groups");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az group show</c>'s single resource-group object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterGroupShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return AwsFilters.FilterResult.New(FormatGroup(v));
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add group show/list filters"
```

---

### Task 4: `deployment group show`/`deployment group list` filters

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs`
- Modify: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Produces: `AzFilters.FilterDeploymentGroupList(string) : AwsFilters.FilterResult?`,
  `AzFilters.FilterDeploymentGroupShow(string) : AwsFilters.FilterResult?`, private
  `FormatDeployment(JsonElement) : string`.

**Note:** parameter values ARE rendered in this filter's output (real captured shape below has
`properties.parameters.<name>.value`) — this is deliberate per the spec's redaction section,
which flags `properties.parameters` as a realistic place secrets could surface. Task 7 adds
redaction that runs over this output.

- [ ] **Step 1: Write the failing tests**

Add to `AzCommandTests.cs` (fixture trimmed from a real `az deployment group list -g
fnz-qhub-test` capture — irrelevant deeply-nested fields like `debugSetting`, `dependencies`,
`providers`, `templateHash` are dropped, but every field the filter reads is real, unmodified data):

```csharp
    // ===================== deployment group list / deployment group show =====================

    private const string DeploymentGroupListRaw = """
        [
          {
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/fnz-qhub-test/providers/Microsoft.Resources/deployments/Microsoft.AppConfiguration-20251202161737-0352",
            "name": "Microsoft.AppConfiguration-20251202161737-0352",
            "properties": {
              "correlationId": "d84d56d3-f66f-45d6-9e06-38bba4b0ec10",
              "duration": "PT38.1221158S",
              "error": null,
              "mode": "Incremental",
              "outputResources": [
                {
                  "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/fnz-qhub-test/providers/Microsoft.AppConfiguration/configurationStores/fnz-qhub-test-config",
                  "resourceGroup": "fnz-qhub-test",
                  "resourceType": "Microsoft.AppConfiguration/configurationStores"
                }
              ],
              "parameters": {
                "authenticationMode": { "type": "String", "value": "Pass-through" },
                "disableLocalAuth": { "type": "Bool", "value": true },
                "name": { "type": "String", "value": "fnz-qhub-test-config" },
                "sku": { "type": "String", "value": "free" },
                "tags": { "type": "Object", "value": {} }
              },
              "provisioningState": "Succeeded",
              "timestamp": "2025-12-02T16:18:11.837245+00:00"
            },
            "resourceGroup": "fnz-qhub-test",
            "type": "Microsoft.Resources/deployments"
          }
        ]
        """;

    [Fact]
    public void FilterDeploymentGroupList_RealCapture_FormatsNameStateModeDurationResources()
    {
        var result = AzFilters.FilterDeploymentGroupList(DeploymentGroupListRaw)!;
        Assert.Contains(
            "Microsoft.AppConfiguration-20251202161737-0352 Succeeded Incremental dur:PT38.1221158S resources:1",
            result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterDeploymentGroupList_RealCapture_RendersSimpleParametersOnly()
    {
        var result = AzFilters.FilterDeploymentGroupList(DeploymentGroupListRaw)!;
        Assert.Contains("authenticationMode=Pass-through", result.Text, StringComparison.Ordinal);
        Assert.Contains("sku=free", result.Text, StringComparison.Ordinal);
        // "tags" parameter's value is an object ({}), not a simple JSON value — must be skipped.
        Assert.DoesNotContain("tags=", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDeploymentGroupShow_RealCapture_FormatsSingleDeployment()
    {
        // deployment group show returns the same object shape as one list element, unwrapped.
        using var doc = JsonDocument.Parse(DeploymentGroupListRaw);
        var showJson = doc.RootElement[0].GetRawText();

        var result = AzFilters.FilterDeploymentGroupShow(showJson)!;
        Assert.Contains("Succeeded Incremental", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDeploymentGroupList_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterDeploymentGroupList(DeploymentGroupListRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(DeploymentGroupListRaw) * 100.0);
        Assert.True(savings >= 60.0, $"deployment group list filter: expected >=60% savings, got {savings:F1}%");
    }
```

This test file needs `using System.Text.Json;` — add it to the top of `AzCommandTests.cs` alongside the existing usings.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL — `AzFilters.FilterDeploymentGroupList`/`FilterDeploymentGroupShow` do not exist

- [ ] **Step 3: Add the deployment filters to `AzFilters.cs`** (insert after the group-show method)

```csharp
    // ===================== deployment group list / deployment group show =====================

    private static string FormatDeployment(JsonElement d)
    {
        var name = JStr(d, "name", "?");
        var state = JNestedStr(d, "properties", "provisioningState", "?");
        var mode = JNestedStr(d, "properties", "mode", "?");
        var duration = JNestedStr(d, "properties", "duration", "?");

        var resourceCount = 0;
        if (TryGetProp(d, "properties", out var props) && TryGetProp(props, "outputResources", out var outRes)
            && outRes.ValueKind == JsonValueKind.Array)
        {
            resourceCount = outRes.GetArrayLength();
        }

        var line = $"{name} {state} {mode} dur:{duration} resources:{resourceCount}";

        if (TryGetProp(d, "properties", out var props2) && TryGetProp(props2, "parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object)
        {
            var paramPairs = new List<string>();
            foreach (var p in parameters.EnumerateObject())
            {
                if (!TryGetProp(p.Value, "value", out var val) || !IsSimpleJson(val))
                {
                    continue;
                }

                var valStr = val.ValueKind == JsonValueKind.String ? val.GetString()! : val.GetRawText();
                paramPairs.Add($"{p.Name}={valStr}");
            }

            if (paramPairs.Count > 0)
            {
                line += $"\n  params: {string.Join(", ", paramPairs)}";
            }
        }

        return line;
    }

    /// <summary>Formats <c>az deployment group list</c>'s bare top-level array of deployment objects.</summary>
    public static AwsFilters.FilterResult? FilterDeploymentGroupList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var d in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatDeployment(d));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "deployments");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az deployment group show</c>'s single deployment object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterDeploymentGroupShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return AwsFilters.FilterResult.New(FormatDeployment(v));
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add deployment group show/list filters"
```

---

### Task 5: `webapp show`/`webapp list` filters

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs`
- Modify: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Produces: `AzFilters.FilterWebappList(string) : AwsFilters.FilterResult?`,
  `AzFilters.FilterWebappShow(string) : AwsFilters.FilterResult?`, private
  `FormatWebapp(JsonElement) : string`.

- [ ] **Step 1: Write the failing tests**

Add to `AzCommandTests.cs` (real field values from `az webapp show -g qhub-mg-prufund-v3-prod-rg
-n qhub-mg-prufund-prod-uks-v3`, trimmed to the fields the filter reads — the real object has
~80 fields, most irrelevant to this compact rendering):

```csharp
    // ===================== webapp list / webapp show =====================

    private const string WebappShowRaw = """
        {
          "defaultHostName": "qhub-mg-prufund-prod-uks-v3.azurewebsites.net",
          "httpsOnly": true,
          "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/qhub-mg-prufund-v3-prod-rg/providers/Microsoft.Web/sites/qhub-mg-prufund-prod-uks-v3",
          "kind": "app,linux",
          "location": "UK South",
          "name": "qhub-mg-prufund-prod-uks-v3",
          "resourceGroup": "qhub-mg-prufund-v3-prod-rg",
          "sku": "PremiumV3",
          "state": "Running",
          "type": "Microsoft.Web/sites"
        }
        """;

    private const string WebappListRaw = """
        [
          {
            "defaultHostName": "qhub-mg-prufund-prod-uks-v3.azurewebsites.net",
            "httpsOnly": true,
            "kind": "app,linux",
            "location": "UK South",
            "name": "qhub-mg-prufund-prod-uks-v3",
            "resourceGroup": "qhub-mg-prufund-v3-prod-rg",
            "sku": "PremiumV3",
            "state": "Running"
          }
        ]
        """;

    [Fact]
    public void FilterWebappShow_RealCapture_FormatsNameStateKindLocationSkuHttpsRgHost()
    {
        var result = AzFilters.FilterWebappShow(WebappShowRaw)!;
        Assert.Equal(
            "qhub-mg-prufund-prod-uks-v3 Running app,linux UK South sku:PremiumV3 https:true rg:qhub-mg-prufund-v3-prod-rg host:qhub-mg-prufund-prod-uks-v3.azurewebsites.net",
            result.Text);
    }

    [Fact]
    public void FilterWebappList_RealCapture_FormatsOneApp()
    {
        var result = AzFilters.FilterWebappList(WebappListRaw)!;
        Assert.Contains("qhub-mg-prufund-prod-uks-v3 Running app,linux", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterWebappList_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterWebappList(WebappListRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(WebappListRaw) * 100.0);
        Assert.True(savings >= 60.0, $"webapp list filter: expected >=60% savings, got {savings:F1}%");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL — `AzFilters.FilterWebappList`/`FilterWebappShow` do not exist

- [ ] **Step 3: Add the webapp filters to `AzFilters.cs`** (insert after the deployment-show method)

```csharp
    // ===================== webapp list / webapp show =====================

    private static string FormatWebapp(JsonElement w)
    {
        var name = JStr(w, "name", "?");
        var state = JStr(w, "state", "?");
        var kind = JStr(w, "kind", "?");
        var location = JStr(w, "location", "?");
        var sku = JStr(w, "sku", "?");
        var https = JBoolStr(w, "httpsOnly", "?");
        var rg = JStr(w, "resourceGroup", "?");
        var host = JStr(w, "defaultHostName", "?");
        return $"{name} {state} {kind} {location} sku:{sku} https:{https} rg:{rg} host:{host}";
    }

    /// <summary>Formats <c>az webapp list</c>'s bare top-level array of App Service site objects.</summary>
    public static AwsFilters.FilterResult? FilterWebappList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var w in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatWebapp(w));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "apps");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az webapp show</c>'s single App Service site object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterWebappShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return AwsFilters.FilterResult.New(FormatWebapp(v));
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add webapp show/list filters"
```

---

### Task 6: `storage account show`/`storage account list` filters

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs`
- Modify: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Produces: `AzFilters.FilterStorageAccountList(string) : AwsFilters.FilterResult?`,
  `AzFilters.FilterStorageAccountShow(string) : AwsFilters.FilterResult?`, private
  `FormatStorageAccount(JsonElement) : string`.

- [ ] **Step 1: Write the failing tests**

Add to `AzCommandTests.cs` (real field values from `az storage account show -g
cloud-shell-storage-westeurope -n csb1003200244ccb05c`, trimmed to the fields the filter reads):

```csharp
    // ===================== storage account list / storage account show =====================

    private const string StorageAccountShowRaw = """
        {
          "accessTier": "Hot",
          "enableHttpsTrafficOnly": true,
          "kind": "StorageV2",
          "location": "westeurope",
          "minimumTlsVersion": "TLS1_2",
          "name": "csb1003200244ccb05c",
          "resourceGroup": "cloud-shell-storage-westeurope",
          "sku": { "name": "Standard_LRS", "tier": "Standard" }
        }
        """;

    private const string StorageAccountListRaw = """
        [
          {
            "accessTier": "Hot",
            "enableHttpsTrafficOnly": true,
            "kind": "StorageV2",
            "location": "westeurope",
            "minimumTlsVersion": "TLS1_2",
            "name": "csb1003200244ccb05c",
            "resourceGroup": "cloud-shell-storage-westeurope",
            "sku": { "name": "Standard_LRS", "tier": "Standard" }
          }
        ]
        """;

    [Fact]
    public void FilterStorageAccountShow_RealCapture_FormatsNameKindSkuLocationTlsHttpsTierRg()
    {
        var result = AzFilters.FilterStorageAccountShow(StorageAccountShowRaw)!;
        Assert.Equal(
            "csb1003200244ccb05c StorageV2 Standard_LRS westeurope tls:TLS1_2 https:true tier:Hot rg:cloud-shell-storage-westeurope",
            result.Text);
    }

    [Fact]
    public void FilterStorageAccountList_RealCapture_FormatsOneAccount()
    {
        var result = AzFilters.FilterStorageAccountList(StorageAccountListRaw)!;
        Assert.Contains("csb1003200244ccb05c StorageV2 Standard_LRS", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterStorageAccountList_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterStorageAccountList(StorageAccountListRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(StorageAccountListRaw) * 100.0);
        Assert.True(savings >= 60.0, $"storage account list filter: expected >=60% savings, got {savings:F1}%");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL — `AzFilters.FilterStorageAccountList`/`FilterStorageAccountShow` do not exist

- [ ] **Step 3: Add the storage account filters to `AzFilters.cs`** (insert after the webapp-show method)

```csharp
    // ===================== storage account list / storage account show =====================

    private static string FormatStorageAccount(JsonElement s)
    {
        var name = JStr(s, "name", "?");
        var kind = JStr(s, "kind", "?");
        var skuName = JNestedStr(s, "sku", "name", "?");
        var location = JStr(s, "location", "?");
        var tls = JStr(s, "minimumTlsVersion", "?");
        var https = JBoolStr(s, "enableHttpsTrafficOnly", "?");
        var tier = JStr(s, "accessTier", "?");
        var rg = JStr(s, "resourceGroup", "?");
        return $"{name} {kind} {skuName} {location} tls:{tls} https:{https} tier:{tier} rg:{rg}";
    }

    /// <summary>Formats <c>az storage account list</c>'s bare top-level array of storage account objects.</summary>
    public static AwsFilters.FilterResult? FilterStorageAccountList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = v.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var s in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatStorageAccount(s));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "storage accounts");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az storage account show</c>'s single storage account object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterStorageAccountShow(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return AwsFilters.FilterResult.New(FormatStorageAccount(v));
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add storage account show/list filters"
```

---

### Task 7: Redaction (`AzFilters.Redact`)

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs`
- Modify: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Produces: `AzFilters.Redact(string text) : string` — called by Task 8's `AzCommand` after every
  named filter and the generic fallback, before printing (per spec: "Applied to filtered output
  before printing, for every named-op filter and the generic fallback alike").

**Design note (grounded in a real capture this session):** `az storage account keys list`
(routed through the generic JSON-compaction fallback — it is not one of the 5 named MVP
families) returns objects shaped `{"creationTime": ..., "keyName": "key1", "permissions":
"FULL", "value": "<secret>"}`. The secret's JSON field name is literally `value` — too generic
to blanket-redact by key name alone (it would also swallow every deployment parameter's
`value`, e.g. `location: "uksouth"`). `StorageAccountKeyValueRegex` below targets this exact
confirmed shape (via the `keyName`/`permissions`/`value` sibling sequence, which
`JsonCompaction.Compact`'s alphabetical key ordering always produces in that order) instead of
matching on `value` generically.

- [ ] **Step 1: Write the failing tests**

Add to `AzCommandTests.cs`:

```csharp
    // ===================== Redact =====================

    [Fact]
    public void Redact_UnquotedParamStyle_RedactsPasswordButKeepsOtherParams()
    {
        var input = "some-deployment Succeeded Incremental dur:PT1S resources:1\n  params: password=hunter2, name=foo";
        var result = AzFilters.Redact(input);
        Assert.Contains("password=[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("name=foo", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_QuotedJsonCompactionStyle_RedactsSecretButPreservesQuoting()
    {
        var input = "{\n  name: \"foo\",\n  secret: \"topsecret\",\n}";
        var result = AzFilters.Redact(input);
        Assert.Contains("secret: \"[REDACTED]\"", result, StringComparison.Ordinal);
        Assert.Contains("name: \"foo\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_StorageAccountKeysShape_RedactsValueButKeepsKeyNameAndPermissions()
    {
        // Reproduces JsonCompaction.Compact's rendering of `az storage account keys list`
        // (alphabetical key order: creationTime, keyName, permissions, value).
        var input =
            "[\n  {\n    creationTime: \"2022-11-18T14:56:52.706532+00:00\",\n" +
            "    keyName: \"key1\",\n    permissions: \"FULL\",\n    value: \"abcdEXAMPLEKEY123==\",\n  },\n]";

        var result = AzFilters.Redact(input);

        Assert.Contains("keyName: \"key1\"", result, StringComparison.Ordinal);
        Assert.Contains("permissions: \"FULL\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdEXAMPLEKEY123==", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_SubscriptionAndTenantIds_AreNeverRedacted()
    {
        var input = "foo (c83a19df-6be1-4eba-9505-9ab469177af5) tenant:7cdbc4de-cf13-4b46-aeea-483d6fed189a state:Enabled";
        var result = AzFilters.Redact(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_ConnectionStringKey_IsRedacted()
    {
        var input = "connectionString: \"DefaultEndpointsProtocol=https;AccountKey=abc123\"";
        var result = AzFilters.Redact(input);
        Assert.DoesNotContain("AccountKey=abc123", result, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL — `AzFilters.Redact` does not exist

- [ ] **Step 3: Add the redaction fields and method to `AzFilters.cs`** (insert the regex fields
  right after the `MaxItems` constant, and the `Redact` method after the storage-account-show
  method)

Add `using System.Text.RegularExpressions;` if not already present (it is, added in Task 2's
skeleton — verify).

```csharp
    // Secret-shaped key names — starting list per the design spec, applied to both quoted
    // (JsonCompaction.Compact's "key: \"value\"" rendering) and unquoted ("key=value", used by
    // FormatDeployment's params line) styles.
    private static readonly string[] SensitiveKeyNames =
    {
        "connectionString", "key", "keys", "password", "secret", "token", "sasToken",
        "primaryKey", "secondaryKey", "accessKey",
    };

    private static readonly Regex SensitiveKeyRegexQuoted = BuildSensitiveKeyRegexQuoted();
    private static readonly Regex SensitiveKeyRegexUnquoted = BuildSensitiveKeyRegexUnquoted();

    // Targets `az storage account keys list`'s real shape (confirmed via a live capture this
    // session): the secret's JSON field name is literally `value`, too generic to redact by key
    // name alone, so this instead anchors on the keyName/permissions/value sibling sequence that
    // JsonCompaction.Compact's alphabetical key ordering always produces.
    private static readonly Regex StorageAccountKeyValueRegex = new(
        @"(?<=keyName:\s*""key\d+"",?\s*\n\s*permissions:\s*""[^""]*"",?\s*\n\s*value:\s*"")[^""]*",
        RegexOptions.Compiled);

    private static Regex BuildSensitiveKeyRegexQuoted()
    {
        var joined = string.Join('|', SensitiveKeyNames.Select(Regex.Escape));
        return new Regex($@"(?<prefix>\b(?:{joined})\b\s*:\s*"")(?<value>[^""]*)(?="")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static Regex BuildSensitiveKeyRegexUnquoted()
    {
        var joined = string.Join('|', SensitiveKeyNames.Select(Regex.Escape));
        return new Regex($@"(?<prefix>\b(?:{joined})\b\s*=\s*)(?<value>[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Redacts secret-shaped values from already-filtered <c>az</c> output. Deliberately does NOT
    /// redact subscription/tenant IDs (see the design spec's Redaction section) — real ARM resource
    /// IDs embed the subscription ID inline, and redacting them would break resource IDs users need
    /// for follow-up <c>az</c> commands. Applied by <c>AzCommand</c> to every named filter's result
    /// and to the generic JSON-compaction fallback's output, before printing.
    /// </summary>
    public static string Redact(string text)
    {
        text = StorageAccountKeyValueRegex.Replace(text, "[REDACTED]");
        text = SensitiveKeyRegexQuoted.Replace(text, "${prefix}[REDACTED]");
        text = SensitiveKeyRegexUnquoted.Replace(text, "${prefix}[REDACTED]");
        return text;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add secret redaction for az filter output"
```

---

### Task 8: `AzCommand` dispatch + execution skeleton

**Files:**
- Create: `RtkSharp/Commands/Cloud/AzCommand.cs`
- Modify: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`

**Interfaces:**
- Consumes: all 10 `AzFilters.Filter*` functions (Tasks 2-6), `AzFilters.Redact` (Task 7),
  `RtkSharp.Core.JsonCompaction.Compact` (Task 1), `AwsFilters.FilterResult`,
  `RtkSharp.Execution.{IProcessExecutor, ExecutionRequest, ExecutionResult, ExecutionCaptureMode}`,
  `RtkSharp.Execution.PathResolver`, `RtkSharp.Core.Tee`, `RtkSharp.Core.Tracking.TimedExecution`,
  `RtkSharp.Cli.RuntimeOptions`.
- Produces: `AzCommand.RunAsync(string[] args) : Task<int>` (registered by Task 9),
  `AzCommand.RunAsync(string[] args, int verbose, IProcessExecutor executor) : Task<int>` (used
  directly by tests, mirroring `AwsCommand`'s two-overload shape).

- [ ] **Step 1: Write the failing dispatch tests**

Add to `AzCommandTests.cs` (needs `using System.Collections.Generic; using System.Threading;
using RtkSharp.Execution;` added to the top):

```csharp
    // ===================== AzCommand dispatch =====================

    private static ExecutionResult Ok(string stdout, string stderr = "") =>
        new(stdout, stderr, ExitCode: 0, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false);

    [Fact]
    public async Task RunAsync_AccountShow_DispatchesToFilterAccountShow()
    {
        var executor = new RecordingExecutor(_ => Ok(AccountShowRaw));
        var exit = await AzCommand.RunAsync(["account", "show"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["account", "show"], request.Arguments);
    }

    [Fact]
    public async Task RunAsync_DeploymentGroupList_DispatchesThreeLevelSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok(DeploymentGroupListRaw));
        var exit = await AzCommand.RunAsync(["deployment", "group", "list", "-g", "fnz-qhub-test"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["deployment", "group", "list", "-g", "fnz-qhub-test"], request.Arguments);
    }

    [Fact]
    public async Task RunAsync_StorageAccountShow_DispatchesThreeLevelSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok(StorageAccountShowRaw));
        var exit = await AzCommand.RunAsync(["storage", "account", "show", "-n", "csb1003200244ccb05c"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["storage", "account", "show", "-n", "csb1003200244ccb05c"], request.Arguments);
    }

    [Theory]
    [InlineData("group", "list")]
    [InlineData("group", "show")]
    [InlineData("webapp", "list")]
    [InlineData("webapp", "show")]
    public async Task RunAsync_NamedOps_ExitZeroOnSuccess(string subcommand, string op)
    {
        var executor = new RecordingExecutor(_ => Ok("{}"));
        var exit = await AzCommand.RunAsync([subcommand, op], verbose: 0, executor);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task RunAsync_UnrecognizedSubcommand_UsesGenericJsonCompactionFallback()
    {
        var executor = new RecordingExecutor(_ => Ok("""{"foo": "bar"}"""));
        var exit = await AzCommand.RunAsync(["monitor", "activity-log", "list"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["monitor", "activity-log", "list"], request.Arguments);
    }

    [Fact]
    public async Task RunAsync_ExplicitOutputTable_PassesThroughUnfiltered()
    {
        var executor = new RecordingExecutor(_ => Ok("Name    Location\n------  --------\nfoo     uksouth\n"));
        var exit = await AzCommand.RunAsync(["group", "list", "--output", "table"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
        // Passthrough path runs the ORIGINAL args unchanged — no filter dispatch, no injection.
        Assert.Equal(["group", "list", "--output", "table"], request.Arguments);
    }

    [Fact]
    public async Task RunAsync_ExplicitOutputEqualsTsv_PassesThroughUnfiltered()
    {
        var executor = new RecordingExecutor(_ => Ok("foo\tuksouth\n"));
        var exit = await AzCommand.RunAsync(["group", "list", "--output=tsv"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
    }

    [Fact]
    public async Task RunAsync_ExplicitOutputJson_StillFiltered()
    {
        var executor = new RecordingExecutor(_ => Ok(AccountShowRaw));
        var exit = await AzCommand.RunAsync(["account", "show", "--output", "json"], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(ExecutionCaptureMode.Separate, request.CaptureMode);
    }

    [Fact]
    public async Task RunAsync_ZeroArgs_PassesThroughRaw()
    {
        var executor = new RecordingExecutor(_ => Ok(""));
        var exit = await AzCommand.RunAsync([], verbose: 0, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(ExecutionCaptureMode.Inherit, request.CaptureMode);
        Assert.Empty(request.Arguments);
    }

    [Fact]
    public async Task RunAsync_FilteredOpFailure_ReturnsExitCodeAndPrintsStderr()
    {
        var executor = new RecordingExecutor(_ =>
            new ExecutionResult("", "ERROR: subscription not found", ExitCode: 1, TimedDuration: TimeSpan.Zero, WasStarted: true, Failure: null, TimedOut: false));

        var previous = Console.Error;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetError(writer);
        int exit;
        try
        {
            exit = await AzCommand.RunAsync(["account", "show"], verbose: 0, executor);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Equal(1, exit);
        Assert.Contains("ERROR: subscription not found", writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingExecutor(Func<ExecutionRequest, ExecutionResult> responder) : IProcessExecutor
    {
        public List<ExecutionRequest> Requests { get; } = [];

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responder(request));
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: FAIL — `AzCommand` does not exist

- [ ] **Step 3: Create `RtkSharp/Commands/Cloud/AzCommand.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk az</c> CLI verb: compresses <c>az</c> JSON output via 10 named
/// list/show filters (account, group, deployment group, webapp, storage account) plus a generic
/// values-preserving JSON-compaction fallback, with secret redaction applied throughout. A pure
/// RtkSharp superset feature — no Rust oracle exists for <c>az</c>. Design source of truth:
/// <c>docs/superpowers/specs/2026-07-08-az-command-module-design.md</c>. Mirrors
/// <see cref="AwsCommand"/>'s dispatch/execution skeleton.
/// </summary>
/// <remarks>
/// <b>Unlike <c>aws</c>, no <c>--output json</c> injection is needed</b> — <c>az</c>'s factory
/// default output format is already JSON (confirmed via a live <c>az account show</c>/
/// <c>az config get core.output</c> capture this session). Filtering therefore applies whenever
/// the effective format is JSON (the default, or an explicit <c>--output json</c>); an explicit
/// non-JSON format (<c>table</c>/<c>tsv</c>/<c>yaml</c>/etc.) passes through completely
/// unfiltered via <see cref="RequestsNonJsonOutput"/>, mirroring <see cref="AwsCommand"/>'s
/// policy of never corrupting a deliberately-requested format.
/// </remarks>
public static class AzCommand
{
    private const int JsonCompressDepth = 4;

    /// <summary>Registry entry point. Runs <c>rtk az</c> with the given arguments (the remainder after the <c>az</c> verb).</summary>
    /// <param name="args">The CLI arguments following <c>az</c> (subcommand first).</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>Runs <c>rtk az</c> with an injectable <see cref="IProcessExecutor"/>, for testing.</summary>
    /// <param name="args">The CLI arguments following <c>az</c> (subcommand first).</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>az</c> with.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            // No Rust oracle constrains zero-args behavior for `az` (unlike `aws`'s clap-required-
            // positional quirk) — simplest sensible behavior: pass through raw so the real `az`
            // binary prints its own help/usage.
            var passthroughResult = await executor.ExecuteAsync(
                new ExecutionRequest(PathResolver.Resolve("az"), args, CaptureMode: ExecutionCaptureMode.Inherit)).ConfigureAwait(false);
            return passthroughResult.ExitCode;
        }

        var subcommand = args[0];
        var rest = args[1..];
        var op = rest.Length > 0 ? rest[0] : null;
        var opArgs = rest.Length > 0 ? rest[1..] : rest;
        var fullSub = rest.Length == 0 ? subcommand : $"{subcommand} {string.Join(' ', rest)}";

        if (RequestsNonJsonOutput(rest))
        {
            return await RunPassthroughAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false);
        }

        return (subcommand, op) switch
        {
            ("account", "show") => await RunAzFilteredAsync(
                ["account", "show"], opArgs, verbose, executor, AzFilters.FilterAccountShow).ConfigureAwait(false),
            ("account", "list") => await RunAzFilteredAsync(
                ["account", "list"], opArgs, verbose, executor, AzFilters.FilterAccountList).ConfigureAwait(false),
            ("group", "list") => await RunAzFilteredAsync(
                ["group", "list"], opArgs, verbose, executor, AzFilters.FilterGroupList).ConfigureAwait(false),
            ("group", "show") => await RunAzFilteredAsync(
                ["group", "show"], opArgs, verbose, executor, AzFilters.FilterGroupShow).ConfigureAwait(false),
            ("deployment", "group") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["deployment", "group", "list"], opArgs[1..], verbose, executor, AzFilters.FilterDeploymentGroupList).ConfigureAwait(false),
            ("deployment", "group") when opArgs.Length > 0 && opArgs[0] == "show" => await RunAzFilteredAsync(
                ["deployment", "group", "show"], opArgs[1..], verbose, executor, AzFilters.FilterDeploymentGroupShow).ConfigureAwait(false),
            ("webapp", "list") => await RunAzFilteredAsync(
                ["webapp", "list"], opArgs, verbose, executor, AzFilters.FilterWebappList).ConfigureAwait(false),
            ("webapp", "show") => await RunAzFilteredAsync(
                ["webapp", "show"], opArgs, verbose, executor, AzFilters.FilterWebappShow).ConfigureAwait(false),
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["storage", "account", "list"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountList).ConfigureAwait(false),
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "show" => await RunAzFilteredAsync(
                ["storage", "account", "show"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountShow).ConfigureAwait(false),
            _ => await RunGenericAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// True when <paramref name="args"/> explicitly requests a non-JSON output format
    /// (<c>--output table</c>/<c>tsv</c>/<c>yaml</c>/etc., or their <c>-o</c>/<c>--output=</c>
    /// shorthand). <c>json</c>/<c>jsonc</c> (explicit or absent, since <c>az</c> already defaults
    /// to JSON) return false.
    /// </summary>
    private static bool RequestsNonJsonOutput(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;

            if (arg is "--output" or "-o" && i + 1 < args.Count)
            {
                value = args[i + 1];
            }
            else if (arg.StartsWith("--output=", StringComparison.Ordinal))
            {
                value = arg["--output=".Length..];
            }
            else if (arg.StartsWith("-o=", StringComparison.Ordinal))
            {
                value = arg["-o=".Length..];
            }

            if (value is not null
                && !value.Equals("json", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("jsonc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs <c>az</c> with the original arguments unchanged, inheriting the console (no filtering, no capture). Used for zero-args and explicit-non-JSON-format cases.</summary>
    private static async Task<int> RunPassthroughAsync(
        string subcommand, IReadOnlyList<string> rest, int verbose, string fullSub, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();
        var cmdArgs = new List<string> { subcommand };
        cmdArgs.AddRange(rest);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: az {fullSub}\n");
        }

        var result = await executor.ExecuteAsync(
            new ExecutionRequest(PathResolver.Resolve("az"), cmdArgs, CaptureMode: ExecutionCaptureMode.Inherit)).ConfigureAwait(false);

        timer.TrackPassthrough($"az {fullSub}", $"rtk az {fullSub}");
        return result.ExitCode;
    }

    /// <summary>Generic fallback strategy for any az subcommand without a dedicated filter: values-preserving JSON compaction + redaction.</summary>
    private static async Task<int> RunGenericAsync(
        string subcommand, IReadOnlyList<string> args, int verbose, string fullSub, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var cmdArgs = new List<string> { subcommand };
        cmdArgs.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: az {fullSub}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("az"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run az CLI{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        var raw = result.Stdout;
        var stderr = result.Stderr;

        if (result.ExitCode != 0)
        {
            timer.Track($"az {fullSub}", $"rtk az {fullSub}", stderr, stderr);
            Console.Error.Write(stderr.Trim() + "\n");
            return result.ExitCode;
        }

        string filtered;
        try
        {
            filtered = AzFilters.Redact(JsonCompaction.Compact(raw, JsonCompressDepth));
            Console.Out.Write(filtered + "\n");
        }
        catch (global::System.Text.Json.JsonException)
        {
            Console.Out.Write(raw);
            filtered = raw;
        }

        timer.Track($"az {fullSub}", $"rtk az {fullSub}", raw, filtered);
        return 0;
    }

    /// <summary>Shared runner for az commands with a dedicated named filter. Follows the six-phase contract: timer → execute → filter (fallback) → redact → tee → track → exit code.</summary>
    private static async Task<int> RunAzFilteredAsync(
        string[] subArgs,
        IReadOnlyList<string> extraArgs,
        int verbose,
        IProcessExecutor executor,
        Func<string, AwsFilters.FilterResult?> filterFn)
    {
        var cmdLabel = $"az {string.Join(' ', subArgs)}";
        var rtkLabel = $"rtk {cmdLabel}";
        var slug = cmdLabel.Replace(' ', '_');
        var timer = TimedExecution.Start();

        var cmdArgs = new List<string>(subArgs);
        cmdArgs.AddRange(extraArgs);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {cmdLabel} {string.Join(' ', extraArgs)}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("az"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run {cmdLabel}{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        var stdout = result.Stdout;
        var stderr = result.Stderr;
        var raw = stderr.Length == 0 ? stdout : $"{stdout}\n{stderr}";

        if (result.ExitCode != 0)
        {
            var hint = Tee.TeeAndHint(raw, slug, result.ExitCode);
            Console.Error.Write(hint is not null ? $"{stderr.Trim()}\n{hint}\n" : $"{stderr.Trim()}\n");
            timer.Track(cmdLabel, rtkLabel, raw, stderr);
            return result.ExitCode;
        }

        var filterResult = filterFn(stdout);
        if (filterResult is null)
        {
            Console.Error.Write("rtk: filter warning: az filter returned None, passing through raw output\n");
            filterResult = AwsFilters.FilterResult.New(stdout);
        }

        var redactedText = AzFilters.Redact(filterResult.Text);
        filterResult = filterResult.IsTruncated
            ? AwsFilters.FilterResult.Truncated(redactedText)
            : AwsFilters.FilterResult.New(redactedText);

        if (filterResult.IsTruncated)
        {
            var hint = Tee.ForceTeeHint(raw, slug);
            Console.Out.Write(hint is not null ? $"{filterResult.Text}\n{hint}\n" : $"{filterResult.Text}\n");
        }
        else
        {
            Console.Out.Write(filterResult.Text + "\n");
        }

        timer.Track(cmdLabel, rtkLabel, raw, filterResult.Text);
        return 0;
    }
}
```

`AwsFilters.FilterResult` is `internal`, and `AzCommand`/`AzFilters` live in the same
`RtkSharp.Commands.Cloud` namespace and assembly, so no visibility change is needed.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AzCommandTests`
Expected: PASS (all dispatch tests, all filter tests, all redaction tests)

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: All tests PASS

- [ ] **Step 6: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzCommand.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add AzCommand dispatch and execution skeleton"
```

---

### Task 9: Registry wiring, docs update, final verification

**Files:**
- Modify: `RtkSharp/Cli/CommandRegistry.cs:141` (insert after the `"glab"` registration)
- Modify: `docs/PLANS.md` (Phase 8 status)

**Interfaces:**
- Consumes: `AzCommand.RunAsync(string[])` (Task 8).

- [ ] **Step 1: Register the `az` verb**

In `RtkSharp/Cli/CommandRegistry.cs`, after line 141 (`Register("glab", GlabCommand.RunAsync);`), add:

```csharp
        Register("az", AzCommand.RunAsync);
```

Add `using RtkSharp.Commands.Cloud;` at the top of the file if not already present (check first —
`AwsCommand`/`DockerCommand`/`KubectlCommand`/`OcCommand`/`GlabCommand` are already registered
from this namespace, so the using should already exist).

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: All tests PASS

- [ ] **Step 3: Manual smoke test against the real `az` CLI**

Run: `dotnet run --project RtkSharp -- az account show`
Expected: Compact one-line output in the `{name} ({id}) tenant:{tenantId} state:{state}
user:{name}({type})` shape, not raw multi-line JSON.

Run: `dotnet run --project RtkSharp -- az group list --output table`
Expected: Raw `az` table output, unmodified (passthrough policy).

- [ ] **Step 4: Update `docs/PLANS.md` Phase 8 status**

In `docs/PLANS.md`, in the "Phase Coverage Matrix"-equivalent table row for Phase 8 (around line
1062, the row starting `| Phase 8: Azure CLI (`az`) Demonstration Module |`), change `**Not
started**` to:

```
**Partial** — MVP landed (`docs/superpowers/plans/2026-07-08-phase8-az-command-module.md`): account/group/deployment group/webapp/storage account show+list filters, generic JSON-compaction fallback, redaction. Deferred: bicep, acr, aks, functionapp, monitor, `az rest`, `deployment sub`, deployment-failure summarization — see the design spec's Non-Goals.
```

This mirrors how Phase 6 (`.cs` file-based-app execution) was updated to "Partial" per this
same convention, referenced in the design spec's Non-Goals section.

- [ ] **Step 5: Run the pre-commit gate**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests PASS

- [ ] **Step 6: Commit**

```bash
git add RtkSharp/Cli/CommandRegistry.cs docs/PLANS.md
git commit -m "feat(az): register az command verb, mark Phase 8 partial"
```

---

## Self-Review Notes

- **Spec coverage:** All 5 named families (account, group, deployment group, webapp, storage
  account) × show/list = 10 filters (Tasks 2-6); generic JSON-compaction fallback (Task 1 + Task
  8's `RunGenericAsync`); redaction with the ID-exclusion rule (Task 7); output-format passthrough
  policy (Task 8's `RequestsNonJsonOutput`); zero-args passthrough (Task 8); shared
  `JsonCompaction` extraction with regression proof (Task 1); registry wiring + `PLANS.md` update
  (Task 9); no parity test (matches `aws`'s existing treatment, not a task — verified no
  `AwsParityTests` file exists to model one from).
- **Deliberately out of scope, matching the spec's Non-Goals:** bicep/acr/aks/functionapp/
  monitor/`az rest`/`deployment sub`/deployment-failure summarization/configurable ID redaction —
  none of these appear as tasks above, by design.
