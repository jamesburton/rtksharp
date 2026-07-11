using System;
using System.Globalization;
using System.Linq;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="GrepFilters"/>. The match-line parsing and formatting helpers are pure
/// functions ported from <c>src/cmds/system/grep_cmd.rs</c>. Moved from
/// <c>RtkSharp.Tests.Commands.GrepCommandTests</c> when <c>CleanLine</c>, <c>CompactPath</c>,
/// <c>ParseMatchLine</c>, and <c>BuildGroupedOutput</c> moved from
/// <c>RtkSharp.Commands.System.GrepCommand</c> to <see cref="GrepFilters"/> (Task 14 of the
/// filters-library extraction). The argument-cluster parsing, rg/grep dispatch, and
/// <c>UnparsedSignal</c>/<c>IsGrepErrorExit</c> helpers are not pure (or not listed for
/// extraction) and remain in <c>GrepCommand</c> alongside its end-to-end tests.
/// </summary>
public sealed class GrepFiltersTests
{
    // ---- clean_line ----

    [Fact]
    public void CleanLine_TrimsAndCaps()
    {
        var cleaned = GrepFilters.CleanLine("            const result = someFunction();", 50, null, "result");
        Assert.False(cleaned.StartsWith(' '));
        Assert.True(cleaned.Length <= 50);
    }

    [Fact]
    public void CleanLine_Multibyte_DoesNotThrow()
    {
        var cleaned = GrepFilters.CleanLine("  สวัสดีครับ นี่คือข้อความที่ยาวมากสำหรับทดสอบ  ", 20, null, "ครับ");
        Assert.NotEqual(0, cleaned.Length);
    }

    [Fact]
    public void CleanLine_Emoji_DoesNotThrow()
    {
        var cleaned = GrepFilters.CleanLine("\U0001F389\U0001F38A\U0001F388\U0001F381\U0001F382\U0001F384 some text \U0001F383\U0001F386\U0001F387✨", 15, null, "text");
        Assert.NotEqual(0, cleaned.Length);
    }

    // ---- compact_path ----

    [Fact]
    public void CompactPath_ShortensLongPaths()
    {
        var compact = GrepFilters.CompactPath("/Users/patrick/dev/project/src/components/Button.tsx");
        Assert.True(compact.Length <= 60);
    }

    // ---- parse_match_line ----

    [Fact]
    public void ParseMatchLine_Simple()
    {
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(BuildLine("file.php", 10, ':', "use FooBar;"))!.Value;
        Assert.Equal("file.php", file);
        Assert.Equal(10, lineNum);
        Assert.True(isMatch);
        Assert.Equal("use FooBar;", content);
    }

    [Fact]
    public void ParseMatchLine_ContentWithDoubleColon()
    {
        var line = BuildLine(
            "externalImportShell.class.php", 81, ':',
            "        this.queueProcessModel = ClassRegistry::init('Collections.QueueProcess');");
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(line)!.Value;
        Assert.Equal("externalImportShell.class.php", file);
        Assert.Equal(81, lineNum);
        Assert.True(isMatch);
        Assert.Equal(
            "        this.queueProcessModel = ClassRegistry::init('Collections.QueueProcess');",
            content);
    }

    [Fact]
    public void ParseMatchLine_WindowsPath()
    {
        var line = BuildLine(@"C:\src\file.rs", 42, ':', "fn main() {}");
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(line)!.Value;
        Assert.Equal(@"C:\src\file.rs", file);
        Assert.Equal(42, lineNum);
        Assert.True(isMatch);
        Assert.Equal("fn main() {}", content);
    }

    [Fact]
    public void ParseMatchLine_FilenameWithColons()
    {
        var line = BuildLine("badly_named:52:file.txt", 1, ':', "xxx");
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(line)!.Value;
        Assert.Equal("badly_named:52:file.txt", file);
        Assert.Equal(1, lineNum);
        Assert.True(isMatch);
        Assert.Equal("xxx", content);
    }

