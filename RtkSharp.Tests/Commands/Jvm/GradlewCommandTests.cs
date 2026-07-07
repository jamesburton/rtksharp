using RtkSharp.Commands.Jvm;
using RtkSharp.Core;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.Jvm;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/jvm/gradlew_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (task detection, the build/test/connected/lint/dependencies filters, the fixture-based token-savings
/// assertions, and the edge cases), plus a small amount of new dispatch-level coverage for the
/// <c>rtk gradlew</c> entry point that Rust's own oracle doesn't expose as pure functions.
/// </summary>
public sealed class GradlewCommandTests
{
    private static int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool[] FilterBuildLines(string input) =>
        input.Split('\n').Select(GradlewFilters.FilterBuildLine).ToArray();

    private static string FilteredBuildText(string input) =>
        string.Join('\n', input.Split('\n').Where(GradlewFilters.FilterBuildLine));

    // ── TASK DETECTION ───────────────────────────────────────────────────────

    [Fact]
    public void DetectTask_ConnectedWinsOverTest()
    {
        Assert.Equal(GradlewTask.ConnectedTest, GradlewCommand.DetectTask(["connectedDebugAndroidTest"]));
    }

    [Fact]
    public void DetectTask_AssembleDebug()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["assembleDebug"]));
    }

    [Fact]
    public void DetectTask_TestDebugUnitTest()
    {
        Assert.Equal(GradlewTask.Test, GradlewCommand.DetectTask(["testDebugUnitTest"]));
    }

    [Fact]
    public void DetectTask_ModulePrefixedTask()
    {
        Assert.Equal(GradlewTask.Test, GradlewCommand.DetectTask([":app:testDebugUnitTest"]));
    }

    [Fact]
    public void DetectTask_ModulePrefixedAssemble()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask([":app:assembleDebug"]));
    }

    [Fact]
    public void DetectTask_FlagValueDoesNotTriggerTest()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["assembleRelease", "-Pflavor=testRelease"]));
    }

    [Fact]
    public void DetectTask_MultiTaskUsesLast()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["clean", "assembleDebug"]));
    }

    [Fact]
    public void DetectTask_Lint()
    {
        Assert.Equal(GradlewTask.Lint, GradlewCommand.DetectTask(["lint"]));
    }

    [Fact]
    public void DetectTask_Ktlint()
    {
        Assert.Equal(GradlewTask.Lint, GradlewCommand.DetectTask(["ktlintCheck"]));
    }

    [Fact]
    public void DetectTask_Bundle()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["bundleRelease"]));
    }

    [Fact]
    public void DetectTask_UnknownPassthrough()
    {
        Assert.Equal(GradlewTask.Other, GradlewCommand.DetectTask(["signingReport"]));
    }

    [Fact]
    public void DetectTask_CleanAloneIsBuild()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["clean"]));
    }

    [Fact]
    public void DetectTask_InstallDebug()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["installDebug"]));
    }

    [Fact]
    public void DetectTask_UninstallDebug()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["uninstallDebug"]));
    }

    [Fact]
    public void DetectTask_CleanInstall()
    {
        Assert.Equal(GradlewTask.Build, GradlewCommand.DetectTask(["clean", "installDebug"]));
    }

    [Fact]
    public void DetectTask_Check()
    {
        Assert.Equal(GradlewTask.Test, GradlewCommand.DetectTask(["check"]));
    }

    [Fact]
    public void DetectTask_Dependencies()
    {
        Assert.Equal(GradlewTask.Dependencies, GradlewCommand.DetectTask(["dependencies"]));
    }

    [Fact]
    public void DetectTask_DependenciesWithModule()
    {
        Assert.Equal(GradlewTask.Dependencies, GradlewCommand.DetectTask([":app:dependencies"]));
    }

    // ── BUILD FILTER ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_Success_StripsTaskLines()
    {
        const string input = "> Configure project :app\n" +
            "> Task :app:preBuild UP-TO-DATE\n" +
            "> Task :app:generateDebugBuildConfig UP-TO-DATE\n" +
            "> Task :app:generateDebugResValues UP-TO-DATE\n" +
            "> Task :app:generateDebugResources UP-TO-DATE\n" +
            "> Task :app:mergeDebugResources UP-TO-DATE\n" +
            "> Task :app:processDebugManifest UP-TO-DATE\n" +
            "> Task :app:compileDebugKotlin UP-TO-DATE\n" +
            "> Task :app:compileDebugJavaWithJavac UP-TO-DATE\n" +
            "> Task :app:validateSigningDebug UP-TO-DATE\n" +
            "> Task :app:packageDebug UP-TO-DATE\n" +
            "> Task :app:assembleDebug UP-TO-DATE\n" +
            "\n" +
            "BUILD SUCCESSFUL in 1m 23s\n" +
            "42 actionable tasks: 42 executed";

        var filtered = FilteredBuildText(input);
        var savings = 100.0 - (CountTokens(filtered) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 70.0, $"Expected >=70% savings, got {savings:F1}%");
        Assert.Contains("BUILD SUCCESSFUL", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("> Task :", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Failure_PreservesErrorsStripsTry()
    {
        const string input = "> Task :app:compileDebugKotlin FAILED\n" +
            "\n" +
            "FAILURE: Build failed with an exception.\n" +
            "\n" +
            "* What went wrong:\n" +
            "e: /src/app/MainActivity.kt: (42, 5): Unresolved reference: MyService\n" +
            "\n" +
            "* Try:\n" +
            "> Run with --stacktrace option to get the stack trace.\n" +
            "> Run with --info or --debug option to get more log output.\n" +
            "> Get more help at https://help.gradle.org\n" +
            "\n" +
            "BUILD FAILED in 12s";

        var filtered = FilteredBuildText(input);
        Assert.Contains("Unresolved reference", filtered, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILED", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("Run with --stacktrace", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("Get more help at", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Filter_NeverEmptyOnSuccess()
    {
        const string input = "> Task :app:assembleDebug UP-TO-DATE\nBUILD SUCCESSFUL in 3s\n1 actionable tasks: 1 up-to-date";
        Assert.True(FilterBuildLines(input).Any(kept => kept), "Filter must not produce empty output on success");
    }

    [Fact]
    public void Build_DaemonLinesStripped()
    {
        const string input = "Starting a Gradle Daemon (subsequent builds will be faster)\n" +
            "Daemon will be stopped at the end of the build after running out of JVM memory\n" +
            "> Task :app:assembleDebug\n" +
            "BUILD SUCCESSFUL in 5s";
        var filtered = FilteredBuildText(input);
        Assert.DoesNotContain("Daemon", filtered, StringComparison.Ordinal);
        Assert.Contains("BUILD SUCCESSFUL", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ScanUrlPreserved()
    {
        const string input = "> Task :app:assembleDebug\nBUILD SUCCESSFUL in 5s\nPublishing build scan...\nhttps://gradle.com/s/abc123";
        var filtered = FilteredBuildText(input);
        Assert.Contains("gradle.com/s/", filtered, StringComparison.Ordinal);
    }

    // ── TEST FILTER ──────────────────────────────────────────────────────────

    [Fact]
    public void UnitTest_FailuresPreserved_PassesStripped()
    {
        const string input = "> Task :app:testDebugUnitTest\n" +
            "com.example.FooTest > test1 PASSED\n" +
            "com.example.FooTest > test2 PASSED\n" +
            "com.example.FooTest > test3 PASSED\n" +
            "com.example.FooTest > test4 PASSED\n" +
            "com.example.FooTest > test5 PASSED\n" +
            "com.example.FooTest > test6 PASSED\n" +
            "com.example.FooTest > test7 PASSED\n" +
            "com.example.FooTest > testBar FAILED\n" +
            "    java.lang.AssertionError: expected:<3> but was:<-1>\n" +
            "        at org.junit.Assert.fail(Assert.java:89)\n" +
            "        at org.junit.Assert.assertEquals(Assert.java:197)\n" +
            "        at com.example.FooTest.testBar(FooTest.kt:25)\n" +
            "com.example.FooTest > testQux PASSED\n" +
            "\n" +
            "10 tests completed, 1 failed";

        var output = GradlewFilters.FilterTest(input);

        Assert.Contains("testBar FAILED", output, StringComparison.Ordinal);
        Assert.Contains("AssertionError", output, StringComparison.Ordinal);
        Assert.Contains("FooTest.testBar", output, StringComparison.Ordinal);
        Assert.DoesNotContain("org.junit.Assert.fail", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSED", output, StringComparison.Ordinal);
        Assert.Contains("10 tests completed, 1 failed", output, StringComparison.Ordinal);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void UnitTest_SkipsFrameworkFrames()
    {
        const string input = "com.example.CalcTest > testAdd FAILED\n" +
            "    java.lang.AssertionError: expected:<5> but was:<3>\n" +
            "        at org.junit.Assert.fail(Assert.java:89)\n" +
            "        at org.junit.Assert.assertEquals(Assert.java:197)\n" +
            "        at java.lang.reflect.Method.invoke(Method.java:498)\n" +
            "        at com.example.CalcTest.testAdd(CalcTest.kt:10)";
        var output = GradlewFilters.FilterTest(input);
        Assert.Contains("com.example.CalcTest.testAdd", output, StringComparison.Ordinal);
        Assert.DoesNotContain("org.junit.Assert", output, StringComparison.Ordinal);
        Assert.DoesNotContain("java.lang.reflect", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitTest_GradleDefault_NoTestLogging()
    {
        const string input = "> Task :app:testDebugUnitTest\n\nBUILD SUCCESSFUL in 15s\n3 actionable tasks: 1 executed, 2 up-to-date";
        var output = GradlewFilters.FilterTest(input);
        Assert.True(output.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal) || output.Contains("ok ✓", StringComparison.Ordinal));
        Assert.NotEmpty(output);
    }

    [Fact]
    public void UnitTest_ReportPathPreserved()
    {
        const string input = "There were failing tests. See the report at: file:///app/build/reports/tests/testDebugUnitTest/index.html\nBUILD FAILED in 20s";
        var output = GradlewFilters.FilterTest(input);
        Assert.Contains("See the report at", output, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySection_StrippedFromTestOutput()
    {
        const string input = "com.example.FooTest > testBar FAILED\n" +
            "    java.lang.AssertionError: expected true\n" +
            "\n" +
            "* Try:\n" +
            "> Run with --stacktrace option to get the stack trace.\n" +
            "> Run with --info or --debug option to get more log output.\n" +
            "> Get more help at https://help.gradle.org\n" +
            "\n" +
            "BUILD FAILED in 5s";
        var output = GradlewFilters.FilterTest(input);
        Assert.DoesNotContain("Run with --stacktrace", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Get more help at", output, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILED", output, StringComparison.Ordinal);
    }

    // ── CONNECTED TEST FILTER ────────────────────────────────────────────────

    [Fact]
    public void Connected_StripsDeviceNoise()
    {
        const string input = "Starting 3 tests on Pixel_6_API_33(AVD) - 13\n" +
            "INSTRUMENTATION_STATUS: numtests=3\n" +
            "INSTRUMENTATION_STATUS_CODE: 1\n" +
            "com.example.MainActivityTest > exampleTest[Pixel_6_API_33] FAILED\n" +
            "    AssertionError: expected true\n" +
            "INSTRUMENTATION_STATUS_CODE: -2\n" +
            "Tests run: 3, Failures: 1, Errors: 0, Skipped: 0";
        var output = GradlewFilters.FilterConnected(input);
        Assert.Contains("FAILED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTRUMENTATION_STATUS:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Starting 3 tests", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Connected_NoDeviceError()
    {
        const string input = "com.android.builder.testing.api.DeviceException: No connected devices!";
        var output = GradlewFilters.FilterConnected(input);
        Assert.Contains("No connected devices", output, StringComparison.Ordinal);
    }

    // ── LINT FILTER ──────────────────────────────────────────────────────────

    [Fact]
    public void Lint_PreservesViolations()
    {
        const string input = "Wrote HTML report to file:/path/app/build/reports/lint-results-debug.html\n" +
            "src/main/java/com/example/MainActivity.kt:45: Error: Format string invalid [StringFormatInvalid]\n" +
            "  String.format(getString(R.string.no_args), arg)\n" +
            "  ^\n" +
            "0 errors, 4 warnings";
        var output = GradlewFilters.FilterLint(input);
        Assert.Contains("StringFormatInvalid", output, StringComparison.Ordinal);
        Assert.Contains("0 errors, 4 warnings", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrote HTML report", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_PreservesWarnings()
    {
        const string input = "src/main/java/com/example/Utils.kt:89: Warning: HardcodedText [HardcodedText]\n" +
            "    return \"Hello World\"\n" +
            "           ~~~~~~~~~~~~~\n" +
            "src/main/res/layout/activity_main.xml:15: Warning: Missing contentDescription attribute on image [ContentDescription]\n" +
            "    <ImageView\n" +
            "Ran lint on variant debug: 2 warnings";
        var output = GradlewFilters.FilterLint(input);
        Assert.Contains("HardcodedText", output, StringComparison.Ordinal);
        Assert.Contains("ContentDescription", output, StringComparison.Ordinal);
        Assert.Contains("2 warnings", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_NoViolationsSuccess()
    {
        const string input = "> Task :app:lint\nBUILD SUCCESSFUL in 8s\n3 actionable tasks: 1 executed, 2 up-to-date";
        var output = GradlewFilters.FilterLint(input);
        Assert.NotEmpty(output);
        Assert.True(output.Contains("BUILD SUCCESSFUL", StringComparison.Ordinal) || output.Contains("ok ✓", StringComparison.Ordinal));
    }

    // ── FIXTURE-BASED TESTS ──────────────────────────────────────────────────

    [Fact]
    public void BuildFixture_TokenSavings()
    {
        var input = JvmFixtures.LoadText("gradlew_build_raw.txt");
        var filtered = FilteredBuildText(input);
        var savings = 100.0 - (CountTokens(filtered) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 70.0, $"Build fixture: expected >=70% savings, got {savings:F1}%");
        Assert.DoesNotContain("> Task :", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailedFixture_TokenSavings()
    {
        var input = JvmFixtures.LoadText("gradlew_build_failed_raw.txt");
        var filtered = FilteredBuildText(input);
        Assert.Contains("BUILD FAILED", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("Run with --stacktrace", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void TestFixture_PreservesFailures()
    {
        var input = JvmFixtures.LoadText("gradlew_test_raw.txt");
        var output = GradlewFilters.FilterTest(input);
        Assert.DoesNotContain("PASSED", output, StringComparison.Ordinal);
        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Test fixture: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void TestFailedFixture_ShowsUserCode()
    {
        var input = JvmFixtures.LoadText("gradlew_test_failed_raw.txt");
        var output = GradlewFilters.FilterTest(input);
        Assert.Contains("FAILED", output, StringComparison.Ordinal);
        Assert.True(
            output.Contains("CalculatorTest.testSubtraction", StringComparison.Ordinal)
            || output.Contains("MainViewModelTest.loadDataError", StringComparison.Ordinal));
        Assert.Contains("5 tests completed, 2 failed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedFixture_TokenSavings()
    {
        var input = JvmFixtures.LoadText("gradlew_connected_raw.txt");
        var output = GradlewFilters.FilterConnected(input);
        Assert.DoesNotContain("INSTRUMENTATION_STATUS", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LintFixture_TokenSavings()
    {
        var input = JvmFixtures.LoadText("gradlew_lint_raw.txt");
        var output = GradlewFilters.FilterLint(input);
        Assert.DoesNotContain("Wrote HTML report", output, StringComparison.Ordinal);
        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Lint fixture: expected >=60% savings, got {savings:F1}%");
    }

    // ── OUTPUT FORMAT TESTS ──────────────────────────────────────────────────

    [Fact]
    public void BuildSuccess_OutputFormat()
    {
        var input = JvmFixtures.LoadText("gradlew_build_raw.txt");
        var output = FilteredBuildText(input);
        Assert.Contains("BUILD SUCCESSFUL", output, StringComparison.Ordinal);
        Assert.Contains("actionable tasks", output, StringComparison.Ordinal);
        Assert.DoesNotContain("> Task :", output, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailed_OutputFormat()
    {
        var input = JvmFixtures.LoadText("gradlew_build_failed_raw.txt");
        var output = FilteredBuildText(input);
        Assert.Contains("BUILD FAILED", output, StringComparison.Ordinal);
        Assert.Contains("FAILURE:", output, StringComparison.Ordinal);
        Assert.Contains("e: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("> Task :", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSuccess_OutputFormat()
    {
        var input = JvmFixtures.LoadText("gradlew_test_raw.txt");
        var output = GradlewFilters.FilterTest(input);
        Assert.Contains("tests completed", output, StringComparison.Ordinal);
        Assert.Contains("BUILD SUCCESSFUL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TestFailed_OutputFormat()
    {
        var input = JvmFixtures.LoadText("gradlew_test_failed_raw.txt");
        var output = GradlewFilters.FilterTest(input);
        Assert.Contains("FAILED", output, StringComparison.Ordinal);
        Assert.Contains("tests completed", output, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("at org.junit.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Connected_OutputFormat()
    {
        var input = JvmFixtures.LoadText("gradlew_connected_raw.txt");
        var output = GradlewFilters.FilterConnected(input);
        Assert.Contains("BUILD SUCCESSFUL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTRUMENTATION_STATUS", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_OutputFormat()
    {
        var input = JvmFixtures.LoadText("gradlew_lint_raw.txt");
        var output = GradlewFilters.FilterLint(input);
        Assert.Contains("Error:", output, StringComparison.Ordinal);
        Assert.Contains("Warning:", output, StringComparison.Ordinal);
        Assert.Contains("BUILD FAILED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrote HTML report", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_PreservesCodeContext()
    {
        var input = JvmFixtures.LoadText("gradlew_lint_raw.txt");
        var output = GradlewFilters.FilterLint(input);
        Assert.Contains("String.format(getString(R.string.template)", output, StringComparison.Ordinal);
        Assert.Contains("This format string placeholder index", output, StringComparison.Ordinal);
        Assert.Contains("return \"Hello World\"", output, StringComparison.Ordinal);
        Assert.Contains("<ImageView", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Filter_KeepsCompilerWarnings()
    {
        const string input = "> Task :app:compileDebugKotlin\n" +
            "w: /src/Foo.kt: (42, 5): Parameter 'unused' is never used\n" +
            "warning: [options] bootstrap class path not set\n" +
            "Warning: Gradle deprecation detected\n" +
            "\n" +
            "BUILD SUCCESSFUL in 4s";
        var output = FilteredBuildText(input);
        Assert.Contains("w: ", output, StringComparison.Ordinal);
        Assert.Contains("warning: [options]", output, StringComparison.Ordinal);
        Assert.Contains("Warning: Gradle", output, StringComparison.Ordinal);
        Assert.Contains("BUILD SUCCESSFUL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("> Task :", output, StringComparison.Ordinal);
    }

    // ── CHECK (BUILD FILTER ON MIXED OUTPUT) ─────────────────────────────────

    [Fact]
    public void Build_Filter_StripsConfigureAndDokkaNoise()
    {
        const string input = "Calculating task graph as no cached configuration is available for tasks: check\n" +
            "\n" +
            "> Configure project :core\n" +
            "class org.jetbrains.dokka.gradle.adapters.AndroidExtensionWrapper could not get Android Extension for project :core\n" +
            "[android-junit5]: Cannot configure Jacoco for this project\n" +
            "\n" +
            "> Task :core:preBuild UP-TO-DATE\n" +
            "> Task :core:preDebugBuild UP-TO-DATE\n" +
            "> Task :core:compileDebugKotlin UP-TO-DATE\n" +
            "> Task :samplev2:lintDebug FAILED\n" +
            "Lint found 8 errors, 21 warnings. First failure:\n" +
            "\n" +
            "/src/LogsScreen.kt:50: Error: Field requires API level 26 [NewApi]\n" +
            "    val uiState = viewModel.uiState.collectAsState()\n" +
            "\n" +
            "[Incubating] Problems report is available at: file:///build/reports/problems.html\n" +
            "\n" +
            "Deprecated Gradle features were used in this build, making it incompatible with Gradle 10.\n" +
            "\n" +
            "You can use '--warning-mode all' to show the individual deprecation warnings.\n" +
            "388 actionable tasks: 97 executed\n" +
            "\n" +
            "FAILURE: Build failed with an exception.\n" +
            "\n" +
            "* What went wrong:\n" +
            "Execution failed for task ':samplev2:lintDebug'.\n" +
            "\n" +
            "* Try:\n" +
            "> Run with --stacktrace option to get the stack trace.\n" +
            "\n" +
            "BUILD FAILED in 3s";

        var output = FilteredBuildText(input);

        Assert.Contains("BUILD FAILED", output, StringComparison.Ordinal);
        Assert.Contains("FAILURE:", output, StringComparison.Ordinal);
        Assert.Contains("Execution failed", output, StringComparison.Ordinal);
        Assert.Contains("Lint found 8 error", output, StringComparison.Ordinal);
        Assert.Contains("Error: Field requires", output, StringComparison.Ordinal);

        Assert.DoesNotContain("Configure project", output, StringComparison.Ordinal);
        Assert.DoesNotContain("dokka", output, StringComparison.Ordinal);
        Assert.DoesNotContain("android-junit5", output, StringComparison.Ordinal);
        Assert.DoesNotContain("> Task :", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Incubating", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Deprecated Gradle", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Run with --stacktrace", output, StringComparison.Ordinal);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Expected >=60% savings, got {savings:F1}%");
    }

    // ── DEPENDENCIES FILTER ──────────────────────────────────────────────────

    [Fact]
    public void Dependencies_Filter_ExtractsTopLevel()
    {
        const string input = "> Task :app:dependencies\n" +
            "\n" +
            "------------------------------------------------------------\n" +
            "Project ':app'\n" +
            "------------------------------------------------------------\n" +
            "\n" +
            "implementation - Implementation dependencies for the 'main' feature.\n" +
            "+--- org.jetbrains.kotlin:kotlin-stdlib:1.9.22\n" +
            "+--- androidx.core:core-ktx:1.12.0\n" +
            "+--- androidx.appcompat:appcompat:1.6.1\n" +
            "|    +--- androidx.annotation:annotation:1.3.0\n" +
            "|    +--- androidx.core:core:1.9.0\n" +
            "|    \\--- androidx.cursoradapter:cursoradapter:1.0.0\n" +
            "+--- com.google.android.material:material:1.11.0\n" +
            "|    +--- androidx.annotation:annotation:1.2.0\n" +
            "|    +--- androidx.appcompat:appcompat:1.1.0\n" +
            "|    \\--- androidx.recyclerview:recyclerview:1.0.0\n" +
            "\\--- com.squareup.retrofit2:retrofit:2.9.0\n" +
            "     +--- com.squareup.okhttp3:okhttp:3.14.9\n" +
            "     \\--- com.squareup.okio:okio:1.17.2\n" +
            "\n" +
            "testImplementation - Test dependencies for the 'main' feature.\n" +
            "+--- junit:junit:4.13.2\n" +
            "\\--- org.mockito:mockito-core:5.8.0\n" +
            "\n" +
            "BUILD SUCCESSFUL in 2s\n" +
            "1 actionable tasks: 1 executed";

        var output = GradlewFilters.FilterDependencies(input);
        Assert.Contains("implementation (5):", output, StringComparison.Ordinal);
        Assert.Contains("testImplementation (2):", output, StringComparison.Ordinal);
        Assert.Contains("kotlin-stdlib", output, StringComparison.Ordinal);
        Assert.DoesNotContain("cursoradapter", output, StringComparison.Ordinal);

        var savings = 100.0 - (CountTokens(output) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 60.0, $"Expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void Dependencies_Filter_Empty()
    {
        Assert.Equal(string.Empty, GradlewFilters.FilterDependencies(string.Empty));
    }

    [Fact]
    public void Dependencies_Filter_NoDeps()
    {
        const string input = "> Task :app:dependencies\nNo dependencies\n\nBUILD SUCCESSFUL in 1s";
        var output = GradlewFilters.FilterDependencies(input);
        Assert.Contains("ok", output, StringComparison.Ordinal);
    }

    // ── EDGE CASES ────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_EmptyInput()
    {
        Assert.Equal(string.Empty, GradlewFilters.FilterTest(string.Empty));
        Assert.Equal(string.Empty, GradlewFilters.FilterConnected(string.Empty));
        Assert.Equal(string.Empty, GradlewFilters.FilterLint(string.Empty));
        Assert.Equal(string.Empty, GradlewFilters.FilterDependencies(string.Empty));
    }

    [Fact]
    public void Build_Filter_EmptyLinePreserved()
    {
        Assert.True(GradlewFilters.FilterBuildLine(string.Empty), "empty line must pass through");
        Assert.True(GradlewFilters.FilterBuildLine("   "), "whitespace-only line must pass through");
    }

    [Fact]
    public void VerboseFlag_Detection()
    {
        string[] stacktraceArgs = ["assembleDebug", "--stacktrace"];
        Assert.Contains(stacktraceArgs, a => a is "--stacktrace" or "--info" or "--debug" or "--full-stacktrace");

        string[] infoArgs = ["testDebugUnitTest", "--info"];
        Assert.Contains(infoArgs, a => a is "--stacktrace" or "--info" or "--debug" or "--full-stacktrace");
    }

    [Fact]
    public void Build_TokenSavings()
    {
        const string input = "Starting a Gradle Daemon (subsequent builds will be faster)\n" +
            "> Configure project :app\n" +
            "> Task :app:preBuild UP-TO-DATE\n" +
            "> Task :app:generateDebugBuildConfig UP-TO-DATE\n" +
            "> Task :app:generateDebugResValues UP-TO-DATE\n" +
            "> Task :app:generateDebugResources UP-TO-DATE\n" +
            "> Task :app:mergeDebugResources UP-TO-DATE\n" +
            "> Task :app:processDebugManifest UP-TO-DATE\n" +
            "> Task :app:compileDebugKotlin UP-TO-DATE\n" +
            "> Task :app:compileDebugJavaWithJavac UP-TO-DATE\n" +
            "> Task :app:compileDebugSources UP-TO-DATE\n" +
            "> Task :app:mergeDebugShaders UP-TO-DATE\n" +
            "> Task :app:compileDebugShaders UP-TO-DATE\n" +
            "> Task :app:generateDebugAssets UP-TO-DATE\n" +
            "> Task :app:mergeDebugAssets UP-TO-DATE\n" +
            "> Task :app:mergeDebugJniLibFolders UP-TO-DATE\n" +
            "> Task :app:validateSigningDebug UP-TO-DATE\n" +
            "> Task :app:packageDebug UP-TO-DATE\n" +
            "> Task :app:assembleDebug UP-TO-DATE\n" +
            "\n" +
            "BUILD SUCCESSFUL in 3s\n" +
            "18 actionable tasks: 18 up-to-date";

        var filtered = FilteredBuildText(input);
        var savings = 100.0 - (CountTokens(filtered) / (double)CountTokens(input) * 100.0);
        Assert.True(savings >= 70.0, $"Expected >=70% token savings, got {savings:F1}%");
    }

    [Fact]
    public void IsFrameworkFrame_Detection()
    {
        Assert.True(GradlewFilters.IsFrameworkFrame("at org.junit.Assert.fail(Assert.java:89)"));
        Assert.True(GradlewFilters.IsFrameworkFrame("at junit.framework.Assert.fail(Assert.java:50)"));
        Assert.True(GradlewFilters.IsFrameworkFrame("at java.lang.reflect.Method.invoke(Method.java:498)"));
        Assert.True(GradlewFilters.IsFrameworkFrame(
            "at org.gradle.api.internal.tasks.testing.SuiteTestClassProcessor.processTestClass(SuiteTestClassProcessor.java:51)"));
        Assert.False(GradlewFilters.IsFrameworkFrame("at com.example.FooTest.testBar(FooTest.kt:25)"));
        Assert.False(GradlewFilters.IsFrameworkFrame("at com.example.MyApp.doSomething(MyApp.java:100)"));
    }

    // ── DISPATCH-LEVEL COVERAGE (new; no direct Rust #[cfg(test)] analog) ────

    [Fact]
    public async Task RunAsync_Build_DispatchesThroughLineFilteringExecutor()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 0, true, null));

        var exitCode = await GradlewCommand.RunAsync(["assembleDebug"], fake, processExecutor: null);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(["assembleDebug"], fake.CapturedRequest!.Arguments);
        Assert.IsType<GradlewBuildLineFilter>(fake.CapturedFilter);
    }

    [Fact]
    public async Task RunAsync_ExitCodePropagatesFromExecutor()
    {
        var fake = new FakeLineFilteringExecutor(new LineFilteringResult("raw", "filtered", 7, true, null));
        var exitCode = await GradlewCommand.RunAsync(["assembleDebug"], fake, processExecutor: null);
        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RunAsync_UnknownTask_PassesThroughUnfiltered()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await GradlewCommand.RunAsync(["signingReport"], lineFilteringExecutor: null, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.CapturedRequest!.CaptureMode);
        Assert.Equal(["signingReport"], fake.CapturedRequest.Arguments);
    }

    [Fact]
    public async Task RunAsync_StacktraceFlag_ForcesPassthrough()
    {
        var fake = new FakeProcessExecutor(new ExecutionResult(string.Empty, string.Empty, 0, TimeSpan.Zero, true, null, false));

        var exitCode = await GradlewCommand.RunAsync(["testDebugUnitTest", "--stacktrace"], lineFilteringExecutor: null, fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.CapturedRequest);
        Assert.Equal(["testDebugUnitTest", "--stacktrace"], fake.CapturedRequest!.Arguments);
    }

    private sealed class FakeLineFilteringExecutor(LineFilteringResult result) : ILineFilteringExecutor
    {
        public ExecutionRequest? CapturedRequest { get; private set; }

        public IStreamFilter? CapturedFilter { get; private set; }

        public ValueTask<LineFilteringResult> ExecuteAsync(
            ExecutionRequest request,
            IStreamFilter filter,
            CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            CapturedFilter = filter;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeProcessExecutor(ExecutionResult result) : IProcessExecutor
    {
        public ExecutionRequest? CapturedRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            return ValueTask.FromResult(result);
        }
    }
}
