using RtkSharp.Filters.Commands.Jvm;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.Jvm;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/jvm/mvn_cmd.rs</c>'s <c>filter_surefire</c> /
/// <c>filter_package</c> / <c>filter_compile</c> / <c>filter_quiet</c> <c>#[cfg(test)]</c> tests: the
/// <see cref="SurefireBlock"/> state machine (single/multi-failure classes, trail re-arm, CRLF
/// handling, the failing-class and failures-summary caps), the reactor-summary toggle, the compile
/// filter's warning dedupe and error continuation, the quiet-mode filter's framework/boilerplate
/// stripping, and the full-fixture savings gates. Merged from the former
/// <c>MvnSurefireFilterTests</c> and <c>MvnCompileQuietFilterTests</c>.
/// </summary>
public sealed class MvnFiltersTests
{
    private static int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ── Surefire filter ──────────────────────────────────────────────────────

    [Fact]
    public void FilterSurefire_PassOutput_Compact()
    {
        var i = JvmFixtures.LoadText("mvn_test_pass_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.DoesNotContain("Running org.apache.commons.cli.help.UtilTest", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Time elapsed: 1.023 s -- in", o, StringComparison.Ordinal);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 50.0, $"pass-fixture savings >=50%, got {savings:F1}%");
    }

    [Fact]
    public void FilterSurefire_Fail_KeepsSignal()
    {
        var i = JvmFixtures.LoadText("mvn_test_fail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("BUILD FAILURE", o, StringComparison.Ordinal);
        Assert.Contains("Failures: 1", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Surefire_DropsPassingBlock()
    {
        var i = JvmFixtures.LoadText("mvn_test_pass_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Running org.apache.commons.cli.ConverterTests", o, StringComparison.Ordinal);
        Assert.Contains("BUILD SUCCESS", o, StringComparison.Ordinal);
        Assert.Contains("Tests run: 977, Failures: 0", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Surefire_PreservesFailingSignal()
    {
        var i = JvmFixtures.LoadText("mvn_test_fail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("Failures: 1", o, StringComparison.Ordinal);
        Assert.Contains("AssertionFailedError", o, StringComparison.Ordinal);
        Assert.Contains("at org.apache.commons.cli.RtkInducedFailTest.rtkInducedFailure", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
    }

    /// <summary>2.x compat: CLOSE regex must still match the single-dash separator emitted by Surefire
    /// 2.x. Locks the <c>--?</c> regex against accidental tightening.</summary>
    [Fact]
    public void Surefire_MatchesLegacy2xCloseLine()
    {
        const string i = "[INFO] -----< x >-----\n[INFO] Running x.Foo\n[INFO] Tests run: 3, Failures: 0, Errors: 0, Skipped: 0, Time elapsed: 0.123 s - in x.Foo\n[INFO] BUILD SUCCESS\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.DoesNotContain("Running x.Foo", o, StringComparison.Ordinal);
        Assert.Contains("BUILD SUCCESS", o, StringComparison.Ordinal);
    }

    /// <summary>3.x WARNING-prefixed close line (class with only skipped tests) must match CLOSE so
    /// the block is dropped (no failures, no errors).</summary>
    [Fact]
    public void Surefire_MatchesWarningSkippedCloseLine()
    {
        const string i = "[INFO] -----< x >-----\n[INFO] Running x.Skip\n[WARNING] Tests run: 5, Failures: 0, Errors: 0, Skipped: 5, Time elapsed: 0.010 s -- in x.Skip\n[INFO] BUILD SUCCESS\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.DoesNotContain("Running x.Skip", o, StringComparison.Ordinal);
    }

    /// <summary>3.x failure-trail: after a CLOSE with <c>&lt;&lt;&lt; FAILURE!</c>, the exception class
    /// and user-code frames Surefire emits outside the block must be preserved until the next blank line.</summary>
    [Fact]
    public void Surefire_Preserves3xFailureTrail()
    {
        const string i = "[INFO] -----< x >-----\n" +
            "[INFO] Running x.Foo\n" +
            "[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 0.033 s <<< FAILURE! -- in x.Foo\n" +
            "[ERROR] x.Foo.bar -- Time elapsed: 0.025 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: expected: <a> but was: <b>\n" +
            "\tat x.Foo.bar(Foo.java:25)\n" +
            "\tat org.junit.jupiter.api.Assertions.assertEquals(Assertions.java:1)\n" +
            "\n" +
            "[INFO] BUILD FAILURE\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("AssertionFailedError", o, StringComparison.Ordinal);
        Assert.Contains("at x.Foo.bar", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
    }

    // ── Multi-failure class (trail re-arm) ───────────────────────────────────

    [Fact]
    public void Surefire_KeepsAllFailuresInMultiFailureClass()
    {
        var i = JvmFixtures.LoadText("mvn_test_multifail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("AssertionFailedError: failOne: addition should equal five", o, StringComparison.Ordinal);
        Assert.Contains("IllegalStateException: failTwo: induced error", o, StringComparison.Ordinal);
        Assert.Contains("at com.example.rtk.CalcTest.failOne(CalcTest.java:12)", o, StringComparison.Ordinal);
        Assert.Contains("at com.example.rtk.CalcTest.failTwo(CalcTest.java:17)", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at java.base/", o, StringComparison.Ordinal);
    }

    /// <summary>Drift guard — the install/verify route shares SurefireBlock and must not diverge.</summary>
    [Fact]
    public void Package_KeepsAllFailuresInMultiFailureClass()
    {
        var i = JvmFixtures.LoadText("mvn_test_multifail_slice_raw.txt");
        var o = MvnFilters.FilterPackage(i);
        Assert.Contains("AssertionFailedError: failOne: addition should equal five", o, StringComparison.Ordinal);
        Assert.Contains("IllegalStateException: failTwo: induced error", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at java.base/", o, StringComparison.Ordinal);
    }

    /// <summary>A capped (dropped) multi-failure class must drop all its per-test blocks — the re-arm
    /// inherits the drop decision — and the tail counts classes, not failures.</summary>
    [Fact]
    public void Surefire_DropFailingDropsAllSublinesOfCappedClass()
    {
        const string i = "[INFO] Scanning for projects...\n" +
            "[INFO] -----< x >-----\n" +
            "[INFO] Running x.FailA\n" +
            "[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 0.011 s <<< FAILURE! -- in x.FailA\n" +
            "[ERROR] x.FailA.one -- Time elapsed: 0.010 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: boomA\n" +
            "\tat x.FailA.one(FailA.java:10)\n" +
            "\n" +
            "[INFO] Running x.MultiFail\n" +
            "[ERROR] Tests run: 2, Failures: 1, Errors: 1, Skipped: 0, Time elapsed: 0.051 s <<< FAILURE! -- in x.MultiFail\n" +
            "[ERROR] x.MultiFail.first -- Time elapsed: 0.020 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: boomFirst\n" +
            "\tat x.MultiFail.first(MultiFail.java:20)\n" +
            "\n" +
            "[ERROR] x.MultiFail.second -- Time elapsed: 0.030 s <<< ERROR!\n" +
            "java.lang.IllegalStateException: boomSecond\n" +
            "\tat x.MultiFail.second(MultiFail.java:30)\n" +
            "\n" +
            "[INFO] BUILD FAILURE\n";
        var o = MvnFilters.FilterSurefireWithCap(i, 1);

        Assert.Contains("boomA", o, StringComparison.Ordinal);
        Assert.False(o.Contains("Running x.MultiFail", StringComparison.Ordinal) || o.Contains("boomFirst", StringComparison.Ordinal));
        Assert.False(o.Contains("x.MultiFail.second", StringComparison.Ordinal) || o.Contains("boomSecond", StringComparison.Ordinal));
        Assert.Contains("… +1 more failing test classes", o, StringComparison.Ordinal);
    }

    /// <summary>A non-subline line (<c>[INFO] Results:</c>) immediately after a trail blank must disarm
    /// the re-arm and be kept normally by the outside-block list.</summary>
    [Fact]
    public void Surefire_RearmDisarmsAtResultsBoundary()
    {
        const string i = "[INFO] -----< x >-----\n" +
            "[INFO] Running x.MultiFail\n" +
            "[ERROR] Tests run: 2, Failures: 2, Errors: 0, Skipped: 0, Time elapsed: 0.051 s <<< FAILURE! -- in x.MultiFail\n" +
            "[ERROR] x.MultiFail.first -- Time elapsed: 0.020 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: boomFirst\n" +
            "\n" +
            "[ERROR] x.MultiFail.second -- Time elapsed: 0.030 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: boomSecond\n" +
            "\n" +
            "[INFO] Results:\n" +
            "[ERROR] Tests run: 2, Failures: 2, Errors: 0, Skipped: 0\n" +
            "[INFO] BUILD FAILURE\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("boomSecond", o, StringComparison.Ordinal);
        Assert.Contains("[INFO] Results:", o, StringComparison.Ordinal);
        Assert.Contains("[ERROR] Tests run: 2, Failures: 2", o, StringComparison.Ordinal);
    }

    /// <summary>Double blank between per-test blocks: stay armed across the extra blank, still
    /// re-enter the trail — and no spurious blank lines leak.</summary>
    [Fact]
    public void Surefire_ToleratesDoubleBlankBetweenFailureBlocks()
    {
        const string i = "[INFO] -----< x >-----\n" +
            "[INFO] Running x.MultiFail\n" +
            "[ERROR] Tests run: 2, Failures: 2, Errors: 0, Skipped: 0, Time elapsed: 0.051 s <<< FAILURE! -- in x.MultiFail\n" +
            "[ERROR] x.MultiFail.first -- Time elapsed: 0.020 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: boomFirst\n" +
            "\n" +
            "\n" +
            "[ERROR] x.MultiFail.second -- Time elapsed: 0.030 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: boomSecond\n" +
            "\n" +
            "[INFO] BUILD FAILURE\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("boomFirst", o, StringComparison.Ordinal);
        Assert.Contains("boomSecond", o, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", o, StringComparison.Ordinal);
    }

    /// <summary>Byte-exact pin of the single-failure path: the re-arm machinery must not change output
    /// for single-failure fixtures (no extra blank lines, no reordering).</summary>
    [Fact]
    public void Surefire_SingleFailureOutputUnchanged()
    {
        var i = JvmFixtures.LoadText("mvn_test_fail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        const string expected = "[INFO] Scanning for projects...\n" +
            "[INFO] ----------------------< commons-cli:commons-cli >-----------------------\n" +
            "[INFO] Building Apache Commons CLI 1.11.1-SNAPSHOT\n" +
            "[INFO] Running org.apache.commons.cli.RtkInducedFailTest\n" +
            "[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 0.033 s <<< FAILURE! -- in org.apache.commons.cli.RtkInducedFailTest\n" +
            "[ERROR] org.apache.commons.cli.RtkInducedFailTest.rtkInducedFailure -- Time elapsed: 0.025 s <<< FAILURE!\n" +
            "org.opentest4j.AssertionFailedError: expected: <expected> but was: <actual>\n" +
            "\tat org.apache.commons.cli.RtkInducedFailTest.rtkInducedFailure(RtkInducedFailTest.java:25)\n" +
            "\n" +
            "[INFO] Results:\n" +
            "[ERROR] Failures:\n" +
            "[ERROR]   RtkInducedFailTest.rtkInducedFailure:25 expected: <expected> but was: <actual>\n" +
            "[ERROR] Tests run: 978, Failures: 1, Errors: 0, Skipped: 61\n" +
            "[INFO] BUILD FAILURE\n" +
            "[INFO] Total time:  01:05 min\n" +
            "[INFO] Finished at: 2026-05-21T14:57:09Z\n" +
            "[ERROR] Failed to execute goal org.apache.maven.plugins:maven-surefire-plugin:3.5.5:test (default-test) on project commons-cli: There are test failures.\n";
        Assert.Equal(expected, o);
    }

    /// <summary>Savings on the multifail slice. Threshold is low by design: the slice is nearly all
    /// kept failure signal.</summary>
    [Fact]
    public void Savings_MvnTestMultifailSlice()
    {
        var i = JvmFixtures.LoadText("mvn_test_multifail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 30.0, $"multifail slice >=30% savings, got {savings:F1}%");
    }

    /// <summary>Non-quiet runs must strip the post-failure help boilerplate the same way filter_quiet
    /// does, while keeping the "Failed to execute goal" terminator (signal).</summary>
    [Fact]
    public void Surefire_DropsHelpBoilerplateInNonquietMode()
    {
        var i = JvmFixtures.LoadText("mvn_test_multifail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("[ERROR] Failed to execute goal", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[Help 1]", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-run Maven", o, StringComparison.Ordinal);
        Assert.DoesNotContain("To see the full stack trace", o, StringComparison.Ordinal);
        Assert.DoesNotContain("See dump files", o, StringComparison.Ordinal);
        Assert.False(o.Split('\n').Any(l => l.TrimEnd() == "[ERROR]"), $"bare [ERROR] dividers stripped; got:\n{o}");
    }

    [Fact]
    public void CloseLine_MatchesErrorMarker()
    {
        const string line = "[ERROR] Tests run: 1, Failures: 0, Errors: 1, Skipped: 0, Time elapsed: 0.006 s <<< ERROR! -- in com.example.rtk.BoomTest";
        var caps = MvnSharedFilters.CloseRegex().Match(line);
        Assert.True(caps.Success);
        Assert.Equal("0", caps.Groups[1].Value);
        Assert.Equal("1", caps.Groups[2].Value);
    }

    /// <summary>Regression guard for the P0 reviewer ask: filter_surefire previously dropped indented
    /// symbol/location continuation lines because it had no keep_continuation flag.</summary>
    [Fact]
    public void Surefire_KeepsCompileContinuationOnTestPhase()
    {
        var i = JvmFixtures.LoadText("mvn_test_compile_fail_slice_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("cannot find symbol", o, StringComparison.Ordinal);
        Assert.Contains("symbol:   variable bar", o, StringComparison.Ordinal);
        Assert.Contains("location: class org.apache.commons.cli.CompileBreaker", o, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILURE", o, StringComparison.Ordinal);
    }

    /// <summary>Regression guard on the package path so the install/verify route does not silently
    /// drift after the filter_surefire continuation fix.</summary>
    [Fact]
    public void Package_StillKeepsCompileErrorContinuationAfterRefactor()
    {
        var i = JvmFixtures.LoadText("mvn_compile_error_slice_raw.txt");
        var o = MvnFilters.FilterPackage(i);
        Assert.Contains("cannot find symbol", o, StringComparison.Ordinal);
        Assert.Contains("symbol:   variable bar", o, StringComparison.Ordinal);
        Assert.Contains("location: class org.apache.commons.cli.CompileBreaker", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Surefire_KeepsModuleBanner()
    {
        const string i = "[INFO] Scanning for projects...\n[INFO] -----< com.example:myapp >-----\n[INFO] BUILD SUCCESS\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("-----< com.example:myapp >-----", o, StringComparison.Ordinal);
    }

    /// <summary>Production must ship raw "Time elapsed"/"Total time" durations untouched — no
    /// normalisation belongs in the production path.</summary>
    [Fact]
    public void Surefire_PreservesRealDurations()
    {
        const string i = "[INFO] -----< x >-----\n[INFO] Running x.Foo\n[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 2.341 s <<< FAILURE! - in x.Foo\n[INFO] BUILD FAILURE\n[INFO] Total time:  4.567 s\n";
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("2.341 s", o, StringComparison.Ordinal);
        Assert.Contains("Total time:  4.567 s", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Time elapsed: T s", o, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterGuard_FrenchPassthrough()
    {
        var i = JvmFixtures.LoadText("mvn_locale_fr_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("BUILD ÉCHEC", o, StringComparison.Ordinal);
        Assert.Equal(i.Split('\n').Length, o.Split('\n').Length);
    }

    [Fact]
    public void FooterGuard_NoPomPassthrough()
    {
        var i = JvmFixtures.LoadText("mvn_no_pom_raw.txt");
        var o = MvnFilters.FilterSurefire(i);
        Assert.Contains("there is no POM", o, StringComparison.Ordinal);
    }

    // ── CRLF line-ending compatibility ───────────────────────────────────────

    [Fact]
    public void Surefire_HandlesCrlfLineEndings()
    {
        var iLf = JvmFixtures.LoadText("mvn_test_pass_slice_raw.txt").Replace("\r\n", "\n");
        var oLf = MvnFilters.FilterSurefire(iLf);
        var iCrlf = iLf.Replace("\n", "\r\n");
        var oCrlf = MvnFilters.FilterSurefire(iCrlf);
        Assert.Equal(oLf, oCrlf.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Package_HandlesCrlfLineEndings()
    {
        var iLf = JvmFixtures.LoadText("mvn_install_slice_raw.txt").Replace("\r\n", "\n");
        var oLf = MvnFilters.FilterPackage(iLf);
        var iCrlf = iLf.Replace("\n", "\r\n");
        var oCrlf = MvnFilters.FilterPackage(iCrlf);
        Assert.Equal(oLf, oCrlf.Replace("\r\n", "\n"));
    }

    // ── Cap: failing-class blocks ─────────────────────────────────────────────

    [Fact]
    public void Surefire_CapsFailingBlocksEmitsTail()
    {
        var i = "[INFO] Scanning for projects...\n[INFO] -----< x >-----\n";
        for (var n = 1; n <= 5; n++)
        {
            i += $"[INFO] Running x.Fail{n}\n" +
                 $"[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 0.0{n}1 s <<< FAILURE! -- in x.Fail{n}\n" +
                 $"[ERROR] x.Fail{n}.bar -- Time elapsed: 0.0{n}0 s <<< FAILURE!\n" +
                 $"org.opentest4j.AssertionFailedError: boom{n}\n" +
                 $"\tat x.Fail{n}.bar(Fail{n}.java:25)\n\n";
        }

        i += "[INFO] BUILD FAILURE\n";

        var o = MvnFilters.FilterSurefireWithCap(i, 3);

        for (var n = 1; n <= 3; n++)
        {
            Assert.Contains($"Running x.Fail{n}", o, StringComparison.Ordinal);
            Assert.Contains($"in x.Fail{n}", o, StringComparison.Ordinal);
        }

        for (var n = 4; n <= 5; n++)
        {
            Assert.DoesNotContain($"Running x.Fail{n}", o, StringComparison.Ordinal);
            Assert.DoesNotContain($"AssertionFailedError: boom{n}", o, StringComparison.Ordinal);
        }

        Assert.Contains("… +2 more failing test classes", o, StringComparison.Ordinal);
    }

    /// <summary>Cap of 0 means summary-only (core cap policy): no failing-class blocks emitted, tail
    /// still counts every dropped class.</summary>
    [Fact]
    public void Surefire_CapZeroEmitsSummaryOnly()
    {
        var i = "[INFO] Scanning for projects...\n[INFO] -----< x >-----\n";
        for (var n = 1; n <= 5; n++)
        {
            i += $"[INFO] Running x.Fail{n}\n" +
                 $"[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 0.0{n}1 s <<< FAILURE! -- in x.Fail{n}\n\n";
        }

        i += "[INFO] BUILD FAILURE\n";
        var o = MvnFilters.FilterSurefireWithCap(i, 0);
        for (var n = 1; n <= 5; n++)
        {
            Assert.DoesNotContain($"Running x.Fail{n}", o, StringComparison.Ordinal);
        }

        Assert.Contains("+5 more failing test classes", o, StringComparison.Ordinal);
    }

    /// <summary>The [ERROR] Failures: summary block cap: with N&gt;cap entries, expect the first
    /// cap entries plus a tail emitted before the aggregate.</summary>
    [Fact]
    public void FailuresSummaryBlock_IsCapped()
    {
        var i = "[INFO] -----< x >-----\n[INFO] Results:\n[INFO]\n[ERROR] Failures:\n";
        for (var n = 1; n <= 5; n++)
        {
            i += $"[ERROR]   ClassA.test{n}:25 expected: <a> but was: <b{n}>\n";
        }

        i += "[INFO]\n[ERROR] Tests run: 100, Failures: 5, Errors: 0, Skipped: 0\n[INFO] BUILD FAILURE\n";
        var o = MvnFilters.FilterSurefireWithCap(i, 3);

        for (var n = 1; n <= 3; n++)
        {
            Assert.Contains($"ClassA.test{n}:25", o, StringComparison.Ordinal);
        }

        for (var n = 4; n <= 5; n++)
        {
            Assert.DoesNotContain($"ClassA.test{n}:25", o, StringComparison.Ordinal);
        }

        var tailIdx = o.IndexOf("… +2 more failures", StringComparison.Ordinal);
        var aggIdx = o.IndexOf("[ERROR] Tests run: 100", StringComparison.Ordinal);
        Assert.True(tailIdx >= 0 && aggIdx >= 0 && tailIdx < aggIdx, $"tail must precede aggregate; got:\n{o}");
    }

    // ── Multi-module reactor summary ─────────────────────────────────────────

    [Fact]
    public void ReactorSummary_KeptOnMultiModulePass()
    {
        var i = JvmFixtures.LoadText("mvn_reactor_pass_slice_raw.txt");
        var o = MvnFilters.FilterPackage(i);
        Assert.Contains("Reactor Summary for multi-module-skeleton", o, StringComparison.Ordinal);
        Assert.Contains("[INFO] child-a ............................................ SUCCESS", o, StringComparison.Ordinal);
        Assert.Contains("[INFO] child-b ............................................ SUCCESS", o, StringComparison.Ordinal);
        Assert.Contains("BUILD SUCCESS", o, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactorSummary_KeptOnMultiModuleFail()
    {
        var i = JvmFixtures.LoadText("mvn_reactor_fail_slice_raw.txt");
        var o = MvnFilters.FilterPackage(i);
        Assert.Contains("Reactor Summary for multi-module-skeleton", o, StringComparison.Ordinal);
        Assert.Contains("child-a ............................................ SUCCESS", o, StringComparison.Ordinal);
        Assert.Contains("child-b ............................................ FAILURE", o, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILURE", o, StringComparison.Ordinal);
        Assert.Contains("[ERROR] Failed to execute goal", o, StringComparison.Ordinal);
        Assert.Contains("mvn <args> -rf :child-b", o, StringComparison.Ordinal);
        Assert.DoesNotContain("[Help 1]", o, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-run Maven", o, StringComparison.Ordinal);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 30.0, $"reactor-fail slice savings >=30% (short fixture); got {savings:F1}%");
    }

    // ── Package filter ───────────────────────────────────────────────────────

    [Fact]
    public void FilterPackage_InstallCompact()
    {
        var i = JvmFixtures.LoadText("mvn_install_slice_raw.txt");
        var o = MvnFilters.FilterPackage(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 50.0, $"install-slice savings >=50%, got {savings:F1}%");
    }

    [Fact]
    public void Package_KeepsInstallLines()
    {
        var i = JvmFixtures.LoadText("mvn_install_slice_raw.txt");
        var o = MvnFilters.FilterPackage(i);
        Assert.Contains("Installing", o, StringComparison.Ordinal);
        Assert.Contains("Building jar:", o, StringComparison.Ordinal);
        Assert.DoesNotContain("at org.junit.", o, StringComparison.Ordinal);
    }

    // ── Token-savings (FULL gzipped fixtures) ────────────────────────────────

    [Fact]
    public void Savings_MvnTestPassFull()
    {
        var i = JvmFixtures.LoadGzText("mvn_test_pass_full_raw.txt.gz");
        var o = MvnFilters.FilterSurefire(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 90.0, $"mvn test >=90% savings on full fixture, got {savings:F1}%");
    }

    [Fact]
    public void Savings_MvnInstallFull()
    {
        var i = JvmFixtures.LoadGzText("mvn_install_full_raw.txt.gz");
        var o = MvnFilters.FilterPackage(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 85.0, $"mvn install >=85% savings on full fixture, got {savings:F1}%");
    }

    // ── Compile filter ───────────────────────────────────────────────────────

    [Fact]
    public void FilterCompile_ErrorCompact()
    {
        var i = JvmFixtures.LoadText("mvn_compile_error_slice_raw.txt");
        var o = MvnFilters.FilterCompile(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 30.0, $"compile-error fixture is small; >=30% savings, got {savings:F1}%");
    }

    [Fact]
    public void Compile_PreservesErrorContinuation()
    {
        var i = JvmFixtures.LoadText("mvn_compile_error_slice_raw.txt");
        var o = MvnFilters.FilterCompile(i);
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
        var o = MvnFilters.FilterCompile(i);
        var warns = o.Split("[WARNING]").Length - 1;
        Assert.Equal(2, warns);
    }

    // ── Quiet mode (mvn -q) ───────────────────────────────────────────────────

    [Fact]
    public void QuietGreenRun_IsEmpty()
    {
        Assert.Equal(string.Empty, MvnFilters.FilterQuiet(string.Empty));
        Assert.Equal(string.Empty, MvnFilters.FilterQuiet("   \n\n  \n"));
    }

    [Fact]
    public void QuietFail_StripsFrameworkAndBoilerplate()
    {
        var i = JvmFixtures.LoadText("mvn_quiet_fail_raw.txt");
        var o = MvnFilters.FilterQuiet(i);

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
        var o = MvnFilters.FilterQuiet(i);
        var savings = 100.0 - (CountTokens(o) / (double)CountTokens(i) * 100.0);
        Assert.True(savings >= 50.0, $"mvn -q fail >=50% savings, got {savings:F1}%");
    }

    /// <summary>Safety net: if the [ERROR] line isn't on the known keep/drop lists, the filter must
    /// NOT silently drop it. Better to leak a line than to hide signal.</summary>
    [Fact]
    public void QuietUnknownErrorLine_KeptAsSafetyNet()
    {
        const string i = "[ERROR] Some unexpected error output we don't classify\n";
        var o = MvnFilters.FilterQuiet(i);
        Assert.Contains("Some unexpected error output", o, StringComparison.Ordinal);
    }
}
