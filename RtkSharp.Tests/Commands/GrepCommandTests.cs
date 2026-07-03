using RtkSharp.Commands.System;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="GrepCommand"/>. The argument-translation, cluster-parsing, match-line
/// parsing, and formatting helpers are pure functions, so the bulk of this suite ports the
/// <c>#[test]</c> functions from <c>src/cmds/system/grep_cmd.rs</c> directly. A small set of
/// end-to-end tests run the real <c>rg</c> binary against deterministic temp files to verify the
/// execution/print pipeline (single match, no-match exit code, grouping header, count passthrough,
/// context lines). All expectations are oracle-derived from the reference <c>rtk.exe</c>.
/// </summary>
public sealed class GrepCommandTests
{
    // ---- is_grep_error_exit ----

    [Fact]
    public void IsGrepErrorExit_MatchesAndNoMatchAreNotErrors()
    {
        Assert.False(GrepCommand.IsGrepErrorExit(0));
        Assert.False(GrepCommand.IsGrepErrorExit(1));
        Assert.True(GrepCommand.IsGrepErrorExit(2));
        Assert.True(GrepCommand.IsGrepErrorExit(3));
        Assert.True(GrepCommand.IsGrepErrorExit(127));
    }

    // ---- clean_line ----

    [Fact]
    public void CleanLine_TrimsAndCaps()
    {
        var cleaned = GrepCommand.CleanLine("            const result = someFunction();", 50, null, "result");
        Assert.False(cleaned.StartsWith(' '));
        Assert.True(cleaned.Length <= 50);
    }

    [Fact]
    public void CleanLine_Multibyte_DoesNotThrow()
    {
        var cleaned = GrepCommand.CleanLine("  สวัสดีครับ นี่คือข้อความที่ยาวมากสำหรับทดสอบ  ", 20, null, "ครับ");
        Assert.NotEqual(0, cleaned.Length);
    }

    [Fact]
    public void CleanLine_Emoji_DoesNotThrow()
    {
        var cleaned = GrepCommand.CleanLine("🎉🎊🎈🎁🎂🎄 some text 🎃🎆🎇✨", 15, null, "text");
        Assert.NotEqual(0, cleaned.Length);
    }

    // ---- compact_path ----

    [Fact]
    public void CompactPath_ShortensLongPaths()
    {
        var compact = GrepCommand.CompactPath("/Users/patrick/dev/project/src/components/Button.tsx");
        Assert.True(compact.Length <= 60);
    }

    // ---- BRE alternation translation ----

    [Fact]
    public void BreAlternation_TranslatedToPcre()
    {
        var pattern = @"fn foo\|pub.*bar";
        Assert.Equal("fn foo|pub.*bar", pattern.Replace(@"\|", "|"));
    }

    // ---- parse_cluster ----

    private static GrepCommand.ClusterResult Vt(string? prefix, char flag, string inline) =>
        GrepCommand.ClusterResult.ValueTaking(prefix, flag, inline);

    [Fact]
    public void ParseCluster_BooleanOnly()
    {
        Assert.Equal(GrepCommand.ClusterResult.Boolean(null), GrepCommand.ParseCluster("r"));
        Assert.Equal(GrepCommand.ClusterResult.Boolean(null), GrepCommand.ParseCluster("R"));
        Assert.Equal(GrepCommand.ClusterResult.Boolean(null), GrepCommand.ParseCluster("rR"));
        Assert.Equal(GrepCommand.ClusterResult.Boolean("n"), GrepCommand.ParseCluster("rn"));
        Assert.Equal(GrepCommand.ClusterResult.Boolean("ni"), GrepCommand.ParseCluster("Rni"));
        Assert.Equal(GrepCommand.ClusterResult.Boolean("n"), GrepCommand.ParseCluster("n"));
        Assert.Equal(GrepCommand.ClusterResult.Boolean("ni"), GrepCommand.ParseCluster("ni"));
    }

    [Fact]
    public void ParseCluster_ENoInline() => Assert.Equal(Vt(null, 'e', ""), GrepCommand.ParseCluster("e"));

