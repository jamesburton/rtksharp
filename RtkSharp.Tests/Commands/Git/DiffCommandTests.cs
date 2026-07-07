using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Commands.Git;
using Xunit;

namespace RtkSharp.Tests.Commands.Git;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/git/diff_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c> (23
/// tests covering <c>similarity</c>, <c>compute_diff</c>, <c>render_file_diff</c>, and
/// <c>condense_unified_diff</c>), plus new coverage for <see cref="DiffCommand.ParseArgs"/> and the
/// PASSTHROUGH parse-failure / lone-positional-means-stdin dispatch quirks.
/// </summary>
public sealed class DiffCommandTests
{
    // ===================== similarity =====================

    [Fact]
    public void Similarity_Identical_ReturnsOne()
    {
        Assert.Equal(1.0, DiffCommand.Similarity("hello", "hello"));
    }

    [Fact]
    public void Similarity_CompletelyDifferent_ReturnsZero()
    {
        Assert.Equal(0.0, DiffCommand.Similarity("abc", "xyz"));
    }

    [Fact]
    public void Similarity_EmptyStrings_ReturnsOneByConvention()
    {
        Assert.Equal(1.0, DiffCommand.Similarity("", ""));
    }

    [Fact]
    public void Similarity_PartialOverlap_ComputesJaccardIndex()
    {
        // Shared: a, b. Union: a, b, c, d, e, f = 6. Jaccard = 2/6.
        var s = DiffCommand.Similarity("abcd", "abef");
        Assert.True(Math.Abs(s - 2.0 / 6.0) < double.Epsilon);
    }

    [Fact]
    public void Similarity_ThresholdForModified_ExceedsHalf()
    {
        Assert.True(DiffCommand.Similarity("let x = 1;", "let x = 2;") > 0.5);
    }

    // ===================== compute_diff =====================

    [Fact]
    public void ComputeDiff_Identical_NoChanges()
    {
        string[] a = ["line1", "line2", "line3"];
        string[] b = ["line1", "line2", "line3"];
        var result = DiffCommand.ComputeDiff(a, b);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Modified);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void ComputeDiff_AddedLines_Counted()
    {
        string[] a = ["line1"];
        string[] b = ["line1", "line2", "line3"];
        var result = DiffCommand.ComputeDiff(a, b);

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void ComputeDiff_RemovedLines_Counted()
    {
        string[] a = ["line1", "line2", "line3"];
        string[] b = ["line1"];
        var result = DiffCommand.ComputeDiff(a, b);

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.Added);
    }

    [Fact]
    public void ComputeDiff_ModifiedLine_SimilarLinesClassifiedAsModified()
    {
        string[] a = ["let x = 1;"];
        string[] b = ["let x = 2;"];
        var result = DiffCommand.ComputeDiff(a, b);

        Assert.Equal(1, result.Modified);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void ComputeDiff_CompletelyDifferentLine_ClassifiedAsAddedAndRemoved()
    {
        string[] a = ["aaaa"];
        string[] b = ["zzzz"];
        var result = DiffCommand.ComputeDiff(a, b);

        Assert.Equal(0, result.Modified);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void ComputeDiff_EmptyInputs_NoChanges()
    {
        var result = DiffCommand.ComputeDiff([], []);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Changes);
    }

    // ===================== render_file_diff (issue #2364 regression) =====================

    [Fact]
    public void RenderFileDiff_ModifiedOnlyYaml_NotReportedIdentical()
    {
        // "a: 1" vs "a: 2" is classified as modified (similarity > 0.5); the identical check must
        // not ignore modified-only diffs.
        var (output, code) = DiffCommand.RenderFileDiff("one.yaml", "two.yaml", "a: 1\n", "a: 2\n");

        Assert.DoesNotContain("identical", output, StringComparison.Ordinal);
        Assert.Contains("~1 modified", output, StringComparison.Ordinal);
        Assert.Contains("a: 1", output, StringComparison.Ordinal);
        Assert.Contains("a: 2", output, StringComparison.Ordinal);
        Assert.Equal(1, code);
    }

    [Fact]
    public void RenderFileDiff_ModifiedOnlyJson_NotReportedIdentical()
    {
        var (output, code) = DiffCommand.RenderFileDiff("j1.json", "j2.json", "{\"a\": 1}\n", "{\"a\": 2}\n");

        Assert.DoesNotContain("identical", output, StringComparison.Ordinal);
        Assert.Equal(1, code);
    }

    [Fact]
    public void RenderFileDiff_IdenticalFiles_ExitZero()
    {
        var (output, code) = DiffCommand.RenderFileDiff("a.yaml", "b.yaml", "a: 1\nb: 2\n", "a: 1\nb: 2\n");

        Assert.Contains("[ok] Files are identical", output, StringComparison.Ordinal);
        Assert.Equal(0, code);
    }

    [Fact]
    public void RenderFileDiff_AddedAndRemoved_ExitOne()
    {
        var (output, code) = DiffCommand.RenderFileDiff("t1.txt", "t2.txt", "x\n", "y\n");

        Assert.Contains("+1 added, -1 removed", output, StringComparison.Ordinal);
        Assert.Equal(1, code);
    }

    // ===================== condense_unified_diff =====================

    [Fact]
    public void CondenseUnifiedDiff_SingleFile()
    {
        const string diff = """
            diff --git a/src/main.rs b/src/main.rs
            --- a/src/main.rs
            +++ b/src/main.rs
            @@ -1,3 +1,4 @@
             fn main() {
            +    println!("hello");
                 println!("world");
             }
            """;

        var result = DiffCommand.CondenseUnifiedDiff(diff);

        Assert.Contains("src/main.rs", result, StringComparison.Ordinal);
        Assert.Contains("+1", result, StringComparison.Ordinal);
        Assert.Contains("println", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CondenseUnifiedDiff_MultipleFiles()
    {
        const string diff = """
            diff --git a/a.rs b/a.rs
            --- a/a.rs
            +++ b/a.rs
            +added line
            diff --git a/b.rs b/b.rs
            --- a/b.rs
            +++ b/b.rs
            -removed line
            """;

        var result = DiffCommand.CondenseUnifiedDiff(diff);

        Assert.Contains("a.rs", result, StringComparison.Ordinal);
        Assert.Contains("b.rs", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CondenseUnifiedDiff_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DiffCommand.CondenseUnifiedDiff(string.Empty));
    }

    private static string MakeLargeUnifiedDiff(int added, int removed)
    {
        var lines = new List<string>
        {
            "diff --git a/config.yaml b/config.yaml",
            "--- a/config.yaml",
            "+++ b/config.yaml",
            "@@ -1,200 +1,200 @@",
        };

        for (var i = 0; i < removed; i++)
        {
            lines.Add($"-old_value_{i}");
        }

        for (var i = 0; i < added; i++)
        {
            lines.Add($"+new_value_{i}");
        }

        return string.Join("\n", lines);
    }

    [Fact]
    public void CondenseUnifiedDiff_OverflowCountAccuracy()
    {
        // 100 added + 100 removed = 200 total changes.
        // True overflow = 200 - 10 = 190 (the stale-overflow-count quirk; see class remarks).
        var diff = MakeLargeUnifiedDiff(100, 100);
        var result = DiffCommand.CondenseUnifiedDiff(diff);

        Assert.Contains("+190 more", result, StringComparison.Ordinal);
        Assert.DoesNotContain("+5 more", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CondenseUnifiedDiff_NoFalseOverflow_WhenUnderTen()
    {
        // 8 changes total — no overflow message.
        var diff = MakeLargeUnifiedDiff(4, 4);
        var result = DiffCommand.CondenseUnifiedDiff(diff);

        Assert.DoesNotContain("more", result, StringComparison.Ordinal);
    }

    // ===================== truncation accuracy =====================

    [Fact]
    public void ComputeDiff_LargeDiff_NoTruncation()
    {
        var a = new string[500];
        var b = new string[500];
        for (var i = 0; i < 500; i++)
        {
            a[i] = $"line_{i}";
            b[i] = i % 3 == 0 ? $"CHANGED_{i}" : $"line_{i}";
        }

        var result = DiffCommand.ComputeDiff(a, b);

        Assert.True(result.Changes.Count > 100, $"Expected 100+ changes, got {result.Changes.Count}");
        Assert.NotEmpty(result.Changes);
    }

    [Fact]
    public void FormatDiffChanges_ShowsAllChanges()
    {
        var a = new string[100];
        var b = new string[100];
        for (var i = 0; i < 100; i++)
        {
            a[i] = $"old_line_{i}";
            b[i] = $"new_line_{i}";
        }

        var diff = DiffCommand.ComputeDiff(a, b);
        var output = DiffCommand.FormatDiffChanges(diff);

        Assert.Contains("old_line_0", output, StringComparison.Ordinal);
        Assert.Contains("new_line_99", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeDiff_LongLines_NotTruncated()
    {
        var longLine = new string('x', 500);
        string[] a = [longLine];
        string[] b = ["short"];
        var result = DiffCommand.ComputeDiff(a, b);

        var change = result.Changes[0];
        var content = change.Kind == DiffCommand.DiffChangeKind.Modified ? change.Text : change.Text;
        Assert.Equal(500, content.Length);
    }

    // ===================== ParseArgs / PASSTHROUGH parse-failure contract =====================

    [Fact]
    public void ParseArgs_ZeroArgs_ThrowsCommandArgumentParseException()
    {
        Assert.Throws<CommandArgumentParseException>(() => DiffCommand.ParseArgs([]));
    }

    [Fact]
    public void ParseArgs_OneArg_File2IsNull()
    {
        var (file1, file2) = DiffCommand.ParseArgs(["foo.txt"]);
        Assert.Equal("foo.txt", file1);
        Assert.Null(file2);
    }

    [Fact]
    public void ParseArgs_TwoArgs_BothReturned()
    {
        var (file1, file2) = DiffCommand.ParseArgs(["foo.txt", "bar.txt"]);
        Assert.Equal("foo.txt", file1);
        Assert.Equal("bar.txt", file2);
    }

    [Fact]
    public void ParseArgs_ThreeArgs_ThrowsCommandArgumentParseException()
    {
        Assert.Throws<CommandArgumentParseException>(() => DiffCommand.ParseArgs(["a", "b", "c"]));
    }

    // ===================== RunAsync integration: lone-positional stdin quirk =====================

    [Fact]
    public async Task RunAsync_LonePositional_IgnoresValueAndReadsStdin()
    {
        // Genuine Rust-source quirk: `rtk diff some-nonexistent-file.txt` (exactly one positional)
        // does NOT try to read that file — it silently discards the value and reads a unified diff
        // from stdin instead, because dispatch only branches on whether file2 was supplied.
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("+++ b/foo.rs\n+added\n"));
            var exitCode = await DiffCommand.RunAsync(["this-file-does-not-exist.txt"]);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void RunStdin_CondensesInputAndReturnsZero()
    {
        var stdout = new StringWriter();
        var exitCode = DiffCommand.RunStdin(0, new StringReader("+++ b/foo.rs\n+added line\n"), stdout);

        Assert.Equal(0, exitCode);
        Assert.Contains("foo.rs", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_TwoFiles_ReturnsDiffConventionExitCode()
    {
        var dir = Directory.CreateTempSubdirectory("rtk-diff-test-");
        try
        {
            var file1 = Path.Combine(dir.FullName, "a.txt");
            var file2 = Path.Combine(dir.FullName, "b.txt");
            File.WriteAllText(file1, "hello\n");
            File.WriteAllText(file2, "world\n");

            var stdout = new StringWriter();
            var exitCode = DiffCommand.Run(file1, file2, 0, stdout);

            Assert.Equal(1, exitCode);
            Assert.Contains("added", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
