using RtkSharp.Commands.Jvm;
using Xunit;

namespace RtkSharp.Tests.Commands.Jvm;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/jvm/mvn_cmd.rs</c>'s <c>filter_compile</c> /
/// <c>filter_quiet</c> <c>#[cfg(test)]</c> tests.
/// </summary>
public sealed class MvnCompileQuietFilterTests
{
    private static int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ── Compile filter ───────────────────────────────────────────────────────

    [Fact]
    public void FilterCompile_ErrorCompact()
    {
        var i = JvmFixtures.LoadText("mvn_compile_error_slice_raw.txt");
        var o = MvnCompileQuietFilters.FilterCompile(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 30.0, $"compile-error fixture is small; >=30% savings, got {savings:F1}%");
    }

    [Fact]
    public void Compile_PreservesErrorContinuation()
    {
        var i = JvmFixtures.LoadText("mvn_compile_error_slice_raw.txt");
        var o = MvnCompileQuietFilters.FilterCompile(i);
        Assert.Contains("cannot find symbol", o, StringComparison.Ordinal);
        Assert.Contains("symbol:   variable bar", o, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILURE", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[Help 1]", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_DedupesWarnings()
    {
        const string i = "[INFO] -----< x >-----\n" +
            "[WARNING] /a.java:[1,2] uses deprecated API\n" +
            "[WARNING] /b.java:[3,4] uses deprecated API\n" +
            "[WARNING] /a.java:[5,6] unchecked cast\n" +
            "[INFO] BUILD SUCCESS\n";
        var o = MvnCompileQuietFilters.FilterCompile(i);
        var warns = o.Split("[WARNING]").Length - 1;
        Assert.Equal(2, warns);
    }

    // ── Quiet mode (mvn -q) ───────────────────────────────────────────────────

    [Fact]
    public void QuietGreenRun_IsEmpty()
    {
        Assert.Equal(string.Empty, MvnCompileQuietFilters.FilterQuiet(string.Empty));
        Assert.Equal(string.Empty, MvnCompileQuietFilters.FilterQuiet("   \n\n  \n"));
    }

    [Fact]
    public void QuietFail_StripsFrameworkAndBoilerplate()
    {
        var i = JvmFixtures.LoadText("mvn_quiet_fail_raw.txt");
        var o = MvnCompileQuietFilters.FilterQuiet(i);

        Assert.Contains("Tests run: 1, Failures: 1, Errors: 0, Skipped: 0", o, StringComparison.Ordinal);
        Assert.Contains("AssertionFailedError", o, StringComparison.Ordinal);
        Assert.Contains("at x.FailTest.this_will_fail", o, StringComparison.Ordinal);
        Assert.Contains("[ERROR] Failures:", o, StringComparison.Ordinal);
        Assert.Contains("[ERROR] Tests run: 6, Failures: 1, Errors: 0, Skipped: 0", o, StringComparison.Ordinal);
        Assert.Contains("[ERROR] Failed to execute goal", o, StringComparison.Ordinal);

        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at java.base/", o, StringComparison.Ordinal);

        Assert.DoesNotContain("To see the full stack trace", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[Help 1] http", o, StringComparison.Ordinal);
        Assert.False(o.Contains("See /tmp/", StringComparison.Ordinal) || o.Contains("See dump files", StringComparison.Ordinal));
    }

    [Fact]
    public void Savings_MvnQuietFail()
    {
        var i = JvmFixtures.LoadText("mvn_quiet_fail_raw.txt");
        var o = MvnCompileQuietFilters.FilterQuiet(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 50.0, $"mvn -q fail >=50% savings, got {savings:F1}%");
    }

    /// <summary>Safety net: if the [ERROR] line isn't on the known keep/drop lists, the filter must
    /// NOT silently drop it. Better to leak a line than to hide signal.</summary>
    [Fact]
    public void QuietUnknownErrorLine_KeptAsSafetyNet()
    {
        const string i = "[ERROR] Some unexpected error output we don't classify\n";
        var o = MvnCompileQuietFilters.FilterQuiet(i);
        Assert.Contains("Some unexpected error output", o, StringComparison.Ordinal);
    }
}
