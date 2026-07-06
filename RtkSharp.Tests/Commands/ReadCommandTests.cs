using RtkSharp.Commands.System;
using RtkSharp.Core;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="ReadCommand"/>. The transformation logic (line windows, smart
/// truncation, line numbering, argument parsing) is tested as pure functions; the file/stdin
/// dispatch and exit-code behavior are tested through <see cref="ReadCommand.RunAsync"/> with
/// captured console streams over real temp files. Expectations were derived from the reference
/// <c>rtk.exe</c> oracle (e.g. <c>rtk read --max-lines 3 nums.txt</c> →
/// <c>line1\n[9 more lines]</c>) and from the synthetic unit tests in
/// <c>src/cmds/system/read.rs</c> / <c>src/core/filter.rs</c>.
/// </summary>
[Collection("Console")]
public sealed class ReadCommandTests
{
    // ---- SplitLines (Rust str::lines() semantics) ----

    [Fact]
    public void SplitLines_DropsTrailingEmptyAfterFinalNewline()
    {
        var lines = ReadCommand.SplitLines("a\nb\nc\nd\n");
        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    [Fact]
    public void SplitLines_KeepsFinalLineWithoutNewline()
    {
        var lines = ReadCommand.SplitLines("a\nb\nc\nd");
        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    [Fact]
    public void SplitLines_StripsCarriageReturns()
    {
        var lines = ReadCommand.SplitLines("a\r\nb\r\n");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void SplitLines_EmptyStringYieldsNoLines() =>
        Assert.Empty(ReadCommand.SplitLines(string.Empty));

    // Regression test for a real divergence caught in independent review, confirmed against the
    // official Rust str::lines() docs: a bare trailing \r with no following \n (the final,
    // unterminated segment) is KEPT, not stripped — only an interior \r immediately before a \n
    // is stripped. An earlier version stripped it unconditionally in both cases.
    [Fact]
    public void SplitLines_BareTrailingCarriageReturnWithNoFollowingNewline_IsKept()
    {
        var lines = ReadCommand.SplitLines("a\nb\r");
        Assert.Equal(new[] { "a", "b\r" }, lines);
    }

    [Fact]
    public void SplitLines_PreservesInteriorBlankLines()
    {
        var lines = ReadCommand.SplitLines("a\n\nb");
        Assert.Equal(new[] { "a", "", "b" }, lines);
    }

    // ---- ApplyLineWindow: tail (ported from read.rs unit tests) ----

    [Fact]
    public void ApplyLineWindow_Tail_KeepsLastNAndTrailingNewline()
    {
        var output = ReadCommand.ApplyLineWindow("a\nb\nc\nd\n", maxLines: null, tailLines: 2);
        Assert.Equal("c\nd\n", output);
    }

    [Fact]
    public void ApplyLineWindow_Tail_NoTrailingNewlineWhenInputHasNone()
    {
        var output = ReadCommand.ApplyLineWindow("a\nb\nc\nd", maxLines: null, tailLines: 2);
        Assert.Equal("c\nd", output);
    }

    [Fact]
    public void ApplyLineWindow_TailZero_ReturnsEmpty() =>
        Assert.Equal(string.Empty, ReadCommand.ApplyLineWindow("a\nb\n", maxLines: null, tailLines: 0));

    [Fact]
    public void ApplyLineWindow_Tail_LargerThanFile_ReturnsWholeFile()
    {
        var output = ReadCommand.ApplyLineWindow("a\nb\n", maxLines: null, tailLines: 10);
        Assert.Equal("a\nb\n", output);
    }

    [Fact]
    public void ApplyLineWindow_NoWindow_ReturnsVerbatim()
    {
        const string content = "keep\r\nthis\nexact\n";
        Assert.Equal(content, ReadCommand.ApplyLineWindow(content, maxLines: null, tailLines: null));
    }

    // ---- SmartTruncate (ported from filter.rs unit tests + oracle) ----

    [Fact]
    public void SmartTruncate_UnderLimit_ReturnsUnchanged()
    {
        const string input = "a\nb\nc\n";
        Assert.Equal(input, ReadCommand.SmartTruncate(input, 10));
    }

    [Fact]
    public void SmartTruncate_ExactLimit_ReturnsUnchanged()
    {
        const string input = "a\nb\nc";
        Assert.Equal(input, ReadCommand.SmartTruncate(input, 3));
    }

    [Fact]
    public void SmartTruncate_PlainText_KeepsHalfBudgetThenMarker()
    {
        // Oracle: `rtk read --max-lines 3` over 10 plain lines → "line1\n[9 more lines]".
        var input = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n";
        var output = ReadCommand.SmartTruncate(input, 3);
        Assert.Equal("line1\n[9 more lines]", output);
        Assert.DoesNotContain("// ...", output);
    }

    [Fact]
    public void SmartTruncate_KeepsStructuralLines()
    {
        // Oracle: `rtk read --max-lines 4` over the code fixture keeps imports, signatures, and
        // the closing brace: "use foo;\nfn main() {\n}\n[5 more lines]".
        var input = "use foo;\nfn main() {\n    let x = 1;\n    let y = 2;\n    let z = 3;\n    println!(\"hi\");\n}\npub fn helper() {}\n";
        var output = ReadCommand.SmartTruncate(input, 4);
        Assert.Equal("use foo;\nfn main() {\n}\n[5 more lines]", output);
    }

    [Fact]
    public void SmartTruncate_OverflowCountIsExact()
    {
        const int total = 200;
        const int max = 20;
        var input = string.Join("\n", Enumerable.Range(0, total).Select(i => $"plain text line number {i}"));

        var output = ReadCommand.SmartTruncate(input, max);
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
            var output = ReadCommand.Render(
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
            var output = ReadCommand.Render(
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
        var output = ReadCommand.FormatWithLineNumbers(input);
        Assert.StartsWith(" 1 │ line1\n", output);
        Assert.EndsWith("10 │ line10\n", output);
    }

    [Fact]
    public void FormatWithLineNumbers_SingleDigitWidth()
    {
        var output = ReadCommand.FormatWithLineNumbers("<Solution>\n</Solution>\n");
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
        Assert.Equal(expected, ReadCommand.GetExtension(path));

    // ---- ParseArgs ----

    [Fact]
    public void ParseArgs_DefaultsToNoneLevelAndCollectsFiles()
    {
        var parsed = ReadCommand.ParseArgs(new[] { "a.txt", "b.txt" });
        Assert.Equal(new[] { "a.txt", "b.txt" }, parsed.Files);
        Assert.Equal(FilterLevel.None, parsed.Level);
        Assert.Null(parsed.MaxLines);
        Assert.Null(parsed.TailLines);
        Assert.False(parsed.LineNumbers);
    }

    [Theory]
    [InlineData("--max-lines", "10")]
    [InlineData("-m", "10")]
    public void ParseArgs_MaxLines_BothForms(string flag, string value)
    {
        var parsed = ReadCommand.ParseArgs(new[] { flag, value, "f.txt" });
        Assert.Equal(10, parsed.MaxLines);
    }

    [Fact]
    public void ParseArgs_EqualsForm()
    {
        var parsed = ReadCommand.ParseArgs(new[] { "--max-lines=5", "f.txt" });
        Assert.Equal(5, parsed.MaxLines);
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("--line-numbers")]
    public void ParseArgs_LineNumbers(string flag)
    {
        var parsed = ReadCommand.ParseArgs(new[] { flag, "f.txt" });
        Assert.True(parsed.LineNumbers);
    }

    [Fact]
    public void ParseArgs_ConflictingWindowFlags_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            ReadCommand.ParseArgs(new[] { "--max-lines", "3", "--tail-lines", "2", "f.txt" }));

    [Fact]
    public void ParseArgs_NoFiles_Throws() =>
        Assert.Throws<ArgumentException>(() => ReadCommand.ParseArgs(new[] { "-n" }));

    [Fact]
    public void ParseArgs_UnknownFlag_Throws() =>
        Assert.Throws<ArgumentException>(() => ReadCommand.ParseArgs(new[] { "--bogus", "f.txt" }));

    [Fact]
    public void ParseArgs_DashIsFileNotFlag()
    {
        var parsed = ReadCommand.ParseArgs(new[] { "-" });
        Assert.Equal(new[] { "-" }, parsed.Files);
    }

    // ---- RunAsync (end-to-end over temp files, captured console) ----

    [Fact]
    public async Task RunAsync_SingleFile_PrintsVerbatimAndReturnsZero()
    {
        var path = NewTempFile("hello\nworld\n");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { path });
            Assert.Equal(0, exit);
            Assert.Equal("hello\nworld\n", outText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_MaxLines_TruncatesWithMarker()
    {
        var path = NewTempFile("line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { "--max-lines", "3", path });
            Assert.Equal(0, exit);
            Assert.Equal("line1\n[9 more lines]", outText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_TailLines_KeepsLastN()
    {
        var path = NewTempFile("a\nb\nc\nd\n");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { "--tail-lines", "2", path });
            Assert.Equal(0, exit);
            Assert.Equal("c\nd\n", outText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_LineNumbers_PrefixesEachLine()
    {
        var path = NewTempFile("first\nsecond\n");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { "-n", path });
            Assert.Equal(0, exit);
            Assert.Equal("1 │ first\n2 │ second\n", outText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_MultipleFiles_ConcatenatesWithoutHeaders()
    {
        var f1 = NewTempFile("aaa\nbbb\n");
        var f2 = NewTempFile("ccc\nddd\n");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { f1, f2 });
            Assert.Equal(0, exit);
            Assert.Equal("aaa\nbbb\nccc\nddd\n", outText);
        }
        finally
        {
            File.Delete(f1);
            File.Delete(f2);
        }
    }

    [Fact]
    public async Task RunAsync_MissingFile_ReportsErrorAndReturnsOne()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rtk_missing_" + Guid.NewGuid().ToString("N") + ".txt");
        var (exit, _, errText) = await RunCaptureAsync(new[] { missing });
        Assert.Equal(1, exit);
        Assert.Contains("cat:", errText);
        Assert.Contains(missing, errText);
    }

    [Fact]
    public async Task RunAsync_ValidAndMissing_PrintsValidStillReturnsOne()
    {
        var valid = NewTempFile("valid content\n");
        var missing = Path.Combine(Path.GetTempPath(), "rtk_missing_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var (exit, outText, errText) = await RunCaptureAsync(new[] { valid, missing });
            Assert.Equal(1, exit);
            Assert.Contains("valid content", outText);
            Assert.Contains(missing, errText);
        }
        finally
        {
            File.Delete(valid);
        }
    }

    [Fact]
    public async Task RunAsync_MinimalLevel_StripsCommentsForDetectedLanguage()
    {
        var path = NewTempFile("// a comment\nfn main() {}\n", extension: ".rs");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { "--level", "minimal", path });
            Assert.Equal(0, exit);
            Assert.DoesNotContain("// a comment", outText);
            Assert.Contains("fn main()", outText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidLevel_ReturnsTwo()
    {
        var path = NewTempFile("x\n");
        try
        {
            var (exit, _, errText) = await RunCaptureAsync(new[] { "--level", "bogus", path });
            Assert.Equal(2, exit);
            Assert.Contains("invalid value", errText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_AstLevel_OnCSharpFile_UsesRoslynAnalyzer()
    {
        var path = NewTempFile(
            "public class Foo\n{\n    public void Bar()\n    {\n        var x = 1;\n    }\n}\n",
            extension: ".cs");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { "--level", "ast", path });
            Assert.Equal(0, exit);
            Assert.Contains("public void Bar()", outText);
            Assert.DoesNotContain("var x = 1;", outText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_AstLevel_OnLanguageWithNoAnalyzer_FallsBackToAggressive()
    {
        // An unrecognized extension maps to Language.Unknown, which has no registered AST
        // analyzer (unlike Rust, which now does — see RustAstAnalyzer) — a stable target for
        // exercising the fallback path.
        var path = NewTempFile("// a comment\nvoid main() {\n    int x = 1;\n}\n", extension: ".rtkunknownext");
        try
        {
            var (exit, outText, errText) = await RunCaptureAsync(new[] { "--level", "ast", path });
            Assert.Equal(0, exit);
            Assert.Contains("void main()", outText);
            Assert.Contains("falling back to aggressive", errText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_AggressiveLevel_OnDotfileWithNoExtension_TreatedAsUnknownNotData()
    {
        // ".env" has no extension under Rust's Path::extension() semantics (leading dot, no
        // other dot) — Language::Unknown, not Language::Data, so it DOES get comment-stripped
        // (with C-style // patterns, which is what the real oracle does for .env files too).
        var dir = Directory.CreateTempSubdirectory("rtk_read_dotfile_").FullName;
        var path = Path.Combine(dir, ".env");
        File.WriteAllText(path, "// comment\nKEY=value\n");
        try
        {
            var (exit, outText, _) = await RunCaptureAsync(new[] { "--level", "minimal", path });
            Assert.Equal(0, exit);
            Assert.DoesNotContain("// comment", outText);
            Assert.Contains("KEY=value", outText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ConflictingFlags_ReturnsTwo()
    {
        var (exit, _, _) = await RunCaptureAsync(new[] { "--max-lines", "3", "--tail-lines", "2", "f.txt" });
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task RunAsync_DuplicateStdin_WarnsOnce()
    {
        var (_, _, errText) = await RunCaptureAsync(new[] { "-", "-" }, stdin: "piped\n");
        Assert.Contains("stdin specified more than once", errText);
    }

    private static string NewTempFile(string content, string extension = ".txt")
    {
        var path = Path.Combine(Path.GetTempPath(), "rtk_read_" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(int Exit, string Out, string Err)> RunCaptureAsync(string[] args, string? stdin = null)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var origIn = Console.In;
        var sw = new StringWriter();
        var se = new StringWriter();
        try
        {
            Console.SetOut(sw);
            Console.SetError(se);
            if (stdin is not null)
            {
                Console.SetIn(new StringReader(stdin));
            }

            var exit = await ReadCommand.RunAsync(args);
            return (exit, sw.ToString(), se.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
            Console.SetIn(origIn);
        }
    }
}

/// <summary>
/// Serializes tests that redirect the process-global <see cref="Console"/> streams so they do
/// not race with each other across the xUnit parallel test runner.
/// </summary>
[CollectionDefinition("Console")]
public sealed class ConsoleCollection;
