using RtkSharp.Commands.System;

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

    // ---- ParseArgs ----

    [Fact]
    public void ParseArgs_DefaultsToNoneLevelAndCollectsFiles()
    {
        var parsed = ReadCommand.ParseArgs(new[] { "a.txt", "b.txt" });
        Assert.Equal(new[] { "a.txt", "b.txt" }, parsed.Files);
        Assert.Equal(ReadCommand.FilterLevelOption.None, parsed.Level);
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
    public async Task RunAsync_MinimalLevel_ReportsUnsupportedAndReturnsTwo()
    {
        var path = NewTempFile("x\n");
        try
        {
            var (exit, _, errText) = await RunCaptureAsync(new[] { "--level", "minimal", path });
            Assert.Equal(2, exit);
            Assert.Contains("not yet supported", errText);
        }
        finally
        {
            File.Delete(path);
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

    private static string NewTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "rtk_read_" + Guid.NewGuid().ToString("N") + ".txt");
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
