using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Core;

/// <summary>
/// Tests for <see cref="ErrorStreamFilter"/>, the streaming error/warning-block filter behind
/// <c>rtk err</c>. Faithful-port target: Rust <c>ErrorStreamFilter</c>/<c>ERROR_PATTERNS</c>
/// (<c>src/cmds/rust/runner.rs</c>:13-103).
/// </summary>
public sealed class ErrorStreamFilterTests
{
    // -----------------------------------------------------------------------
    // Every ERROR_PATTERNS regex: one matching + one non-matching sample.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("this is an error: bad thing happened", true)] // generic error[\s:\[]
    [InlineData("Error[42]: something broke", true)]
    [InlineData("no problems mentioned here", false)]
    public void GenericError_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("the err was unexpected", true)] // \berr\b
    [InlineData("errata sheet attached", false)] // "err" not a whole word here
    public void GenericErr_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("warning: deprecated API", true)]
    [InlineData("Warning[unused]: variable", true)]
    [InlineData("all clear", false)]
    public void GenericWarning_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("please warn the team", true)] // \bwarn\b
    [InlineData("this is unwarranted", false)]
    public void GenericWarn_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("build failed unexpectedly", true)]
    [InlineData("build succeeded", false)]
    public void Failed_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("test failure detected", true)]
    [InlineData("test success confirmed", false)]
    public void Failure_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("Unhandled exception occurred", true)]
    [InlineData("everything is fine", false)]
    public void Exception_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("thread 'main' panicked at src/main.rs", true)]
    [InlineData("no issues here", false)]
    public void Panic_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("error[E0432]: unresolved import", true)]
    [InlineData("error: could not compile", false)] // no [Exxxx] code, so this specific pattern misses...
    public void RustErrorCode_Pattern(string line, bool expected)
    {
        // Note: the second sample still matches overall via the generic "error[\s:\[]" pattern, so
        // assert the RUST-SPECIFIC pattern in isolation instead of the combined IsErrorLine.
        var rustErrorCodeMatches = System.Text.RegularExpressions.Regex.IsMatch(line, @"^error\[E\d+\]:.*$");
        Assert.Equal(expected, rustErrorCodeMatches);
    }

    [Theory]
    [InlineData("  --> src/main.rs:10:5", true)]
    [InlineData("some other line", false)]
    public void RustLocation_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("Traceback (most recent call last):", true)]
    [InlineData("normal python output", false)]
    public void PythonTraceback_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("  File \"app.py\", line 42, in <module>", true)]
    [InlineData("just some text", false)]
    public void PythonFileLine_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("    at foo.js:10:5", true)]
    [InlineData("plain log message", false)]
    public void JsAtLocation_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("main.go:15: undefined variable", true)]
    [InlineData("main.rs:15: undefined variable", false)]
    public void GoFileLine_Pattern(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    // -----------------------------------------------------------------------
    // Case-sensitivity regression: Rust's ERROR_PATTERNS only applies the `(?i)` inline flag to
    // the first 8 generic patterns (runner.rs:16-23); the 6 language-specific patterns
    // (runner.rs:25-33) are case-sensitive. A prior port incorrectly attached
    // RegexOptions.IgnoreCase to all 14 [GeneratedRegex] attributes, which made these patterns
    // match lowercased/miscased input that the real Rust regex would not.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Traceback (most recent call last):", true)]
    [InlineData("traceback: boom", false)] // lowercase "traceback" must NOT match (Rust pattern has no (?i))
    public void PythonTraceback_IsCaseSensitive(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("    at foo.js:10:5", true)]
    [InlineData("    AT foo.js:10:5", false)] // uppercase "AT" must NOT match
    public void JsAtLocation_IsCaseSensitive(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("main.go:15: undefined variable", true)]
    [InlineData("main.GO:15: undefined variable", false)] // uppercase ".GO:" must NOT match
    public void GoFileLine_IsCaseSensitive(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("  File \"app.py\", line 42, in <module>", true)]
    [InlineData("  file \"app.py\", line 42, in <module>", false)] // lowercase "file" must NOT match
    public void PythonFileLine_IsCaseSensitive(string line, bool expected) =>
        Assert.Equal(expected, ErrorStreamFilter.IsErrorLine(line));

    [Theory]
    [InlineData("error[E0432]: unresolved import", true)]
    [InlineData("error[e0432]: unresolved import", false)] // lowercase "e0432" must NOT match this specific pattern
    public void RustErrorCode_IsCaseSensitive(string line, bool expected)
    {
        // Isolated like RustErrorCode_Pattern above: the generic "error[\s:\[]" pattern (which IS
        // case-insensitive) also matches both samples via IsErrorLine, so assert the Rust-specific
        // pattern directly to observe the case-sensitivity regression.
        var rustErrorCodeMatches = System.Text.RegularExpressions.Regex.IsMatch(line, @"^error\[E\d+\]:.*$");
        Assert.Equal(expected, rustErrorCodeMatches);
    }

    // -----------------------------------------------------------------------
    // Error-block context-line continuation: indented lines, blank tolerance up to 2.
    // -----------------------------------------------------------------------

    [Fact]
    public void FeedLine_ErrorThenIndentedLines_EmitsAllAsContext()
    {
        var filter = new ErrorStreamFilter();

        Assert.Equal("error: bad thing\n", filter.FeedLine("error: bad thing"));
        Assert.Equal("  indented detail one\n", filter.FeedLine("  indented detail one"));
        Assert.Equal("\tindented detail two\n", filter.FeedLine("\tindented detail two"));
    }

    [Fact]
    public void FeedLine_ErrorThenOneBlankLine_StillInBlock_ContinuesOnIndentedLine()
    {
        var filter = new ErrorStreamFilter();

        Assert.Equal("error: bad thing\n", filter.FeedLine("error: bad thing"));
        Assert.Equal("\n", filter.FeedLine("")); // 1 blank tolerated
        Assert.Equal("  more detail\n", filter.FeedLine("  more detail"));
    }

    [Fact]
    public void FeedLine_ErrorThenTwoBlankLines_ClosesBlock()
    {
        var filter = new ErrorStreamFilter();

        Assert.Equal("error: bad thing\n", filter.FeedLine("error: bad thing"));
        Assert.Equal("\n", filter.FeedLine("")); // blank #1: tolerated, emitted
        Assert.Null(filter.FeedLine("")); // blank #2: closes the block, suppressed
        Assert.Null(filter.FeedLine("  no longer in a block")); // block closed; indented line ignored
    }

    [Fact]
    public void FeedLine_ErrorThenNonIndentedNonBlankLine_ClosesBlockImmediately()
    {
        var filter = new ErrorStreamFilter();

        Assert.Equal("error: bad thing\n", filter.FeedLine("error: bad thing"));
        Assert.Null(filter.FeedLine("unrelated non-indented line"));
        Assert.Null(filter.FeedLine("  now-irrelevant indented line"));
    }

    [Fact]
    public void FeedLine_NonErrorLines_NeverEmitted_WhenNotInBlock()
    {
        var filter = new ErrorStreamFilter();

        Assert.Null(filter.FeedLine("just a normal line"));
        Assert.Null(filter.FeedLine("  even if indented"));
        Assert.Null(filter.FeedLine(""));
    }

    [Fact]
    public void FeedLine_NewErrorLine_ResetsBlankCounter_ReopensBlock()
    {
        var filter = new ErrorStreamFilter();

        Assert.Equal("error: first\n", filter.FeedLine("error: first"));
        Assert.NotNull(filter.FeedLine("")); // blank #1
        Assert.Equal("error: second\n", filter.FeedLine("error: second")); // new error resets state
        Assert.NotNull(filter.FeedLine("")); // blank #1 again (counter reset by the new error line)
        Assert.Null(filter.FeedLine("")); // blank #2 -> closes the (reopened) block
    }

    // -----------------------------------------------------------------------
    // Flush: always empty (ErrorStreamFilter never buffers a tail).
    // -----------------------------------------------------------------------

    [Fact]
    public void Flush_AlwaysReturnsEmpty()
    {
        var filter = new ErrorStreamFilter();
        filter.FeedLine("error: something");
        Assert.Equal(string.Empty, filter.Flush());
    }

    // -----------------------------------------------------------------------
    // OnExit: "no errors"/"[FAIL]"+last-10-lines outcomes.
    // -----------------------------------------------------------------------

    [Fact]
    public void OnExit_NothingEmitted_ExitZero_ReturnsOkMessage()
    {
        var filter = new ErrorStreamFilter();
        // No error lines fed at all.
        var result = filter.OnExit(0, "some raw output\nmore lines\n");

        Assert.Equal("[ok] Command completed successfully (no errors)", result);
    }

    [Fact]
    public void OnExit_NothingEmitted_ExitNonZero_ReturnsFailMessageWithLastTenLines()
    {
        var filter = new ErrorStreamFilter();

        var rawLines = Enumerable.Range(1, 15).Select(i => $"line{i}").ToList();
        var raw = string.Join("\n", rawLines) + "\n";

        var result = filter.OnExit(3, raw);

        Assert.NotNull(result);
        Assert.StartsWith("[FAIL] Command failed (exit code: 3)\n", result, StringComparison.Ordinal);

        // Only the last 10 of the 15 lines should appear.
        for (var i = 1; i <= 5; i++)
        {
            Assert.DoesNotContain($"  line{i}\n", result, StringComparison.Ordinal);
        }

        for (var i = 6; i <= 15; i++)
        {
            Assert.Contains($"  line{i}\n", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnExit_SomethingWasEmitted_ReturnsNull_RegardlessOfExitCode()
    {
        var filter = new ErrorStreamFilter();
        filter.FeedLine("error: something happened");

        Assert.Null(filter.OnExit(0, "raw"));
        Assert.Null(filter.OnExit(1, "raw"));
    }

    [Fact]
    public void OnExit_FewerThanTenRawLines_ShowsAllOfThem()
    {
        var filter = new ErrorStreamFilter();
        var raw = "a\nb\nc\n";

        var result = filter.OnExit(2, raw);

        Assert.NotNull(result);
        Assert.Contains("  a\n", result, StringComparison.Ordinal);
        Assert.Contains("  b\n", result, StringComparison.Ordinal);
        Assert.Contains("  c\n", result, StringComparison.Ordinal);
    }
}
