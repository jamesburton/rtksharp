using System;
using System.Linq;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="JsonFilters"/>: the compact-with-values and keys-only-schema JSON
/// renderers, plus the multibyte-safe truncation helper. Moved from
/// <c>RtkSharp.Tests.Commands.System.JsonCommandTests</c> when <c>FilterJsonCompact</c>,
/// <c>FilterJsonSchema</c>, <c>Utf8ByteLength</c>, and <c>FloorCharBoundaryTruncate</c> moved
/// from <c>RtkSharp.Commands.System.JsonCommand</c> to <see cref="JsonFilters"/> (Task 14 of the
/// filters-library extraction). Test-for-test port of Rust <c>src/cmds/system/json_cmd.rs</c>'s
/// <c>#[cfg(test)] mod tests</c> (extract_schema and the multibyte-truncation regression tests).
/// Extension validation, argument parsing, and the file/stdin dispatch are not pure and remain in
/// <c>JsonCommand</c> alongside its own tests.
/// </summary>
public sealed class JsonFiltersTests
{
    // --- extract_schema (json_cmd.rs test_extract_schema_simple / test_extract_schema_array) ---

    [Fact]
    public void FilterJsonSchema_SimpleObject_ShowsTypes()
    {
        var schema = JsonFilters.FilterJsonSchema("""{"name": "test", "count": 42}""", 5);
        Assert.Contains("name", schema, StringComparison.Ordinal);
        Assert.Contains("string", schema, StringComparison.Ordinal);
        Assert.Contains("int", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonSchema_Array_ShowsCount()
    {
        var schema = JsonFilters.FilterJsonSchema("""{"items": [1, 2, 3]}""", 5);
        Assert.Contains("items", schema, StringComparison.Ordinal);
        Assert.Contains("(3)", schema, StringComparison.Ordinal);
    }

    // --- multibyte truncation regressions (test_compact_truncates_*) ---

    [Fact]
    public void FilterJsonCompact_TruncatesPureMultibyteString()
    {
        AssertValueTruncated(string.Concat(Enumerable.Repeat("日本語テスト", 85)));
    }

    [Fact]
    public void FilterJsonCompact_TruncatesMixedAsciiMultibyteString()
    {
        AssertValueTruncated(new string('a', 76) + string.Concat(Enumerable.Repeat("日本語", 5)));
    }

    private static void AssertValueTruncated(string payload)
    {
        var json = $$"""{"key": "{{payload}}"}""";
        var output = JsonFilters.FilterJsonCompact(json, 5);

        Assert.Contains("key", output, StringComparison.Ordinal);
        Assert.Contains("...", output, StringComparison.Ordinal);

        var parts = output.Split('"');
        Assert.True(parts.Length > 1, "output should contain a quoted string value");
        var value = parts[1];
        Assert.True(JsonFilters.Utf8ByteLength(value) <= 80, $"truncated value is {JsonFilters.Utf8ByteLength(value)} bytes: {value}");
    }

    // --- FloorCharBoundaryTruncate direct coverage ---

    [Fact]
    public void FloorCharBoundaryTruncate_NeverSplitsMultibyteCharacter()
    {
        var s = string.Concat(Enumerable.Repeat("日", 50)); // each rune is 3 UTF-8 bytes
        var truncated = JsonFilters.FloorCharBoundaryTruncate(s, 77);
        Assert.True(JsonFilters.Utf8ByteLength(truncated) <= 77);
        Assert.Equal(0, JsonFilters.Utf8ByteLength(truncated) % 3);
    }

    // --- FilterJsonCompact: additional structural coverage (arrays, objects, nesting) ---

    [Fact]
    public void FilterJsonCompact_EmptyArrayAndObject_RenderCompactly()
    {
        var output = JsonFilters.FilterJsonCompact("""{"a": [], "b": {}}""", 5);
        Assert.Contains("[]", output, StringComparison.Ordinal);
        Assert.Contains("{}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonCompact_SmallSimpleArray_InlinedCommaSeparated()
    {
        var output = JsonFilters.FilterJsonCompact("""{"nums": [1, 2, 3]}""", 5);
        Assert.Contains("[1, 2, 3]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonCompact_LargeArray_SummarizedWithMoreCount()
    {
        var output = JsonFilters.FilterJsonCompact("""{"nums": [1, 2, 3, 4, 5, 6, 7]}""", 5);
        Assert.Contains("more", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonCompact_ObjectKeysSortedOrdinal()
    {
        var output = JsonFilters.FilterJsonCompact("""{"zeta": 1, "alpha": 2}""", 5);
        Assert.True(output.IndexOf("alpha", StringComparison.Ordinal) < output.IndexOf("zeta", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterJsonCompact_DepthExceeded_ElidesWithEllipsis()
    {
        var output = JsonFilters.FilterJsonCompact("""{"a": {"b": {"c": 1}}}""", 1);
        Assert.Contains("...", output, StringComparison.Ordinal);
    }

}