    [Fact]
    public void ParseCluster_EInlineValue() => Assert.Equal(Vt(null, 'e', "carrot"), GrepCommand.ParseCluster("ecarrot"));

    [Fact]
    public void ParseCluster_EInlineValueNoRStrip()
    {
        var result = GrepCommand.ParseCluster("ecarrot");
        Assert.Equal(GrepCommand.ClusterKind.ValueTaking, result.Kind);
        Assert.Equal("carrot", result.Inline);
    }

    [Fact]
    public void ParseCluster_GInlineGlob()
    {
        Assert.Equal(Vt(null, 'g', "*.rs"), GrepCommand.ParseCluster("g*.rs"));
        Assert.Equal("*.rs", GrepCommand.ParseCluster("g*.rs").Inline);
    }

    [Fact]
    public void ParseCluster_Rne() => Assert.Equal(Vt("n", 'e', ""), GrepCommand.ParseCluster("rne"));

    [Fact]
    public void ParseCluster_RA() => Assert.Equal(Vt(null, 'A', ""), GrepCommand.ParseCluster("rA"));

    [Fact]
    public void ParseCluster_NiA() => Assert.Equal(Vt("ni", 'A', ""), GrepCommand.ParseCluster("niA"));

    [Fact]
    public void ParseCluster_AiInline() => Assert.Equal(Vt(null, 'A', "i"), GrepCommand.ParseCluster("Ai"));

    [Fact]
    public void ParseCluster_ShortType()
    {
        Assert.Equal(Vt(null, 't', ""), GrepCommand.ParseCluster("t"));
        Assert.Equal(Vt(null, 't', "py"), GrepCommand.ParseCluster("tpy"));
    }

    [Fact]
    public void ParseCluster_ShortMaxColumns()
    {
        Assert.Equal(Vt(null, 'M', ""), GrepCommand.ParseCluster("M"));
        Assert.Equal(Vt(null, 'M', "120"), GrepCommand.ParseCluster("M120"));
    }

    // ---- strip_r ----

    [Fact]
    public void StripR_RemovesRAndR()
    {
        Assert.Null(GrepCommand.StripR("r"));
        Assert.Null(GrepCommand.StripR("R"));
        Assert.Null(GrepCommand.StripR("rR"));
        Assert.Null(GrepCommand.StripR(""));
        Assert.Equal("n", GrepCommand.StripR("rn"));
        Assert.Equal("ni", GrepCommand.StripR("Rni"));
        Assert.Equal("i", GrepCommand.StripR("i"));
        Assert.Equal("caot", GrepCommand.StripR("carrot"));
    }

    // ---- strip_recursive ----

    [Fact]
    public void StripRecursive_DropsRecursiveKeepsRest()
    {
        Assert.Null(GrepCommand.StripRecursive("--recursive"));
        Assert.Equal("--glob", GrepCommand.StripRecursive("--glob"));
        Assert.Equal("--type", GrepCommand.StripRecursive("--type"));
    }

    // ---- extract_pattern_path ----

    private static void AssertExtract(
        GrepCommand.ExtractResult result, string[] patterns, string[] paths, string[] flags)
    {
        Assert.Equal(patterns, result.Patterns);
        Assert.Equal(paths, result.Paths);
        Assert.Equal(flags, result.Flags);
    }

