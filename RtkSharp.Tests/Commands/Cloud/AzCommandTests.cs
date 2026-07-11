using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.Cloud;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="AzCommand"/>'s dispatch/execution behavior (argument routing, non-JSON
/// output passthrough, generic-fallback redaction, error propagation) -- the process-exec-level
/// tests that complement <c>RtkSharp.Filters.Tests.Commands.Cloud.AzFiltersTests</c>'s pure-filter
/// coverage. Fixture constants below are duplicated from that file (not shared) because both test
/// classes now live in different assemblies/projects.
/// </summary>
public sealed class AzCommandTests
{
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

    [Fact]
    public async Task RunAsync_AcrList_DispatchesTwoLevelSubcommand()
    {
        var executor = new RecordingExecutor(_ => Ok(AcrListRaw));
        var stdout = await CaptureStdoutAsync(() => AzCommand.RunAsync(["acr", "list"], verbose: 0, executor));

        Assert.Contains("icppi icppi.azurecr.io Basic westeurope admin:true rg:iprotect-ifp state:Succeeded", stdout, StringComparison.Ordinal);
        var request = Assert.Single(executor.Requests);
        Assert.Equal(["acr", "list"], request.Arguments);
    }

    [Theory]
    [InlineData("group", "list")]
    [InlineData("group", "show")]
    [InlineData("webapp", "list")]
    [InlineData("webapp", "show")]
    [InlineData("functionapp", "list")]
    [InlineData("functionapp", "show")]
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
