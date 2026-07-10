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
/// Test-for-test port of Rust <c>src/cmds/git/diff_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>, covering
/// new coverage for <see cref="DiffCommand.ParseArgs"/> and the PASSTHROUGH parse-failure /
/// lone-positional-means-stdin dispatch quirks. Tests for <c>similarity</c>, <c>compute_diff</c>,
/// <c>render_file_diff</c>, and <c>condense_unified_diff</c> moved to
/// <c>RtkSharp.Filters.Tests.Commands.Git.DiffFiltersTests</c> when those pure methods moved to
/// <see cref="RtkSharp.Filters.Commands.Git.DiffFilters"/> (Task 4 of the filters-library extraction).
/// </summary>
public sealed class DiffCommandTests
{
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
