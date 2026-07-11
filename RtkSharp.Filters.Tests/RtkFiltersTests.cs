using RtkSharp.Filters;

namespace RtkSharp.Filters.Tests;

public class RtkFiltersTests
{
    [Theory]
    [InlineData("git")]
    [InlineData("npm")]
    [InlineData("ls")]
    [InlineData("aws")]
    public void IsRegistered_ReturnsTrue_ForKnownFilters(string command)
    {
        Assert.True(RtkFilters.IsRegistered(command));
    }

    [Theory]
    [InlineData("notacommand")]
    [InlineData("")]
    [InlineData("err")]   // explicitly excluded per the design's non-goals
    [InlineData("run")]   // no filtering by design
    [InlineData("proxy")] // no filtering by design
    public void IsRegistered_ReturnsFalse_ForUnknownOrExcludedCommands(string command)
    {
        Assert.False(RtkFilters.IsRegistered(command));
    }

    [Fact]
    public void Filter_ThrowsInvalidOperationException_ForUnregisteredCommand()
    {
        Assert.Throws<InvalidOperationException>(
            () => RtkFilters.Filter("notacommand", [], "", "", 0));
    }

    [Fact]
    public void Filter_DispatchesToRegisteredFilter()
    {
        var filtered = RtkFilters.Filter("git", ["log"], "commit abc123\nAuthor: Test\n\n    msg\n", "", 0);

        Assert.Contains("abc123", filtered);
    }

    [Fact]
    public void Filter_Prisma_MigrateDev_ReachesMigrateDevFilter_NotGenerate()
    {
        var filtered = RtkFilters.Filter(
            "prisma",
            ["migrate", "dev"],
            "Applied migration `20240101120000_init`\n",
            "",
            0);

        // FilterMigrateDev's "Changes:" header is never emitted by FilterPrismaGenerate - this
        // confirms the migrate-dev subcommand token reached a different filter than the pre-fix
        // always-FilterPrismaGenerate default.
        Assert.Contains("Changes:", filtered);
        Assert.DoesNotContain("Prisma Client generated", filtered);
    }

    [Fact]
    public void Filter_Prisma_MigrateStatus_ReachesMigrateStatusFilter_NotGenerate()
    {
        var filtered = RtkFilters.Filter(
            "prisma",
            ["migrate", "status"],
            "3 migrations found\n1 migration applied\n2 migrations pending\n",
            "",
            0);

        Assert.Contains("applied", filtered);
        Assert.Contains("pending", filtered);
        Assert.DoesNotContain("Prisma Client generated", filtered);
    }

    [Fact]
    public void Filter_Prisma_Generate_StillReachesGenerateFilter()
    {
        var filtered = RtkFilters.Filter("prisma", ["generate"], "5 models generated\n", "", 0);

        Assert.Contains("Prisma Client generated", filtered);
    }

    [Fact]
    public void Filter_Pnpm_List_ReachesFormatPnpmList_NotFilterPnpmInstall()
    {
        const string json = """[{"name":"root","version":"1.0.0","dependencies":{"left-pad":{"version":"1.3.0"}}}]""";

        var filtered = RtkFilters.Filter("pnpm", ["list"], json, "", 0);

        Assert.Contains("left-pad", filtered);
        Assert.Contains("packages", filtered);
    }

    [Fact]
    public void Filter_Pnpm_Outdated_ReachesFormatPnpmOutdated_NotFilterPnpmInstall()
    {
        const string json = """{"left-pad":{"current":"1.0.0","latest":"1.3.0","wanted":"1.3.0","dependencyType":"dependencies"}}""";

        var filtered = RtkFilters.Filter("pnpm", ["outdated"], json, "", 0);

        Assert.Contains("left-pad", filtered);
    }

    [Fact]
    public void Filter_Pnpm_Install_StillReachesFilterPnpmInstall()
    {
        var filtered = RtkFilters.Filter("pnpm", ["install"], "+ 3 dependencies added\n", "", 0);

        Assert.Contains("dependencies", filtered);
    }

    [Fact]
    public void Filter_Wget_NonZeroExitCode_ReachesFormatWgetFailure_NotSuccessFormatter()
    {
        var filtered = RtkFilters.Filter(
            "wget",
            ["https://example.com/file.zip"],
            string.Empty,
            "ERROR 404: Not Found\n",
            exitCode: 8);

        Assert.Contains("FAILED", filtered);
        Assert.Contains("404", filtered);
    }

    [Fact]
    public void Filter_Wget_ZeroExitCode_StillReachesSuccessFormatter()
    {
        var filtered = RtkFilters.Filter(
            "wget",
            ["https://example.com/file.zip"],
            "line one\n",
            string.Empty,
            exitCode: 0);

        Assert.Contains("ok", filtered);
        Assert.DoesNotContain("FAILED", filtered);
    }
}

public class RtkFiltersTryParseSingleCommandTests
{
    [Fact]
    public void TryParseSingleCommand_SplitsSimpleCommand()
    {
        var result = RtkFilters.TryParseSingleCommand("git status", out var command, out var args);

        Assert.True(result);
        Assert.Equal("git", command);
        Assert.Equal(["status"], args);
    }

    [Fact]
    public void TryParseSingleCommand_HandlesQuotedArguments()
    {
        var result = RtkFilters.TryParseSingleCommand("git commit -m \"fix bug\"", out var command, out var args);

        Assert.True(result);
        Assert.Equal("git", command);
        Assert.Equal(["commit", "-m", "fix bug"], args);
    }

    [Theory]
    [InlineData("git status && echo done")]
    [InlineData("git status || echo failed")]
    [InlineData("git status; echo done")]
    [InlineData("git log | head -5")]
    [InlineData("git log > out.txt")]
    [InlineData("ls *.txt")] // TokenKind.Shellism (glob) — also non-single-command
    public void TryParseSingleCommand_ReturnsFalse_ForCompoundCommands(string commandLine)
    {
        var result = RtkFilters.TryParseSingleCommand(commandLine, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParseSingleCommand_ReturnsFalse_ForEmptyInput()
    {
        var result = RtkFilters.TryParseSingleCommand("", out _, out _);

        Assert.False(result);
    }
}
