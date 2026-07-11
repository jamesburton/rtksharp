using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RtkSharp;
using RtkSharp.Cli;
using RtkSharp.Commands.System;
using RtkSharp.Core.Tracking;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="EnvCommand"/>, the <c>rtk env</c> categorized/masked/truncated environment
/// variable display. The pure masking/categorization helpers moved to
/// <c>RtkSharp.Filters.Commands.System.EnvFilters</c> - see
/// <c>RtkSharp.Filters.Tests.Commands.System.EnvFiltersTests</c>. Faithful-port target: Rust
/// <c>src/cmds/system/env_cmd.rs</c>, covering argument parsing and the full
/// <see cref="EnvCommand.Run"/> flow (now delegating to
/// <c>RtkSharp.Filters.Commands.System.EnvFilters.FormatEnvReport</c>).
/// </summary>
public sealed class EnvCommandTests
{
    // -----------------------------------------------------------------------
    // ParseArgs
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseArgs_DashFFlag_SetsFilter()
    {
        var (filter, showAll) = EnvCommand.ParseArgs(["-f", "CARGO"]);
        Assert.Equal("CARGO", filter);
        Assert.False(showAll);
    }

    [Fact]
    public void ParseArgs_LongFilterEquals_SetsFilter()
    {
        var (filter, _) = EnvCommand.ParseArgs(["--filter=CARGO"]);
        Assert.Equal("CARGO", filter);
    }

    [Fact]
    public void ParseArgs_ShowAllFlag_SetsShowAll()
    {
        var (filter, showAll) = EnvCommand.ParseArgs(["--show-all"]);
        Assert.Null(filter);
        Assert.True(showAll);
    }

    // Regression tests for a real, pre-existing divergence caught during independent review of
    // the broader clap-fallback-exec fix: Rust's Env { filter: Option<String>, show_all: bool }
    // clap struct (main.rs:247-254) has no positional field and no trailing_var_arg, so an
    // unrecognized flag, a stray positional, or a missing --filter value all fail at the clap
    // layer — an earlier version of ParseArgs silently ignored any such token instead of
    // rejecting it, which (since env is Rust-classified PASSTHROUGH, not RTK_META_COMMANDS) meant
    // RtkSharp printed filtered env output with exit 0 where the oracle falls back to running the
    // real `env` binary (its own "unknown option"/"No such file or directory" errors, exit
    // 125/127 — verified directly against target/release/rtk.exe).
    [Fact]
    public void ParseArgs_UnrecognizedFlag_ThrowsCommandArgumentParseException() =>
        Assert.Throws<CommandArgumentParseException>(() => EnvCommand.ParseArgs(["--badflag"]));

    [Fact]
    public void ParseArgs_StrayPositional_ThrowsCommandArgumentParseException() =>
        Assert.Throws<CommandArgumentParseException>(() => EnvCommand.ParseArgs(["somepositional"]));

    [Fact]
    public void ParseArgs_FilterFlagMissingValue_ThrowsCommandArgumentParseException() =>
        Assert.Throws<CommandArgumentParseException>(() => EnvCommand.ParseArgs(["-f"]));

    [Fact]
    public async Task RunAsync_UnrecognizedFlag_ViaRtkProgram_FallsBackToRealEnvBinary()
    {
        // End-to-end: confirms the fallback genuinely re-execs the real `env` binary rather than
        // RtkSharp silently printing its own filtered output — this is the actual fix.
        var exit = await RtkProgram.RunAsync(["env", "--badflag"]);
        Assert.NotEqual(0, exit);
    }

    // -----------------------------------------------------------------------
    // Run - full flow, integration-style, against an injectable env var dictionary
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_BucketCategorization_IsPriorityOrdered_AndMutuallyExclusive()
    {
        // CARGO_HOME contains no "PATH" substring, so it should land in lang, not path/other. A key
        // that would match BOTH a lang pattern and a cloud pattern (if such existed) should only ever
        // appear in the first bucket checked - here we prove each seeded var lands in exactly one
        // bucket by checking the rendered sections.
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CARGO_HOME"] = "/home/user/.cargo",
            ["AWS_REGION"] = "us-east-1",
            ["EDITOR"] = "vim",
            ["HOME"] = "/home/user",
        };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("Language/Runtime:", output, StringComparison.Ordinal);
        Assert.Contains("  CARGO_HOME=/home/user/.cargo", output, StringComparison.Ordinal);
        Assert.Contains("Cloud/Services:", output, StringComparison.Ordinal);
        Assert.Contains("  AWS_REGION=us-east-1", output, StringComparison.Ordinal);
        Assert.Contains("Tools:", output, StringComparison.Ordinal);
        Assert.Contains("  EDITOR=vim", output, StringComparison.Ordinal);
        Assert.Contains("Other:", output, StringComparison.Ordinal);
        Assert.Contains("  HOME=/home/user", output, StringComparison.Ordinal);

