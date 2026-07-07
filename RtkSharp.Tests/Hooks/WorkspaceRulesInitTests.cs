using System;
using System.IO;
using RtkSharp.Hooks;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Tests for <see cref="WorkspaceRulesInit"/>: the four "workspace rules file" writers (Cline,
/// Windsurf, Kilo Code, Antigravity). Oracle-derived expectations were captured against
/// <c>src/hooks/init.rs</c>'s <c>run_cline_mode</c>/<c>run_windsurf_mode</c>/
/// <c>run_kilocode_mode_at</c>/<c>run_antigravity_mode_at</c> and their embedded
/// <c>hooks/*/rules.md</c> templates. Uninstall coverage is for this port's own (non-Rust-derived)
/// addition — see <see cref="WorkspaceRulesInit"/>'s remarks.
/// </summary>
public sealed class WorkspaceRulesInitTests
{
    // ═══════════════════════════════ Cline ═══════════════════════════════

    [Fact]
    public void Cline_FirstRun_CreatesRulesFile()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunCline(InitContext.Default);

        var path = Path.Combine(tmp.Root, ".clinerules");
        Assert.True(File.Exists(path));
        Assert.Equal(WorkspaceRulesInit.ClineRules, File.ReadAllText(path));
        Assert.Contains("RTK configured for Cline.", console.Out.ToString());
        Assert.Contains("Rules: .clinerules (installed)", console.Out.ToString());
        Assert.Contains("Cline will now use rtk commands for token savings.", console.Out.ToString());
        Assert.Contains("Test with: git status", console.Out.ToString());
    }

    [Fact]
    public void Cline_SecondRun_IsIdempotent_ReportsAlreadyPresent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        WorkspaceRulesInit.RunCline(InitContext.Default);
        var firstContent = File.ReadAllText(Path.Combine(tmp.Root, ".clinerules"));

        using var console = new ConsoleCapture();
        WorkspaceRulesInit.RunCline(InitContext.Default);
        var secondContent = File.ReadAllText(Path.Combine(tmp.Root, ".clinerules"));

        Assert.Equal(firstContent, secondContent);
        Assert.Contains("RTK already configured for Cline in this project.", console.Out.ToString());
        Assert.Contains("Rules: .clinerules (already present)", console.Out.ToString());
    }

    [Fact]
    public void Cline_PreservesExistingFileContent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        File.WriteAllText(Path.Combine(tmp.Root, ".clinerules"), "# My team rules\n\nBe nice.");

        WorkspaceRulesInit.RunCline(InitContext.Default);

        var content = File.ReadAllText(Path.Combine(tmp.Root, ".clinerules"));
        Assert.StartsWith("# My team rules\n\nBe nice.\n\n", content);
        Assert.Contains(WorkspaceRulesInit.ClineRules, content);
    }

    [Fact]
    public void Cline_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunCline(new InitContext(Verbose: 0, DryRun: true));

        Assert.False(File.Exists(Path.Combine(tmp.Root, ".clinerules")));
        Assert.Contains("[dry-run] would write .clinerules: .clinerules", console.Out.ToString());
        // Cline relies on the outer `run()` wrapper for its dry-run footer in Rust; this
        // function alone prints no footer/nothing-written line.
        Assert.DoesNotContain("Nothing written", console.Out.ToString());
    }

    [Fact]
    public void Cline_AlreadyConfigured_DryRun_PrintsNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        File.WriteAllText(Path.Combine(tmp.Root, ".clinerules"), "# rtk already here\n");

        using var console = new ConsoleCapture();
        WorkspaceRulesInit.RunCline(new InitContext(Verbose: 0, DryRun: true));

        Assert.Equal(string.Empty, console.Out.ToString());
    }

    [Fact]
    public void Cline_Uninstall_RoundTrip()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        WorkspaceRulesInit.RunCline(InitContext.Default);
        Assert.True(File.Exists(Path.Combine(tmp.Root, ".clinerules")));

        using var console = new ConsoleCapture();
        var removed = WorkspaceRulesInit.UninstallCline(InitContext.Default);

        Assert.True(removed);
        Assert.False(File.Exists(Path.Combine(tmp.Root, ".clinerules")));
        Assert.Contains("RTK uninstalled for Cline", console.Out.ToString());

        using var console2 = new ConsoleCapture();
        var removedAgain = WorkspaceRulesInit.UninstallCline(InitContext.Default);
        Assert.False(removedAgain);
        Assert.Contains("nothing to remove", console2.Out.ToString());
    }

    [Fact]
    public void Cline_Uninstall_PreservesUserContent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        File.WriteAllText(Path.Combine(tmp.Root, ".clinerules"), "# My team rules\n\nBe nice.");

        WorkspaceRulesInit.RunCline(InitContext.Default);
        WorkspaceRulesInit.UninstallCline(InitContext.Default);

        var content = File.ReadAllText(Path.Combine(tmp.Root, ".clinerules"));
        Assert.Equal("# My team rules\n\nBe nice.", content);
    }

    [Fact]
    public void Cline_Uninstall_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        WorkspaceRulesInit.RunCline(InitContext.Default);
        var before = File.ReadAllText(Path.Combine(tmp.Root, ".clinerules"));

        WorkspaceRulesInit.UninstallCline(new InitContext(Verbose: 0, DryRun: true));

        Assert.Equal(before, File.ReadAllText(Path.Combine(tmp.Root, ".clinerules")));
    }

    // ═══════════════════════════════ Windsurf ═══════════════════════════════

    [Fact]
    public void Windsurf_FirstRun_CreatesRulesFile()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunWindsurf(InitContext.Default);

        var path = Path.Combine(tmp.Root, ".windsurfrules");
        Assert.True(File.Exists(path));
        Assert.Equal(WorkspaceRulesInit.WindsurfRules, File.ReadAllText(path));
        Assert.Contains("RTK configured for Windsurf Cascade.", console.Out.ToString());
        Assert.Contains("Rules: .windsurfrules (installed)", console.Out.ToString());
        Assert.Contains("Cascade will now use rtk commands for token savings.", console.Out.ToString());
        Assert.Contains("Restart Windsurf. Test with: git status", console.Out.ToString());
    }

    [Fact]
    public void Windsurf_SecondRun_IsIdempotent()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        WorkspaceRulesInit.RunWindsurf(InitContext.Default);
        var first = File.ReadAllText(Path.Combine(tmp.Root, ".windsurfrules"));

        using var console = new ConsoleCapture();
        WorkspaceRulesInit.RunWindsurf(InitContext.Default);
        var second = File.ReadAllText(Path.Combine(tmp.Root, ".windsurfrules"));

        Assert.Equal(first, second);
        Assert.Contains("RTK already configured for Windsurf in this project.", console.Out.ToString());
    }

    [Fact]
    public void Windsurf_DryRun_WritesNothing()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunWindsurf(new InitContext(Verbose: 0, DryRun: true));

        Assert.False(File.Exists(Path.Combine(tmp.Root, ".windsurfrules")));
        Assert.Contains("[dry-run] would write .windsurfrules: .windsurfrules", console.Out.ToString());
    }

    [Fact]
    public void Windsurf_Uninstall_RoundTrip()
    {
        using var tmp = new TempDir();
        using var cwd = new CwdGuard(tmp.Root);

        WorkspaceRulesInit.RunWindsurf(InitContext.Default);
        var removed = WorkspaceRulesInit.UninstallWindsurf(InitContext.Default);

        Assert.True(removed);
        Assert.False(File.Exists(Path.Combine(tmp.Root, ".windsurfrules")));

        var removedAgain = WorkspaceRulesInit.UninstallWindsurf(InitContext.Default);
        Assert.False(removedAgain);
    }

    // ═══════════════════════════════ Kilo Code ═══════════════════════════════

    [Fact]
    public void Kilocode_FirstRun_CreatesRulesFileUnderNestedDir()
    {
        using var tmp = new TempDir();
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunKilocodeAt(tmp.Root, InitContext.Default);

        var path = Path.Combine(tmp.Root, ".kilocode", "rules", "rtk-rules.md");
        Assert.True(File.Exists(path));
        Assert.Equal(WorkspaceRulesInit.KilocodeRules, File.ReadAllText(path));
        Assert.Contains("RTK configured for Kilo Code.", console.Out.ToString());
        Assert.Contains("Rules: .kilocode/rules/rtk-rules.md (installed)", console.Out.ToString());
        Assert.Contains("Kilo Code will now use rtk commands for token savings.", console.Out.ToString());
    }

    [Fact]
    public void Kilocode_SecondRun_IsIdempotent()
    {
        using var tmp = new TempDir();

        WorkspaceRulesInit.RunKilocodeAt(tmp.Root, InitContext.Default);
        var path = Path.Combine(tmp.Root, ".kilocode", "rules", "rtk-rules.md");
        var first = File.ReadAllText(path);

        using var console = new ConsoleCapture();
        WorkspaceRulesInit.RunKilocodeAt(tmp.Root, InitContext.Default);
        var second = File.ReadAllText(path);

        Assert.Equal(first, second);
        Assert.Contains("RTK already configured for Kilo Code in this project.", console.Out.ToString());
    }

    [Fact]
    public void Kilocode_DryRun_WritesNothing_AndPrintsExplicitFooter()
    {
        using var tmp = new TempDir();
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunKilocodeAt(tmp.Root, new InitContext(Verbose: 0, DryRun: true));

        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".kilocode")));
        var stdout = console.Out.ToString();
        Assert.Contains("(and create parent dir if missing)", stdout);
        // Unlike Cline/Windsurf, Kilo Code calls print_dry_run_footer() itself.
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void Kilocode_Uninstall_RoundTrip()
    {
        using var tmp = new TempDir();

        WorkspaceRulesInit.RunKilocodeAt(tmp.Root, InitContext.Default);
        var removed = WorkspaceRulesInit.UninstallKilocodeAt(tmp.Root, InitContext.Default);

        Assert.True(removed);
        Assert.False(File.Exists(Path.Combine(tmp.Root, ".kilocode", "rules", "rtk-rules.md")));

        var removedAgain = WorkspaceRulesInit.UninstallKilocodeAt(tmp.Root, InitContext.Default);
        Assert.False(removedAgain);
    }

    // ═══════════════════════════════ Antigravity ═══════════════════════════════

    [Fact]
    public void Antigravity_FirstRun_CreatesRulesFileUnderNestedDir()
    {
        using var tmp = new TempDir();
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunAntigravityAt(tmp.Root, InitContext.Default);

        var path = Path.Combine(tmp.Root, ".agents", "rules", "antigravity-rtk-rules.md");
        Assert.True(File.Exists(path));
        Assert.Equal(WorkspaceRulesInit.AntigravityRules, File.ReadAllText(path));
        Assert.Contains("RTK configured for Google Antigravity.", console.Out.ToString());
        Assert.Contains("Rules: .agents/rules/antigravity-rtk-rules.md (installed)", console.Out.ToString());
        Assert.Contains("Antigravity will now use rtk commands for token savings.", console.Out.ToString());
    }

    [Fact]
    public void Antigravity_SecondRun_IsIdempotent()
    {
        using var tmp = new TempDir();

        WorkspaceRulesInit.RunAntigravityAt(tmp.Root, InitContext.Default);
        var path = Path.Combine(tmp.Root, ".agents", "rules", "antigravity-rtk-rules.md");
        var first = File.ReadAllText(path);

        using var console = new ConsoleCapture();
        WorkspaceRulesInit.RunAntigravityAt(tmp.Root, InitContext.Default);
        var second = File.ReadAllText(path);

        Assert.Equal(first, second);
        Assert.Contains("RTK already configured for Antigravity in this project.", console.Out.ToString());
    }

    [Fact]
    public void Antigravity_DryRun_WritesNothing_AndPrintsExplicitFooter()
    {
        using var tmp = new TempDir();
        using var console = new ConsoleCapture();

        WorkspaceRulesInit.RunAntigravityAt(tmp.Root, new InitContext(Verbose: 0, DryRun: true));

        Assert.False(Directory.Exists(Path.Combine(tmp.Root, ".agents")));
        var stdout = console.Out.ToString();
        Assert.Contains("(and create parent dir if missing)", stdout);
        Assert.EndsWith("\n[dry-run] Nothing written.\n", stdout);
    }

    [Fact]
    public void Antigravity_Uninstall_RoundTrip()
    {
        using var tmp = new TempDir();

        WorkspaceRulesInit.RunAntigravityAt(tmp.Root, InitContext.Default);
        var removed = WorkspaceRulesInit.UninstallAntigravityAt(tmp.Root, InitContext.Default);

        Assert.True(removed);
        Assert.False(File.Exists(Path.Combine(tmp.Root, ".agents", "rules", "antigravity-rtk-rules.md")));

        var removedAgain = WorkspaceRulesInit.UninstallAntigravityAt(tmp.Root, InitContext.Default);
        Assert.False(removedAgain);
    }

    [Fact]
    public void Antigravity_Uninstall_PreservesUserContent()
    {
        using var tmp = new TempDir();
        var targetDir = Path.Combine(tmp.Root, ".agents", "rules");
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, "antigravity-rtk-rules.md");
        File.WriteAllText(path, "# Team notes\n\nDon't break prod.");

        WorkspaceRulesInit.RunAntigravityAt(tmp.Root, InitContext.Default);
        WorkspaceRulesInit.UninstallAntigravityAt(tmp.Root, InitContext.Default);

        Assert.Equal("# Team notes\n\nDon't break prod.", File.ReadAllText(path));
    }

    // ═══════════════════════════════ Cross-agent template sanity ═══════════════════════════════

    [Fact]
    public void AllFourTemplates_ContainRtkAndAreDistinct()
    {
        Assert.Contains("RTK", WorkspaceRulesInit.ClineRules);
        Assert.Contains("RTK", WorkspaceRulesInit.WindsurfRules);
        Assert.Contains("RTK", WorkspaceRulesInit.KilocodeRules);
        Assert.Contains("RTK", WorkspaceRulesInit.AntigravityRules);

        Assert.Contains("Cline", WorkspaceRulesInit.ClineRules);
        Assert.Contains("Windsurf", WorkspaceRulesInit.WindsurfRules);
        Assert.Contains("Kilo Code", WorkspaceRulesInit.KilocodeRules);
        Assert.Contains("Google Antigravity", WorkspaceRulesInit.AntigravityRules);
    }
}
