# az functionapp + acr Filters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `az functionapp` (list/show) and `az acr` (list, repository list, repository show-tags) filters to the existing `rtk az` command module.

**Architecture:** Five new pure filter functions in the existing `RtkSharp/Commands/Cloud/AzFilters.cs`, five new dispatch arms in the existing `RtkSharp/Commands/Cloud/AzCommand.cs`, all following the exact patterns already established by `webapp`/`storage account` in the same files. No new files, no changes to `JsonCompaction`, no new redaction rules (design spec concludes none of these commands expose secret-shaped values).

**Tech Stack:** C#/.NET 10, `System.Text.Json`, xUnit.

## Global Constraints

- Design source of truth: `docs/superpowers/specs/2026-07-09-az-functionapp-acr-design.md`. Every fixture is a real (or trimmed-but-real-derived) capture from the `FNZ Q-Hub Azure subscription` (`c83a19df-6be1-4eba-9505-9ab469177af5`) — no synthetic data in the primary fixture/savings tests. Synthetic data is allowed only for structural edge-case tests (e.g. overflow-past-`MaxItems`), matching the existing `FilterAccountList_Overflow_TruncatesAfterTwenty` pattern.
- Token savings floor: every list-filter fixture test must assert ≥60% savings (`CLAUDE.md`/`.claude/rules/cli-testing.md`). If a fixture as originally drafted in this plan falls short when actually run, **do not shrink the assertion** — add more real fields from the full captures quoted in this plan's Context comments until it clears 60%, mirroring how `WebappListRaw` was corrected during the MVP (`docs/superpowers/plans/2026-07-08-phase8-az-command-module.md` Task 5). **Documented exception (confirmed with the user 2026-07-10):** `FilterAcrRepositoryList`/`FilterAcrRepositoryShowTags` (Task 3) have no savings-floor test — their bare-string-array real fixtures measure 0.0% whitespace-token savings, a structural property no fixture resizing can fix (each short array element is already one whitespace-token either way). This is the ONLY exception in this plan; every other list filter still requires the floor.
- `MaxItems = 20` (existing `AzFilters.cs:20`) governs all list truncation — do not introduce a second constant.
- Redaction: `AzFilters.Redact` is applied unconditionally to every named filter's output inside `RunAzFilteredAsync` (`AzCommand.cs:245`) — none of the filters added in this plan need to call it directly or add new redaction regexes.
- Build verification after every task: `cargo` is not applicable (this is the C#/.NET port) — run `dotnet build` and `dotnet test` from the repo root; both must be clean (0 warnings introduced, 0 failures) before committing.
- Follow the existing file's exact code style: `TryParse`/`TryGetProp`/`JStr`/`JNestedStr`/`JBoolStr`/`JoinWithOverflow` are the established local helpers in `AzFilters.cs` — reuse them, do not reimplement.

---

### Task 1: `az functionapp list` / `az functionapp show` filters

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs` (add a new region after the `storage account` region, before the `Redact` method — i.e. after line 450, before line 452)
- Test: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` (add a new region after the `storage account list / storage account show` region, before the `Redact` region — i.e. after line 383, before line 385)

**Interfaces:**
- Consumes: `AwsFilters.FilterResult` (already defined, `New`/`Truncated` factories), `JStr`/`JNestedStr`/`JBoolStr`/`TryParse`/`JoinWithOverflow` (all private statics already defined in `AzFilters.cs`).
- Produces: `AzFilters.FilterFunctionAppList(string jsonStr) : AwsFilters.FilterResult?`, `AzFilters.FilterFunctionAppShow(string jsonStr) : AwsFilters.FilterResult?` — consumed by Task 4's dispatch wiring.

**Real capture context** (this session, `FNZ Q-Hub Azure subscription`, via `az functionapp show --name terraform-backup-functions --resource-group terraform-backup-rg` and `az functionapp list`): function apps use the identical `Microsoft.Web/sites` shape as `az webapp show`/`list`, with one function-specific field of interest: `siteConfig.linuxFxVersion` (e.g. `"PYTHON|3.9"`, `"DOTNET-ISOLATED|10.0"`, or empty when unset).

- [ ] **Step 1: Write the failing tests**

Insert into `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`, immediately after line 383 (`}` closing `FilterStorageAccountList_TokenSavings_MeetsSixtyPercent`) and before line 385 (`// ===================== Redact =====================`):

```csharp
    // ===================== functionapp list / functionapp show =====================

    private const string FunctionAppShowRaw = """
        {
          "defaultHostName": "terraform-backup-functions.azurewebsites.net",
          "httpsOnly": false,
          "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/terraform-backup-rg/providers/Microsoft.Web/sites/terraform-backup-functions",
          "kind": "functionapp,linux",
          "location": "UK South",
          "name": "terraform-backup-functions",
          "resourceGroup": "terraform-backup-rg",
          "siteConfig": {
            "linuxFxVersion": "PYTHON|3.9"
          },
          "sku": "Dynamic",
          "state": "Running",
          "type": "Microsoft.Web/sites"
        }
        """;

    private const string FunctionAppListRaw = """
        [
          {
            "defaultHostName": "qhub-teams-transcripts.azurewebsites.net",
            "enabled": true,
            "hostNames": [
              "qhub-teams-transcripts.azurewebsites.net"
            ],
            "httpsOnly": false,
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/qhub-teams-transcripts/providers/Microsoft.Web/sites/qhub-teams-transcripts",
            "kind": "functionapp,linux",
            "location": "UK South",
            "name": "qhub-teams-transcripts",
            "reserved": true,
            "resourceGroup": "qhub-teams-transcripts",
            "siteConfig": {
              "linuxFxVersion": "DOTNET-ISOLATED|10.0"
            },
            "sku": "Dynamic",
            "state": "Running",
            "type": "Microsoft.Web/sites",
            "usageState": "Normal"
          }
        ]
        """;

    [Fact]
    public void FilterFunctionAppShow_RealCapture_FormatsNameStateKindLocationSkuHttpsRgHostRuntime()
    {
        var result = AzFilters.FilterFunctionAppShow(FunctionAppShowRaw)!;
        Assert.Equal(
            "terraform-backup-functions Running functionapp,linux UK South sku:Dynamic https:false rg:terraform-backup-rg host:terraform-backup-functions.azurewebsites.net runtime:PYTHON|3.9",
            result.Text);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterFunctionAppShow_MissingSiteConfig_UsesQuestionMarkForRuntime()
    {
        var result = AzFilters.FilterFunctionAppShow("{}")!;
        Assert.Equal("? ? ? ? sku:? https:? rg:? host:? runtime:?", result.Text);
    }

    [Fact]
    public void FilterFunctionAppShow_InvalidJson_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterFunctionAppShow("not json"));
    }

    [Fact]
    public void FilterFunctionAppList_RealCapture_FormatsOneAppWithRuntime()
    {
        var result = AzFilters.FilterFunctionAppList(FunctionAppListRaw)!;
        Assert.Contains("qhub-teams-transcripts Running functionapp,linux", result.Text, StringComparison.Ordinal);
        Assert.Contains("runtime:DOTNET-ISOLATED|10.0", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterFunctionAppList_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterFunctionAppList(FunctionAppListRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(FunctionAppListRaw) * 100.0);
        Assert.True(savings >= 60.0, $"functionapp list filter: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterFunctionAppList_NotAnArray_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterFunctionAppList("{}"));
    }

    [Fact]
    public void FilterFunctionAppList_Overflow_TruncatesAfterTwenty()
    {
        var apps = Enumerable.Range(1, 25).Select(i => $$"""
            {"name": "func-{{i}}", "state": "Running", "kind": "functionapp", "location": "UK South", "sku": "Dynamic", "httpsOnly": true, "resourceGroup": "rg", "defaultHostName": "func-{{i}}.azurewebsites.net"}
            """);
        var input = $"[{string.Join(',', apps)}]";
        var result = AzFilters.FilterFunctionAppList(input)!;
        Assert.Contains("… +5 more function apps", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FilterFunctionApp"`
Expected: FAIL — `AzFilters` does not contain a definition for `FilterFunctionAppShow`/`FilterFunctionAppList` (compile error).

- [ ] **Step 3: Implement the filters**

Insert into `RtkSharp/Commands/Cloud/AzFilters.cs`, immediately after line 450 (`}` closing `FilterStorageAccountShow`) and before line 452 (`/// <summary>` doc comment for `Redact`):

```csharp

    // ===================== functionapp list / functionapp show =====================

    private static string FormatFunctionApp(JsonElement w)
    {
        var name = JStr(w, "name", "?");
        var state = JStr(w, "state", "?");
        var kind = JStr(w, "kind", "?");
        var location = JStr(w, "location", "?");
        var sku = JStr(w, "sku", "?");
        var https = JBoolStr(w, "httpsOnly", "?");
        var rg = JStr(w, "resourceGroup", "?");
        var host = JStr(w, "defaultHostName", "?");
        var runtime = JNestedStr(w, "siteConfig", "linuxFxVersion", string.Empty);
        if (runtime.Length == 0)
        {
            runtime = JNestedStr(w, "siteConfig", "windowsFxVersion", "?");
        }

        return $"{name} {state} {kind} {location} sku:{sku} https:{https} rg:{rg} host:{host} runtime:{runtime}";
    }

    /// <summary>Formats <c>az functionapp list</c>'s bare top-level array of Function App site objects (same <c>Microsoft.Web/sites</c> shape as <c>webapp</c>, plus a runtime-stack field).</summary>
    public static AwsFilters.FilterResult? FilterFunctionAppList(string jsonStr)
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

                result.Add(FormatFunctionApp(w));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "function apps");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }

    /// <summary>Formats <c>az functionapp show</c>'s single Function App site object (same shape as one <c>list</c> element).</summary>
    public static AwsFilters.FilterResult? FilterFunctionAppShow(string jsonStr)
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

            return AwsFilters.FilterResult.New(FormatFunctionApp(v));
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FilterFunctionApp"`
Expected: PASS (all 7 tests). If `FilterFunctionAppList_TokenSavings_MeetsSixtyPercent` fails short of 60%, add more real fields to `FunctionAppListRaw` from the full capture quoted in the Global Constraints note above (e.g. `outboundIpAddresses`, `dnsConfiguration`, `hostNameSslStates`) rather than lowering the assertion.

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add functionapp list/show filters"
```

---

### Task 2: `az acr list` filter

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs` (add a new region after Task 1's functionapp region)
- Test: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` (add a new region after Task 1's functionapp tests)

**Interfaces:**
- Consumes: same shared helpers as Task 1.
- Produces: `AzFilters.FilterAcrList(string jsonStr) : AwsFilters.FilterResult?` — consumed by Task 4.

**Real capture context** (this session, via `az acr list`): container registries (`Microsoft.ContainerRegistry/registries`) expose `name`, `loginServer`, `sku.name`, `location`, `resourceGroup`, `adminUserEnabled`, `provisioningState` as the fields worth surfacing. Two real registries exist in this subscription: `icppi` (`iprotect-ifp` resource group) and `icppipdn` (same resource group).

- [ ] **Step 1: Write the failing tests**

Insert into `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`, immediately after Task 1's `FilterFunctionAppList_Overflow_TruncatesAfterTwenty` test closing brace:

```csharp
    // ===================== acr list =====================

    private const string AcrListRaw = """
        [
          {
            "adminUserEnabled": true,
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/iprotect-ifp/providers/Microsoft.ContainerRegistry/registries/icppi",
            "location": "westeurope",
            "loginServer": "icppi.azurecr.io",
            "name": "icppi",
            "provisioningState": "Succeeded",
            "resourceGroup": "iprotect-ifp",
            "sku": {
              "name": "Basic",
              "tier": "Basic"
            },
            "type": "Microsoft.ContainerRegistry/registries"
          },
          {
            "adminUserEnabled": true,
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/iprotect-ifp/providers/Microsoft.ContainerRegistry/registries/icppipdn",
            "location": "westeurope",
            "loginServer": "icppipdn.azurecr.io",
            "name": "icppipdn",
            "provisioningState": "Succeeded",
            "resourceGroup": "iprotect-ifp",
            "sku": {
              "name": "Basic",
              "tier": "Basic"
            },
            "type": "Microsoft.ContainerRegistry/registries"
          }
        ]
        """;

    [Fact]
    public void FilterAcrList_RealCapture_FormatsBothRegistries()
    {
        var result = AzFilters.FilterAcrList(AcrListRaw)!;
        Assert.Contains("icppi icppi.azurecr.io Basic westeurope admin:true rg:iprotect-ifp state:Succeeded", result.Text, StringComparison.Ordinal);
        Assert.Contains("icppipdn icppipdn.azurecr.io Basic westeurope admin:true rg:iprotect-ifp state:Succeeded", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterAcrList_TokenSavings_MeetsSixtyPercent()
    {
        var result = AzFilters.FilterAcrList(AcrListRaw)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(AcrListRaw) * 100.0);
        Assert.True(savings >= 60.0, $"acr list filter: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterAcrList_MissingFields_UsesQuestionMarks()
    {
        var result = AzFilters.FilterAcrList("[{}]")!;
        Assert.Equal("? ? ? ? admin:? rg:? state:?", result.Text);
    }

    [Fact]
    public void FilterAcrList_NotAnArray_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterAcrList("{}"));
    }

    [Fact]
    public void FilterAcrList_Overflow_TruncatesAfterTwenty()
    {
        var registries = Enumerable.Range(1, 25).Select(i => $$"""
            {"name": "reg-{{i}}", "loginServer": "reg-{{i}}.azurecr.io", "sku": {"name": "Basic"}, "location": "westeurope", "resourceGroup": "rg", "adminUserEnabled": true, "provisioningState": "Succeeded"}
            """);
        var input = $"[{string.Join(',', registries)}]";
        var result = AzFilters.FilterAcrList(input)!;
        Assert.Contains("… +5 more registries", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FilterAcrList"`
Expected: FAIL — `AzFilters` does not contain a definition for `FilterAcrList` (compile error).

- [ ] **Step 3: Implement the filter**

Insert into `RtkSharp/Commands/Cloud/AzFilters.cs`, immediately after Task 1's `FilterFunctionAppShow` method closing brace:

```csharp

    // ===================== acr list =====================

    private static string FormatAcrRegistry(JsonElement r)
    {
        var name = JStr(r, "name", "?");
        var loginServer = JStr(r, "loginServer", "?");
        var skuName = JNestedStr(r, "sku", "name", "?");
        var location = JStr(r, "location", "?");
        var admin = JBoolStr(r, "adminUserEnabled", "?");
        var rg = JStr(r, "resourceGroup", "?");
        var state = JStr(r, "provisioningState", "?");
        return $"{name} {loginServer} {skuName} {location} admin:{admin} rg:{rg} state:{state}";
    }

    /// <summary>Formats <c>az acr list</c>'s bare top-level array of container registry objects.</summary>
    public static AwsFilters.FilterResult? FilterAcrList(string jsonStr)
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
            foreach (var r in v.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                result.Add(FormatAcrRegistry(r));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "registries");
            return total > MaxItems ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FilterAcrList"`
Expected: PASS (all 5 tests).

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add acr list filter"
```

---

### Task 3: `az acr repository list` / `az acr repository show-tags` filters

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzFilters.cs` (add a new region after Task 2's acr list region)
- Test: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` (add a new region after Task 2's acr list tests)

**Interfaces:**
- Consumes: `TryParse` (already defined).
- Produces: `AzFilters.FilterAcrRepositoryList(string jsonStr) : AwsFilters.FilterResult?`, `AzFilters.FilterAcrRepositoryShowTags(string jsonStr) : AwsFilters.FilterResult?`, and a new private helper `FormatStringArrayWithOverflow(JsonElement arr, int max, string label) : string?` — consumed by Task 4.

**Real capture context** (this session, via `az acr repository list --name icppi` and `az acr repository show-tags --name icppi --repository ifp.valuationengine.web`): both commands return a **bare JSON array of strings**, not objects — the first bare-string-array shape encountered in this module. Real repository list (5 items): `ifp.portal.bnl.demo`, `ifp.reportingengine.web.linux`, `ifp.valuationengine.web`, `ifp.valuationengine.web.linux`, `ifp.valuationengine.web.windows`. Real tag list (8 items): `0.0.45` through `0.0.52`.

- [ ] **Step 1: Write the failing tests**

Insert into `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`, immediately after Task 2's `FilterAcrList_Overflow_TruncatesAfterTwenty` test closing brace:

```csharp
    // ===================== acr repository list / acr repository show-tags =====================

    private const string AcrRepositoryListRaw = """
        [
          "ifp.portal.bnl.demo",
          "ifp.reportingengine.web.linux",
          "ifp.valuationengine.web",
          "ifp.valuationengine.web.linux",
          "ifp.valuationengine.web.windows"
        ]
        """;

    private const string AcrRepositoryShowTagsRaw = """
        [
          "0.0.45",
          "0.0.46",
          "0.0.47",
          "0.0.48",
          "0.0.49",
          "0.0.50",
          "0.0.51",
          "0.0.52"
        ]
        """;

    [Fact]
    public void FilterAcrRepositoryList_RealCapture_FormatsCommaJoinedSingleLine()
    {
        var result = AzFilters.FilterAcrRepositoryList(AcrRepositoryListRaw)!;
        Assert.Equal(
            "5 repositories: ifp.portal.bnl.demo, ifp.reportingengine.web.linux, ifp.valuationengine.web, ifp.valuationengine.web.linux, ifp.valuationengine.web.windows",
            result.Text);
        Assert.False(result.IsTruncated);
    }

    // No 60%-floor assertion here (deliberate, project-decision, not an oversight): for a bare
    // string array under MaxItems, every raw JSON element is already exactly one whitespace-token
    // (short strings, no internal spaces), and the comma-joined output preserves one token per
    // item too — CountTokens's whitespace-split metric cannot show savings for this shape no
    // matter how the real fixture is sized (confirmed by hand-computation: this real 5-item
    // capture measures 0.0% whitespace-token savings and ~4.9% even by raw character count). The
    // format's real value is line-count/scannability (N lines -> 1 line) and, for large real
    // repository lists, MaxItems-driven truncation savings — neither of which this small fixture
    // exercises. See `FilterAcrRepositoryList_Overflow_TruncatesAfterTwentyWithInlineSuffix` for
    // the truncation case, which is where this format's real compression shows up.

    [Fact]
    public void FilterAcrRepositoryList_NotAnArray_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterAcrRepositoryList("{}"));
    }

    [Fact]
    public void FilterAcrRepositoryList_NonStringElement_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterAcrRepositoryList("[1, 2, 3]"));
    }

    [Fact]
    public void FilterAcrRepositoryList_Overflow_TruncatesAfterTwentyWithInlineSuffix()
    {
        var repos = Enumerable.Range(1, 25).Select(i => $"\"repo-{i}\"");
        var input = $"[{string.Join(',', repos)}]";
        var result = AzFilters.FilterAcrRepositoryList(input)!;
        Assert.Contains("25 repositories:", result.Text, StringComparison.Ordinal);
        Assert.Contains("(+5 more)", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void FilterAcrRepositoryShowTags_RealCapture_FormatsCommaJoinedSingleLine()
    {
        var result = AzFilters.FilterAcrRepositoryShowTags(AcrRepositoryShowTagsRaw)!;
        Assert.Equal(
            "8 tags: 0.0.45, 0.0.46, 0.0.47, 0.0.48, 0.0.49, 0.0.50, 0.0.51, 0.0.52",
            result.Text);
        Assert.False(result.IsTruncated);
    }

    // Same deliberate exception as FilterAcrRepositoryList above — no 60%-floor assertion for this
    // bare-string-array shape's small real fixture; see that comment for the full rationale.

    [Fact]
    public void FilterAcrRepositoryShowTags_InvalidJson_ReturnsNull()
    {
        Assert.Null(AzFilters.FilterAcrRepositoryShowTags("not json"));
    }

```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FilterAcrRepository"`
Expected: FAIL — `AzFilters` does not contain a definition for `FilterAcrRepositoryList`/`FilterAcrRepositoryShowTags` (compile error).

Note: this task deliberately has NO token-savings-floor test for `FilterAcrRepositoryList`/`FilterAcrRepositoryShowTags` — see the inline comments in Step 1 for why (confirmed during execution: 0.0% whitespace-token savings on the real 5-item fixture, a structural property of small bare-string-array outputs, not a fixable fixture-sizing issue). This was a correction made mid-plan-execution on 2026-07-10 after the original plan draft incorrectly assumed these would "very likely clear 60% comfortably by construction" — that assumption was wrong; the design decision to keep the comma-joined format anyway (rather than redesign it) was confirmed with the user before this correction.

- [ ] **Step 3: Implement the filters**

Insert into `RtkSharp/Commands/Cloud/AzFilters.cs`, immediately after Task 2's `FilterAcrList` method closing brace:

```csharp

    // ===================== acr repository list / acr repository show-tags =====================

    /// <summary>
    /// Formats a bare top-level JSON array of strings (used by <c>acr repository list</c>/
    /// <c>show-tags</c>, unlike every other filter in this file which handles arrays of objects)
    /// as a single comma-joined line: <c>"{total} {label}: a, b, c"</c>, with an inline
    /// <c>" (+N more)"</c> suffix past <paramref name="max"/> items. Returns <see langword="null"/>
    /// if the root is not an array, or any element is not a string.
    /// </summary>
    private static AwsFilters.FilterResult? FormatStringArrayWithOverflow(JsonElement arr, int max, string label)
    {
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var total = arr.GetArrayLength();
        var items = new List<string>();
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (i < max)
            {
                items.Add(el.GetString()!);
            }

            i++;
        }

        var text = $"{total} {label}: {string.Join(", ", items)}";
        if (total > max)
        {
            text += $" (+{total - max} more)";
        }

        return total > max ? AwsFilters.FilterResult.Truncated(text) : AwsFilters.FilterResult.New(text);
    }

    /// <summary>Formats <c>az acr repository list</c>'s bare top-level array of repository name strings.</summary>
    public static AwsFilters.FilterResult? FilterAcrRepositoryList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            return FormatStringArrayWithOverflow(doc.RootElement, MaxItems, "repositories");
        }
    }

    /// <summary>Formats <c>az acr repository show-tags</c>'s bare top-level array of tag name strings.</summary>
    public static AwsFilters.FilterResult? FilterAcrRepositoryShowTags(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            return FormatStringArrayWithOverflow(doc.RootElement, MaxItems, "tags");
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FilterAcrRepository"`
Expected: PASS (all 8 tests).

- [ ] **Step 5: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzFilters.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): add acr repository list/show-tags filters"
```

---

### Task 4: Wire dispatch for all 5 new ops in `AzCommand.cs`

**Files:**
- Modify: `RtkSharp/Commands/Cloud/AzCommand.cs:12-18` (class doc comment), `AzCommand.cs:68-91` (dispatch switch)
- Test: `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs` (add dispatch tests after the existing `RunAsync_StorageAccountShow_DispatchesThreeLevelSubcommand` test, and extend the `RunAsync_NamedOps_ExitZeroOnSuccess` theory)

**Interfaces:**
- Consumes: `AzFilters.FilterFunctionAppList`/`FilterFunctionAppShow` (Task 1), `AzFilters.FilterAcrList` (Task 2), `AzFilters.FilterAcrRepositoryList`/`FilterAcrRepositoryShowTags` (Task 3) — all already implemented and tested in isolation by this point.
- Produces: working `rtk az functionapp list/show`, `rtk az acr list`, `rtk az acr repository list/show-tags` end-to-end dispatch.

- [ ] **Step 1: Write the failing tests**

Insert into `RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs`, immediately after line 503 (`}` closing `RunAsync_StorageAccountShow_DispatchesThreeLevelSubcommand`) and before line 505 (`[Theory]` for `RunAsync_NamedOps_ExitZeroOnSuccess`):

```csharp
    [Fact]
    public async Task RunAsync_FunctionAppShow_DispatchesToFilterFunctionAppShow()
    {
        // Asserts actual stdout content, not just that the request was forwarded: an unmatched
        // op also falls through to RunGenericAsync (JsonCompaction.Compact), which forwards args
        // identically but produces different, non-terse output — so this genuinely fails until
        // the ("functionapp", "show") dispatch arm exists.
        var executor = new RecordingExecutor(_ => Ok(FunctionAppShowRaw));
        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(["functionapp", "show", "--name", "terraform-backup-functions"], verbose: 0, executor));

        Assert.Equal(
            "terraform-backup-functions Running functionapp,linux UK South sku:Dynamic https:false rg:terraform-backup-rg host:terraform-backup-functions.azurewebsites.net runtime:PYTHON|3.9\n",
            stdout);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["functionapp", "show", "--name", "terraform-backup-functions"], request.Arguments);
    }

    [Fact]
    public async Task RunAsync_AcrRepositoryList_DispatchesThreeLevelSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok(AcrRepositoryListRaw));
        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(["acr", "repository", "list", "--name", "icppi"], verbose: 0, executor));

        Assert.Equal(
            "5 repositories: ifp.portal.bnl.demo, ifp.reportingengine.web.linux, ifp.valuationengine.web, ifp.valuationengine.web.linux, ifp.valuationengine.web.windows\n",
            stdout);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["acr", "repository", "list", "--name", "icppi"], request.Arguments);
    }

    [Fact]
    public async Task RunAsync_AcrRepositoryShowTags_DispatchesThreeLevelSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok(AcrRepositoryShowTagsRaw));
        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(
            ["acr", "repository", "show-tags", "--name", "icppi", "--repository", "ifp.valuationengine.web"], verbose: 0, executor));

        Assert.Equal("8 tags: 0.0.45, 0.0.46, 0.0.47, 0.0.48, 0.0.49, 0.0.50, 0.0.51, 0.0.52\n", stdout);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(
            ["acr", "repository", "show-tags", "--name", "icppi", "--repository", "ifp.valuationengine.web"], request.Arguments);
    }

