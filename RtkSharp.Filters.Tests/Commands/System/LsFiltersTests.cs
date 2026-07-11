using System;
using System.IO;
using System.Runtime.CompilerServices;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="LsFilters"/>. The filter is a pure function, so the fixture-based
/// tests capture REAL <c>ls -la</c> output once (frozen under
/// <c>RtkSharp.Tests/Fixtures/ls/*_raw.txt</c>) together with the expected filtered form
/// produced by the reference <c>rtk.exe</c> oracle at capture time
/// (<c>*_expected.txt</c>). Because directory listings drift over time, the raw input and
/// its oracle-derived expected output are frozen together and never regenerated live. The
/// remaining tests port the synthetic-input unit tests from <c>src/cmds/system/ls.rs</c>.
/// Moved from <c>RtkSharp.Tests.Commands.LsCommandTests</c> when the underlying pure methods
/// moved from <c>RtkSharp.Commands.System.LsCommand</c> to <see cref="LsFilters"/> (Task 14 of
/// the filters-library extraction). <c>RunAsync</c> is not pure and remains in
/// <c>LsCommand</c>.
/// </summary>
public sealed class LsFiltersTests
{
    private static string FixturesDir([CallerFilePath] string? thisFile = null) =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "RtkSharp.Tests", "Fixtures", "ls");

    // Normalize CRLF -> LF: git's autocrlf may rewrite the checked-in fixtures to CRLF on
    // checkout, but the filter always emits LF, so comparisons must be newline-agnostic.
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FixturesDir(), name)).Replace("\r\n", "\n");

    // ---- Fixture-based tests (real captured `ls -la` output vs oracle) ----

    [Fact]
    public void FilterLs_PlainDirectory_MatchesOracle()
    {
        var raw = ReadFixture("rtksharp_raw.txt");
        var expected = ReadFixture("rtksharp_expected.txt");

        var actual = LsFilters.FilterLs(raw, showAll: false, showLong: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FilterLs_LongMode_ShowsOctalPermsMatchingOracle()
    {
        var raw = ReadFixture("rtksharp_raw.txt");
        var expected = ReadFixture("rtksharp_long_expected.txt");

        var actual = LsFilters.FilterLs(raw, showAll: false, showLong: true);

        Assert.Equal(expected, actual);
        // Octal perms are the distinguishing feature of long mode.
        Assert.Contains("755  Cli/", actual, StringComparison.Ordinal);
        Assert.Contains("644  Program.cs  2.2K", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterLs_HiddenFilesWithoutAll_FiltersNoiseDirs()
    {
        var raw = ReadFixture("root_raw.txt");
        var expected = ReadFixture("root_noall_expected.txt");

        var actual = LsFilters.FilterLs(raw, showAll: false, showLong: false);

        Assert.Equal(expected, actual);
        // Non-noise hidden entries are kept; noise dirs (.git, target) are dropped.
        Assert.Contains(".gitignore", actual, StringComparison.Ordinal);
        Assert.DoesNotContain(".git/", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("target/", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterLs_HiddenFilesWithAll_KeepsNoiseDirs()
    {
        var raw = ReadFixture("root_raw.txt");
        var expected = ReadFixture("root_all_expected.txt");

        var actual = LsFilters.FilterLs(raw, showAll: true, showLong: false);

        Assert.Equal(expected, actual);
        Assert.Contains(".git/", actual, StringComparison.Ordinal);
        Assert.Contains("target/", actual, StringComparison.Ordinal);
    }

    // ---- Flag detection ----

    [Theory]
    [InlineData(new[] { "-la" }, true)]
    [InlineData(new[] { "-a" }, true)]
    [InlineData(new[] { "--all" }, true)]
    [InlineData(new[] { "-l" }, false)]
    [InlineData(new[] { "path" }, false)]
    public void DetectShowAll_MatchesRustSemantics(string[] args, bool expected) =>
        Assert.Equal(expected, LsFilters.DetectShowAll(args));

    [Theory]
    [InlineData(new[] { "-l" }, true)]
    [InlineData(new[] { "-g" }, true)]
    [InlineData(new[] { "-n" }, true)]
    [InlineData(new[] { "-o" }, true)]
    [InlineData(new[] { "--full-time" }, true)]
    [InlineData(new[] { "--format=long" }, true)]
    [InlineData(new[] { "--format=verbose" }, true)]
    [InlineData(new[] { "-a" }, false)]
    [InlineData(new[] { "--all" }, false)]
    public void DetectShowLong_MatchesRustSemantics(string[] args, bool expected) =>
        Assert.Equal(expected, LsFilters.DetectShowLong(args));

    // ---- human_size ----

    [Theory]
    [InlineData(0UL, "0B")]
    [InlineData(500UL, "500B")]
    [InlineData(1024UL, "1.0K")]
    [InlineData(1234UL, "1.2K")]
    [InlineData(1_048_576UL, "1.0M")]
    [InlineData(2_500_000UL, "2.4M")]
    public void HumanSize_MatchesRust(ulong bytes, string expected) =>
        Assert.Equal(expected, LsFilters.HumanSize(bytes));

    // ---- perms_to_octal ----

    [Theory]
    [InlineData("-rw-r--r--", "644")]
    [InlineData("-rwxr-xr-x", "755")]
    [InlineData("drwxr-xr-x", "755")]
    [InlineData("-rw-------", "600")]
    [InlineData("-rwxrwxrwx", "777")]
    [InlineData("----------", "000")]
    [InlineData("lrwxr-xr-x", "755")]
    public void PermsToOctal_Common(string perms, string expected) =>
        Assert.Equal(expected, LsFilters.PermsToOctal(perms));

    [Theory]
    [InlineData("-rwsr-xr-x", "4755")]
    [InlineData("-rwSr--r--", "4644")]
    [InlineData("-rwxr-sr-x", "2755")]
    [InlineData("drwxrwxrwt", "1777")]
    [InlineData("-rwsrwsrwt", "7777")]
    public void PermsToOctal_SpecialBits(string perms, string expected) =>
        Assert.Equal(expected, LsFilters.PermsToOctal(perms));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void PermsToOctal_Garbage_ReturnsNull(string perms) =>
        Assert.Null(LsFilters.PermsToOctal(perms));

    // ---- parse_ls_line ----

    [Fact]
    public void ParseLsLine_Basic()
    {
        var result = LsFilters.ParseLsLine("-rw-r--r--  1 user staff 1234 Jan  1 12:00 file.txt");
        Assert.NotNull(result);
        Assert.Equal(('-', "-rw-r--r--", 1234UL, "file.txt"), result!.Value);
    }

    [Fact]
    public void ParseLsLine_MultilineGroupWithSpaces()
    {
        var result = LsFilters.ParseLsLine(
            "-rw-r--r--  1 fjeanne utilisa. du domaine 0 Mar 31 16:18 empty.txt");
        Assert.NotNull(result);
        Assert.Equal(('-', "-rw-r--r--", 0UL, "empty.txt"), result!.Value);
    }

    [Fact]
    public void ParseLsLine_DirWithSpaceInGroupAndName()
    {
        var result = LsFilters.ParseLsLine(
            "drwxr-xr-x  2 fjeanne utilisa. du domaine 64 Mar 31 16:18 my dir");
        Assert.NotNull(result);
        Assert.Equal(('d', "drwxr-xr-x", 64UL, "my dir"), result!.Value);
    }

    [Fact]
    public void ParseLsLine_Symlink()
    {
        var result = LsFilters.ParseLsLine("lrwxr-xr-x  1 user staff 10 Jan  1 12:00 link -> target");
        Assert.NotNull(result);
        Assert.Equal(('l', "lrwxr-xr-x", 10UL, "link -> target"), result!.Value);
    }

    [Fact]
    public void ParseLsLine_YearFormatDate()
    {
        var result = LsFilters.ParseLsLine("-rw-r--r--  1 user staff 5678 Dec 25  2024 old.tar.gz");
        Assert.NotNull(result);
        Assert.Equal(('-', "-rw-r--r--", 5678UL, "old.tar.gz"), result!.Value);
    }

    [Fact]
    public void ParseLsLine_TotalHeader_ReturnsNull() =>
        Assert.Null(LsFilters.ParseLsLine("total 48"));

    // ---- compact_ls (synthetic inputs ported from ls.rs) ----

    [Fact]
    public void CompactLs_Basic_GroupsDirsAndRendersSizes()
    {
        const string input =
            "total 48\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 .\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 ..\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 src\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 Cargo.toml\n" +
            "-rw-r--r--  1 user  staff  5678 Jan  1 12:00 README.md\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);

        Assert.Contains("src/", entries, StringComparison.Ordinal);
        Assert.Contains("1.2K", entries, StringComparison.Ordinal);
        Assert.Contains("5.5K", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("drwx", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("staff", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("total", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_FiltersNoiseDirs()
    {
        const string input =
            "total 8\n" +
            "drwxr-xr-x  2 user  staff  64 Jan  1 12:00 node_modules\n" +
            "drwxr-xr-x  2 user  staff  64 Jan  1 12:00 .git\n" +
            "drwxr-xr-x  2 user  staff  64 Jan  1 12:00 target\n" +
            "drwxr-xr-x  2 user  staff  64 Jan  1 12:00 src\n" +
            "-rw-r--r--  1 user  staff  100 Jan  1 12:00 main.rs\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);

        Assert.DoesNotContain("node_modules", entries, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("target", entries, StringComparison.Ordinal);
        Assert.Contains("src/", entries, StringComparison.Ordinal);
        Assert.Contains("main.rs", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_ShowAll_KeepsNoiseDirs()
    {
        const string input =
            "total 8\n" +
            "drwxr-xr-x  2 user  staff  64 Jan  1 12:00 .git\n" +
            "drwxr-xr-x  2 user  staff  64 Jan  1 12:00 src\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: true, showLong: false);

        Assert.Contains(".git/", entries, StringComparison.Ordinal);
        Assert.Contains("src/", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_Empty()
    {
        var (entries, summary, _) = LsFilters.CompactLs("total 0\n", showAll: false, showLong: false);
        Assert.Equal("(empty)\n", entries);
        Assert.Equal(string.Empty, summary);
    }

    [Fact]
    public void CompactLs_OnlyDotDirs_ReportsEmpty()
    {
        const string input =
            "total 0\n" +
            "drwxr-xr-x  2 lumin  wheel  64 Apr 23 00:37 .\n" +
            "drwxr-xr-x 16 root  wheel 164576 Apr 23 00:37 ..\n";
        var (entries, summary, parsedCount) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Equal(0, parsedCount);
        Assert.Equal("(empty)\n", entries);
        Assert.Equal(string.Empty, summary);
    }

    [Fact]
    public void CompactLs_NonEnglishLocale_FallsBackToEmptyParse()
    {
        const string input =
            "total 8\n" +
            "drwxr-xr-x  2 user staff  64  1月  1 12:00 src\n" +
            "-rw-r--r--  1 user staff 1234  1月  1 12:00 main.rs\n";
        var (entries, summary, parsedCount) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Equal(0, parsedCount);
        Assert.Equal(string.Empty, entries);
        Assert.Equal(string.Empty, summary);
    }

    [Fact]
    public void CompactLs_Summary_CountsFilesDirsAndExtensions()
    {
        const string input =
            "total 48\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 src\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 main.rs\n" +
            "-rw-r--r--  1 user  staff  5678 Jan  1 12:00 lib.rs\n" +
            "-rw-r--r--  1 user  staff   100 Jan  1 12:00 Cargo.toml\n";
        var (_, summary, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("Summary: 3 files, 1 dirs", summary, StringComparison.Ordinal);
        Assert.Contains(".rs", summary, StringComparison.Ordinal);
        Assert.Contains(".toml", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_LongFormat_IncludesOctal()
    {
        const string input =
            "total 48\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 src\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 Cargo.toml\n" +
            "-rwxr-xr-x  1 user  staff   500 Jan  1 12:00 build.sh\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: true);
        Assert.Contains("755  src/", entries, StringComparison.Ordinal);
        Assert.Contains("644  Cargo.toml  1.2K", entries, StringComparison.Ordinal);
        Assert.Contains("755  build.sh  500B", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_ShortFormat_OmitsOctal()
    {
        const string input =
            "total 48\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 Cargo.toml\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.DoesNotContain("644", entries, StringComparison.Ordinal);
        Assert.Contains("Cargo.toml", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_FilenamesWithSpaces()
    {
        const string input =
            "total 8\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 my file.txt\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("my file.txt", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_Symlinks()
    {
        const string input =
            "total 8\n" +
            "lrwxr-xr-x  1 user  staff  10 Jan  1 12:00 link -> target\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("link -> target", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_MultilineGroup_SizesAndNames()
    {
        const string input =
            "total 8\n" +
            "-rw-r--r--  1 fjeanne utilisa. du domaine    0 Mar 31 16:18 empty.txt\n" +
            "-rw-r--r--  1 fjeanne utilisa. du domaine 1234 Mar 31 16:18 data.json\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("empty.txt", entries, StringComparison.Ordinal);
        Assert.Contains("data.json", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("16:18", entries, StringComparison.Ordinal);
        Assert.Contains("0B", entries, StringComparison.Ordinal);
        Assert.Contains("1.2K", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_YearFormatDate()
    {
        const string input =
            "total 8\n" +
            "-rw-r--r--  1 user staff  5678 Dec 25  2024 archive.tar\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("archive.tar", entries, StringComparison.Ordinal);
        Assert.Contains("5.5K", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_CharacterDevice()
    {
        const string input =
            "crw-rw----  1 root  dialout  166, 0 Apr 22 09:46 /dev/ttyACM0\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("/dev/ttyACM0", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("(empty)", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_BlockDevice()
    {
        const string input = "brw-rw----  1 root  disk  8, 0 Apr 22 09:46 /dev/sda\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        Assert.Contains("/dev/sda", entries, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactLs_PipeLineCount_OneLinePerEntry()
    {
        const string input =
            "total 48\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 src\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 main.rs\n" +
            "-rw-r--r--  1 user  staff  5678 Jan  1 12:00 lib.rs\n";
        var (entries, _, _) = LsFilters.CompactLs(input, showAll: false, showLong: false);
        var lineCount = entries.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(3, lineCount);
    }

    [Fact]
    public void FilterLs_EntriesNeverContainSummary()
    {
        const string input =
            "total 48\n" +
            "drwxr-xr-x  2 user  staff    64 Jan  1 12:00 src\n" +
            "-rw-r--r--  1 user  staff  1234 Jan  1 12:00 main.rs\n";
        var filtered = LsFilters.FilterLs(input, showAll: false, showLong: false);
        Assert.DoesNotContain("Summary:", filtered, StringComparison.Ordinal);
    }
}
