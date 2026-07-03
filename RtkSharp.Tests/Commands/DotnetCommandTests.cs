using RtkSharp.Commands.Dotnet;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="DotnetCommand"/>'s build/restore text filters. The filters are pure
/// functions of raw <c>dotnet</c> output, so they are exercised directly against the fixtures
/// captured under <c>tests/fixtures/dotnet/</c> and <c>tests/parity/fixtures/dotnet-workflow/</c>.
/// Expectations are derived from the Rust filter logic in <c>src/cmds/dotnet/dotnet_cmd.rs</c> +
/// <c>binlog.rs</c> (text path) and validated against the <c>rtk.exe</c> oracle, whose only
/// nondeterministic component is the trailing duration token.
/// </summary>
public sealed class DotnetCommandTests
{
    // tests/parity/fixtures/dotnet-workflow/dotnet_build_success_raw.txt
    private const string BuildSuccessRaw =
        "  Determining projects to restore...\n" +
        "  All projects are up-to-date for restore.\n" +
        "  RtkSharp -> C:\\Development\\rtksharp\\RtkSharp\\bin\\Debug\\net10.0\\RtkSharp.dll\n" +
        "C:\\Development\\rtksharp\\RtkSharp.Tests\\Rewrite\\ShellLexerTests.cs(34,9): warning xUnit2012: Do not use Assert.False() to check if a value exists in a collection. Use Assert.DoesNotContain instead. (https://xunit.net/xunit.analyzers/rules/xUnit2012) [C:\\Development\\rtksharp\\RtkSharp.Tests\\RtkSharp.Tests.csproj]\n" +
        "  RtkSharp.Tests -> C:\\Development\\rtksharp\\RtkSharp.Tests\\bin\\Debug\\net10.0\\RtkSharp.Tests.dll\n" +
        "\n" +
        "Build succeeded.\n" +
        "\n" +
        "C:\\Development\\rtksharp\\RtkSharp.Tests\\Rewrite\\ShellLexerTests.cs(34,9): warning xUnit2012: Do not use Assert.False() to check if a value exists in a collection. Use Assert.DoesNotContain instead. (https://xunit.net/xunit.analyzers/rules/xUnit2012) [C:\\Development\\rtksharp\\RtkSharp.Tests\\RtkSharp.Tests.csproj]\n" +
        "    1 Warning(s)\n" +
        "    0 Error(s)\n" +
        "\n" +
        "Time Elapsed 00:01:45.81\n";

    // tests/fixtures/dotnet/build_failed.txt
    private const string BuildFailedRaw =
        "  Determining projects to restore...\n" +
        "  All projects are up-to-date for restore.\n" +
        "/private/tmp/RtkDotnetSmoke/Broken.cs(7,17): error CS1525: Invalid expression term ';' [/private/tmp/RtkDotnetSmoke/RtkDotnetSmoke.csproj]\n" +
        "\n" +
        "Build FAILED.\n" +
        "\n" +
        "/private/tmp/RtkDotnetSmoke/Broken.cs(7,17): error CS1525: Invalid expression term ';' [/private/tmp/RtkDotnetSmoke/RtkDotnetSmoke.csproj]\n" +
        "    0 Warning(s)\n" +
        "    1 Error(s)\n" +
        "\n" +
        "Time Elapsed 00:00:00.76\n";

    // tests/parity/fixtures/dotnet-workflow/dotnet_restore_raw.txt
    private const string RestoreRaw =
        "  Determining projects to restore...\n" +
        "  All projects are up-to-date for restore.\n";

    // ---- FilterBuild ----