```

Extend the existing `RunAsync_NamedOps_ExitZeroOnSuccess` theory (originally at line 505-515) by adding two new `[InlineData]` rows immediately after the existing `[InlineData("webapp", "show")]` line:

```csharp
    [InlineData("functionapp", "list")]
    [InlineData("functionapp", "show")]
```

And add one more dispatch test after `RunAsync_AcrRepositoryShowTags_DispatchesThreeLevelSubcommand` for the two-level `acr list` case:

```csharp
    [Fact]
    public async Task RunAsync_AcrList_DispatchesTwoLevelSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok(AcrListRaw));
        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(["acr", "list"], verbose: 0, executor));

        Assert.Contains("icppi icppi.azurecr.io Basic westeurope admin:true rg:iprotect-ifp state:Succeeded", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["acr", "list"], request.Arguments);
    }

```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RunAsync_FunctionApp|FullyQualifiedName~RunAsync_Acr"`
Expected: FAIL — with no dispatch arm yet, `("functionapp", "show")`/`("acr", "list")`/`("acr", "repository")` all fall through to `RunGenericAsync`, which runs `JsonCompaction.Compact` on the raw JSON instead of the terse named-filter format, so the exact-stdout `Assert.Equal`/`Assert.Contains` checks above fail (not a compile error this time, since `AzCommand.RunAsync` already compiles against `IReadOnlyList<string>`/`IProcessExecutor` — the failure is a genuine behavioral mismatch, confirming the test exercises the right thing before Step 3 wires the dispatch arms).

