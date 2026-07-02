using RtkSharp.Commands.System;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="FindCommand"/>. The argument classifier and glob matcher are pure and are
/// tested directly against the vectors ported from <c>src/cmds/system/find_cmd.rs</c>'s own
/// <c>#[test]</c> cases. The walk-and-group output is verified against real, deterministic temp
/// directory trees (the test controls the tree, so expectations are exact and oracle-consistent
/// with the reference <c>rtk.exe</c>). Each tree includes an empty <c>.git</c> directory so the
/// gitignore engine roots at the test tree and no stray ancestor ignore rules leak in.
/// </summary>
public sealed class FindCommandTests
{
    // ---- GlobMatch: ported from find_cmd.rs glob_match_* tests ----

    [Fact]
    public void GlobMatch_StarRs()
    {
        Assert.True(FindCommand.GlobMatch("*.rs", "main.rs"));
        Assert.True(FindCommand.GlobMatch("*.rs", "find_cmd.rs"));
        Assert.False(FindCommand.GlobMatch("*.rs", "main.py"));
        Assert.False(FindCommand.GlobMatch("*.rs", "rs"));
    }

    [Fact]
    public void GlobMatch_StarAll()
    {
        Assert.True(FindCommand.GlobMatch("*", "anything.txt"));
        Assert.True(FindCommand.GlobMatch("*", "a"));
        Assert.True(FindCommand.GlobMatch("*", ".hidden"));
    }

    [Fact]
    public void GlobMatch_QuestionMark()
    {
        Assert.True(FindCommand.GlobMatch("?.rs", "a.rs"));
        Assert.False(FindCommand.GlobMatch("?.rs", "ab.rs"));
    }

    [Fact]
    public void GlobMatch_Exact()
    {
        Assert.True(FindCommand.GlobMatch("Cargo.toml", "Cargo.toml"));
        Assert.False(FindCommand.GlobMatch("Cargo.toml", "cargo.toml"));
    }

    [Fact]
    public void GlobMatch_Complex()
    {
        Assert.True(FindCommand.GlobMatch("test_*", "test_foo"));
        Assert.True(FindCommand.GlobMatch("test_*", "test_"));
        Assert.False(FindCommand.GlobMatch("test_*", "test"));
    }

    // ---- ParseFindArgs: native find syntax ----

    [Fact]
    public void ParseNativeFindName()
    {
        var parsed = FindCommand.ParseFindArgs([".", "-name", "*.rs"]);
        Assert.Equal("*.rs", parsed.Pattern);
        Assert.Equal(".", parsed.Path);
        Assert.Equal("f", parsed.FileType);
        Assert.Equal(50, parsed.MaxResults);
    }

    [Fact]
    public void ParseNativeFindNameAndType()
    {
        var parsed = FindCommand.ParseFindArgs(["src", "-name", "*.rs", "-type", "f"]);
        Assert.Equal("*.rs", parsed.Pattern);
        Assert.Equal("src", parsed.Path);
        Assert.Equal("f", parsed.FileType);
    }

    [Fact]
    public void ParseNativeFindTypeD()
    {
        var parsed = FindCommand.ParseFindArgs([".", "-type", "d"]);
        Assert.Equal("*", parsed.Pattern);
        Assert.Equal("d", parsed.FileType);
    }

    [Fact]
    public void ParseNativeFindMaxDepth()
    {
        var parsed = FindCommand.ParseFindArgs([".", "-name", "*.toml", "-maxdepth", "2"]);
        Assert.Equal("*.toml", parsed.Pattern);
        Assert.Equal(2, parsed.MaxDepth);
        Assert.Equal(50, parsed.MaxResults);
    }

    [Fact]
    public void ParseNativeFindIname()
    {
        var parsed = FindCommand.ParseFindArgs([".", "-iname", "Makefile"]);
        Assert.Equal("Makefile", parsed.Pattern);
        Assert.True(parsed.CaseInsensitive);
    }

    [Fact]
    public void ParseNativeFindNameIsCaseSensitive()
    {
        var parsed = FindCommand.ParseFindArgs([".", "-name", "*.rs"]);
        Assert.False(parsed.CaseInsensitive);
    }

