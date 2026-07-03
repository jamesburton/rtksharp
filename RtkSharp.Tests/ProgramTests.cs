using RtkSharp;
using Xunit;

namespace RtkSharp.Tests;

public class ProgramTests
{
    [Fact]
    public async Task RunAsync_VersionFlag_PrintsVersionAndReturnsZero()
    {
        int exitCode = await RtkProgram.RunAsync(new[] { "--version" });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_NoArgs_PrintsHelpAndReturnsZero()
    {
        int exitCode = await RtkProgram.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_UnknownCommand_PassesThroughWithExitCode()
    {
        var isWindows = OperatingSystem.IsWindows();
        string[] args = isWindows
            ? new[] { "cmd", "/c", "exit", "3" }
            : new[] { "sh", "-c", "exit 3" };

        int exitCode = await RtkProgram.RunAsync(args);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task RunAsync_CommandNotFound_ReturnsExitCode127()
    {
        int exitCode = await RtkProgram.RunAsync(new[] { "rtksharp-definitely-not-a-real-command-xyz" });

        Assert.Equal(127, exitCode);
    }

    /// <summary>
    /// Regression test for the UTF-8 stdout fix (commit f85dffb): on Windows, .NET's default
    /// <see cref="Console.OutputEncoding"/> is the system OEM code page, which mangles non-ASCII
    /// glyphs the Rust oracle writes as raw UTF-8 bytes (e.g. the U+2502 gutter in `read -n`).
    /// This asserts the encoding directly rather than depending on the Rust oracle or spawning a
    /// child process: a <see cref="StringWriter"/>-based capture would bypass the encoding layer
    /// entirely (encoding only applies at the byte/stream boundary) and would not catch a
    /// regression here.
    /// </summary>
    [Fact]
    public void TrySetUtf8Output_SetsUtf8NoBomConsoleEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The fix targets the Windows OEM code page default; other platforms already
            // default to UTF-8 and are out of scope for this regression guard.
            return;
        }

        RtkProgram.TrySetUtf8Output();

        Assert.Equal(65001, Console.OutputEncoding.CodePage);
        Assert.Empty(Console.OutputEncoding.GetPreamble());
    }
}