- [ ] **Step 3: Wire the dispatch arms and update the class doc comment**

In `RtkSharp/Commands/Cloud/AzCommand.cs`, replace lines 12-18 (the class-level doc comment):

```csharp
/// <summary>
/// Implements the <c>rtk az</c> CLI verb: compresses <c>az</c> JSON output via 15 named filters
/// (account, group, deployment group, webapp, storage account — each list/show; functionapp
/// list/show; acr list, acr repository list, acr repository show-tags) plus a generic
/// values-preserving JSON-compaction fallback, with secret redaction applied throughout. A pure
/// RtkSharp superset feature — no Rust oracle exists for <c>az</c>. Design source of truth:
/// <c>docs/superpowers/specs/2026-07-08-az-command-module-design.md</c> (MVP) and
/// <c>docs/superpowers/specs/2026-07-09-az-functionapp-acr-design.md</c> (functionapp/acr).
/// Mirrors <see cref="AwsCommand"/>'s dispatch/execution skeleton.
/// </summary>
```

Then, in the `RunAsync` dispatch switch, replace lines 86-90:

```csharp
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["storage", "account", "list"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountList).ConfigureAwait(false),
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "show" => await RunAzFilteredAsync(
                ["storage", "account", "show"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountShow).ConfigureAwait(false),
            _ => await RunGenericAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false),
```