        // Mutual exclusivity: CARGO_HOME's line must appear exactly once across the whole output.
        Assert.Single(SplitOccurrences(output, "CARGO_HOME=/home/user/.cargo"));
    }

    [Fact]
    public void Run_PathBucket_TakesPriorityOverLangVar()
    {
        // GOPATH contains both "PATH" (path bucket, checked first) and would otherwise match
        // is_lang_var's "GO"/"GOPATH" pattern (checked after path) - PATH-containment wins because
        // it's checked FIRST in the strict priority order.
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["GOPATH"] = "/home/user/go" };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("PATH Variables:", output, StringComparison.Ordinal);
        Assert.Contains("  GOPATH=/home/user/go", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Language/Runtime:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ExactPathKey_SplitsOnColon_WithOverflowMarker()
    {
        var segments = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            segments.Add($"/seg{i}");
        }

        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = string.Join(':', segments) };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("  PATH (15 entries):", output, StringComparison.Ordinal);
        Assert.Contains("    /seg0", output, StringComparison.Ordinal);
        Assert.Contains("    /seg9", output, StringComparison.Ordinal);
        Assert.DoesNotContain("    /seg10", output, StringComparison.Ordinal);
        Assert.Contains("    ... +5 more", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_PathValueExceedsTruncationThreshold_SplitsOnColon_UsingTruncatedDisplayValueNotRawValue()
    {
        // 12 realistic-looking segments comfortably over the 100-byte truncation threshold - proves
        // the PATH split operates on the already-truncated 50-char-preview display value
        // (env_cmd.rs:42-54 then :70-85), NOT the raw untruncated PATH string. Unlike
        // Run_ExactPathKey_SplitsOnColon_WithOverflowMarker above (~94 bytes, under the threshold),
        // this fixture is deliberately sized to exceed it.
        var rawSegments = Enumerable.Range(0, 12)
            .Select(i => $"C:\\SomeLongDirectoryName{i}\\bin")
            .ToList();
        var rawPath = string.Join(':', rawSegments);

        Assert.True(
            Encoding.UTF8.GetByteCount(rawPath) > 100,
            "test fixture must exceed the 100-byte truncation threshold to be discriminating");

        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = rawPath };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        // Independently recompute the expected TRUNCATED display value the same way EnvCommand.Run
        // does (first 50 Unicode scalar values + "... ({N} chars)" suffix). The fixture is all-ASCII,
        // so char count and Rune count coincide here - this test isolates the truncate-before-split
        // ORDERING only; the byte-vs-rune unit distinction itself is covered by a separate test.
        var expectedTruncated = rawPath[..50] + $"... ({rawPath.Length} chars)";
        var expectedSegments = expectedTruncated.Split(':');

        // The truncated string's colon-split count must differ from the raw segment count - proving
        // the split did not operate on the original, full PATH value.
        Assert.NotEqual(rawSegments.Count, expectedSegments.Length);

        Assert.Contains($"  PATH ({expectedSegments.Length} entries):", output, StringComparison.Ordinal);
        foreach (var segment in expectedSegments.Take(10))
        {
            Assert.Contains($"    {segment}\n", output, StringComparison.Ordinal);
        }

        if (expectedSegments.Length > 10)
        {
            Assert.Contains($"    ... +{expectedSegments.Length - 10} more\n", output, StringComparison.Ordinal);
        }

        // The last RAW segment (only reachable if the split had used the untruncated value) must not
        // appear anywhere as a displayed entry.
        Assert.DoesNotContain($"    {rawSegments[^1]}\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_KeyContainsPathButIsNotExactlyPath_PrintedAsNormalLine_NotSplit()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MY_PATH_THING"] = "a:b:c",
        };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("  MY_PATH_THING=a:b:c", output, StringComparison.Ordinal);
        Assert.DoesNotContain("entries):", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SensitiveKey_IsMaskedByDefault()
    {
        // AWS_SECRET_ACCESS_KEY both matches is_cloud_var's "AWS" pattern (so it lands in the Cloud
        // bucket and is actually printed) and is sensitive (contains "secret"/"access_key"/"key").
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["AWS_SECRET_ACCESS_KEY"] = "supersecretvalue123" };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.DoesNotContain("supersecretvalue123", output, StringComparison.Ordinal);
        Assert.Contains("****", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ShowAllFlag_DisplaysSensitiveValueUnmasked()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["AWS_SECRET_ACCESS_KEY"] = "supersecretvalue123" };

        using var db = new TempTrackingDb();
        var output = CaptureRun(["--show-all"], vars, verbosity: 0);

        Assert.Contains("supersecretvalue123", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_LongValue_IsTruncatedWithCharCount()
    {
        var longValue = new string('x', 150);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = longValue };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains(new string('x', 50) + "... (150 chars)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 150), output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_LongValue_TruncationThresholdIsByteBased_ButPreviewAndCountAreRuneBased()
    {
        // 40 emoji (U+1F600, "😀"): each is 1 Unicode scalar value / 1 Rune but 4 UTF-8 bytes and 2
        // UTF-16 code units. 40 runes -> 160 UTF-8 bytes: OVER the 100-BYTE truncation threshold, even
        // though the RUNE count (40) is nowhere near 100. A naive rune-based threshold check would NOT
        // truncate this value at all - so truncation firing here proves the >100 check is byte-based,
        // not rune-based. Unlike Run_LongValue_IsTruncatedWithCharCount above (150 ASCII 'x', where
        // byte length and rune count are numerically identical and so cannot discriminate the two
        // units), this fixture makes byte count and rune count diverge across the threshold.
        const string emoji = "😀";
        var longValue = string.Concat(Enumerable.Repeat(emoji, 40));

        var byteCount = Encoding.UTF8.GetByteCount(longValue);
        var runeCount = longValue.EnumerateRunes().Count();
        Assert.Equal(160, byteCount);
        Assert.Equal(40, runeCount);
        Assert.True(byteCount > 100, "fixture must exceed the byte threshold");
        Assert.True(runeCount < 100, "fixture's RUNE count must stay well under the byte threshold value");

        // .NET string.Length (UTF-16 code units) diverges from the Rune count too, confirming each
        // emoji is a surrogate pair - so a naive substring/Length-based preview could split one in half.
        Assert.Equal(80, longValue.Length);

        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = longValue };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        // Preview is `value.chars().take(50)` (Rune-based) over only 40 runes, so the ENTIRE value is
        // retained as the preview - and the reported count is the RUNE count (40), not the byte count
        // (160). This also proves the preview is Rune-sliced (whole emoji preserved), not naively
        // byte- or UTF-16-sliced (which could mangle a surrogate pair mid-character).
        Assert.Contains($"  EDITOR={longValue}... (40 chars)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(160 chars)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ShortValue_IsNeitherMaskedNorTruncated()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = "vim" };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("  EDITOR=vim", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Filter_CaseInsensitiveSubstringMatch_OnKeyOnly()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CARGO_HOME"] = "/home/user/.cargo",
            ["EDITOR"] = "vim",
        };

        using var db = new TempTrackingDb();
        var output = CaptureRun(["-f", "cargo"], vars, verbosity: 0);

        Assert.Contains("CARGO_HOME", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EDITOR", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_FilterSet_SuppressesSummaryLine()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["CARGO_HOME"] = "/x" };

        using var db = new TempTrackingDb();
        var output = CaptureRun(["-f", "CARGO"], vars, verbosity: 0);

        Assert.DoesNotContain("Total:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_NoFilter_PrintsSummaryLine()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = "vim" };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("\nTotal: 1 vars (showing 1 relevant)\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_EmptyFilter_MatchesEveryKey()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EDITOR"] = "vim",
            ["HOME"] = "/home/user",
        };

        using var db = new TempTrackingDb();
        var output = CaptureRun(["-f", ""], vars, verbosity: 0);

        Assert.Contains("EDITOR", output, StringComparison.Ordinal);
        Assert.Contains("HOME", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ZeroMatchFilter_ExitsZero_WithNoBucketHeaders()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = "vim" };

        using var db = new TempTrackingDb();
        var originalOut = Console.Out;
        var capture = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(capture);
            exitCode = EnvCommand.Run(["-f", "zzz_no_such_match_zzz"], vars, verbosity: 0);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("PATH Variables:", capture.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Other:", capture.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Total:", capture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_VerboseFlag_WritesHeaderToStderr_NotStdout()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = "vim" };

        using var db = new TempTrackingDb();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            EnvCommand.Run([], vars, verbosity: 1);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.Contains("Environment variables:\n", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Environment variables:", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_OtherBucket_CapsAtTwenty_WithOverflowMarker()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 25; i++)
        {
            // "-f" empty-string-equivalent isn't used here; instead each key starts with "HOME" so
            // is_interesting_var's StartsWith("HOME") gate lets every one land in "Other" without a
            // filter, exercising the CAP_LIST=20 overflow path.
            vars[$"HOME_LIKE_{i:D2}"] = $"v{i}";
        }

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.Contains("  ... +5 more", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_UninterestingUnfilteredVar_IsDropped_ButStillCountedTowardTotal()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SOME_RANDOM_APP_SETTING"] = "value",
            ["EDITOR"] = "vim",
        };

        using var db = new TempTrackingDb();
        var output = CaptureRun([], vars, verbosity: 0);

        Assert.DoesNotContain("SOME_RANDOM_APP_SETTING", output, StringComparison.Ordinal);
        // total=2 (both vars counted), shown=1 (only EDITOR landed in a bucket).
        Assert.Contains("\nTotal: 2 vars (showing 1 relevant)\n", output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Tracking - the unmasked `raw` baseline diverges from the masked on-screen display
    // -----------------------------------------------------------------------

    [Fact]
    public void Run_TrackedRawBaseline_ReflectsUnmaskedValue_EvenThoughDisplayIsMasked()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["AWS_SECRET_ACCESS_KEY"] = "supersecretvalue123" };

        using var db = new TempTrackingDb();
        var rtkCmd = "rtk env";

        var output = CaptureRun([], vars, verbosity: 0);

        // On-screen: masked.
        Assert.DoesNotContain("supersecretvalue123", output, StringComparison.Ordinal);

        // Tracked: the raw baseline's token count must reflect the full unmasked
        // "AWS_SECRET_ACCESS_KEY=supersecretvalue123\n" line, not the masked display line - proving
        // the documented divergence.
        var maskedLineTokens = Tracker.EstimateTokens("AWS_SECRET_ACCESS_KEY=su****23\n");
        var unmaskedLineTokens = Tracker.EstimateTokens("AWS_SECRET_ACCESS_KEY=supersecretvalue123\n");
        Assert.NotEqual(maskedLineTokens, unmaskedLineTokens);

        var row = QueryRow(db.DbPath, rtkCmd);
        Assert.NotNull(row);
        Assert.Equal(unmaskedLineTokens, row!.Value.InputTokens);
    }

    [Fact]
    public void Run_Tracking_StillHappens_EvenWhenFilterIsSet()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["CARGO_HOME"] = "/x" };

        using var db = new TempTrackingDb();
        CaptureRun(["-f", "CARGO"], vars, verbosity: 0);

        var row = QueryRow(db.DbPath, "rtk env");
        Assert.NotNull(row);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string CaptureRun(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> vars, int verbosity)
    {
        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            var exitCode = EnvCommand.Run(args, vars, verbosity);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return capture.ToString();
    }

    private static List<int> SplitOccurrences(string haystack, string needle)
    {
        var indices = new List<int>();
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            indices.Add(idx);
            idx += needle.Length;
        }

        return indices;
    }

    private static (int InputTokens, int OutputTokens, long ExecTimeMs)? QueryRow(string dbPath, string rtkCmd)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT input_tokens, output_tokens, exec_time_ms FROM commands WHERE rtk_cmd = $rtkCmd ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$rtkCmd", rtkCmd);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
    }

    private sealed class TempTrackingDb : IDisposable
    {
        private readonly string? _previousDbPath = Environment.GetEnvironmentVariable(Tracker.DbPathEnvVar);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rtksharp-env-cmd-tests-" + Guid.NewGuid().ToString("N"));

        public string DbPath { get; }

        public TempTrackingDb()
        {
            Directory.CreateDirectory(_root);
            DbPath = Path.Combine(_root, "history.db");
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, DbPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Tracker.DbPathEnvVar, _previousDbPath);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
