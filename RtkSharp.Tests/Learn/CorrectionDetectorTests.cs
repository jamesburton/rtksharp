using System.Collections.Generic;
using RtkSharp.Learn;
using Xunit;

namespace RtkSharp.Tests.Learn;

/// <summary>
/// Test-for-test port of Rust <c>src/learn/detector.rs</c>'s <c>#[cfg(test)] mod tests</c> (13 tests
/// covering <c>is_command_error</c>, <c>classify_error</c>, <c>extract_base_command</c>,
/// <c>command_similarity</c>, <c>find_corrections</c>, and <c>deduplicate_corrections</c>).
/// </summary>
public sealed class CorrectionDetectorTests
{
    // ===================== is_command_error (detector.rs:357-376) =====================

    [Fact]
    public void IsCommandError_RequiresErrorFlag()
    {
        Assert.False(CorrectionDetector.IsCommandError(false, "error: unknown flag"));
        Assert.True(CorrectionDetector.IsCommandError(true, "error: unknown flag"));
    }

    [Fact]
    public void IsCommandError_FiltersUserRejection()
    {
        Assert.False(CorrectionDetector.IsCommandError(true, "The user doesn't want to proceed"));
        Assert.False(CorrectionDetector.IsCommandError(true, "Operation cancelled by user"));
        Assert.True(CorrectionDetector.IsCommandError(true, "error: permission denied"));
    }

    [Fact]
    public void IsCommandError_RequiresErrorContent()
    {
        Assert.False(CorrectionDetector.IsCommandError(true, "All good, success!"));
        Assert.True(CorrectionDetector.IsCommandError(true, "error: something failed"));
        Assert.True(CorrectionDetector.IsCommandError(true, "unknown flag --foo"));
        Assert.True(CorrectionDetector.IsCommandError(true, "invalid option"));
    }

    // ===================== classify_error (detector.rs:378-424) =====================

    [Fact]
    public void ClassifyError_UnknownFlag()
    {
        Assert.Equal(ErrorType.UnknownFlag, CorrectionDetector.ClassifyError("error: unexpected argument '--foo'"));
        Assert.Equal(ErrorType.UnknownFlag, CorrectionDetector.ClassifyError("unknown option: --bar"));
        Assert.Equal(ErrorType.UnknownFlag, CorrectionDetector.ClassifyError("unrecognized flag: -x"));
    }

    [Fact]
    public void ClassifyError_CommandNotFound()
    {
        Assert.Equal(ErrorType.CommandNotFound, CorrectionDetector.ClassifyError("bash: foobar: command not found"));
        Assert.Equal(
            ErrorType.CommandNotFound,
            CorrectionDetector.ClassifyError("'xyz' is not recognized as an internal or external command"));
    }

    [Fact]
    public void ClassifyError_AllTypes()
    {
        Assert.Equal(ErrorType.WrongPath, CorrectionDetector.ClassifyError("No such file or directory: foo.txt"));
        Assert.Equal(ErrorType.MissingArg, CorrectionDetector.ClassifyError("error: --output requires a value"));
        Assert.Equal(ErrorType.PermissionDenied, CorrectionDetector.ClassifyError("permission denied: /etc/shadow"));
        Assert.Equal(ErrorTypeKind.Other, CorrectionDetector.ClassifyError("something went wrong").Kind);
    }

    // ===================== extract_base_command (detector.rs:427-438) =====================

    [Fact]
    public void ExtractBaseCommand()
    {
        Assert.Equal("git commit", CorrectionDetector.ExtractBaseCommand("git commit"));
        Assert.Equal("cargo test", CorrectionDetector.ExtractBaseCommand("cargo test"));
        Assert.Equal("git commit", CorrectionDetector.ExtractBaseCommand("git commit --amend -m 'fix'"));
        Assert.Equal("cargo test", CorrectionDetector.ExtractBaseCommand("RUST_BACKTRACE=1 cargo test"));
    }

    // ===================== command_similarity (detector.rs:441-449) =====================

    [Fact]
    public void CommandSimilarity_SameBase()
    {
        Assert.Equal(1.0, CorrectionDetector.CommandSimilarity("git commit", "git commit"));
        Assert.Equal(0.0, CorrectionDetector.CommandSimilarity("git status", "npm install"));

        // Same base (0.5) + both have 1 arg, 0 intersection = 0.5 + 0 = 0.5.
        var sim = CorrectionDetector.CommandSimilarity("git commit --amend", "git commit --ammend");
        Assert.Equal(0.5, sim);
    }