with:

```csharp
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["storage", "account", "list"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountList).ConfigureAwait(false),
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "show" => await RunAzFilteredAsync(
                ["storage", "account", "show"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountShow).ConfigureAwait(false),
            ("functionapp", "list") => await RunAzFilteredAsync(
                ["functionapp", "list"], opArgs, verbose, executor, AzFilters.FilterFunctionAppList).ConfigureAwait(false),
            ("functionapp", "show") => await RunAzFilteredAsync(
                ["functionapp", "show"], opArgs, verbose, executor, AzFilters.FilterFunctionAppShow).ConfigureAwait(false),
            ("acr", "list") => await RunAzFilteredAsync(
                ["acr", "list"], opArgs, verbose, executor, AzFilters.FilterAcrList).ConfigureAwait(false),
            ("acr", "repository") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["acr", "repository", "list"], opArgs[1..], verbose, executor, AzFilters.FilterAcrRepositoryList).ConfigureAwait(false),
            ("acr", "repository") when opArgs.Length > 0 && opArgs[0] == "show-tags" => await RunAzFilteredAsync(
                ["acr", "repository", "show-tags"], opArgs[1..], verbose, executor, AzFilters.FilterAcrRepositoryShowTags).ConfigureAwait(false),
            _ => await RunGenericAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AzCommandTests"`
