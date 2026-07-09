using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
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
            "enabled": true,
            "hostNames": [
              "prod.mgprufund.cloud.fnz-qhub.com",
              "prod.mgprufund.uks.cloud.fnz-qhub.com",
              "qhub-mg-prufund-prod-uks-v3.azurewebsites.net"
            ],
            "httpsOnly": true,
            "id": "/subscriptions/c83a19df-6be1-4eba-9505-9ab469177af5/resourceGroups/qhub-mg-prufund-v3-prod-rg/providers/Microsoft.Web/sites/qhub-mg-prufund-prod-uks-v3",
            "kind": "app,linux",
            "location": "UK South",
            "name": "qhub-mg-prufund-prod-uks-v3",
            "reserved": true,
            "resourceGroup": "qhub-mg-prufund-v3-prod-rg",
            "sku": "PremiumV3",
            "state": "Running",
            "type": "Microsoft.Web/sites",
            "usageState": "Normal"
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

    [Fact]
    public void Redact_CamelCaseSuffixParamNames_AreRedacted()
    {
        // Real Azure deployment parameters overwhelmingly use camelCase names like
        // `adminPassword`/`sqlAdminPassword`/`clientSecret` rather than a bare `password` — the
        // sensitive-key regex must match these as a suffix, not just an exact whole word.
        var input = "params: adminPassword=hunter2, sqlAdminPassword=hunter3, clientSecret=x, name=foo";
        var result = AzFilters.Redact(input);

        Assert.Contains("adminPassword=[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("sqlAdminPassword=[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("clientSecret=[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("name=foo", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter3", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_SensitiveWordAsPrefixOfUnrelatedWord_IsNotRedacted()
    {
        // `passwordless` contains "password" as a PREFIX of an unrelated word, not as a suffix of
        // a real camelCase identifier — the suffix-matching fix must not over-redact this.
        var input = "params: passwordless=true, name=foo";
        var result = AzFilters.Redact(input);

        Assert.Equal(input, result);
    }

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

    [Fact]
    public async Task RunAsync_GenericFallback_RedactsSecretValueBeforePrintingToConsole()
    {
        // `storage account keys list` is a 3-part subcommand that doesn't match any of the 10
        // named dispatch arms, so it routes through RunGenericAsync (JsonCompaction.Compact +
        // AzFilters.Redact). This proves Redact()'s output actually reaches Console.Out through
        // the full dispatch, not just that Redact() works in isolation.
        const string secret = "abcdEXAMPLESECRET==";
        var raw = $$"""
            [{"creationTime": "2022-11-18T14:56:52.706532+00:00", "keyName": "key1", "permissions": "FULL", "value": "{{secret}}"}]
            """;
        var executor = new RecordingExecutor(_ => Ok(raw));

        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(["storage", "account", "keys", "list"], verbose: 0, executor));

        Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NamedFilteredOp_RedactsSecretValueBeforePrintingToConsole()
    {
        // `deployment group list` is a named dispatch arm (RunAzFilteredAsync). This proves
        // Redact()'s output reaches Console.Out through THAT path too, not just the generic
        // fallback covered above — both call sites are exercised end-to-end.
        var raw = DeploymentGroupListRaw.Replace(
            "\"authenticationMode\": { \"type\": \"String\", \"value\": \"Pass-through\" },",
            "\"authenticationMode\": { \"type\": \"String\", \"value\": \"Pass-through\" }, \"password\": { \"type\": \"String\", \"value\": \"hunter2\" },",
            StringComparison.Ordinal);
        var executor = new RecordingExecutor(_ => Ok(raw));

        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(["deployment", "group", "list"], verbose: 0, executor));

        Assert.DoesNotContain("hunter2", stdout, StringComparison.Ordinal);
        Assert.Contains("password=[REDACTED]", stdout, StringComparison.Ordinal);
    }

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var previous = Console.Out;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetOut(writer);
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(previous);
        }
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
}
