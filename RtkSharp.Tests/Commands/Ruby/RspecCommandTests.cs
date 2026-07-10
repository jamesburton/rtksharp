using RtkSharp.Commands.Ruby;
using Xunit;

namespace RtkSharp.Tests.Commands.Ruby;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/ruby/rspec_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// subset covering the <c>has_format</c> flag-detection expression used by
/// <see cref="RspecCommand.RunAsync"/> (dispatch-level logic that stays in
/// <see cref="RspecCommand"/>). The <c>filter_rspec_output</c>/<c>filter_rspec_text</c>/
/// <c>strip_noise</c> tests moved to <c>RtkSharp.Filters.Tests.Commands.Ruby.RspecFiltersTests</c>
/// alongside <c>RtkSharp.Filters.Commands.Ruby.RspecFilters</c> (Task 11).
/// </summary>
public sealed class RspecCommandTests
{
    // ── Format flag detection tests (from PR #534) ────────────────────────────
    // Rust's own test module duplicates the has_format detection expression inline rather than
    // calling a standalone function; this port matches that structure by asserting the same
    // boolean expression RspecCommand.RunAsync uses (RtkSharp doesn't expose a standalone
    // HasFormatFlag helper either — see rspec_cmd.rs:72-77, which inlines the check in `run`).

    private static bool HasFormat(IEnumerable<string> args) => args.Any(a =>
        a == "--format"
        || a == "-f"
        || a.StartsWith("--format=", StringComparison.Ordinal)
        || (a.StartsWith("-f", StringComparison.Ordinal) && a.Length > 2 && !a.StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public void HasFormatFlagNone()
    {
        Assert.False(HasFormat([]));
    }

    [Fact]
    public void HasFormatFlagLong()
    {
        Assert.True(HasFormat(["--format", "documentation"]));
    }

    [Fact]
    public void HasFormatFlagShortCombined()
    {
        foreach (var flag in new[] { "-fjson", "-fj", "-fdocumentation" })
        {
            Assert.True(HasFormat([flag]), $"should detect {flag}");
        }
    }

    [Fact]
    public void HasFormatFlagEquals()
    {
        Assert.True(HasFormat(["--format=json"]));
    }
}
