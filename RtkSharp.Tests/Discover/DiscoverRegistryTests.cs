using RtkSharp.Discover;
using Xunit;

namespace RtkSharp.Tests.Discover;

/// <summary>
/// Test-for-test port of Rust <c>src/discover/registry.rs</c>'s <c>#[cfg(test)] mod tests</c>,
/// limited (per this port's scope) to the tests targeting <c>classify_command</c>,
/// <c>category_avg_tokens</c>, <c>split_command_chain</c>, <c>strip_disabled_prefix</c>,
/// <c>prefix_contains_rtk_disabled</c>, <c>extract_base_command</c>, <c>strip_golangci*</c>,
/// <c>strip_git_global_opts</c>, and <c>strip_absolute_path</c>. Rust's own <c>rewrite_command</c>/
/// <c>rewrite_segment</c> tests are intentionally NOT ported here — that engine already has its own
/// test suite as <c>RtkSharp.Tests.Rewrite.RewriteEngineTests</c>.
/// </summary>
public sealed class DiscoverRegistryTests
{
    // ===================== classify_command =====================

    [Fact]
    public void ClassifyCommand_GitStatus_SupportedExisting()
    {
        Assert.Equal(
            new Classification.Supported("rtk git", "Git", 70.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("git status"));
    }

    [Fact]
    public void ClassifyCommand_YadmStatus_SupportedExisting()
    {
        Assert.Equal(
            new Classification.Supported("rtk git", "Git", 70.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("yadm status"));
    }

    [Fact]
    public void ClassifyCommand_YadmDiff_SupportedWithDiffSavingsOverride()
    {
        Assert.Equal(
            new Classification.Supported("rtk git", "Git", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("yadm diff"));
    }

    [Fact]
    public void ClassifyCommand_GitDiffCached_SupportedWithDiffSavingsOverride()
    {
        Assert.Equal(
            new Classification.Supported("rtk git", "Git", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("git diff --cached"));
    }

    [Fact]
    public void ClassifyCommand_CargoTestWithFilter_SupportedWithTestSavingsOverride()
    {
        Assert.Equal(
            new Classification.Supported("rtk cargo", "Cargo", 90.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("cargo test filter::"));
    }

    [Fact]
    public void ClassifyCommand_NpxTsc_SupportedBuild()
    {
        Assert.Equal(
            new Classification.Supported("rtk tsc", "Build", 83.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("npx tsc --noEmit"));
    }

    [Fact]
    public void ClassifyCommand_CatFile_SupportedRead()
    {
        Assert.Equal(
            new Classification.Supported("rtk read", "Files", 60.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("cat src/main.rs"));
    }

    [Theory]
    [InlineData("cat > /tmp/output.txt")]
    [InlineData("cat >> /tmp/output.txt")]
    [InlineData("cat file.txt > output.txt")]
    [InlineData("cat -n file.txt >> log.txt")]
    [InlineData("head -10 README.md > output.txt")]
    [InlineData("tail -f app.log > /dev/null")]
    public void ClassifyCommand_CatHeadTailWithRedirect_NeverSupported(string cmd)
    {
        Assert.IsNotType<Classification.Supported>(DiscoverRegistry.ClassifyCommand(cmd));
    }

    [Fact]
    public void ClassifyCommand_CdIgnored()
    {
        Assert.Equal(Classification.Ignored.Instance, DiscoverRegistry.ClassifyCommand("cd /tmp"));
    }

    [Fact]
    public void ClassifyCommand_RtkAlready_Ignored()
    {
        Assert.Equal(Classification.Ignored.Instance, DiscoverRegistry.ClassifyCommand("rtk git status"));
    }

    [Fact]
    public void ClassifyCommand_EchoIgnored()
    {
        Assert.Equal(Classification.Ignored.Instance, DiscoverRegistry.ClassifyCommand("echo hello world"));
    }

    [Fact]
    public void ClassifyCommand_HtopUnsupported()
    {
        var result = Assert.IsType<Classification.Unsupported>(DiscoverRegistry.ClassifyCommand("htop -d 10"));
        Assert.Equal("htop", result.BaseCommand);
    }

    [Fact]
    public void ClassifyCommand_EnvPrefixStripped_SupportedGit()
    {
        Assert.Equal(
            new Classification.Supported("rtk git", "Git", 70.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("GIT_SSH_COMMAND=ssh git push"));
    }

    [Fact]
    public void ClassifyCommand_SudoStripped_SupportedDocker()
    {
        Assert.Equal(
            new Classification.Supported("rtk docker", "Infra", 85.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("sudo docker ps"));
    }

    [Fact]
    public void ClassifyCommand_CargoCheck_Supported()
    {
        Assert.Equal(
            new Classification.Supported("rtk cargo", "Cargo", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("cargo check"));
    }

    [Fact]
    public void ClassifyCommand_CargoCheckAllTargets_Supported()
    {
        Assert.Equal(
            new Classification.Supported("rtk cargo", "Cargo", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("cargo check --all-targets"));
    }

    [Fact]
    public void ClassifyCommand_CargoFmt_SupportedPassthrough()
    {
        Assert.Equal(
            new Classification.Supported("rtk cargo", "Cargo", 80.0, RtkStatus.Passthrough),
            DiscoverRegistry.ClassifyCommand("cargo fmt"));
    }

    [Fact]
    public void ClassifyCommand_CargoClippy_SupportedExisting()
    {
        Assert.Equal(
            new Classification.Supported("rtk cargo", "Cargo", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("cargo clippy --all-targets"));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("clippy")]
    [InlineData("check")]
    [InlineData("fmt")]
    public void ClassifyCommand_RegistryCoversAllCargoSubcommands(string subcmd)
    {
        Assert.IsType<Classification.Supported>(DiscoverRegistry.ClassifyCommand($"cargo {subcmd}"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("log")]
    [InlineData("diff")]
    [InlineData("show")]
    [InlineData("add")]
    [InlineData("commit")]
    [InlineData("push")]
    [InlineData("pull")]
    [InlineData("branch")]
    [InlineData("fetch")]
    [InlineData("stash")]
    [InlineData("worktree")]
    public void ClassifyCommand_RegistryCoversAllGitSubcommands(string subcmd)
    {
        Assert.IsType<Classification.Supported>(DiscoverRegistry.ClassifyCommand($"git {subcmd}"));
    }

    [Fact]
    public void ClassifyCommand_FindNotBlockedByFi()
    {
        // Regression: "fi" in IGNORED_PREFIXES used to shadow "find" commands because
        // "find".starts_with("fi") is true. "fi" should only match exactly.
        Assert.Equal(
            new Classification.Supported("rtk find", "Files", 70.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("find . -name foo"));
    }

    [Fact]
    public void ClassifyCommand_BareFi_StillIgnoredExact()
    {
        Assert.Equal(Classification.Ignored.Instance, DiscoverRegistry.ClassifyCommand("fi"));
    }

    [Fact]
    public void ClassifyCommand_BareDone_StillIgnoredExact()
    {
        Assert.Equal(Classification.Ignored.Instance, DiscoverRegistry.ClassifyCommand("done"));
    }

    [Fact]
    public void ClassifyCommand_Mypy_Supported()
    {
        Assert.Equal(
            new Classification.Supported("rtk mypy", "Build", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("mypy src/"));
    }

    [Fact]
    public void ClassifyCommand_Python3MMypy_Supported()
    {
        Assert.Equal(
            new Classification.Supported("rtk mypy", "Build", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("python3 -m mypy --strict"));
    }

    [Theory]
    [InlineData("golangci-lint run")]
    [InlineData("golangci-lint -v run ./...")]
    [InlineData("golangci-lint --color never run ./...")]
    [InlineData("golangci-lint --color=never run ./...")]
    [InlineData("golangci-lint --config=foo.yml run ./...")]
    public void ClassifyCommand_GolangciLintRunVariants_SupportedGolangciLintRun(string cmd)
    {
        var result = Assert.IsType<Classification.Supported>(DiscoverRegistry.ClassifyCommand(cmd));
        Assert.Equal("rtk golangci-lint run", result.RtkEquivalent);
    }

    [Fact]
    public void ClassifyCommand_GolangciLintBare_NotCompactWrapper()
    {
        var result = DiscoverRegistry.ClassifyCommand("golangci-lint");
        if (result is Classification.Supported sup)
        {
            Assert.NotEqual("rtk golangci-lint run", sup.RtkEquivalent);
        }
    }

    [Fact]
    public void ClassifyCommand_GolangciLintOtherSubcommand_NotCompactWrapper()
    {
        var result = DiscoverRegistry.ClassifyCommand("golangci-lint version");
        if (result is Classification.Supported sup)
        {
            Assert.NotEqual("rtk golangci-lint run", sup.RtkEquivalent);
        }
    }

    [Fact]
    public void ClassifyCommand_WcSupported()
    {
        // Regression: "wc " was previously shadowed in IGNORED_PREFIXES despite having a full filter.
        Assert.Equal(
            new Classification.Supported("rtk wc", "Files", 60.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("wc -l src/main.rs"));
    }

    [Fact]
    public void ClassifyCommand_WcMultiFile_Supported()
    {
        Assert.Equal(
            new Classification.Supported("rtk wc", "Files", 60.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("wc src/*.rs"));
    }

    [Fact]
    public void ClassifyCommand_CommandSubstitutionPassthrough_SupportedGit()
    {
        Assert.Equal(
            new Classification.Supported("rtk git", "Git", 70.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("git log $(git rev-parse HEAD~1)"));
    }

    [Fact]
    public void ClassifyCommand_CommandNoLongerIgnored_NotIgnored()
    {
        Assert.NotEqual(Classification.Ignored.Instance, DiscoverRegistry.ClassifyCommand("command git status"));
    }

    [Fact]
    public void ClassifyCommand_AwsNoCapturingGroup_SubcmdSavingsTableNeverApplies()
    {
        // Rust quirk preserved verbatim: the `aws` rule's pattern `^aws\s+` has NO capture group, so
        // classify_command's `caps.get(1)` is always None and the elaborate per-service subcmd_savings
        // table in rules.rs is dead code for classify_command — every aws invocation gets the rule's
        // flat 80.0 base savings_pct, never e.g. ec2's 85.0 override.
        Assert.Equal(
            new Classification.Supported("rtk aws", "Infra", 80.0, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand("aws ec2 describe-instances"));
    }

    [Theory]
    [InlineData("/usr/bin/grep -rni pattern", "rtk grep", "Files", 75.0)]
    [InlineData("/bin/ls -la", "rtk ls", "Files", 65.0)]
    [InlineData("/usr/local/bin/git status", "rtk git", "Git", 70.0)]
    [InlineData("/usr/bin/find .", "rtk find", "Files", 70.0)]
    public void ClassifyCommand_AbsolutePathNormalized_SupportedAsIfBareCommand(
        string cmd, string rtkEquivalent, string category, double savingsPct)
    {
        Assert.Equal(
            new Classification.Supported(rtkEquivalent, category, savingsPct, RtkStatus.Existing),
            DiscoverRegistry.ClassifyCommand(cmd));
    }

    [Theory]
    [InlineData("git -C /tmp status")]
    [InlineData("git --no-pager log -5")]
    [InlineData("git --git-dir /tmp/.git status")]
    public void ClassifyCommand_GitGlobalOptions_StillSupported(string cmd)
    {
        var result = Assert.IsType<Classification.Supported>(DiscoverRegistry.ClassifyCommand(cmd));
        Assert.Equal("rtk git", result.RtkEquivalent);
        Assert.Equal(70.0, result.EstimatedSavingsPct);
    }

    // ===================== category_avg_tokens =====================

    [Theory]
    [InlineData("Git", "log", 200)]
    [InlineData("Git", "diff", 200)]
    [InlineData("Git", "show", 200)]
    [InlineData("Git", "status", 40)]
    [InlineData("Cargo", "test", 500)]
    [InlineData("Cargo", "build", 150)]
    [InlineData("Tests", "", 800)]
    [InlineData("Files", "", 100)]
    [InlineData("Build", "", 300)]
    [InlineData("Infra", "", 120)]
    [InlineData("Network", "", 150)]
    [InlineData("GitHub", "", 200)]
    [InlineData("GitLab", "", 200)]
    [InlineData("PackageManager", "", 150)]
    [InlineData("SomeUnknownCategory", "", 150)]
    public void CategoryAvgTokens_MatchesRustTable(string category, string subcmd, int expected)
    {
        Assert.Equal(expected, DiscoverRegistry.CategoryAvgTokens(category, subcmd));
    }

    // ===================== split_command_chain =====================

    [Fact]
    public void SplitCommandChain_And_SplitsIntoTwo()
    {
        Assert.Equal(["a", "b"], DiscoverRegistry.SplitCommandChain("a && b"));
    }

    [Fact]
    public void SplitCommandChain_Semicolon_SplitsIntoTwo()
    {
        Assert.Equal(["a", "b"], DiscoverRegistry.SplitCommandChain("a ; b"));
    }

    [Fact]
    public void SplitCommandChain_Pipe_FirstOnly()
    {
        Assert.Equal(["a"], DiscoverRegistry.SplitCommandChain("a | b"));
    }

    [Fact]
    public void SplitCommandChain_Single_Unchanged()
    {
        Assert.Equal(["git status"], DiscoverRegistry.SplitCommandChain("git status"));
    }

    [Fact]
    public void SplitCommandChain_QuotedAnd_NotSplit()
    {
        Assert.Equal([@"echo ""a && b"""], DiscoverRegistry.SplitCommandChain(@"echo ""a && b"""));
    }

    [Fact]
    public void SplitCommandChain_Heredoc_NoSplit()
    {
        var cmd = "cat <<'EOF'\nhello && world\nEOF";
        Assert.Equal([cmd], DiscoverRegistry.SplitCommandChain(cmd));
    }

    [Fact]
    public void SplitCommandChain_CommandSubstitution_NoSplit()
    {
        Assert.Equal(
            ["git log $(git rev-parse HEAD~1)"],
            DiscoverRegistry.SplitCommandChain("git log $(git rev-parse HEAD~1)"));
    }

    // ===================== strip_disabled_prefix / prefix_contains_rtk_disabled / cmd_has_rtk_disabled_prefix =====================

    [Fact]
    public void CmdHasRtkDisabledPrefix_DetectsVariousForms()
    {
        Assert.True(DiscoverRegistry.CmdHasRtkDisabledPrefix("RTK_DISABLED=1 git status"));
        Assert.True(DiscoverRegistry.CmdHasRtkDisabledPrefix("FOO=1 RTK_DISABLED=1 cargo test"));
        Assert.True(DiscoverRegistry.CmdHasRtkDisabledPrefix("RTK_DISABLED=true git log --oneline"));
        Assert.False(DiscoverRegistry.CmdHasRtkDisabledPrefix("git status"));
        Assert.False(DiscoverRegistry.CmdHasRtkDisabledPrefix("rtk git status"));
        Assert.False(DiscoverRegistry.CmdHasRtkDisabledPrefix("SOME_VAR=1 git status"));
    }

    [Fact]
    public void StripDisabledPrefix_SplitsEnvPrefixFromCommand()
    {
        Assert.Equal(
            ("RTK_DISABLED=1 ", "git status"),
            DiscoverRegistry.StripDisabledPrefix("RTK_DISABLED=1 git status"));
        Assert.Equal(
            ("FOO=1 RTK_DISABLED=1 ", "cargo test"),
            DiscoverRegistry.StripDisabledPrefix("FOO=1 RTK_DISABLED=1 cargo test"));
        Assert.Equal(("", "git status"), DiscoverRegistry.StripDisabledPrefix("git status"));
    }

    // ===================== extract_base_command =====================

    [Theory]
    [InlineData("htop -d 10", "htop")]
    [InlineData("git status", "git status")]
    [InlineData("some-tool", "some-tool")]
    public void ExtractBaseCommand_SubcommandShapedVsFlagShapedVsBare(string cmd, string expected)
    {
        Assert.Equal(expected, DiscoverRegistry.ExtractBaseCommand(cmd));
    }

    [Fact]
    public void ExtractBaseCommand_SecondTokenLooksLikeFlag_ReturnsFirstWordOnly()
    {
        Assert.Equal("htop", DiscoverRegistry.ExtractBaseCommand("htop -d 10"));
    }

    [Fact]
    public void ExtractBaseCommand_SecondTokenContainsSlash_ReturnsFirstWordOnly()
    {
        Assert.Equal("mytool", DiscoverRegistry.ExtractBaseCommand("mytool a/b"));
    }

    [Fact]
    public void ExtractBaseCommand_SecondTokenContainsDot_ReturnsFirstWordOnly()
    {
        Assert.Equal("mytool", DiscoverRegistry.ExtractBaseCommand("mytool file.txt"));
    }

    // ===================== strip_absolute_path =====================

    [Fact]
    public void StripAbsolutePath_MatchesRustOracle()
    {
        Assert.Equal("grep -rn foo", DiscoverRegistry.StripAbsolutePath("/usr/bin/grep -rn foo"));
        Assert.Equal("ls -la", DiscoverRegistry.StripAbsolutePath("/bin/ls -la"));
        Assert.Equal("grep -rn foo", DiscoverRegistry.StripAbsolutePath("grep -rn foo"));
        Assert.Equal("git", DiscoverRegistry.StripAbsolutePath("/usr/local/bin/git"));
    }

    // ===================== strip_git_global_opts =====================

    [Fact]
    public void StripGitGlobalOpts_MatchesRustOracle()
    {
        Assert.Equal("git status", DiscoverRegistry.StripGitGlobalOpts("git -C /tmp status"));
        Assert.Equal("git log", DiscoverRegistry.StripGitGlobalOpts("git --no-pager log"));
        Assert.Equal("git status", DiscoverRegistry.StripGitGlobalOpts("git status"));
        Assert.Equal("cargo test", DiscoverRegistry.StripGitGlobalOpts("cargo test"));
    }

    // ===================== strip_golangci_global_opts (+ helpers) =====================

    [Fact]
    public void StripGolangciGlobalOpts_MatchesRustOracle()
    {
        Assert.Equal("golangci-lint run ./...", DiscoverRegistry.StripGolangciGlobalOpts("golangci-lint -v run ./..."));
        Assert.Equal(
            "golangci-lint run ./...",
            DiscoverRegistry.StripGolangciGlobalOpts("golangci-lint --color never run ./..."));
        Assert.Equal(
            "golangci-lint run ./...",
            DiscoverRegistry.StripGolangciGlobalOpts("golangci-lint --color=never run ./..."));
        Assert.Equal(
            "golangci-lint run ./...",
            DiscoverRegistry.StripGolangciGlobalOpts("golangci-lint --config=foo.yml run ./..."));
        Assert.Equal("golangci-lint version", DiscoverRegistry.StripGolangciGlobalOpts("golangci-lint version"));
        Assert.Equal("cargo test", DiscoverRegistry.StripGolangciGlobalOpts("cargo test"));
    }
}
