using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/system/json_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (validate_json_extension, extract_schema, and the multibyte-truncation regression tests), plus new
/// coverage for <see cref="JsonCommand.ParseArgs"/> and the RTK_META_COMMANDS parse-failure contract.
/// </summary>
public sealed class JsonCommandTests
{
    // --- #347: validate_json_extension (json_cmd.rs test_toml_file_rejected et al.) ---

    [Fact]
    public void ValidateJsonExtension_TomlFile_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonCommand.ValidateJsonExtension("config.toml"));
        Assert.Contains("not a JSON file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TOML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateJsonExtension_CargoToml_SuggestsDeps()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonCommand.ValidateJsonExtension("Cargo.toml"));
        Assert.Contains("rtk deps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateJsonExtension_YamlFile_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonCommand.ValidateJsonExtension("config.yaml"));
        Assert.Contains("YAML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateJsonExtension_JsonFile_Accepted()
    {
        JsonCommand.ValidateJsonExtension("data.json"); // does not throw
    }

    [Fact]
    public void ValidateJsonExtension_UnknownExtension_Accepted()
    {
        JsonCommand.ValidateJsonExtension("data.xyz"); // does not throw
    }

    [Fact]
    public void ValidateJsonExtension_NoExtension_Accepted()
    {
        JsonCommand.ValidateJsonExtension("Makefile"); // does not throw
    }

    // --- extract_schema (json_cmd.rs test_extract_schema_simple / test_extract_schema_array) ---

    [Fact]
    public void FilterJsonSchema_SimpleObject_ShowsTypes()
    {
        var schema = JsonCommand.FilterJsonSchema("""{"name": "test", "count": 42}""", 5);
        Assert.Contains("name", schema, StringComparison.Ordinal);
        Assert.Contains("string", schema, StringComparison.Ordinal);
        Assert.Contains("int", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonSchema_Array_ShowsCount()
    {
        var schema = JsonCommand.FilterJsonSchema("""{"items": [1, 2, 3]}""", 5);
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
        var output = JsonCommand.FilterJsonCompact(json, 5);

        Assert.Contains("key", output, StringComparison.Ordinal);
        Assert.Contains("...", output, StringComparison.Ordinal);

        var parts = output.Split('"');
        Assert.True(parts.Length > 1, "output should contain a quoted string value");
        var value = parts[1];
        Assert.True(JsonCommand.Utf8ByteLength(value) <= 80, $"truncated value is {JsonCommand.Utf8ByteLength(value)} bytes: {value}");
    }

    // --- FloorCharBoundaryTruncate direct coverage ---

    [Fact]
    public void FloorCharBoundaryTruncate_NeverSplitsMultibyteCharacter()
    {
        var s = string.Concat(Enumerable.Repeat("日", 50)); // each rune is 3 UTF-8 bytes
        var truncated = JsonCommand.FloorCharBoundaryTruncate(s, 77);
        Assert.True(JsonCommand.Utf8ByteLength(truncated) <= 77);
        Assert.Equal(0, JsonCommand.Utf8ByteLength(truncated) % 3);
    }

    // --- FilterJsonCompact: additional structural coverage (arrays, objects, nesting) ---

    [Fact]
    public void FilterJsonCompact_EmptyArrayAndObject_RenderCompactly()
    {
        var output = JsonCommand.FilterJsonCompact("""{"a": [], "b": {}}""", 5);
        Assert.Contains("[]", output, StringComparison.Ordinal);
        Assert.Contains("{}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonCompact_SmallSimpleArray_InlinedCommaSeparated()
    {
        var output = JsonCommand.FilterJsonCompact("""{"nums": [1, 2, 3]}""", 5);
        Assert.Contains("[1, 2, 3]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonCompact_LargeArray_SummarizedWithMoreCount()
    {
        var output = JsonCommand.FilterJsonCompact("""{"nums": [1, 2, 3, 4, 5, 6, 7]}""", 5);
        Assert.Contains("more", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterJsonCompact_ObjectKeysSortedOrdinal()
    {
        var output = JsonCommand.FilterJsonCompact("""{"zeta": 1, "alpha": 2}""", 5);
        Assert.True(output.IndexOf("alpha", StringComparison.Ordinal) < output.IndexOf("zeta", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterJsonCompact_DepthExceeded_ElidesWithEllipsis()
    {
        var output = JsonCommand.FilterJsonCompact("""{"a": {"b": {"c": 1}}}""", 1);
        Assert.Contains("...", output, StringComparison.Ordinal);
    }

    // --- ParseArgs / RTK_META_COMMANDS parse-failure contract ---

    [Fact]
    public void ParseArgs_FileOnly_UsesDefaults()
    {
        var parsed = JsonCommand.ParseArgs(["data.json"]);
        Assert.Equal("data.json", parsed.File);
        Assert.Equal(5, parsed.MaxDepth);
        Assert.False(parsed.KeysOnly);
    }

    [Fact]
    public void ParseArgs_DepthFlag_BothForms()
    {
        Assert.Equal(3, JsonCommand.ParseArgs(["data.json", "--depth", "3"]).MaxDepth);
        Assert.Equal(3, JsonCommand.ParseArgs(["data.json", "--depth=3"]).MaxDepth);
        Assert.Equal(3, JsonCommand.ParseArgs(["data.json", "-d", "3"]).MaxDepth);
    }

    [Fact]
    public void ParseArgs_KeysOnlyFlag_Recognized()
    {
        Assert.True(JsonCommand.ParseArgs(["data.json", "--keys-only"]).KeysOnly);
    }

    [Fact]
    public void ParseArgs_StdinDash_AcceptedAsFile()
    {
        Assert.Equal("-", JsonCommand.ParseArgs(["-"]).File);
    }

    [Fact]
    public void ParseArgs_MissingFile_ThrowsJsonArgsException()
    {
        Assert.Throws<JsonArgsException>(() => JsonCommand.ParseArgs([]));
    }

    [Fact]
    public void ParseArgs_UnrecognizedFlag_ThrowsJsonArgsException()
    {
        Assert.Throws<JsonArgsException>(() => JsonCommand.ParseArgs(["data.json", "--bogus"]));
    }

    [Fact]
    public void ParseArgs_SecondPositional_ThrowsJsonArgsException()
    {
        Assert.Throws<JsonArgsException>(() => JsonCommand.ParseArgs(["data.json", "extra.json"]));
    }

    [Fact]
    public void ParseArgs_DepthMissingValue_ThrowsJsonArgsException()
    {
        Assert.Throws<JsonArgsException>(() => JsonCommand.ParseArgs(["data.json", "--depth"]));
    }

    [Fact]
    public void ParseArgs_DepthNonNumeric_ThrowsJsonArgsException()
    {
        Assert.Throws<JsonArgsException>(() => JsonCommand.ParseArgs(["data.json", "--depth", "abc"]));
    }

    [Fact]
    public async Task RunAsync_ParseFailure_ReturnsExitCode2()
    {
        var exitCode = await JsonCommand.RunAsync(["--bogus"]);
        Assert.Equal(2, exitCode);
    }

    // --- RunCore integration: stdin path and file path ---

    [Fact]
    public void RunCore_Stdin_ReadsFromConsoleIn()
    {
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("""{"a": 1}"""));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = JsonCommand.RunCore(new JsonCommand.JsonArgs("-", 5, false), 0, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("a", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void RunCore_File_ReadsAndFiltersFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"rtk-json-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, """{"name": "test"}""");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = JsonCommand.RunCore(new JsonCommand.JsonArgs(tempFile, 5, false), 0, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("name", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void RunCore_KeysOnly_UsesSchemaFilter()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"rtk-json-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, """{"name": "test"}""");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            JsonCommand.RunCore(new JsonCommand.JsonArgs(tempFile, 5, true), 0, stdout, stderr);

            Assert.Contains("string", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
