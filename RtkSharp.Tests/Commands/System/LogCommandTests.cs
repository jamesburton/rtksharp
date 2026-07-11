using System;
using System.IO;
using RtkSharp.Commands.System;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Covers <see cref="LogCommand"/>, a faithful, test-for-test port of Rust
/// <c>src/cmds/system/log_cmd.rs</c>'s own <c>#[cfg(test)] mod tests</c>. <c>AnalyzeLogs</c>/
/// <c>NormalizeLogLine</c> coverage moved to
/// <c>RtkSharp.Filters.Tests.Commands.System.LogFiltersTests</c> alongside those methods'
/// relocation to <c>RtkSharp.Filters.Commands.System.LogFilters</c>.
/// </summary>
public sealed class LogCommandTests
{
    [Fact]
    public void RunFile_ReadsAndAnalyzesContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtk-log-test-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "2024-01-01 10:00:00 ERROR: boom\n");
        try
        {
            var stdout = new StringWriter { NewLine = "\n" };

            var exit = LogCommand.RunFile(path, verbose: 0, stdout);

            Assert.Equal(0, exit);
            Assert.Contains("ERRORS", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunStdin_ReadsAllLinesAndAnalyzes()
    {
        var stdin = new StringReader("2024-01-01 10:00:00 ERROR: boom\n2024-01-01 10:00:01 WARN: careful\n");
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = LogCommand.RunStdin(stdin, stdout);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("ERRORS", output, StringComparison.Ordinal);
        Assert.Contains("WARNINGS", output, StringComparison.Ordinal);
    }
}