    [Fact]
    public void ParseNativeFindNoPath()
    {
        var parsed = FindCommand.ParseFindArgs(["-name", "*.rs"]);
        Assert.Equal("*.rs", parsed.Pattern);
        Assert.Equal(".", parsed.Path);
    }

    // ---- ParseFindArgs: unsupported flags ----

    [Fact]
    public void ParseNativeFindRejectsNot()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            FindCommand.ParseFindArgs([".", "-name", "*.rs", "-not", "-name", "*_test.rs"]));
        Assert.Contains("compound predicates", ex.Message);
    }

    [Fact]
    public void ParseNativeFindRejectsExec() =>
        Assert.Throws<ArgumentException>(() =>
            FindCommand.ParseFindArgs([".", "-name", "*.tmp", "-exec", "rm", "{}", ";"]));

    // ---- ParseFindArgs: RTK syntax ----

    [Fact]
    public void ParseRtkSyntaxPatternOnly()
    {
        var parsed = FindCommand.ParseFindArgs(["*.rs"]);
        Assert.Equal("*.rs", parsed.Pattern);
        Assert.Equal(".", parsed.Path);
    }

    [Fact]
    public void ParseRtkSyntaxPatternAndPath()
    {
        var parsed = FindCommand.ParseFindArgs(["*.rs", "src"]);
        Assert.Equal("*.rs", parsed.Pattern);
        Assert.Equal("src", parsed.Path);
    }

    [Fact]
    public void ParseRtkSyntaxWithFlags()
    {
        var parsed = FindCommand.ParseFindArgs(["*.rs", "src", "-m", "10", "-t", "d"]);
        Assert.Equal("*.rs", parsed.Pattern);
        Assert.Equal("src", parsed.Path);
        Assert.Equal(10, parsed.MaxResults);
        Assert.Equal("d", parsed.FileType);
    }

    [Fact]
    public void ParseEmptyArgs()
    {
        var parsed = FindCommand.ParseFindArgs([]);
        Assert.Equal("*", parsed.Pattern);
        Assert.Equal(".", parsed.Path);
    }

    // ---- run_from_args style: parse + run must not throw ----

    [Fact]
    public void RunFromArgs_NativeFindSyntax_DoesNotThrow() =>
        RunFromArgs([".", "-name", "*.rs", "-type", "f"]);

    [Fact]
    public void RunFromArgs_RtkSyntax_DoesNotThrow() =>
        RunFromArgs(["*.rs", "src"]);

    [Fact]
    public void RunFromArgs_InameCaseInsensitive_DoesNotThrow() =>
        RunFromArgs([".", "-iname", "cargo.toml"]);

    // ---- Deterministic walk-and-group output against real temp trees ----

    [Fact]
    public void Run_FindsFilesByType_SkipsHidden()
    {
        var root = CreateTree(new Dictionary<string, string>
        {
            ["a.txt"] = "a",
            ["b.rs"] = "b",
            [Combine("sub", "c.rs")] = "c",
            [Combine("sub", "d.md")] = "d",
            [Combine(".hidden", "e.rs")] = "e",
            [".secret.txt"] = "s"
        });

        var output = RunToString(new FindCommand.FindArgs("*.rs", root, 50, null, "f", false));
        // b.rs and sub/c.rs match; the hidden .hidden/e.rs is skipped.
        Assert.Equal("2F 2D:\n\n./ b.rs\nsub/ c.rs\n", output);
    }

    [Fact]
    public void Run_TypeDirectory_ListsDirectories()
    {
        var root = CreateTree(new Dictionary<string, string>
        {
            ["b.rs"] = "b",
            [Combine("sub", "c.rs")] = "c",
            [Combine(".hidden", "e.rs")] = "e"
        });

        var output = RunToString(new FindCommand.FindArgs("*", root, 50, null, "d", false));
        // Only the non-hidden "sub" directory; the search root itself is excluded.
        Assert.Equal("1F 1D:\n\n./ sub\n", output);
    }

    [Fact]
    public void Run_DotfilePattern_IncludesHidden()
    {
        // #1101: a pattern targeting a dotfile must walk hidden entries.
        var root = CreateTree(new Dictionary<string, string>
        {
            ["keep.rs"] = "k",
            [".secret.txt"] = "s"
        });

        var output = RunToString(new FindCommand.FindArgs(".secret.txt", root, 50, null, "f", false));
        Assert.Equal("1F 1D:\n\n./ .secret.txt\n", output);
    }

    [Fact]
    public void Run_MixedExtensions_EmitsExtensionSummary()
    {
        var root = CreateTree(new Dictionary<string, string>
        {
            ["a.txt"] = "a",
            ["b.rs"] = "b",
            [Combine("sub", "c.rs")] = "c",
            [Combine("sub", "d.md")] = "d"
        });

        var output = RunToString(new FindCommand.FindArgs("*", root, 50, null, "f", false));
        // Ext summary ordered by count desc, ties broken by ordinal name (md before txt).
        Assert.Equal(
            "4F 2D:\n\n./ a.txt b.rs\nsub/ c.rs d.md\n\next: .rs(2) .md(1) .txt(1)\n",
            output);
    }

    [Fact]
    public void Run_MaxResults_CapsAndReportsOverflow()
    {
        var root = CreateTree(new Dictionary<string, string>
        {
            ["a.txt"] = "a",
            ["b.rs"] = "b",
            [Combine("sub", "c.rs")] = "c",
            [Combine("sub", "d.md")] = "d"
        });

        var output = RunToString(new FindCommand.FindArgs("*", root, 1, null, "f", false));
        // Only one file shown; overflow marker for the remaining three; ext summary spans all four.
        Assert.Equal(
            "4F 2D:\n\n./ a.txt\n+3 more\n\next: .rs(2) .md(1) .txt(1)\n",
            output);
    }

    [Fact]
    public void Run_NoMatches_ReportsZero()
    {
        var root = CreateTree(new Dictionary<string, string> { ["a.txt"] = "a" });
        var output = RunToString(new FindCommand.FindArgs("*.xyz", root, 50, null, "f", false));
        Assert.Equal("0 for '*.xyz'\n", output);
    }

    [Fact]
    public void Run_MaxDepth_LimitsRecursion()
    {
        var root = CreateTree(new Dictionary<string, string>
        {
            ["top.rs"] = "t",
            [Combine("sub", "deep.rs")] = "d"
        });

        // Depth 1 = search root's immediate children only; sub/deep.rs (depth 2) is excluded.
        var output = RunToString(new FindCommand.FindArgs("*.rs", root, 50, 1, "f", false));
        Assert.Equal("1F 1D:\n\n./ top.rs\n", output);
    }

    [Fact]
    public void Run_RespectsGitignore()
    {
        var root = CreateTree(new Dictionary<string, string>
        {
            [".gitignore"] = "ignored/\n",
            ["keep.rs"] = "k",
            [Combine("ignored", "x.rs")] = "x"
        });

        var output = RunToString(new FindCommand.FindArgs("*.rs", root, 50, null, "f", false));
        // The ignored/ directory is pruned, so ignored/x.rs never appears.
        Assert.Equal("1F 1D:\n\n./ keep.rs\n", output);
    }

    // ---- Helpers ----

    private static string Combine(params string[] parts) => Path.Combine(parts);

    private static string RunToString(FindCommand.FindArgs args)
    {
        using var sw = new StringWriter();
        FindCommand.Run(args, sw);
        return sw.ToString();
    }

    private static void RunFromArgs(string[] args)
    {
        var parsed = FindCommand.ParseFindArgs(args);
        using var sw = new StringWriter();
        FindCommand.Run(parsed, sw);
    }

    /// <summary>
    /// Creates a fresh temp directory tree from a map of relative path to file content, creating
    /// intermediate directories as needed. An empty <c>.git</c> directory is always added so the
    /// find walker's gitignore engine roots at this tree.
    /// </summary>
    private static string CreateTree(IReadOnlyDictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "rtk_find_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(root, relative);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(full, content);
        }

        return root;
    }
}