    [Fact]
    public void Extract_Simple() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["foo", "src/"]), ["foo"], ["src/"], []);

    [Fact]
    public void Extract_WithBoolFlag() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-i", "foo", "src/"]), ["foo"], ["src/"], ["-i"]);

    [Fact]
    public void Extract_ValueTakingFlag() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-A", "2", "error", "src"]), ["error"], ["src"], ["-A", "2"]);

    [Fact]
    public void Extract_ClusterStripR() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-rn", "foo", "src"]), ["foo"], ["src"], ["-n"]);

    [Fact]
    public void Extract_ClusterEndingInE() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-rne", "PATTERN", "src"]), ["PATTERN"], ["src"], ["-n"]);

    [Fact]
    public void Extract_ClusterEndingInValueFlag() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-rA", "2", "foo", "src"]), ["foo"], ["src"], ["-A", "2"]);

    [Fact]
    public void Extract_MultiPath() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["TODO", "src", "tests"]), ["TODO"], ["src", "tests"], []);

    [Fact]
    public void Extract_GlobValue() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["-i", "x", "agent", "-g", "*.md"]),
            ["x"], ["agent"], ["-i", "-g", "*.md"]);

    [Fact]
    public void Extract_EFlag() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-e", "fn run", "src"]), ["fn run"], ["src"], []);

    [Fact]
    public void Extract_MultiE() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-e", "foo", "-e", "bar", "src"]), ["foo", "bar"], ["src"], []);

    [Fact]
    public void Extract_DashDashBoundary() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["--", "--version"]), ["--version"], [], []);

    [Fact]
    public void Extract_NoArgs() =>
        AssertExtract(GrepCommand.ExtractPatternPath([]), [], [], []);

    [Fact]
    public void Extract_DefaultPathEmpty()
    {
        var result = GrepCommand.ExtractPatternPath(["foo"]);
        Assert.Equal(["foo"], result.Patterns);
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void Extract_EndingE() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["-e", "foo", "-e", "bar", "src", "-e"]),
            ["foo", "bar"], ["src"], ["-e"]);

    [Fact]
    public void Extract_InlineEValue() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-ecarrot", "file"]), ["carrot"], ["file"], []);

    [Fact]
    public void Extract_InlineEValueNoRStrip() =>
        Assert.Equal(["carrot"], GrepCommand.ExtractPatternPath(["-ecarrot", "file"]).Patterns);

    [Fact]
    public void Extract_InlineGValue() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["aaa", "sub", "-g*.rs"]), ["aaa"], ["sub"], ["-g", "*.rs"]);

    [Fact]
    public void Extract_InlineGValueNoRStrip() =>
        Assert.Contains("*.rs", GrepCommand.ExtractPatternPath(["aaa", "sub", "-g*.rs"]).Flags);

    [Fact]
    public void Extract_LongGlobValue() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["compact", "sub", "--glob", "*.md"]),
            ["compact"], ["sub"], ["--glob", "*.md"]);

    [Fact]
    public void Extract_LongMaxCount() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["--max-count", "1", "fn", "file"]),
            ["fn"], ["file"], ["--max-count", "1"]);

    [Fact]
    public void Extract_ShortType() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-t", "rust", "fn", "src"]), ["fn"], ["src"], ["-t", "rust"]);

    [Fact]
    public void Extract_ShortMaxDepth() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-d", "3", "foo", "src"]), ["foo"], ["src"], ["-d", "3"]);

    [Fact]
    public void Extract_ShortMaxColumns() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["-M", "120", "foo", "src"]), ["foo"], ["src"], ["-M", "120"]);

    [Fact]
    public void Extract_LongRegexp() =>
        AssertExtract(GrepCommand.ExtractPatternPath(["--regexp", "fn run", "src"]), ["fn run"], ["src"], []);

    [Fact]
    public void Extract_LongRegexpMulti()
    {
        var result = GrepCommand.ExtractPatternPath(["--regexp", "foo", "-e", "bar", "src"]);
        Assert.Equal(["foo", "bar"], result.Patterns);
        Assert.Equal(["src"], result.Paths);
    }

    [Fact]
    public void Extract_LongIgnoreFile() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["--ignore-file", ".myignore", "foo", "src"]),
            ["foo"], ["src"], ["--ignore-file", ".myignore"]);

    [Fact]
    public void Extract_LongEngine() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["--engine", "pcre2", "foo", "src"]),
            ["foo"], ["src"], ["--engine", "pcre2"]);

    [Fact]
    public void Extract_LongTypeClear() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["--type-clear", "rust", "foo", "src"]),
            ["foo"], ["src"], ["--type-clear", "rust"]);

    [Fact]
    public void Extract_LongPathSeparator() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["--path-separator", "/", "foo", "src"]),
            ["foo"], ["src"], ["--path-separator", "/"]);

    [Fact]
    public void Extract_LongFlagInlineEqPassthrough() =>
        AssertExtract(
            GrepCommand.ExtractPatternPath(["foo", "src", "--glob=*.rs"]),
            ["foo"], ["src"], ["--glob=*.rs"]);

    // ---- has_format_flag ----

    [Fact]
    public void FormatFlag_DetectsCountMatches() => Assert.True(GrepCommand.HasFormatFlag(["--count-matches"]));

    [Fact]
    public void FormatFlag_DetectsJson() => Assert.True(GrepCommand.HasFormatFlag(["--json"]));

    [Fact]
    public void FormatFlag_DetectsPassthru() => Assert.True(GrepCommand.HasFormatFlag(["--passthru"]));

    [Fact]
    public void FormatFlag_DetectsFiles() => Assert.True(GrepCommand.HasFormatFlag(["--files"]));

    [Fact]
    public void FormatFlag_DetectsCount()
    {
        Assert.True(GrepCommand.HasFormatFlag(["-c"]));
        Assert.True(GrepCommand.HasFormatFlag(["--count"]));
    }

    [Fact]
    public void FormatFlag_DetectsFilesWithMatches()
    {
        Assert.True(GrepCommand.HasFormatFlag(["-l"]));
        Assert.True(GrepCommand.HasFormatFlag(["--files-with-matches"]));
    }

    [Fact]
    public void FormatFlag_DetectsFilesWithoutMatch()
    {
        Assert.True(GrepCommand.HasFormatFlag(["-L"]));
        Assert.True(GrepCommand.HasFormatFlag(["--files-without-match"]));
    }

    [Fact]
    public void FormatFlag_DetectsOnlyMatching()
    {
        Assert.True(GrepCommand.HasFormatFlag(["-o"]));
        Assert.True(GrepCommand.HasFormatFlag(["--only-matching"]));
    }

    [Fact]
    public void FormatFlag_DetectsNull()
    {
        Assert.True(GrepCommand.HasFormatFlag(["-Z"]));
        Assert.True(GrepCommand.HasFormatFlag(["--null"]));
    }

    [Fact]
    public void FormatFlag_IgnoresNormalFlags() => Assert.False(GrepCommand.HasFormatFlag(["-i", "-w", "-A", "3"]));

    // ---- strip_rg_only ----

    [Fact]
    public void StripRgOnly_DropsGlobAndValue()
    {
        Assert.Equal(["-i"], GrepCommand.StripRgOnly(["--glob", "*.rs", "-i"]));
        Assert.Equal(["-i"], GrepCommand.StripRgOnly(["-g", "*.rs", "-i"]));
    }

    [Fact]
    public void StripRgOnly_DropsInlineGlob() =>
        Assert.Equal(["-i"], GrepCommand.StripRgOnly(["--glob=*.rs", "-i"]));

    [Fact]
    public void StripRgOnly_DropsBoolFlags() =>
        Assert.Equal(["-n"], GrepCommand.StripRgOnly(["--hidden", "--pcre2", "--json", "-n"]));

    [Fact]
    public void StripRgOnly_DropsTypeAndValue()
    {
        Assert.Equal(["-w"], GrepCommand.StripRgOnly(["--type", "rust", "-w"]));
        Assert.Equal(["-w"], GrepCommand.StripRgOnly(["-T", "rust", "-w"]));
    }

    [Fact]
    public void StripRgOnly_KeepsGrepCompatible() =>
        Assert.Equal(["-i", "-w", "-A", "3", "-v"], GrepCommand.StripRgOnly(["-i", "-w", "-A", "3", "-v"]));

    // ---- parse_match_line ----

    [Fact]
    public void ParseMatchLine_Simple()
    {
        var (file, lineNum, isMatch, content) = GrepCommand.ParseMatchLine("file.php\u000010:use Foo\\Bar;")!.Value;
        Assert.Equal("file.php", file);
        Assert.Equal(10, lineNum);
        Assert.True(isMatch);
        Assert.Equal("use Foo\\Bar;", content);
    }

    [Fact]
    public void ParseMatchLine_ContentWithDoubleColon()
    {
        var line =
            "externalImportShell.class.php\u000081:        $this->queueProcessModel = ClassRegistry::init('Collections.QueueProcess');";
        var (file, lineNum, isMatch, content) = GrepCommand.ParseMatchLine(line)!.Value;
        Assert.Equal("externalImportShell.class.php", file);
        Assert.Equal(81, lineNum);
        Assert.True(isMatch);
        Assert.Equal(
            "        $this->queueProcessModel = ClassRegistry::init('Collections.QueueProcess');",
            content);
    }

    [Fact]
    public void ParseMatchLine_WindowsPath()
    {
        var (file, lineNum, isMatch, content) = GrepCommand.ParseMatchLine("C:\\src\\file.rs\u000042:fn main() {}")!.Value;
        Assert.Equal(@"C:\src\file.rs", file);
        Assert.Equal(42, lineNum);
        Assert.True(isMatch);
        Assert.Equal("fn main() {}", content);
    }

    [Fact]
    public void ParseMatchLine_FilenameWithColons()
    {
        var (file, lineNum, isMatch, content) = GrepCommand.ParseMatchLine("badly_named:52:file.txt\u00001:xxx")!.Value;
        Assert.Equal("badly_named:52:file.txt", file);
        Assert.Equal(1, lineNum);
        Assert.True(isMatch);
        Assert.Equal("xxx", content);
    }

    [Fact]
    public void ParseMatchLine_ContentWithDigitColons()
    {
        var (file, lineNum, isMatch, content) =
            GrepCommand.ParseMatchLine("log.txt\u00007:debug: counter is :42: now")!.Value;
        Assert.Equal("log.txt", file);
        Assert.Equal(7, lineNum);
        Assert.True(isMatch);
        Assert.Equal("debug: counter is :42: now", content);
    }

    [Fact]
    public void ParseMatchLine_MalformedReturnsNull()
    {
        Assert.Null(GrepCommand.ParseMatchLine("file.rs:1:content"));
        Assert.Null(GrepCommand.ParseMatchLine("not a match line"));
        Assert.Null(GrepCommand.ParseMatchLine("file.rs\u0000fn foo()"));
        Assert.Null(GrepCommand.ParseMatchLine(""));
    }

    [Fact]
    public void ParseMatchLine_EmptyContent()
    {
        var (file, lineNum, isMatch, content) = GrepCommand.ParseMatchLine("file.rs\u00007:")!.Value;
        Assert.Equal("file.rs", file);
        Assert.Equal(7, lineNum);
        Assert.True(isMatch);
        Assert.Equal("", content);
    }

    [Fact]
    public void ParseMatchLine_ContextLine()
    {
        var (file, lineNum, isMatch, content) = GrepCommand.ParseMatchLine("file.txt\u00004-after1")!.Value;
        Assert.Equal("file.txt", file);
        Assert.Equal(4, lineNum);
        Assert.False(isMatch);
        Assert.Equal("after1", content);
    }

    // ---- unparsed_signal ----

    [Fact]
    public void UnparsedSignal_ParseableLinesYieldZero() =>
        Assert.Equal(0, GrepCommand.UnparsedSignal("file.txt\u00001:hello\nfile.txt\u00002:world\n"));

    [Fact]
    public void UnparsedSignal_ContextSeparatorNotCounted() =>
        Assert.Equal(0, GrepCommand.UnparsedSignal("file.txt\u00001:hello\n--\nfile.txt\u00003:world\n"));

    [Fact]
    public void UnparsedSignal_EmptyLineNotCounted() =>
        Assert.Equal(0, GrepCommand.UnparsedSignal("file.txt\u00001:hello\n\nfile.txt\u00002:world\n"));

    [Fact]
    public void UnparsedSignal_BareColonLineCounted() =>
        Assert.Equal(1, GrepCommand.UnparsedSignal("file.rs:1:content\n"));

    [Fact]
    public void UnparsedSignal_BinaryNoticeCounted() =>
        Assert.Equal(1, GrepCommand.UnparsedSignal("Binary file foo matches\n"));

    [Fact]
    public void UnparsedSignal_ContextLinesParseOk() =>
        Assert.Equal(0, GrepCommand.UnparsedSignal(
            "file.txt\u00003-context_before\nfile.txt\u00004:match\nfile.txt\u00005-context_after\n"));

    // ---- has_shape_flag ----

    [Fact]
    public void ShapeFlag_DetectsColumnAndVimgrep()
    {
        Assert.True(GrepCommand.HasShapeFlag(["--column"]));
        Assert.True(GrepCommand.HasShapeFlag(["--vimgrep"]));
        Assert.True(GrepCommand.HasShapeFlag(["-b"]));
        Assert.True(GrepCommand.HasShapeFlag(["--byte-offset"]));
        Assert.True(GrepCommand.HasShapeFlag(["--null-data"]));
        Assert.False(GrepCommand.HasShapeFlag(["-i", "-n"]));
    }

    // ================= End-to-end (real rg) =================

    private static async Task<(int Exit, string Out, string Err)> RunAsync(
        int maxLen, int maxResults, bool contextOnly, string? fileType, params string[] args)
    {
        var outWriter = new StringWriter { NewLine = "\n" };
        var errWriter = new StringWriter { NewLine = "\n" };
        var exit = await GrepCommand.RunAsync(maxLen, maxResults, contextOnly, fileType, args, outWriter, errWriter);
        return (exit, outWriter.ToString(), errWriter.ToString());
    }

    [Fact]
    public async Task EndToEnd_SingleMatch_ExitZeroWithMatchSuffix()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.txt", "alpha\nbeta needle gamma\ndelta\n");

        var (exit, output, _) = await RunAsync(80, 200, false, null, "needle", file);

        // The path prefix may be compacted (long temp dir), but the ":line:content" suffix is stable.
        Assert.Equal(0, exit);
        Assert.Contains(":2:beta needle gamma\n", output);
    }

    [Fact]
    public async Task EndToEnd_NoMatch_ZeroMatchesExit1()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.txt", "alpha\nbeta\n");

        var (exit, output, _) = await RunAsync(80, 200, false, null, "zzznotfound", file);

        Assert.Equal(1, exit);
        Assert.Equal("0 matches for 'zzznotfound'\n", output);
    }

    [Fact]
    public async Task EndToEnd_MultipleMatches_GroupingHeader()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.txt", "needle 1\nneedle 2\nother\nneedle 3\n");

        var (exit, output, _) = await RunAsync(80, 200, false, null, "needle", file);

        Assert.Equal(0, exit);
        Assert.StartsWith("3 matches in 1 files:\n\n", output);
        Assert.Contains(":1:needle 1\n", output);
        Assert.Contains(":4:needle 3\n", output);
    }

    [Fact]
    public async Task EndToEnd_CountMode_PassthroughNumeric()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.txt", "needle\nneedle\nother\n");

        var (exit, output, _) = await RunAsync(80, 200, false, null, "-c", "needle", file);

        Assert.Equal(0, exit);
        Assert.Equal("2\n", output);
    }

    [Fact]
    public async Task EndToEnd_ContextLines_DashSeparators()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.txt", "line1\nline2\nneedle\nline4\nline5\n");

        var (exit, output, _) = await RunAsync(80, 200, false, null, "-C", "1", "needle", file);

        Assert.Equal(0, exit);
        Assert.StartsWith("1 matches in 1 files:\n\n", output);
        Assert.Contains("-2-line2\n", output);
        Assert.Contains(":3:needle\n", output);
        Assert.Contains("-4-line4\n", output);
    }

    [Fact]
    public async Task EndToEnd_MaxResultsCap_EmitsMoreMarker()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.txt", "needle 1\nneedle 2\nneedle 3\n");

        var (exit, output, _) = await RunAsync(80, 2, false, null, "needle", file);

        Assert.Equal(0, exit);
        Assert.Contains("[+1 more]\n", output);
    }

    /// <summary>A throwaway directory with forward-slash relative file paths for rg to search.</summary>
    private sealed class TempDir : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "rtksharp-grep-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(_root);

        /// <summary>Writes <paramref name="content"/> to <paramref name="name"/> and returns its path (forward slashes).</summary>
        public string Write(string name, string content)
        {
            var full = Path.Combine(_root, name);
            File.WriteAllText(full, content);
            return full.Replace('\\', '/');
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
