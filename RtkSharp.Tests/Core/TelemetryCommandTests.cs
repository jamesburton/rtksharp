using System;
using System.IO;
using System.Linq;
using RtkSharp.Core;
using RtkSharp.Tests.Hooks;
using Xunit;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Covers <see cref="TelemetryCommand"/>, a faithful port of Rust <c>src/core/telemetry_cmd.rs</c>
/// (plus the salt/device-hash primitives from <c>src/core/telemetry.rs</c>). Every test isolates both
/// the config directory (<see cref="GlobalScopeGuard"/>, <c>RTK_CONFIG_DIR_OVERRIDE</c>) and the
/// local-data directory (<see cref="DataDirGuard"/>, <c>RTK_DATA_DIR_OVERRIDE</c>) so none of these
/// ever touch the real user profile — see <c>TelemetryParityTests.cs</c>'s class remarks for why that
/// matters here specifically (a manual oracle sanity-check during development wrote to the real
/// <c>%APPDATA%\rtk\config.toml</c> because the *oracle* has no such override).
/// </summary>
public sealed class TelemetryCommandTests
{
    // ===================== RunStatus =====================

    [Fact]
    public void RunStatus_Defaults_PrintsNeverAskedAndNoSaltFile()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = TelemetryCommand.RunStatus(stdout);