    [Fact]
    public void FilterBuild_Success_SummarizesProjectsAndWarnings()
    {
        var output = DotnetCommand.FilterBuild(BuildSuccessRaw, commandSuccess: true);

        Assert.Equal(
            "Warnings:\n" +
            "  C:\\Development\\rtksharp\\RtkSharp.Tests\\Rewrite\\ShellLexerTests.cs(34,9) warning xUnit2012: Do not use Assert.False() to check if a value exists in a collection. Use Assert.DoesNotContain instead. (https://xunit.net/xunit.analyzers/rules/xUnit2012) [C:\\Development\\rtks...\n" +
            "\n" +
            "ok dotnet build: 1 projects, 0 errors, 1 warnings (00:01:45.81)",
            output);
    }

    [Fact]
    public void FilterBuild_Failure_SummarizesErrorAndVerdict()
    {
        var output = DotnetCommand.FilterBuild(BuildFailedRaw, commandSuccess: false);

        Assert.Equal(
            "Errors:\n" +
            "  /private/tmp/RtkDotnetSmoke/Broken.cs(7,17) error CS1525: Invalid expression term ';' [/private/tmp/RtkDotnetSmoke/RtkDotnetSmoke.csproj]\n" +
            "\n" +
            "fail dotnet build: 1 projects, 1 errors, 0 warnings (00:00:00.76)",
            output);
    }

    [Fact]
    public void FilterBuild_WindowsDriveLetterPath_ExtractsRealDetailsNotPlaceholder()
    {
        // Regression test for the drive-letter IssueRegex widening: a Windows-absolute path
        // diagnostic must be captured whole (through the drive-letter colon) and produce real
        // file/line/col/code/message details, not the "details omitted" placeholder that Rust's
        // ISSUE_RE (binlog.rs:56) falls back to for this same shape of line.
        const string raw =
            "  Determining projects to restore...\n" +
            "C:\\src\\RtkDotnetSmoke\\Program.cs(1,40): error CS1525: Invalid expression term ';' [C:\\src\\RtkDotnetSmoke\\RtkDotnetSmoke.csproj]\n" +
            "\n" +
            "Build FAILED.\n" +
            "\n" +
            "C:\\src\\RtkDotnetSmoke\\Program.cs(1,40): error CS1525: Invalid expression term ';' [C:\\src\\RtkDotnetSmoke\\RtkDotnetSmoke.csproj]\n" +
            "    0 Warning(s)\n" +
            "    1 Error(s)\n" +
            "\n" +
            "Time Elapsed 00:00:00.50\n";

        var output = DotnetCommand.FilterBuild(raw, commandSuccess: false);

        Assert.Equal(
            "Errors:\n" +
            "  C:\\src\\RtkDotnetSmoke\\Program.cs(1,40) error CS1525: Invalid expression term ';' [C:\\src\\RtkDotnetSmoke\\RtkDotnetSmoke.csproj]\n" +
            "\n" +
            "fail dotnet build: 1 projects, 1 errors, 0 warnings (00:00:00.50)",
            output);
        Assert.DoesNotContain("details omitted", output);
    }

    [Fact]
    public void ParseBuildFromText_WindowsDriveLetterPath_ExtractsRealDetails()
    {
        const string raw =
            "C:\\src\\Program.cs(1,40): error CS1525: Invalid expression term ';' [C:\\src\\App.csproj]\n" +
            "Build FAILED.\n" +
            "    0 Warning(s)\n" +
            "    1 Error(s)\n" +
            "Time Elapsed 00:00:01.00\n";

        var summary = DotnetCommand.ParseBuildFromText(raw);

        Assert.Single(summary.Errors);
        var issue = summary.Errors[0];
        Assert.Equal("C:\\src\\Program.cs", issue.File);
        Assert.Equal(1, issue.Line);
        Assert.Equal(40, issue.Column);
        Assert.Equal("CS1525", issue.Code);
        Assert.DoesNotContain("details omitted", issue.Message);
    }

    [Fact]
    public void FilterBuild_Empty_ReportsSingleProjectOnSuccess()
    {
        // command_success normalization forces at least one project even with no text signal.
        var output = DotnetCommand.FilterBuild("", commandSuccess: true);

        Assert.Equal("ok dotnet build: 1 projects, 0 errors, 0 warnings (unknown)", output);
    }