    [Fact]
    public void ParseMatchLine_ContentWithDigitColons()
    {
        var line = BuildLine("log.txt", 7, ':', "debug: counter is :42: now");
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(line)!.Value;
        Assert.Equal("log.txt", file);
        Assert.Equal(7, lineNum);
        Assert.True(isMatch);
        Assert.Equal("debug: counter is :42: now", content);
    }

    [Fact]
    public void ParseMatchLine_MalformedReturnsNull()
    {
        Assert.Null(GrepFilters.ParseMatchLine("file.rs:1:content"));
        Assert.Null(GrepFilters.ParseMatchLine("not a match line"));
        Assert.Null(GrepFilters.ParseMatchLine("file.rs" + '\0' + "fn foo()"));
        Assert.Null(GrepFilters.ParseMatchLine(""));
    }

    [Fact]
    public void ParseMatchLine_EmptyContent()
    {
        var line = BuildLine("file.rs", 7, ':', string.Empty);
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(line)!.Value;
        Assert.Equal("file.rs", file);
        Assert.Equal(7, lineNum);
        Assert.True(isMatch);
        Assert.Equal("", content);
    }

    [Fact]
    public void ParseMatchLine_ContextLine()
    {
        var line = BuildLine("file.txt", 4, '-', "after1");
        var (file, lineNum, isMatch, content) = GrepFilters.ParseMatchLine(line)!.Value;
        Assert.Equal("file.txt", file);
        Assert.Equal(4, lineNum);
        Assert.False(isMatch);
        Assert.Equal("after1", content);
    }

    // ---- BuildGroupedOutput (new coverage: previously only exercised via GrepCommand end-to-end) ----

    [Fact]
    public void BuildGroupedOutput_GroupsMatchesByFile_WithHeader()
    {
        // Long, deeply-nested paths (each >50 bytes, >3 segments) so CompactPath's shortening
        // makes the grouped rendering shorter than the plain "file:line:content" form even
        // though grouping otherwise repeats the (now-compacted) path on every line — exercising
        // the "never-worse" comparison honestly, rather than relying on it not firing.
        var raw =
            BuildLine("/Users/patrick/dev/project/src/components/very/nested/a.rs", 1, ':', "fn main() {}") + "\n" +
            BuildLine("/Users/patrick/dev/project/src/components/very/nested/b.rs", 5, ':', "pub fn helper() {}") + "\n";
        var output = GrepFilters.BuildGroupedOutput(raw, "fn", maxLen: 80, maxResults: 200, contextOnly: false);

        Assert.StartsWith("2 matches in 2 files:\n\n", output);
        Assert.Contains(":1:fn main() {}\n", output);
        Assert.Contains(":5:pub fn helper() {}\n", output);
        // CompactPath collapses to "Users/.../nested/a.rs" — the full absolute path is gone.
        Assert.DoesNotContain("/Users/patrick/dev/project/src/components/very/nested/a.rs:", output);
    }

    [Fact]
    public void BuildGroupedOutput_CapsPerFileMatches_UsingSuppliedLimit()
    {
        var raw = string.Concat(Enumerable.Range(1, 5).Select(i => BuildLine("big.rs", i, ':', $"line {i}") + "\n"));
        var output = GrepFilters.BuildGroupedOutput(
            raw, "line", maxLen: 80, maxResults: 200, contextOnly: false, grepMaxPerFile: 2);

        Assert.Contains("big.rs:1:line 1\n", output);
        Assert.Contains("big.rs:2:line 2\n", output);
        Assert.DoesNotContain("big.rs:3:line 3\n", output);
        Assert.Contains("[+3 more]\n", output);
    }

    /// <summary>
    /// Builds a NUL-separated match/context line of the form <c>file\0line[:-]content</c>, matching
    /// the shape <c>rg -0</c>/<c>grep --null</c> produce (see <see cref="GrepFilters.ParseMatchLine"/>).
    /// </summary>
    private static string BuildLine(string file, int lineNum, char separator, string content) =>
        file + '\0' + lineNum.ToString(CultureInfo.InvariantCulture) + separator + content;
}
