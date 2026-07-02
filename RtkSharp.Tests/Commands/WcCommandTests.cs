using RtkSharp.Commands.System;

namespace RtkSharp.Tests.Commands;

/// <summary>
/// Tests for <see cref="WcCommand"/>. The compaction logic is a pure function of raw <c>wc</c>
/// output, so it is tested directly against captured/synthetic <c>wc</c> output rather than by
/// executing the <c>wc</c> binary. Expectations mirror the unit tests in
/// <c>src/cmds/system/wc_cmd.rs</c> and the reference <c>rtk.exe</c> oracle
/// (<c>rtk wc -l Cargo.toml</c> → <c>72</c>, <c>rtk wc README.md</c> →
/// <c>505L 2820W 22746B</c>).
/// </summary>
public sealed class WcCommandTests
{
    // ---- DetectMode ----

    [Fact]
    public void DetectMode_NoFlags_Full() =>
        Assert.Equal(WcCommand.WcMode.Full, WcCommand.DetectMode(new[] { "file.py" }));

    [Fact]
    public void DetectMode_Lines() =>
        Assert.Equal(WcCommand.WcMode.Lines, WcCommand.DetectMode(new[] { "-l", "file.py" }));

    [Fact]
    public void DetectMode_Words() =>
        Assert.Equal(WcCommand.WcMode.Words, WcCommand.DetectMode(new[] { "-w", "file.py" }));

    [Fact]
    public void DetectMode_Bytes() =>
        Assert.Equal(WcCommand.WcMode.Bytes, WcCommand.DetectMode(new[] { "-c", "file.py" }));

    [Fact]
    public void DetectMode_Chars() =>
        Assert.Equal(WcCommand.WcMode.Chars, WcCommand.DetectMode(new[] { "-m", "file.py" }));

    [Fact]
    public void DetectMode_CombinedFlags_Mixed() =>
        Assert.Equal(WcCommand.WcMode.Mixed, WcCommand.DetectMode(new[] { "-lw", "file.py" }));

    [Fact]
    public void DetectMode_SeparateFlags_Mixed() =>
        Assert.Equal(WcCommand.WcMode.Mixed, WcCommand.DetectMode(new[] { "-l", "-w", "file.py" }));

    [Fact]
    public void DetectMode_NonCountFlagOnly_Full() =>
        // A flag with no l/w/c/m chars (e.g. --files0-from style) leaves flag_count 0.
        Assert.Equal(WcCommand.WcMode.Full, WcCommand.DetectMode(new[] { "-L", "file.py" }));

    // ---- FilterWcOutput: single file / stdin ----

    [Fact]
    public void FilterWcOutput_SingleFileFull()
    {
        var raw = "      30      96     978 scripts/find_duplicate_attrs.py\n";
        Assert.Equal("30L 96W 978B", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Full));
    }

    [Fact]
    public void FilterWcOutput_SingleFileLinesOnly()
    {
        var raw = "      30 scripts/find_duplicate_attrs.py\n";
        Assert.Equal("30", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Lines));
    }

    [Fact]
    public void FilterWcOutput_SingleFileWordsOnly()
    {
        var raw = "      96 scripts/find_duplicate_attrs.py\n";
        Assert.Equal("96", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Words));
    }

    [Fact]
    public void FilterWcOutput_StdinFull()
    {
        var raw = "      30      96     978\n";
        Assert.Equal("30L 96W 978B", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Full));
    }

    [Fact]
    public void FilterWcOutput_StdinLines()
    {
        var raw = "      30\n";
        Assert.Equal("30", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Lines));
    }

    [Fact]
    public void FilterWcOutput_OracleLinesOnly()
    {
        // Oracle: `rtk wc -l Cargo.toml` → 72 (Cargo.toml is 72 lines).
        var raw = "      72 Cargo.toml\n";
        Assert.Equal("72", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Lines));
    }

    [Fact]
    public void FilterWcOutput_OracleFull()
    {
        // Oracle: `rtk wc README.md` → 505L 2820W 22746B.
        var raw = "     505    2820   22746 README.md\n";
        Assert.Equal("505L 2820W 22746B", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Full));
    }

    // ---- FilterWcOutput: multiple files ----

    [Fact]
    public void FilterWcOutput_MultiFileLines()
    {
        var raw = "      30 src/main.rs\n      50 src/lib.rs\n      80 total\n";
        Assert.Equal("30 main.rs\n50 lib.rs\nΣ 80", WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Lines));
    }

    [Fact]
    public void FilterWcOutput_MultiFileFull()
    {
        var raw = "      30      96     978 src/main.rs\n      50     120    1500 src/lib.rs\n      80     216    2478 total\n";
        Assert.Equal(
            "30L 96W 978B main.rs\n50L 120W 1500B lib.rs\nΣ 80L 216W 2478B",
            WcCommand.FilterWcOutput(raw, WcCommand.WcMode.Full));
    }

    [Fact]
    public void FilterWcOutput_Empty() =>
        Assert.Equal(string.Empty, WcCommand.FilterWcOutput(string.Empty, WcCommand.WcMode.Full));

    [Fact]
    public void FilterWcOutput_MissingFile_EmptyStdout() =>
        // wc writes its error to stderr (dropped by FilterStdoutOnly); stdout is empty.
        Assert.Equal(string.Empty, WcCommand.FilterWcOutput(string.Empty, WcCommand.WcMode.Lines));

    // ---- FindCommonPrefix / StripPrefix ----

    [Fact]
    public void FindCommonPrefix_ShallowShared() =>
        Assert.Equal("src/", WcCommand.FindCommonPrefix(new[] { "src/main.rs", "src/lib.rs", "src/utils.rs" }));

    [Fact]
    public void FindCommonPrefix_None() =>
        Assert.Equal(string.Empty, WcCommand.FindCommonPrefix(new[] { "main.rs", "lib.rs" }));

    [Fact]
    public void FindCommonPrefix_Deep() =>
        Assert.Equal("src/cmd/", WcCommand.FindCommonPrefix(new[] { "src/cmd/wc.rs", "src/cmd/ls.rs" }));

    [Fact]
    public void FindCommonPrefix_SinglePath_Empty() =>
        Assert.Equal(string.Empty, WcCommand.FindCommonPrefix(new[] { "src/main.rs" }));

    [Fact]
    public void FindCommonPrefix_DivergentSubdirs_ClimbsToShared() =>
        Assert.Equal("src/", WcCommand.FindCommonPrefix(new[] { "src/a/x.rs", "src/b/y.rs" }));

    [Fact]
    public void StripPrefix_Present() =>
        Assert.Equal("main.rs", WcCommand.StripPrefix("src/main.rs", "src/"));

    [Fact]
    public void StripPrefix_EmptyPrefix_ReturnsPath() =>
        Assert.Equal("main.rs", WcCommand.StripPrefix("main.rs", string.Empty));

    [Fact]
    public void StripPrefix_NonMatching_ReturnsPath() =>
        Assert.Equal("main.rs", WcCommand.StripPrefix("main.rs", "src/"));
}