        Assert.Equal(0, exit);
        Assert.Equal(
            "Telemetry status:\n" +
            "  consent:       never asked\n" +
            "  enabled:       no\n" +
            "  device hash:   (no salt file)\n" +
            "\n" +
            "Data controller: RTK AI Labs, contact@rtk-ai.app\n" +
            "Details: https://github.com/rtk-ai/rtk/blob/master/docs/TELEMETRY.md\n",
            stdout.ToString());
    }

    [Fact]
    public void RunStatus_ConsentGiven_ShowsYesAndConsentDate()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var config = Config.LoadOrDefault();
        config.Telemetry.ConsentGiven = true;
        config.Telemetry.Enabled = true;
        config.Telemetry.ConsentDate = "2026-01-01T00:00:00.000000+00:00";
        config.Save();

        var stdout = new StringWriter { NewLine = "\n" };
        TelemetryCommand.RunStatus(stdout);
        var output = stdout.ToString();

        Assert.Contains("  consent:       yes\n", output, StringComparison.Ordinal);
        Assert.Contains("  consent date:  2026-01-01T00:00:00.000000+00:00\n", output, StringComparison.Ordinal);
        Assert.Contains("  enabled:       yes\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunStatus_ConsentDeclined_ShowsNo()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var config = Config.LoadOrDefault();
        config.Telemetry.ConsentGiven = false;
        config.Telemetry.ConsentDate = "2026-01-01T00:00:00.000000+00:00";
        config.Save();

        var stdout = new StringWriter { NewLine = "\n" };
        TelemetryCommand.RunStatus(stdout);

        Assert.Contains("  consent:       no\n", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunStatus_WithSaltFile_ShowsTruncatedDeviceHash()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var saltPath = TelemetryCommand.SaltFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllText(saltPath, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd");

        var stdout = new StringWriter { NewLine = "\n" };
        TelemetryCommand.RunStatus(stdout);
        var output = stdout.ToString();

        var expectedHash = TelemetryCommand.GenerateDeviceHash();
        Assert.Contains($"  device hash:   {expectedHash[..8]}...{expectedHash[56..]}\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(no salt file)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunStatus_EnvOverrideSet_ShowsBlockedLine()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var previous = Environment.GetEnvironmentVariable("RTK_TELEMETRY_DISABLED");
        try
        {
            Environment.SetEnvironmentVariable("RTK_TELEMETRY_DISABLED", "1");
            var stdout = new StringWriter { NewLine = "\n" };

            TelemetryCommand.RunStatus(stdout);

            Assert.Contains("  env override:  RTK_TELEMETRY_DISABLED=1 (blocked)\n", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TELEMETRY_DISABLED", previous);
        }
    }

    [Fact]
    public void RunStatus_EnvOverrideNotOne_OmitsBlockedLine()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var previous = Environment.GetEnvironmentVariable("RTK_TELEMETRY_DISABLED");
        try
        {
            Environment.SetEnvironmentVariable("RTK_TELEMETRY_DISABLED", "true"); // not the literal "1"
            var stdout = new StringWriter { NewLine = "\n" };

            TelemetryCommand.RunStatus(stdout);

            Assert.DoesNotContain("env override", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RTK_TELEMETRY_DISABLED", previous);
        }
    }

    // ===================== RunEnable =====================

    [Fact]
    public void RunEnable_NonInteractive_BailsWithExitOne()
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        var exit = TelemetryCommand.RunEnable(new StringReader(""), stdout, stderr, isInteractive: false);

        Assert.Equal(1, exit);
        Assert.Equal("rtk: consent requires interactive terminal — cannot enable telemetry in piped mode\n", stderr.ToString());
        Assert.Equal("", stdout.ToString());
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("YES")]
    public void RunEnable_AffirmativeResponse_SavesConsentAndPrintsEnabled(string response)
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        var exit = TelemetryCommand.RunEnable(new StringReader(response + "\n"), stdout, stderr, isInteractive: true);

        Assert.Equal(0, exit);
        Assert.Equal("Telemetry enabled. Disable anytime: rtk telemetry disable\n", stdout.ToString());

        var config = Config.LoadOrDefault();
        Assert.True(config.Telemetry.ConsentGiven);
        Assert.True(config.Telemetry.Enabled);
        Assert.NotNull(config.Telemetry.ConsentDate);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("whatever")]
    public void RunEnable_NonAffirmativeResponse_SavesDeclineAndPrintsNotEnabled(string response)
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        var exit = TelemetryCommand.RunEnable(new StringReader(response + "\n"), stdout, stderr, isInteractive: true);

        Assert.Equal(0, exit);
        Assert.Equal("Telemetry not enabled.\n", stdout.ToString());

        var config = Config.LoadOrDefault();
        Assert.False(config.Telemetry.ConsentGiven);
        Assert.False(config.Telemetry.Enabled);
    }

    [Fact]
    public void RunEnable_PrintsConsentNoticeToStderr()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        TelemetryCommand.RunEnable(new StringReader("n\n"), stdout, stderr, isInteractive: true);

        Assert.Equal(
            "RTK collects anonymous usage metrics once per day to improve filters.\n" +
            "\n" +
            "  What:    command names (not arguments), token savings, OS, version\n" +
            "  Who:     RTK AI Labs, contact@rtk-ai.app\n" +
            "  Details: https://github.com/rtk-ai/rtk/blob/master/docs/TELEMETRY.md\n" +
            "\n" +
            "Enable anonymous telemetry? [y/N] ",
            stderr.ToString());
    }

    // ===================== RunDisable =====================

    [Fact]
    public void RunDisable_SavesDeclineAndPrintsMessage()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var config = Config.LoadOrDefault();
        config.Telemetry.ConsentGiven = true;
        config.Telemetry.Enabled = true;
        config.Save();

        var stdout = new StringWriter { NewLine = "\n" };
        var exit = TelemetryCommand.RunDisable(stdout);

        Assert.Equal(0, exit);
        Assert.Equal("Telemetry disabled.\n", stdout.ToString());

        var reloaded = Config.LoadOrDefault();
        Assert.False(reloaded.Telemetry.ConsentGiven);
        Assert.False(reloaded.Telemetry.Enabled);
        Assert.NotNull(reloaded.Telemetry.ConsentDate);
    }

    // ===================== RunForget =====================

    [Fact]
    public void RunForget_NothingToDelete_PrintsOnlyFinalMessage()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        var exit = TelemetryCommand.RunForget(stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal("Local telemetry data deleted. Telemetry disabled.\n", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void RunForget_SaltMarkerAndDbPresent_DeletesAllAndReportsErasureFailure()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var saltPath = TelemetryCommand.SaltFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllText(saltPath, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd");
        var expectedHash = TelemetryCommand.GenerateDeviceHash();

        var markerPath = TelemetryCommand.TelemetryMarkerPath();
        File.WriteAllText(markerPath, "marker");

        var dbPath = Path.Combine(dataGuard.DataDir, "rtk", "history.db");
        File.WriteAllText(dbPath, "fake-db");

        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        var exit = TelemetryCommand.RunForget(stdout, stderr);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(saltPath));
        Assert.False(File.Exists(markerPath));
        Assert.False(File.Exists(dbPath));

        Assert.Contains($"Local tracking database deleted: {dbPath}\n", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Local telemetry data deleted. Telemetry disabled.\n", stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal(
            "rtk: could not reach server: no telemetry endpoint configured\n" +
            "  To complete erasure, email contact@rtk-ai.app\n" +
            $"  with your device hash: {expectedHash}\n",
            stderr.ToString());

        var config = Config.LoadOrDefault();
        Assert.False(config.Telemetry.ConsentGiven);
    }

    [Fact]
    public void RunForget_NoSaltFile_NeverPrintsErasureLines()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };

        TelemetryCommand.RunForget(stdout, stderr);

        Assert.DoesNotContain("device hash", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("could not reach server", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunForget_SaltDeleteFails_ThrowsAndDoesNotSwallow()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var saltPath = TelemetryCommand.SaltFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllText(saltPath, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd");
        File.SetAttributes(saltPath, FileAttributes.ReadOnly);

        try
        {
            var stdout = new StringWriter { NewLine = "\n" };
            var stderr = new StringWriter { NewLine = "\n" };

            Assert.ThrowsAny<Exception>(() => TelemetryCommand.RunForget(stdout, stderr));
        }
        finally
        {
            File.SetAttributes(saltPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void RunForget_MarkerDeleteFails_IsSwallowed()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var markerPath = TelemetryCommand.TelemetryMarkerPath();
        File.WriteAllText(markerPath, "marker");
        File.SetAttributes(markerPath, FileAttributes.ReadOnly);

        try
        {
            var stdout = new StringWriter { NewLine = "\n" };
            var stderr = new StringWriter { NewLine = "\n" };

            // No salt file exists, so the marker-delete failure is the only thing that could throw —
            // Rust's `let _ = std::fs::remove_file(&marker_path);` swallows it unconditionally.
            var exit = TelemetryCommand.RunForget(stdout, stderr);

            Assert.Equal(0, exit);
        }
        finally
        {
            File.SetAttributes(markerPath, FileAttributes.Normal);
        }
    }

    // ===================== salt / device-hash primitives (mirrors telemetry.rs's own mod tests) =====================

    [Fact]
    public void GenerateDeviceHash_IsStableAcrossCalls()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var first = TelemetryCommand.GenerateDeviceHash();
        var second = TelemetryCommand.GenerateDeviceHash();

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void GenerateDeviceHash_IsValidLowercaseHex()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var hash = TelemetryCommand.GenerateDeviceHash();

        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public void GetOrCreateSalt_PersistsAcrossFreshCalls_WhenFileValid()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var saltPath = TelemetryCommand.SaltFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllText(saltPath, "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"[..64]);

        var salt = TelemetryCommand.GetOrCreateSalt();

        Assert.Equal(File.ReadAllText(saltPath).Trim(), salt);
    }

    [Fact]
    public void GetOrCreateSalt_InvalidExistingFile_GeneratesAndPersistsFreshOne()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var saltPath = TelemetryCommand.SaltFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(saltPath)!);
        File.WriteAllText(saltPath, "not-valid-hex-and-wrong-length");

        var salt = TelemetryCommand.GetOrCreateSalt();

        Assert.Equal(64, salt.Length);
        Assert.All(salt, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.NotEqual("not-valid-hex-and-wrong-length", File.ReadAllText(saltPath));
    }

    [Fact]
    public void SaltFilePath_IsUnderRtkDataDir()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var path = TelemetryCommand.SaltFilePath();

        Assert.Contains("rtk", path, StringComparison.Ordinal);
        Assert.EndsWith(".device_salt", path, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryMarkerPath_IsUnderRtkDataDir()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();

        var path = TelemetryCommand.TelemetryMarkerPath();

        Assert.Contains("rtk", path, StringComparison.Ordinal);
        Assert.EndsWith(".telemetry_last_ping", path, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryMarkerPath_CreatesContainingDirectoryAsSideEffect()
    {
        using var tmp = new TempDir();
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        var expectedDir = Path.Combine(dataGuard.DataDir, "rtk");
        Assert.False(Directory.Exists(expectedDir));

        TelemetryCommand.TelemetryMarkerPath();

        Assert.True(Directory.Exists(expectedDir));
    }

    // ===================== Run: argument-parsing / dispatch =====================

    [Fact]
    public void Run_NoArgs_PrintsUsageErrorAndReturnsTwo()
    {
        using var console = new ConsoleCapture();

        var exit = TelemetryCommand.Run([]);

        Assert.Equal(2, exit);
        Assert.Contains("requires a subcommand", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_UnrecognizedSubcommand_PrintsUsageErrorAndReturnsTwo()
    {
        using var console = new ConsoleCapture();

        var exit = TelemetryCommand.Run(["bogus"]);

        Assert.Equal(2, exit);
        Assert.Contains("unrecognized subcommand 'bogus'", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ExtraArgumentAfterSubcommand_PrintsUsageErrorAndReturnsTwo()
    {
        using var console = new ConsoleCapture();

        var exit = TelemetryCommand.Run(["status", "extra"]);

        Assert.Equal(2, exit);
        Assert.Contains("unexpected argument 'extra'", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Status_DispatchesToRunStatus()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        using var console = new ConsoleCapture();

        var exit = TelemetryCommand.Run(["status"]);

        Assert.Equal(0, exit);
        Assert.Contains("Telemetry status:", console.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Disable_DispatchesToRunDisable()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        using var console = new ConsoleCapture();

        var exit = TelemetryCommand.Run(["disable"]);

        Assert.Equal(0, exit);
        Assert.Equal("Telemetry disabled.\n", console.Out.ToString());
    }

    [Fact]
    public void Run_Forget_DispatchesToRunForget()
    {
        using var tmp = new TempDir();
        using var configGuard = new GlobalScopeGuard(tmp);
        using var dataGuard = new DataDirGuard(tmp);
        TelemetryCommand.ResetSaltCacheForTests();
        using var console = new ConsoleCapture();

        var exit = TelemetryCommand.Run(["forget"]);

        Assert.Equal(0, exit);
        Assert.Contains("Local telemetry data deleted.", console.Out.ToString(), StringComparison.Ordinal);
    }
}