    // ===================== find_corrections (detector.rs:452-569) =====================

    [Fact]
    public void FindCorrections_Basic()
    {
        var commands = new List<CommandExecution>
        {
            new("git commit --ammend", true, "error: unexpected argument '--ammend'"),
            new("git commit --amend", false, "[main abc123] Fix bug"),
        };

        var corrections = CorrectionDetector.FindCorrections(commands);
        Assert.Single(corrections);
        Assert.Equal("git commit --ammend", corrections[0].WrongCommand);
        Assert.Equal("git commit --amend", corrections[0].RightCommand);
        Assert.True(corrections[0].Confidence >= 0.6);
    }

    [Fact]
    public void FindCorrections_WindowLimit()
    {
        var commands = new List<CommandExecution>
        {
            new("git commit --ammend", true, "error: unexpected argument '--ammend'"),
            new("ls", false, "file1.txt\nfile2.txt"),
            new("pwd", false, "/home/user"),
            new("echo test", false, "test"),

            // Outside CORRECTION_WINDOW (3).
            new("git commit --amend", false, "[main abc123] Fix"),
        };

        var corrections = CorrectionDetector.FindCorrections(commands);
        Assert.Empty(corrections); // Too far apart.
    }

    [Fact]
    public void FindCorrections_ExcludesTddCycle()
    {
        var commands = new List<CommandExecution>
        {
            new("cargo test", true, "error[E0425]: cannot find value `x`\ntest result: FAILED"),
            new("cargo test", false, "test result: ok. 5 passed"),
        };

        var corrections = CorrectionDetector.FindCorrections(commands);
        Assert.Empty(corrections); // TDD cycle, not CLI correction.
    }

    [Fact]
    public void FindCorrections_PathExploration()
    {
        var commands = new List<CommandExecution>
        {
            new("cat file1.txt", true, "cat: file1.txt: No such file or directory"),
            new("cat file2.txt", false, "content here"),
        };

        var corrections = CorrectionDetector.FindCorrections(commands);

        // Different files = exploration.
        Assert.Empty(corrections);
    }

    [Fact]
    public void FindCorrections_MinConfidence()
    {
        var commands = new List<CommandExecution>
        {
            new("git commit --foo --bar --baz", true, "error: unexpected argument '--foo'"),
            new("git commit --qux", false, "[main abc123] Fix"),
        };

        var corrections = CorrectionDetector.FindCorrections(commands);

        // Similarity = 0.5 (same base) + 0 (no arg overlap) = 0.5. With success boost: 0.5 + 0.2 =
        // 0.7, which passes MIN_CONFIDENCE. So we expect 1 correction (a valid correction despite
        // different args).
        Assert.Single(corrections);
    }

    // ===================== deduplicate_corrections (detector.rs:572-628) =====================

    [Fact]
    public void DeduplicateCorrections_MergesSame()
    {
        var pairs = new List<CorrectionPair>
        {
            new("git commit --ammend", "git commit --amend", "error: unexpected argument '--ammend'", ErrorType.UnknownFlag, 0.8),
            new(
                "git commit --ammend -m 'fix'",
                "git commit --amend -m 'fix'",
                "error: unexpected argument '--ammend'",
                ErrorType.UnknownFlag,
                0.9),
            new("git commit --ammend", "git commit --amend", "error: unexpected argument '--ammend'", ErrorType.UnknownFlag, 0.7),
        };

        var rules = CorrectionDetector.DeduplicateCorrections(pairs);
        Assert.Single(rules); // Merged into single rule.
        Assert.Equal(3, rules[0].Occurrences);
        Assert.Equal("git commit", rules[0].BaseCommand);

        // Should keep highest confidence example (0.9).
        Assert.Contains("'fix'", rules[0].WrongPattern);
    }

    [Fact]
    public void DeduplicateCorrections_KeepsDistinct()
    {
        var pairs = new List<CorrectionPair>
        {
            new("git commit --ammend", "git commit --amend", "error: unexpected argument '--ammend'", ErrorType.UnknownFlag, 0.8),
            new("git push --force", "git push --force-with-lease", "error: --force is dangerous", ErrorType.WrongSyntax, 0.7),
        };

        var rules = CorrectionDetector.DeduplicateCorrections(pairs);
        Assert.Equal(2, rules.Count); // Different base commands and errors.
        Assert.Equal(1, rules[0].Occurrences);
        Assert.Equal(1, rules[1].Occurrences);
    }
}
