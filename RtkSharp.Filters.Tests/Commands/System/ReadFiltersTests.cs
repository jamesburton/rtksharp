using System;
using System.IO;
using System.Linq;
using RtkSharp.Core;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="ReadFilters"/>. The transformation logic (line splitting, line windows,
/// smart truncation, line numbering, extension detection) is tested as pure functions. Moved
/// from <c>RtkSharp.Tests.Commands.ReadCommandTests</c> when the underlying pure methods moved
/// from <c>RtkSharp.Commands.System.ReadCommand</c> to <see cref="ReadFilters"/> (Task 14 of the
/// filters-library extraction). Argument parsing (<c>ParseArgs</c>) and the file/stdin dispatch
/// (<c>RunAsync</c>) are not pure and remain in <c>ReadCommand</c>.
/// </summary>
public sealed class ReadFiltersTests
{
    // ---- SplitLines (Rust str::lines() semantics) ----

    [Fact]
    public void SplitLines_DropsTrailingEmptyAfterFinalNewline()
    {
        var lines = ReadFilters.SplitLines("a\nb\nc\nd\n");
        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    [Fact]
    public void SplitLines_KeepsFinalLineWithoutNewline()
    {
        var lines = ReadFilters.SplitLines("a\nb\nc\nd");
        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    [Fact]
    public void SplitLines_StripsCarriageReturns()
    {
        var lines = ReadFilters.SplitLines("a\r\nb\r\n");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void SplitLines_EmptyStringYieldsNoLines() =>
        Assert.Empty(ReadFilters.SplitLines(string.Empty));

    // Regression test for a real divergence caught in independent review, confirmed against the
    // official Rust str::lines() docs: a bare trailing \r with no following \n (the final,
    // unterminated segment) is KEPT, not stripped — only an interior \r immediately before a \n
    // is stripped. An earlier version stripped it unconditionally in both cases.
    [Fact]
    public void SplitLines_BareTrailingCarriageReturnWithNoFollowingNewline_IsKept()
    {
        var lines = ReadFilters.SplitLines("a\nb\r");
        Assert.Equal(new[] { "a", "b\r" }, lines);
    }

    [Fact]
    public void SplitLines_PreservesInteriorBlankLines()
    {
        var lines = ReadFilters.SplitLines("a\n\nb");
        Assert.Equal(new[] { "a", "", "b" }, lines);
    }

    // ---- ApplyLineWindow: tail (ported from read.rs unit tests) ----

    [Fact]
    public void ApplyLineWindow_Tail_KeepsLastNAndTrailingNewline()
    {
        var output = ReadFilters.ApplyLineWindow("a\nb\nc\nd\n", maxLines: null, tailLines: 2);
        Assert.Equal("c\nd\n", output);
    }

    [Fact]
    public void ApplyLineWindow_Tail_NoTrailingNewlineWhenInputHasNone()
    {
        var output = ReadFilters.ApplyLineWindow("a\nb\nc\nd", maxLines: null, tailLines: 2);
        Assert.Equal("c\nd", output);
    }

    [Fact]
    public void ApplyLineWindow_TailZero_ReturnsEmpty() =>
        Assert.Equal(string.Empty, ReadFilters.ApplyLineWindow("a\nb\n", maxLines: null, tailLines: 0));

    [Fact]
    public void ApplyLineWindow_Tail_LargerThanFile_ReturnsWholeFile()
    {
        var output = ReadFilters.ApplyLineWindow("a\nb\n", maxLines: null, tailLines: 10);
        Assert.Equal("a\nb\n", output);
    }

    [Fact]
    public void ApplyLineWindow_NoWindow_ReturnsVerbatim()
    {
        const string content = "keep\r\nthis\nexact\n";
        Assert.Equal(content, ReadFilters.ApplyLineWindow(content, maxLines: null, tailLines: null));
    }

    // ---- SmartTruncate (ported from filter.rs unit tests + oracle) ----

    [Fact]
    public void SmartTruncate_UnderLimit_ReturnsUnchanged()
    {
        const string input = "a\nb\nc\n";
        Assert.Equal(input, ReadFilters.SmartTruncate(input, 10));
    }

    [Fact]
    public void SmartTruncate_ExactLimit_ReturnsUnchanged()
    {
        const string input = "a\nb\nc";
        Assert.Equal(input, ReadFilters.SmartTruncate(input, 3));
    }

    [Fact]
    public void SmartTruncate_PlainText_KeepsHalfBudgetThenMarker()
    {
        // Oracle: `rtk read --max-lines 3` over 10 plain lines → "line1\n[9 more lines]".
        var input = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n";
        var output = ReadFilters.SmartTruncate(input, 3);
        Assert.Equal("line1\n[9 more lines]", output);
        Assert.DoesNotContain("// ...", output);
    }

    [Fact]
    public void SmartTruncate_KeepsStructuralLines()
    {
        // Oracle: `rtk read --max-lines 4` over the code fixture keeps imports, signatures, and
        // the closing brace: "use foo;\nfn main() {\n}\n[5 more lines]".
        var input = "use foo;\nfn main() {\n    let x = 1;\n    let y = 2;\n    let z = 3;\n    println!(\"hi\");\n}\npub fn helper() {}\n";
        var output = ReadFilters.SmartTruncate(input, 4);
        Assert.Equal("use foo;\nfn main() {\n}\n[5 more lines]", output);
    }

    [Fact]
    public void SmartTruncate_OverflowCountIsExact()
    {
        const int total = 200;
        const int max = 20;
        var input = string.Join("\n", Enumerable.Range(0, total).Select(i => $"plain text line number {i}"));

        var output = ReadFilters.SmartTruncate(input, max);
        var overflow = output.Split('\n').Single(l => l.Contains("more lines"));
        var reported = int.Parse(overflow.Trim().TrimStart('[').Split(' ')[0]);
        var kept = output.Split('\n').Count(l => !l.Contains("more lines"));

        Assert.Equal(total, kept + reported);
    }

    // ---- Render: filter-emptied-non-empty-content safety fallback (read.rs's own guard) ----

    [Fact]
    public void Render_FilterEmptiesNonEmptyContent_FallsBackToRawAndWarns()
    {
        // A file containing only a single-line comment: MinimalFilter strips it to nothing.
        var origErr = Console.Error;
        var se = new StringWriter();
        try
        {
            Console.SetError(se);
            var output = ReadFilters.Render(
                "// only a comment\n", Language.Rust, FilterLevel.Minimal, maxLines: null, tailLines: null,
                lineNumbers: false, filePath: "comment-only.rs");
            Assert.Equal("// only a comment\n", output);
            Assert.Contains("filter produced empty output", se.ToString());
            Assert.Contains("comment-only.rs", se.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public void Render_StdinPath_FilterEmptiesContent_NoFallbackNoWarning()
    {
        // Mirrors read.rs's run_stdin, which has NO empty-output safety guard (only run() does)
        // — filePath: null skips the check entirely, matching a genuine oracle asymmetry.
        var origErr = Console.Error;
        var se = new StringWriter();
        try
        {
            Console.SetError(se);
            var output = ReadFilters.Render(
                "// only a comment\n", Language.Rust, FilterLevel.Minimal, maxLines: null, tailLines: null,
                lineNumbers: false, filePath: null);
            Assert.Equal(string.Empty, output);
            Assert.DoesNotContain("filter produced empty output", se.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    // ---- FormatWithLineNumbers (oracle-derived) ----

    [Fact]
    public void FormatWithLineNumbers_RightAlignsToDigitWidth()
    {
        // Oracle: `rtk read -n nums.txt` (10 lines) → " 1 │ line1\n" … "10 │ line10\n".
        var input = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n";
        var output = ReadFilters.FormatWithLineNumbers(input);
        Assert.StartsWith(" 1 │ line1\n", output);
        Assert.EndsWith("10 │ line10\n", output);
    }

    [Fact]
    public void FormatWithLineNumbers_SingleDigitWidth()
    {
        var output = ReadFilters.FormatWithLineNumbers("<Solution>\n</Solution>\n");
        Assert.Equal("1 │ <Solution>\n2 │ </Solution>\n", output);
    }

    // ---- GetExtension (Rust Path::extension() semantics) ----

    [Theory]
    [InlineData("foo.rs", "rs")]
    [InlineData("dir/foo.tar.gz", "gz")]
    [InlineData("noext", "")]
    [InlineData(".gitignore", "")]
    [InlineData(".env", "")]
    [InlineData(".env.local", "local")]
    [InlineData("dir/.env", "")]
    public void GetExtension_MatchesRustPathExtensionSemantics(string path, string expected) =>
        Assert.Equal(expected, ReadFilters.GetExtension(path));
}
