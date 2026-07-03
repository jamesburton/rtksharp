using RtkSharp.Hooks;
using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Hooks;

/// <summary>
/// Oracle-derived tests for the four native hook processors. Every case injects an explicit
/// <see cref="PermissionRuleSet"/> (never the host's real settings) so the JSON-in/JSON-out
/// contract is exercised deterministically. Expected strings were captured byte-for-byte from
/// <c>rtk.exe hook &lt;agent&gt;</c>. The <c>preserve_order</c> serde feature means response key
/// order is insertion order — the assertions pin that order exactly.
/// </summary>
public class HookCommandTests
{
    private static readonly PermissionRuleSet Allowed = new([], [], ["*"]);
    private static readonly PermissionRuleSet NoRules = PermissionRuleSet.Empty;

    // ── Claude ────────────────────────────────────────────────────────────

    [Fact]
    public void Claude_AllowedGitStatus_EmitsAllowDecision()
    {
        var output = HookCommand.ProcessClaude(
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}""",
            Allowed);

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecisionReason":"RTK auto-rewrite","updatedInput":{"command":"rtk git status"},"permissionDecision":"allow"}}""",
            output);
    }

    [Fact]
    public void Claude_NoAllowRule_OmitsPermissionDecision()
    {
        var output = HookCommand.ProcessClaude(
            """{"tool_name":"Bash","tool_input":{"command":"cargo test"}}""",
            NoRules);

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecisionReason":"RTK auto-rewrite","updatedInput":{"command":"rtk cargo test"}}}""",
            output);
    }

    [Fact]
    public void Claude_PreservesExtraToolInputFieldsInOrder()
    {
        var output = HookCommand.ProcessClaude(
            """{"tool_name":"Bash","tool_input":{"command":"git status","timeout":30000,"description":"Check repo status"}}""",
            Allowed);

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecisionReason":"RTK auto-rewrite","updatedInput":{"command":"rtk git status","timeout":30000,"description":"Check repo status"},"permissionDecision":"allow"}}""",
            output);
    }

    [Fact]
    public void Claude_CompoundCommand_RewritesEachSegment()
    {
        var output = HookCommand.ProcessClaude(
            """{"tool_name":"Bash","tool_input":{"command":"git add . && cargo test"}}""",
            NoRules);

        Assert.Contains("\"command\":\"rtk git add . && rtk cargo test\"", output);
    }

    [Theory]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"htop"}}""")] // non-rewritable
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"rtk git status"}}""")] // already rtk
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":""}}""")] // empty command
    [InlineData("""{"tool_name":"Bash"}""")] // no tool_input
    [InlineData("""{"tool_name":"Read","tool_input":{"file_path":"/x"}}""")] // non-Bash tool still has command? no
    [InlineData("not valid json {{{")] // malformed
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"git status $(rm -rf /tmp/x)"}}""")] // substitution defers
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"git log > /tmp/out.txt"}}""")] // file redirect defers
    public void Claude_DeferralCases_EmitNothing(string input)
    {
        Assert.Null(HookCommand.ProcessClaude(input, Allowed));
    }

    [Fact]
    public void Claude_DenyRule_EmitsNothing()
    {
        var deny = new PermissionRuleSet(["git status"], [], ["*"]);
        Assert.Null(HookCommand.ProcessClaude(
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}""",
            deny));
    }

    [Fact]
    public void Claude_FdDupRedirect_StillRewrites()
    {
        var output = HookCommand.ProcessClaude(
            """{"tool_name":"Bash","tool_input":{"command":"git status 2>&1"}}""",
            Allowed);

        Assert.Contains("\"command\":\"rtk git status 2>&1\"", output!);
    }

    // ── Cursor ────────────────────────────────────────────────────────────

    [Fact]
    public void Cursor_Allowed_EmitsFlatAllow()
    {
        var output = HookCommand.ProcessCursor(
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}""",
            Allowed);

        Assert.Equal(
            """{"continue":true,"permission":"allow","updated_input":{"command":"rtk git status"}}""",
            output);
    }

    [Fact]
    public void Cursor_CompoundAllowed_PreservesLeadingCd()
    {
        var output = HookCommand.ProcessCursor(
            """{"tool_name":"Bash","tool_input":{"command":"cd \"/tmp/proj\" && git status"}}""",
            Allowed);

        Assert.Equal(
            """{"continue":true,"permission":"allow","updated_input":{"command":"cd \"/tmp/proj\" && rtk git status"}}""",
            output);
    }

    [Theory]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"git status"}}""")] // no allow rule → defer
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"htop"}}""")] // non-rewritable
    [InlineData("")] // empty stdin
    [InlineData("xx{{")] // malformed
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"cat <<EOF\nhi\nEOF"}}""")] // heredoc
    public void Cursor_DeferralCases_EmitEmptyObject(string input)
    {
        Assert.Equal("{}", HookCommand.ProcessCursor(input, NoRules));
    }

    [Fact]
    public void Cursor_SubstitutionDefers_EvenWhenAllowed()
    {
        Assert.Equal("{}", HookCommand.ProcessCursor(
            """{"tool_name":"Bash","tool_input":{"command":"git status `rm -rf /tmp/x`"}}""",
            Allowed));
    }

    [Fact]
    public void Cursor_StripsDoubleUtf8Bom()
    {
        var output = HookCommand.ProcessCursor(
            "﻿﻿" + """{"tool_name":"Bash","tool_input":{"command":"git status"}}""",
            Allowed);

        Assert.Equal(
            """{"continue":true,"permission":"allow","updated_input":{"command":"rtk git status"}}""",
            output);
    }

    // ── Gemini ────────────────────────────────────────────────────────────

    [Fact]
    public void Gemini_ShellCommand_EmitsAskUserRewrite()
    {
        var (output, exit) = HookCommand.ProcessGemini(
            """{"tool_name":"run_shell_command","tool_input":{"command":"git status"}}""",
            NoRules);

        Assert.Equal(0, exit);
        Assert.Equal(
            """{"decision":"ask_user","hookSpecificOutput":{"tool_input":{"command":"rtk git status"}}}""",
            output);
    }

    [Fact]
    public void Gemini_AllowedShellCommand_EmitsAllowRewrite()
    {
        var (output, exit) = HookCommand.ProcessGemini(
            """{"tool_name":"run_shell_command","tool_input":{"command":"git status"}}""",
            Allowed);

        Assert.Equal(0, exit);
        Assert.Equal(
            """{"decision":"allow","hookSpecificOutput":{"tool_input":{"command":"rtk git status"}}}""",
            output);
    }

    [Theory]
    [InlineData("""{"tool_name":"read_file","tool_input":{}}""")] // non-shell tool
    [InlineData("""{"tool_name":"run_shell_command","tool_input":{"command":""}}""")] // empty command
    public void Gemini_AllowByPassthrough(string input)
    {
        var (output, exit) = HookCommand.ProcessGemini(input, Allowed);
        Assert.Equal(0, exit);
        Assert.Equal("""{"decision":"allow"}""", output);
    }

    [Theory]
    [InlineData("""{"tool_name":"run_shell_command","tool_input":{"command":"htop"}}""")] // non-rewritable
    [InlineData("""{"tool_name":"run_shell_command","tool_input":{"command":"git status `whoami`"}}""")] // substitution
    public void Gemini_Defer_AsksUserWithoutRewrite(string input)
    {
        var (output, exit) = HookCommand.ProcessGemini(input, Allowed);
        Assert.Equal(0, exit);
        Assert.Equal("""{"decision":"ask_user"}""", output);
    }

    [Fact]
    public void Gemini_DenyRule_EmitsDenyDecision()
    {
        var deny = new PermissionRuleSet(["cargo test"], [], []);
        var (output, exit) = HookCommand.ProcessGemini(
            """{"tool_name":"run_shell_command","tool_input":{"command":"cargo test"}}""",
            deny);

        Assert.Equal(0, exit);
        Assert.Equal("""{"decision":"deny","reason":"Blocked by RTK permission rule"}""", output);
    }

    [Theory]
    [InlineData("xx{{")]
    [InlineData("")]
    public void Gemini_MalformedOrEmpty_ExitsOneNoOutput(string input)
    {
        var (output, exit) = HookCommand.ProcessGemini(input, NoRules);
        Assert.Equal(1, exit);
        Assert.Null(output);
    }

    // ── Copilot (VS Code Chat) ────────────────────────────────────────────

    [Fact]
    public void CopilotVsCode_Allowed_EmitsAllow()
    {
        var output = HookCommand.ProcessCopilot(
            """{"tool_name":"Bash","tool_input":{"command":"git status"}}""",
            Allowed);

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":"RTK auto-rewrite","updatedInput":{"command":"rtk git status"}}}""",
            output);
    }

    [Fact]
    public void CopilotVsCode_NoAllowRule_EmitsAsk()
    {
        var output = HookCommand.ProcessCopilot(
            """{"tool_name":"Bash","tool_input":{"command":"cargo test"}}""",
            NoRules);

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"RTK auto-rewrite","updatedInput":{"command":"rtk cargo test"}}}""",
            output);
    }

    [Theory]
    [InlineData("""{"tool_name":"editFiles"}""")] // non-bash tool
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"htop"}}""")] // non-rewritable
    [InlineData("not json {{{")] // malformed
    public void CopilotVsCode_Passthrough_EmitsNothing(string input)
    {
        Assert.Null(HookCommand.ProcessCopilot(input, Allowed));
    }

    // ── Copilot (CLI) ─────────────────────────────────────────────────────

    [Fact]
    public void CopilotCli_Allowed_EmitsModifiedArgsWithAllow()
    {
        var output = HookCommand.ProcessCopilot(
            """{"toolName":"bash","toolArgs":"{\"command\":\"git status\"}"}""",
            Allowed);

        Assert.Equal(
            """{"permissionDecisionReason":"RTK auto-rewrite","modifiedArgs":{"command":"rtk git status"},"permissionDecision":"allow"}""",
            output);
    }

    [Fact]
    public void CopilotCli_AskRewrite_OmitsPermissionDecisionAndPreservesArgs()
    {
        var output = HookCommand.ProcessCopilot(
            """{"toolName":"bash","toolArgs":"{\"command\":\"cargo test\",\"description\":\"run\",\"initial_wait\":30}"}""",
            NoRules);

        Assert.Equal(
            """{"permissionDecisionReason":"RTK auto-rewrite","modifiedArgs":{"command":"rtk cargo test","description":"run","initial_wait":30}}""",
            output);
    }

    [Fact]
    public void CopilotCli_Substitution_ReturnsNothing()
    {
        Assert.Null(HookCommand.ProcessCopilot(
            """{"toolName":"bash","toolArgs":"{\"command\":\"git log --pretty=$(rm -rf /tmp/x)\"}"}""",
            Allowed));
    }

    // ── hook check (via the live dispatch) ────────────────────────────────

    [Fact]
    public void Check_Rewritable_PrintsRewriteExitZero()
    {
        var (exit, stdout) = CaptureRun(["check", "git", "status"]);
        Assert.Equal(0, exit);
        Assert.Equal("rtk git status\n", stdout);
    }

    [Fact]
    public void Check_AgentFlagIgnored_StillRewrites()
    {
        var (exit, stdout) = CaptureRun(["check", "--agent", "gemini", "cargo", "test"]);
        Assert.Equal(0, exit);
        Assert.Equal("rtk cargo test\n", stdout);
    }

    [Fact]
    public void Check_NonRewritable_ExitsOneNoStdout()
    {
        var (exit, stdout) = CaptureRun(["check", "htop"]);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
    }

    private static (int Exit, string Stdout) CaptureRun(string[] args)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = HookCommand.Run(args);
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