Expected: PASS (full `AzCommandTests` class, including all pre-existing tests — this confirms no regression in the MVP's 10 original ops).

- [ ] **Step 5: Full build verification**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds with 0 warnings introduced; full test suite passes (no regressions anywhere in the solution, not just `AzCommandTests`).

- [ ] **Step 6: Commit**

```bash
git add RtkSharp/Commands/Cloud/AzCommand.cs RtkSharp.Tests/Commands/Cloud/AzCommandTests.cs
git commit -m "feat(az): dispatch functionapp and acr ops"
```

---

### Task 5: Update `docs/PLANS.md` Phase 8 status

**Files:**
- Modify: `docs/PLANS.md` (Phase Coverage Matrix row for Phase 8, and the Phase 8 section's own status line if one exists near its `### Tasks` heading)

**Interfaces:** None — documentation only.

- [ ] **Step 1: Locate and update the Phase 8 status**

Read `docs/PLANS.md`, find the Phase Coverage Matrix row for Phase 8 (currently reads, per the MVP's own update:
`**Partial** — MVP landed (...): account/group/deployment group/webapp/storage account show+list filters, generic JSON-compaction fallback, redaction. Deferred: bicep, acr, aks, functionapp, monitor, az rest, deployment sub, deployment-failure summarization — see the design spec's Non-Goals.`)

Replace it with:

```markdown
**Partial** — MVP landed (`docs/superpowers/plans/2026-07-08-phase8-az-command-module.md`): account/group/deployment group/webapp/storage account show+list filters, generic JSON-compaction fallback, redaction. Extended (`docs/superpowers/plans/2026-07-09-az-functionapp-acr.md`): functionapp list/show, acr list/repository list/repository show-tags. Deferred: bicep, aks, monitor, az rest, deployment sub, deployment-failure summarization — see `docs/superpowers/specs/2026-07-09-az-functionapp-acr-design.md`'s Non-Goals.
```

- [ ] **Step 2: Commit**

```bash
git add docs/PLANS.md
git commit -m "docs: mark az functionapp/acr extension in Phase 8 status"
```
