using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RtkSharp.Commands.System;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="RunCommand"/>, the <c>rtk run</c> transparent shell-spawn wrapper. Rust's
/// <c>Commands::Run</c> arm (<c>main.rs</c>:2312-2331) has no dedicated <c>#[cfg(test)]</c> module of
/// its own (it is inline in <c>main</c>'s dispatch, not a <c>cmds::</c> filter), so this suite is
/// original coverage written directly against the documented Rust behavior: <c>-c</c>/positional-args
/// precedence, the empty-command no-spawn short-circuit, exit-code passthrough, spawn-failure error
/// surfacing, and the "no tracking whatsoever" contract.
/// </summary>
public sealed class RunCommandTests
{
    // -----------------------------------------------------------------------
    // -c / positional-args resolution (main.rs:2313-2317 precedence)
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveRawCommand_DashCFlag_ReturnsItsValue()
    {
        var raw = RunCommand.ResolveRawCommand(["-c", "echo hi"]);
        Assert.Equal("echo hi", raw);
    }

    [Fact]
    public void ResolveRawCommand_LongCommandFlag_ReturnsItsValue()
    {
        var raw = RunCommand.ResolveRawCommand(["--command", "echo hi"]);
        Assert.Equal("echo hi", raw);
    }

    [Fact]
    public void ResolveRawCommand_PositionalArgs_JoinedWithSpaces()
    {
        var raw = RunCommand.ResolveRawCommand(["echo", "hi", "there"]);
        Assert.Equal("echo hi there", raw);
    }

    [Fact]
    public void ResolveRawCommand_NoFlagsNoArgs_ReturnsEmptyString()
    {
        var raw = RunCommand.ResolveRawCommand([]);
        Assert.Equal(string.Empty, raw);
    }

    [Fact]
    public void ResolveRawCommand_BothDashCAndPositionalArgsPresent_DashCWins()
    {
        // Mirrors Rust's `match command { Some(c) => c, None if !args.is_empty() => ..., None => ... }`:
        // `command` (populated by clap from -c/--command) is checked first via `Some`, so whenever it
        // is present the positional `args` vector is never consulted at all, regardless of order or
        // whether args is also non-empty.
        var raw = RunCommand.ResolveRawCommand(["-c", "echo from-c", "echo", "from-positional"]);
        Assert.Equal("echo from-c", raw);
    }

    // -----------------------------------------------------------------------
    // Empty-command no-op: exit 0, nothing spawned (main.rs:2318-2320)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_EmptyCommand_ReturnsZero_NeverSpawns()
    {
        var fake = new FakeExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await RunCommand.RunAsync([], fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task RunAsync_WhitespaceOnlyDashC_ReturnsZero_NeverSpawns()
    {
        var fake = new FakeExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));

        var exitCode = await RunCommand.RunAsync(["-c", "   "], fake);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, fake.Calls);
    }

    // -----------------------------------------------------------------------
    // Spawn-failure error surfacing (fake executor: WasStarted = false)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_SpawnFailure_PrintsRtkPrefixedError_ReturnsOne()
    {
        var fake = new FakeExecutor(
            new ExecutionResult("", "", 127, TimeSpan.Zero, false, "The system cannot find the file specified", false));

        using var console = new ConsoleErrorCapture();
        var exitCode = await RunCommand.RunAsync(["-c", "does-not-matter"], fake);

        Assert.Equal(1, exitCode);
        var text = console.ToString();
        Assert.StartsWith("rtk: Failed to execute: does-not-matter", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SpawnFailure_RequestTargetsCmdWithSlashCFlag()
    {
        // Verifies the shell/flag selection Rust's `if cfg!(windows) { "cmd" } else { "sh" }` /
        // `if cfg!(windows) { "/C" } else { "-c" }` maps to on this (Windows) test host.
        var fake = new FakeExecutor(new ExecutionResult("", "", 0, TimeSpan.Zero, true, null, false));
        _ = await RunCommand.RunAsync(["-c", "echo hi"], fake);

        Assert.Equal(1, fake.Calls);
        Assert.Equal("cmd", fake.LastRequest!.FileName);
        Assert.Equal(["/C", "echo hi"], fake.LastRequest.Arguments);
        Assert.Equal(ExecutionCaptureMode.Inherit, fake.LastRequest.CaptureMode);
    }

    // -----------------------------------------------------------------------
    // Real spawn via cmd /C: -c form, positional-args form, exit-code passthrough
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_RealSpawn_DashCForm_SuccessExitsZero()
    {
        var exitCode = await RunCommand.RunAsync(["-c", "exit 0"], executor: null);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_RealSpawn_PositionalArgsForm_SuccessExitsZero()
    {
        // "exit" "0" joined with a space reproduces the same raw command as the -c form above.
        var exitCode = await RunCommand.RunAsync(["exit", "0"], executor: null);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_RealSpawn_NonZeroExitCode_PropagatesExactly()
    {
        var exitCode = await RunCommand.RunAsync(["-c", "exit 3"], executor: null);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task RunAsync_RealSpawn_PositionalArgsForm_NonZeroExitCode_PropagatesExactly()
    {
        var exitCode = await RunCommand.RunAsync(["exit", "3"], executor: null);
        Assert.Equal(3, exitCode);
    }

    // -----------------------------------------------------------------------
    // No tracking side-effects: RTK_DB_PATH set, no db file ever created
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_RealSpawn_NeverCreatesTrackingDatabase()
    {
        using var guard = new EnvVarScope();
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Root, "history.db");
        Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, dbPath);

        var exitCode = await RunCommand.RunAsync(["-c", "exit 0"], executor: null);

        Assert.Equal(0, exitCode);

        // Tracker only ever creates/opens the sqlite file on construction (schema creation happens
        // in the constructor) - its total absence after a full rtk-run round trip is direct evidence
        // no TimedExecution/Tracker call occurred anywhere in RunCommand, matching Rust's "raw, no
        // filtering or tracking" doc comment literally.
        Assert.False(File.Exists(dbPath), "rtk run must never construct a Tracker or write a tracking database.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed class FakeExecutor(ExecutionResult result) : IProcessExecutor
    {
        public int Calls { get; private set; }

        public ExecutionRequest? LastRequest { get; private set; }

        public ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Captures <see cref="Console.Error"/> output for the lifetime of the instance.</summary>
    private sealed class ConsoleErrorCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Error;
        private readonly StringWriter _capture = new();

        public ConsoleErrorCapture() => Console.SetError(_capture);

        public override string ToString() => _capture.ToString();

        public void Dispose() => Console.SetError(_original);
    }

    /// <summary>Saves and restores <c>RTK_DB_PATH</c> for the lifetime of the instance.</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);

        public void Dispose() => Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, _previous);
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtksharp-run-cmd-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