    // ---- FilterRestore ----

    [Fact]
    public void FilterRestore_UpToDate_ReportsZeroProjects()
    {
        var output = DotnetCommand.FilterRestore(RestoreRaw, commandSuccess: true);

        Assert.Equal("ok dotnet restore: 0 projects, 0 errors, 0 warnings (unknown)", output);
    }

    [Fact]
    public void FilterRestore_Failure_ForcesErrorVerdict()
    {
        // With no parsed diagnostics but a non-zero exit, normalization surfaces one error.
        var output = DotnetCommand.FilterRestore(RestoreRaw, commandSuccess: false);

        Assert.Equal("fail dotnet restore: 0 projects, 1 errors, 0 warnings (unknown)", output);
    }

    // ---- ParseBuildFromText ----

    [Fact]
    public void ParseBuildFromText_Success_ExtractsCountsAndDuration()
    {
        var summary = DotnetCommand.ParseBuildFromText(BuildSuccessRaw);

        Assert.True(summary.Succeeded);
        Assert.Equal(1, summary.ProjectCount);
        Assert.Empty(summary.Errors);
        Assert.Single(summary.Warnings);
        Assert.Equal("00:01:45.81", summary.DurationText);
    }

    [Fact]
    public void ParseBuildFromText_Failure_ExtractsDiagnostic()
    {
        var summary = DotnetCommand.ParseBuildFromText(BuildFailedRaw);

        Assert.False(summary.Succeeded);
        Assert.Single(summary.Errors);
        Assert.Equal("CS1525", summary.Errors[0].Code);
        Assert.Equal(7, summary.Errors[0].Line);
        Assert.Equal(17, summary.Errors[0].Column);
        Assert.Empty(summary.Warnings);
        Assert.Equal("00:00:00.76", summary.DurationText);
    }

    // ---- ParseRestoreFromText ----

    [Fact]
    public void ParseRestoreFromText_UpToDate_HasNoIssues()
    {
        var summary = DotnetCommand.ParseRestoreFromText(RestoreRaw);

        Assert.Equal(0, summary.RestoredProjects);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(0, summary.Warnings);
        Assert.Null(summary.DurationText);
    }

    // ---- BuildEffectiveArgs ----

    [Fact]
    public void BuildEffectiveArgs_InjectsVerbosityAndNoLogo()
    {
        var effective = DotnetCommand.BuildEffectiveArgs("build", new[] { "RtkSharp/RtkSharp.csproj" });

        Assert.Equal(new[] { "-v:minimal", "-nologo", "RtkSharp/RtkSharp.csproj" }, effective);
    }

    [Fact]
    public void BuildEffectiveArgs_RespectsUserVerbosityAndNoLogo()
    {
        var effective = DotnetCommand.BuildEffectiveArgs("build", new[] { "-v:detailed", "-nologo", "proj.csproj" });

        Assert.Equal(new[] { "-v:detailed", "-nologo", "proj.csproj" }, effective);
    }

    [Fact]
    public void BuildEffectiveArgs_DoesNotInjectBinlog()
    {
        // Binlog is deferred: no -bl flag should ever be injected.
        var effective = DotnetCommand.BuildEffectiveArgs("build", Array.Empty<string>());

        Assert.DoesNotContain(effective, arg => arg.StartsWith("-bl", StringComparison.OrdinalIgnoreCase));
    }

    // ---- ScrubSensitiveEnvVars ----

    [Fact]
    public void ScrubSensitiveEnvVars_RedactsTokenValues()
    {
        var scrubbed = DotnetCommand.ScrubSensitiveEnvVars("GITHUB_TOKEN=ghp_secret123 rest");

        Assert.Equal("GITHUB_TOKEN=[REDACTED] rest", scrubbed);
    }
}
