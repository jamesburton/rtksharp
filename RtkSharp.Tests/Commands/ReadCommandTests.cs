using RtkSharp;
using RtkSharp.Cli;
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
        Assert.Throws<CommandArgumentParseException>(() =>
            ReadCommand.ParseArgs(new[] { "--max-lines", "3", "--tail-lines", "2", "f.txt" }));

    [Fact]
    public void ParseArgs_NoFiles_Throws() =>
        Assert.Throws<CommandArgumentParseException>(() => ReadCommand.ParseArgs(new[] { "-n" }));

    [Fact]
    public void ParseArgs_UnknownFlag_Throws() =>
        Assert.Throws<CommandArgumentParseException>(() => ReadCommand.ParseArgs(new[] { "--bogus", "f.txt" }));

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
    public async Task RunAsync_InvalidLevel_ThrowsCommandArgumentParseException()
    {
        // RunAsync no longer catches its own parse failures — it's RtkProgram's dispatch layer
        // that catches CommandArgumentParseException and re-routes to the fallback path (see
        // RunAsync_InvalidLevel_ViaRtkProgram_FallsBackAndReturns127 for the end-to-end case).
        var path = NewTempFile("x\n");
        try
        {
            var ex = await Assert.ThrowsAsync<CommandArgumentParseException>(
                () => ReadCommand.RunAsync(new[] { "--level", "bogus", path }));
            Assert.Contains("invalid value", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidLevel_ViaRtkProgram_FallsBackAndReturns127()
    {
        // End-to-end: oracle-verified real behavior is that `rtk read --level bogus file` (a
        // clap-level parse failure for a PASSTHROUGH-classified verb) falls back to a raw PATH
        // exec attempt of "read" (no such binary exists) and exits 127 — NOT read.rs's own body,
        // which never runs. This is the actual fix for the compatibility-ledger's documented
        // clap-fallback-exec divergence.
        var path = NewTempFile("x\n");
        try
        {
            var exit = await RtkProgram.RunAsync(new[] { "read", "--level", "bogus", path });
            Assert.Equal(127, exit);
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
    public async Task RunAsync_ConflictingFlags_ThrowsCommandArgumentParseException() =>
        await Assert.ThrowsAsync<CommandArgumentParseException>(
            () => ReadCommand.RunAsync(new[] { "--max-lines", "3", "--tail-lines", "2", "f.txt" }));

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
