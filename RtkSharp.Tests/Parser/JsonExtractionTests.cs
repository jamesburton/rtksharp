using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Tests.Parser;

/// <summary>
/// Tests for <see cref="JsonExtraction.ExtractJsonObject"/>. Ports Rust's
/// <c>test_extract_json_object_*</c> family verbatim (<c>src/parser/mod.rs:264-321</c>).
/// </summary>
public sealed class JsonExtractionTests
{
    [Fact]
    public void ExtractJsonObject_CleanInput_ReturnsInputUnchanged()
    {
        const string input = /*lang=json,strict*/ """{"numTotalTests": 13, "numPassedTests": 13}""";
        Assert.Equal(input, JsonExtraction.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_WithPnpmBannerPrefix_ExtractsJsonOnly()
    {
        var input = "\nScope: all 6 workspace projects\n WARN  deprecated inflight@1.0.6: This module is not supported\n\n{\"numTotalTests\": 13, \"numPassedTests\": 13, \"numFailedTests\": 0}\n";

        var extracted = JsonExtraction.ExtractJsonObject(input);

        Assert.NotNull(extracted);
        Assert.Contains("numTotalTests", extracted);
        Assert.StartsWith("{", extracted);
        Assert.EndsWith("}", extracted);
    }

    [Fact]
    public void ExtractJsonObject_WithDotenvBannerPrefix_ExtractsJsonOnly()
    {
        var input = "[dotenv] Loading environment variables from .env\n[dotenv] Injected 5 variables\n\n{\"numTotalTests\": 5, \"testResults\": [{\"name\": \"test.js\"}]}\n";

        var extracted = JsonExtraction.ExtractJsonObject(input);

        Assert.NotNull(extracted);
        Assert.Contains("numTotalTests", extracted);
        Assert.Contains("testResults", extracted);
    }

    [Fact]
    public void ExtractJsonObject_NestedBraces_BalancesCorrectly()
    {
        var input = "prefix text\n{\"numTotalTests\": 2, \"testResults\": [{\"name\": \"test\", \"data\": {\"nested\": true}}]}\n";

        var extracted = JsonExtraction.ExtractJsonObject(input);

        Assert.NotNull(extracted);
        Assert.Contains("\"nested\": true", extracted);
        Assert.StartsWith("{", extracted);
        Assert.EndsWith("}", extracted);
    }

    [Fact]
    public void ExtractJsonObject_NoJson_ReturnsNull()
    {
        Assert.Null(JsonExtraction.ExtractJsonObject("Just plain text with no JSON"));
    }

    [Fact]
    public void ExtractJsonObject_BracesInsideStringValue_NotConfusedForStructuralBraces()
    {
        const string input = /*lang=json,strict*/ """{"numTotalTests": 1, "message": "test {should} not confuse parser"}""";

        var extracted = JsonExtraction.ExtractJsonObject(input);

        Assert.NotNull(extracted);
        Assert.Contains("test {should} not confuse parser", extracted);
        Assert.Equal(input, extracted);
    }
}
