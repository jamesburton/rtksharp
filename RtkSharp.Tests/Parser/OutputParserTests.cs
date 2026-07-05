using RtkSharp.Parser;
using Xunit;

namespace RtkSharp.Tests.Parser;

/// <summary>
/// Tests for <see cref="ParseResult{T}"/> and <see cref="OutputParser{T}"/>. Ports Rust's
/// <c>test_parse_result_tier</c>, <c>test_parse_result_map</c>, <c>test_truncate_output</c>,
/// <c>test_truncate_output_multibyte</c>, and <c>test_truncate_output_emoji</c>
/// (<c>src/parser/mod.rs:205-262</c>), plus new tier-fallback coverage for the
/// <see cref="OutputParser{T}"/> template method exercising the JSON→regex→passthrough chain
/// described in the task brief.
/// </summary>
public sealed class OutputParserTests
{
    [Fact]
    public void ParseResult_Full_HasTierOneAndIsOk()
    {
        ParseResult<int> full = new ParseResult<int>.Full(42);
        Assert.Equal(1, full.Tier);
        Assert.True(full.IsOk);
    }

    [Fact]
    public void ParseResult_Degraded_HasTierTwoAndWarnings()
    {
        ParseResult<int> degraded = new ParseResult<int>.Degraded(42, ["warning"]);
        Assert.Equal(2, degraded.Tier);
        Assert.True(degraded.IsOk);
        Assert.Single(degraded.GetWarnings());
    }

    [Fact]
    public void ParseResult_Passthrough_HasTierThreeAndIsNotOk()
    {
        ParseResult<int> passthrough = new ParseResult<int>.Passthrough("raw");
        Assert.Equal(3, passthrough.Tier);
        Assert.False(passthrough.IsOk);
    }

    [Fact]
    public void ParseResult_Map_PreservesFullTier()
    {
        ParseResult<int> full = new ParseResult<int>.Full(42);
        var mapped = full.Map(x => x * 2);
        Assert.Equal(1, mapped.Tier);
        Assert.Equal(84, mapped.Unwrap());
    }

    [Fact]
    public void ParseResult_Map_PreservesDegradedTierAndWarnings()
    {
        ParseResult<int> degraded = new ParseResult<int>.Degraded(42, ["warn"]);
        var mapped = degraded.Map(x => x * 2);
        Assert.Equal(2, mapped.Tier);
        Assert.Single(mapped.GetWarnings());
        Assert.Equal(84, mapped.Unwrap());
    }

    [Fact]
    public void ParseResult_Unwrap_OnPassthrough_Throws()
    {
        ParseResult<int> passthrough = new ParseResult<int>.Passthrough("raw");
        Assert.Throws<InvalidOperationException>(() => passthrough.Unwrap());
    }

    [Fact]
    public void TruncateOutput_ShortString_ReturnsUnchanged()
    {
        Assert.Equal("hello", OutputParserSupport.TruncateOutput("hello", 10));
    }

    [Fact]
    public void TruncateOutput_LongString_TruncatesWithMarker()
    {
        var truncated = OutputParserSupport.TruncateOutput(new string('a', 1000), 100);
        Assert.Contains("[RTK:PASSTHROUGH]", truncated);
        Assert.Contains("1000 chars → 100 chars", truncated);
    }

    [Fact]
    public void TruncateOutput_Multibyte_DoesNotSplitCharacters()
    {
        // Thai text: each char is a multi-UTF-16-code-unit-adjacent scalar; counting by Rune
        // must not panic/corrupt when the byte-count truncation point lands mid-character.
        var thai = string.Concat(Enumerable.Repeat("สวัสดีครับ", 100));
        var result = OutputParserSupport.TruncateOutput(thai, 50);
        Assert.Contains("[RTK:PASSTHROUGH]", result);
    }

    [Fact]
    public void TruncateOutput_Emoji_HandlesSurrogatePairsSafely()
    {
        var emoji = string.Concat(Enumerable.Repeat("🎉", 200));
        var result = OutputParserSupport.TruncateOutput(emoji, 100);
        Assert.Contains("[RTK:PASSTHROUGH]", result);
    }

    /// <summary>A minimal JSON-first/regex-fallback parser used to exercise the tier chain.</summary>
    private sealed class FakeParser : OutputParser<TestResult>
    {
        protected override TestResult? TryFull(string input)
        {
            if (!input.TrimStart().StartsWith('{'))
            {
                return null;
            }

            // "Parse" a trivial `{"passed": N}` shape; anything else fails tier 1.
            var match = System.Text.RegularExpressions.Regex.Match(input, @"""passed"":\s*(\d+)");
            if (!match.Success)
            {
                return null;
            }

            var passed = int.Parse(match.Groups[1].Value);
            return new TestResult { Total = passed, Passed = passed, Failed = 0, Skipped = 0 };
        }

        protected override (TestResult Data, IReadOnlyList<string> Warnings)? TryDegraded(string input)
        {
            var match = System.Text.RegularExpressions.Regex.Match(input, @"(\d+) passed");
            if (!match.Success)
            {
                return null;
            }

            var passed = int.Parse(match.Groups[1].Value);
            var data = new TestResult { Total = passed, Passed = passed, Failed = 0, Skipped = 0 };
            return (data, ["fell back to regex extraction"]);
        }
    }

    [Fact]
    public void Parse_ValidJson_ReturnsFullTier()
    {
        var result = new FakeParser().Parse(/*lang=json,strict*/ "{\"passed\": 5}");
        Assert.Equal(1, result.Tier);
        Assert.Equal(5, result.Unwrap().Passed);
    }

    [Fact]
    public void Parse_JsonParseFails_FallsBackToRegexTier()
    {
        // Not JSON at all, but matches the regex fallback shape.
        var result = new FakeParser().Parse("Tests: 7 passed, 0 failed");
        Assert.Equal(2, result.Tier);
        Assert.Equal(7, result.Unwrap().Passed);
        Assert.Single(result.GetWarnings());
    }

    [Fact]
    public void Parse_JsonAndRegexBothFail_FallsBackToTruncatedPassthrough()
    {
        var input = "completely unstructured gibberish output with no recognizable shape";
        var result = new FakeParser().Parse(input);
        Assert.Equal(3, result.Tier);
        Assert.IsType<ParseResult<TestResult>.Passthrough>(result);
        var passthrough = (ParseResult<TestResult>.Passthrough)result;
        Assert.Equal(input, passthrough.Raw); // short input: under the 2000-char default, unchanged.
    }

    [Fact]
    public void ParseWithTier_ForcesDegradedDownToPassthrough_WhenCeilingIsOne()
    {
        var result = new FakeParser().ParseWithTier("Tests: 7 passed, 0 failed", maxTier: 1);
        Assert.Equal(3, result.Tier);
        Assert.IsType<ParseResult<TestResult>.Passthrough>(result);
    }

    [Fact]
    public void ParseWithTier_AllowsFullTier_WhenCeilingIsOne()
    {
        var result = new FakeParser().ParseWithTier(/*lang=json,strict*/ "{\"passed\": 5}", maxTier: 1);
        Assert.Equal(1, result.Tier);
    }
}
