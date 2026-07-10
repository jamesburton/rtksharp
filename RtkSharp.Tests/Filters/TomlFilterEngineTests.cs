using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RtkSharp.Filters.Toml;
using Xunit;

namespace RtkSharp.Tests.Filters;

/// <summary>
/// Tests for <see cref="TomlFilterCompiler"/> and <see cref="TomlFilterEngine"/>: parse/compile
/// validation, the 8-stage <c>ApplyFilter</c> pipeline (one test per stage in isolation, plus full
/// end-to-end fixtures), and the 63 embedded built-in filters. Mirrors the test coverage of the Rust
/// oracle's <c>src/core/toml_filter.rs</c> <c>#[cfg(test)] mod tests</c>.
/// </summary>
public sealed class TomlFilterEngineTests
{
    private static CompiledFilter FirstFilter(string toml)
    {
        var filters = TomlFilterCompiler.ParseAndCompile(toml, "test", TextWriter.Null);
        return filters.Single();
    }

    // -----------------------------------------------------------------
    // Stage 1: strip_ansi
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_StripAnsi_RemovesEscapeCodes()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            strip_ansi = true
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "\x1b[31mError\x1b[0m\nnormal");

        Assert.Equal("Error\nnormal", output);
    }

    // -----------------------------------------------------------------
    // Stage 2: replace
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_Replace_AllOccurrencesOnEachLine()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            replace = [
              { pattern = "foo", replacement = "bar" },
            ]
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "foo baz foo\nfoo");

        Assert.Equal("bar baz bar\nbar", output);
    }

    [Fact]
    public void ApplyFilter_Replace_RulesChainSequentially()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            replace = [
              { pattern = "aaa", replacement = "bbb" },
              { pattern = "bbb", replacement = "ccc" },
            ]
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "aaa");

        Assert.Equal("ccc", output);
    }

    [Fact]
    public void ApplyFilter_Replace_SupportsBackreferences()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            replace = [
              { pattern = '(\w+):(\w+)', replacement = "$2:$1" },
            ]
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "hello:world");

        Assert.Equal("world:hello", output);
    }

    // -----------------------------------------------------------------
    // Stage 3: match_output
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_MatchOutput_ShortCircuitsOnFirstMatch()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            match_output = [
              { pattern = "Switched to branch", message = "switched" },
              { pattern = "Already on", message = "already" },
            ]
            """);

        Assert.Equal("switched", TomlFilterEngine.ApplyFilter(filter, "Switched to branch 'main'"));
        Assert.Equal("already", TomlFilterEngine.ApplyFilter(filter, "Already on 'main'"));
    }

    [Fact]
    public void ApplyFilter_MatchOutput_UnlessBlocksShortCircuitWhenErrorsPresent()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^rsync"
            match_output = [
              { pattern = "total size is", message = "ok (synced)", unless = "error|failed" },
            ]
            """);

        var output = TomlFilterEngine.ApplyFilter(
            filter,
            "rsync: [sender] error\ntotal size is 1000  speedup is 3.33\n");

        Assert.NotEqual("ok (synced)", output.Trim());
        Assert.Contains("error", output);
    }

    [Fact]
    public void ApplyFilter_MatchOutput_UnlessAllowsShortCircuitWhenNoErrors()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^rsync"
            match_output = [
              { pattern = "total size is", message = "ok (synced)", unless = "error|failed" },
            ]
            """);

        var output = TomlFilterEngine.ApplyFilter(
            filter,
            "file.txt\ntotal size is 98765  speedup is 77.31\n");

        Assert.Equal("ok (synced)", output.Trim());
    }

    [Fact]
    public void ApplyFilter_MatchOutput_NoMatch_PipelineContinues()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            match_output = [
              { pattern = "Switched to branch", message = "ok" },
            ]
            strip_lines_matching = ["^noise"]
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "noise\nkeep this");

        Assert.Equal("keep this", output);
    }

    // -----------------------------------------------------------------
    // Stage 4: strip_lines_matching / keep_lines_matching
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_StripLinesMatching_DropsMatchingLines()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            strip_lines_matching = ["^noise", "^verbose"]
            """);

        var output = TomlFilterEngine.ApplyFilter(
            filter,
            "noise line\nkeep this\nverbose stuff\nalso keep");

        Assert.Equal("keep this\nalso keep", output);
    }

    [Fact]
    public void ApplyFilter_KeepLinesMatching_KeepsOnlyMatchingLines()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            keep_lines_matching = ["^PASS", "^FAIL"]
            """);

        var output = TomlFilterEngine.ApplyFilter(
            filter,
            "PASS test_a\nsome noise\nFAIL test_b\nmore noise");

        Assert.Equal("PASS test_a\nFAIL test_b", output);
    }

    // -----------------------------------------------------------------
    // Stage 5: truncate_lines_at
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_TruncateLinesAt_IsUnicodeSafe()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            truncate_lines_at = 5
            """);

        // "hello" = 5 chars exactly, stays unchanged.
        // "日本語xyz" = 6 scalar values, truncated to take 2 + "..." = "日本...".
        var output = TomlFilterEngine.ApplyFilter(filter, "hello\n日本語xyz");

        Assert.Equal("hello\n日本...", output);
    }

    // -----------------------------------------------------------------
    // Stage 6: head_lines / tail_lines
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_HeadLines_KeepsFirstNAndInsertsOmissionMarker()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            head_lines = 2
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "a\nb\nc\nd\ne");

        Assert.StartsWith("a\nb\n", output);
        Assert.Contains("3 lines omitted", output);
    }

    [Fact]
    public void ApplyFilter_TailLines_KeepsLastNAndInsertsOmissionMarker()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            tail_lines = 2
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "a\nb\nc\nd\ne");

        Assert.Contains("3 lines omitted", output);
        Assert.EndsWith("d\ne", output);
    }

    [Fact]
    public void ApplyFilter_HeadAndTailLines_Combined()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            head_lines = 2
            tail_lines = 2
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "a\nb\nc\nd\ne\nf");

        Assert.StartsWith("a\nb\n", output);
        Assert.Contains("2 lines omitted", output);
        Assert.EndsWith("e\nf", output);
    }

    // -----------------------------------------------------------------
    // Stage 7: max_lines (applies AFTER head/tail; the omission marker itself counts)
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_MaxLines_CountsOmitMessageTowardCap()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            max_lines = 3
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "a\nb\nc\nd\ne");
        var lineCount = output.Split('\n').Length;

        // 3 content lines + 1 truncated-message line = 4 lines total.
        Assert.Equal(4, lineCount);
        Assert.Contains("lines truncated", output);
    }

    [Fact]
    public void ApplyFilter_MaxLines_AppliesAfterHeadLines_OmissionLineCountsTowardCap()
    {
        // head_lines=2 on 6 input lines produces 3 output lines: "a", "b", and the stage-6
        // "... (4 lines omitted)" marker. If max_lines=2 counted only the 2 real content lines
        // (ignoring the marker), 2 would not exceed the cap of 2 and the marker would survive
        // untouched. Because the marker DOES count toward the cap, 3 lines > max_lines=2, so stage
        // 7 truncates further: the omission marker itself gets cut and replaced by a new
        // "... (N lines truncated)" marker — proving max_lines runs after head_lines and counts the
        // stage-6 marker line.
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            head_lines = 2
            max_lines = 2
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "a\nb\nc\nd\ne\nf");
        var lines = output.Split('\n');

        Assert.Equal(3, lines.Length); // "a", "b", "... (1 lines truncated)"
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.DoesNotContain("lines omitted", output); // stage-6 marker was truncated away...
        Assert.Contains("lines truncated", output); // ...and replaced by the stage-7 marker
    }

    // -----------------------------------------------------------------
    // Stage 8: on_empty
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_OnEmpty_FiresWhenAllLinesFiltered()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            strip_lines_matching = [".*"]
            on_empty = "nothing left"
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "line1\nline2");

        Assert.Equal("nothing left", output);
    }

    [Fact]
    public void ApplyFilter_OnEmpty_NotTriggeredWhenOutputRemains()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            keep_lines_matching = ["keep"]
            on_empty = "nothing left"
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "keep this\nnoise");

        Assert.Equal("keep this", output);
    }

    // -----------------------------------------------------------------
    // Full pipeline order
    // -----------------------------------------------------------------

    [Fact]
    public void ApplyFilter_FullPipeline_StagesFireInOrder()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            strip_ansi = true
            strip_lines_matching = ["^noise"]
            truncate_lines_at = 10
            head_lines = 3
            max_lines = 4
            on_empty = "empty"
            """);

        var input = "\x1b[31mred line\x1b[0m\nnoise skip\nkeep one\nkeep two\nkeep three\nkeep four";
        var output = TomlFilterEngine.ApplyFilter(filter, input);

        Assert.Contains("red line", output);
        Assert.DoesNotContain("noise skip", output);
        Assert.True(output.Contains("lines omitted") || output.Contains("lines truncated"));
    }

    [Fact]
    public void ApplyFilter_EmptyFilter_IsPassthrough()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            """);

        const string input = "line1\nline2\nline3";

        Assert.Equal(input, TomlFilterEngine.ApplyFilter(filter, input));
    }

    [Fact]
    public void ApplyFilter_UnicodePreserved()
    {
        var filter = FirstFilter("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            strip_lines_matching = ["^noise"]
            """);

        var output = TomlFilterEngine.ApplyFilter(filter, "日本語テスト\nnoise\n中文内容");

        Assert.Equal("日本語テスト\n中文内容", output);
    }

    // -----------------------------------------------------------------
    // Compile-time validation
    // -----------------------------------------------------------------

    [Fact]
    public void ParseAndCompile_MutualExclusion_SkipsFilterAndWarns()
    {
        var warnings = new StringWriter();

        var filters = TomlFilterCompiler.ParseAndCompile("""
            schema_version = 1
            [filters.f]
            match_command = "^cmd"
            strip_lines_matching = ["a"]
            keep_lines_matching = ["b"]
            """, "test", warnings);

        Assert.Empty(filters);
        Assert.Contains("mutually exclusive", warnings.ToString());
    }

    [Fact]
    public void ParseAndCompile_InvalidMatchCommandRegex_SkipsFilter()
    {
        var filters = TomlFilterCompiler.ParseAndCompile("""
            schema_version = 1
            [filters.f]
            match_command = "["
            """, "test", TextWriter.Null);

        Assert.Empty(filters);
    }

    [Fact]
    public void ParseAndCompile_SchemaVersionMismatch_Throws()
    {
        Assert.Throws<TomlFilterParseException>(() =>
            TomlFilterCompiler.ParseAndCompile("""
                schema_version = 99
                [filters.f]
                match_command = "^cmd"
                """, "test", TextWriter.Null));
    }

    [Fact]
    public void ParseAndCompile_UnknownFieldTypo_Throws()
    {
        Assert.Throws<TomlFilterParseException>(() =>
            TomlFilterCompiler.ParseAndCompile("""
                schema_version = 1
                [filters.f]
                match_command = "^cmd"
                strip_ansi_typo = true
                """, "test", TextWriter.Null));
    }

    [Fact]
    public void ParseAndCompile_ShadowWarning_WarnsButDoesNotFail()
    {
        var warnings = new StringWriter();

        var filters = TomlFilterCompiler.ParseAndCompile("""
            schema_version = 1
            [filters.my-git-filter]
            match_command = "^git\\b"
            """, "test", warnings);

        // The filter still compiles and loads (warn, don't fail)...
        var filter = Assert.Single(filters);
        Assert.Equal("my-git-filter", filter.Name);

        // ...but a warning is emitted because "git" is in RUST_HANDLED_COMMANDS. Assert the full
        // exact message (byte-for-byte parity with Rust's toml_filter.rs:330-334 eprintln!) rather
        // than loose substrings, so an accidental rewording is caught immediately.
        var warningText = warnings.ToString();
        Assert.Equal(
            "[rtk] warning: filter 'my-git-filter' match_command matches 'git' which is already " +
            "handled by a Rust module — this filter will never activate for that command\n",
            warningText);
    }

    // -----------------------------------------------------------------
    // find_filter_in
    // -----------------------------------------------------------------

    [Fact]
    public void FindFilter_ReturnsFirstMatch()
    {
        var filters = TomlFilterCompiler.ParseAndCompile("""
            schema_version = 1
            [filters.terraform-plan]
            match_command = "^terraform\\s+plan"
            strip_ansi = true
            """, "test", TextWriter.Null);

        var found = TomlFilterEngine.FindFilter("terraform plan -out=tfplan", filters);

        Assert.NotNull(found);
        Assert.Equal("terraform-plan", found!.Name);
    }

    [Fact]
    public void FindFilter_NoMatch_ReturnsNull()
    {
        var filters = TomlFilterCompiler.ParseAndCompile("""
            schema_version = 1
            [filters.f]
            match_command = "^terraform"
            """, "test", TextWriter.Null);

        Assert.Null(TomlFilterEngine.FindFilter("kubectl get pods", filters));
    }

    // -----------------------------------------------------------------
    // Built-in filters (embedded resources)
    // -----------------------------------------------------------------

    [Fact]
    public void Builtins_LoadConcatenated_HasSchemaVersionHeader()
    {
        Assert.Contains("schema_version = 1", TomlFilterBuiltins.LoadConcatenated());
    }

    [Fact]
    public void Builtins_AllCompile()
    {
        var content = TomlFilterBuiltins.LoadConcatenated();
        var filters = TomlFilterCompiler.ParseAndCompile(content, "builtin", TextWriter.Null);

        Assert.NotEmpty(filters);
    }

    [Fact]
    public void Builtins_ExactlyOfSixtyThreeFilters()
    {
        var content = TomlFilterBuiltins.LoadConcatenated();
        var filters = TomlFilterCompiler.ParseAndCompile(content, "builtin", TextWriter.Null);

        Assert.Equal(63, filters.Count);
    }

    [Fact]
    public void Builtins_EveryExpectedFilterNamePresent()
    {
        var content = TomlFilterBuiltins.LoadConcatenated();
        var filters = TomlFilterCompiler.ParseAndCompile(content, "builtin", TextWriter.Null);
        var names = filters.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        string[] expected =
        [
            "ansible-playbook", "basedpyright", "biome", "brew-install", "bundle-install",
            "composer-install", "df", "dotnet-build", "du", "fail2ban-client", "gcc", "gcloud",
            "gradle", "hadolint", "helm", "iptables", "jira", "jj", "jq", "just", "liquibase",
            "make", "markdownlint", "mise", "mix-compile", "mix-format", "nx", "ollama", "oxlint",
            "ping", "pio-run", "poetry-install", "pre-commit", "ps", "pulumi-destroy",
            "pulumi-preview", "pulumi-refresh", "pulumi-stack", "pulumi-up", "quarto-render",
            "rsync", "shellcheck", "shopify-theme", "skopeo", "sops", "spring-boot", "ssh", "stat",
            "swift-build", "systemctl-status", "task", "terraform-plan", "tofu-fmt", "tofu-init",
            "tofu-plan", "tofu-validate", "trunk-build", "turbo", "ty", "uv-sync", "xcodebuild",
            "yadm", "yamllint",
        ];

        Assert.Equal(63, expected.Length);
        foreach (var name in expected)
        {
            Assert.Contains(name, names);
        }
    }

    // -----------------------------------------------------------------
    // Full end-to-end fixtures: make.toml, rsync.toml, df.toml
    // -----------------------------------------------------------------

    [Fact]
    public void Builtins_MakeFilter_StripsEnteringLeavingAndBlankLines()
    {
        var filters = TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", TextWriter.Null);
        var make = filters.Single(f => f.Name == "make");

        var input = "make[1]: Entering directory '/home/user'\ngcc -O2 foo.c\nmake[1]: Leaving directory '/home/user'";
        var output = TomlFilterEngine.ApplyFilter(make, input);

        Assert.Equal("gcc -O2 foo.c", output);
    }

    [Fact]
    public void Builtins_MakeFilter_OnEmptyWhenAllStripped()
    {
        var filters = TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", TextWriter.Null);
        var make = filters.Single(f => f.Name == "make");

        var input = "make[1]: Entering directory '/home/user'\nmake[1]: Leaving directory '/home/user'";

        Assert.Equal("make: ok", TomlFilterEngine.ApplyFilter(make, input));
    }

    [Fact]
    public void Builtins_RsyncFilter_ShortCircuitsToOkOnSuccessfulSync()
    {
        var filters = TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", TextWriter.Null);
        var rsync = filters.Single(f => f.Name == "rsync");

        var input = "sending incremental file list\n./\nfile1.txt\nfile2.txt\n\nsent 1,234 bytes  received 42 bytes  2,552.00 bytes/sec\ntotal size is 98,765  speedup is 77.31\n";

        Assert.Equal("ok (synced)", TomlFilterEngine.ApplyFilter(rsync, input));
    }

    [Fact]
    public void Builtins_RsyncFilter_ErrorsNotSwallowedWhenTotalSizePresent()
    {
        var filters = TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", TextWriter.Null);
        var rsync = filters.Single(f => f.Name == "rsync");

        var input = "rsync: [sender] error\nerror in rsync protocol data stream (code 12)\nsent 100 bytes  received 200 bytes  60.00 bytes/sec\ntotal size is 1000  speedup is 3.33\n";
        var output = TomlFilterEngine.ApplyFilter(rsync, input);

        Assert.Contains("rsync: [sender] error", output);
        Assert.Contains("total size is 1000  speedup is 3.33", output);
        Assert.NotEqual("ok (synced)", output.Trim());
    }

    [Fact]
    public void Builtins_DfFilter_ShortOutputPassesThroughUnchanged()
    {
        var filters = TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", TextWriter.Null);
        var df = filters.Single(f => f.Name == "df");

        var input = "Filesystem     1K-blocks   Used Available Use% Mounted on\n/dev/sda1        4096000 123456   3972544   4% /";

        Assert.Equal(input, TomlFilterEngine.ApplyFilter(df, input));
    }

    [Fact]
    public void Builtins_DfFilter_EmptyInputPassesThrough()
    {
        var filters = TomlFilterCompiler.ParseAndCompile(TomlFilterBuiltins.LoadConcatenated(), "builtin", TextWriter.Null);
        var df = filters.Single(f => f.Name == "df");

        Assert.Equal(string.Empty, TomlFilterEngine.ApplyFilter(df, string.Empty));
    }

    [Fact]
    public void Builtins_InlineTests_AllPass()
    {
        // Every built-in filter's [[tests.<name>]] inline cases are re-run here directly against
        // the compiled pipeline, exercising all 63 filters against their own author-supplied
        // fixtures in one pass (mirrors Rust's `rtk verify`/run_filter_tests, minus the CLI wiring
        // which is a later task).
        var content = TomlFilterBuiltins.LoadConcatenated();
        var filters = TomlFilterCompiler.ParseAndCompile(content, "builtin", TextWriter.Null);
        var byName = filters.ToDictionary(f => f.Name, StringComparer.Ordinal);

        var file = Tomlyn.TomlSerializer.Deserialize(content, TomlFilterFileContext.Default.TomlFilterFile)
            ?? throw new InvalidOperationException("expected a non-null TomlFilterFile");

        var failures = new List<string>();
        foreach (var (filterName, tests) in file.Tests)
        {
            if (!byName.TryGetValue(filterName, out var filter))
            {
                continue;
            }

            foreach (var test in tests)
            {
                // ApplyFilter already normalizes \r\n -> \n internally (ReadCommand.SplitLines
                // mirrors Rust's str::lines(), which strips a trailing \r from each line), so
                // `actual` is always \n-only. `expected` is compared as raw TOML string content and
                // needs the same normalization applied explicitly: a Windows checkout of
                // src/filters/*.toml (no enforced .gitattributes EOL policy for that path) yields
                // literal \r\n bytes inside a triple-quoted TOML string's non-trimmed interior
                // newlines, which would otherwise make an environment-dependent (checkout-EOL-
                // dependent) test fail despite the printed strings looking identical.
                var actual = TomlFilterEngine.ApplyFilter(filter, test.Input).TrimEnd('\n');
                var expected = test.Expected.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
                if (actual != expected)
                {
                    failures.Add($"{filterName}/{test.Name}: expected [{expected}] got [{actual}]");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
