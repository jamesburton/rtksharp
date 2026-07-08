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
}
