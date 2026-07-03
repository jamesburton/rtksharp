namespace RtkSharp.Rewrite;

/// <summary>
/// Implements the <c>rtk rewrite</c> CLI verb: evaluates a raw shell command against the
/// permission/rewrite pipeline and produces the exit code / stdout pair that the host
/// (Claude Code, or any PreToolUse-style hook consumer) expects. Faithful port of Rust
/// <c>evaluate</c> (<c>src/hooks/rewrite_cmd.rs:48</c>). The public <see cref="Evaluate(string)"/>
/// loads the user's Claude Code permission rules via <see cref="PermissionRules.Default"/>, so an
/// allowed command (e.g. <c>Bash(git:*)</c>) resolves to <see cref="PermissionVerdict.Allow"/>
/// (exit 0). <c>~/.config/rtk/config.toml</c> exclude/prefix lists remain out of scope, so
/// <see cref="RewriteEngine.RewriteCommand"/> is still invoked with empty exclude/prefix lists.
/// </summary>
public static class RewriteCommand
{
    /// <summary>
    /// Evaluates a raw command per the <c>rtk rewrite</c> contract and returns the exit code
    /// and stdout the process should produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exit code contract (mirrors Rust <c>evaluate</c>):
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>2</c>, empty output — <see cref="PermissionVerdict.Deny"/> matched;
    /// the host's native deny handling takes over.</description></item>
    /// <item><description><c>1</c>, empty output — the command contains an unattestable shell
    /// construct, or <see cref="RewriteEngine.RewriteCommand"/> found no rewrite to apply.</description></item>
    /// <item><description><c>0</c>, rewritten output — a rewrite was found and the verdict is
    /// <see cref="PermissionVerdict.Allow"/> (a loaded allow rule, e.g. <c>Bash(git:*)</c>,
    /// matched the command).</description></item>
    /// <item><description><c>3</c>, rewritten output — a rewrite was found and the verdict is
    /// <see cref="PermissionVerdict.Ask"/> (no allow rule matched — the default outcome).</description></item>
    /// </list>
    /// </remarks>
    /// <param name="cmd">The raw shell command string to evaluate.</param>
    /// <returns>A tuple of the process exit code and the stdout text to emit (no trailing newline).</returns>
    public static (int ExitCode, string Output) Evaluate(string cmd)
    {
        return Evaluate(cmd, PermissionRules.Default);
    }

    /// <summary>
    /// Rule-injected core of <see cref="Evaluate(string)"/>, used by tests to evaluate the
    /// contract deterministically without reading the host's real settings files.
    /// </summary>
    /// <param name="cmd">The raw shell command string to evaluate.</param>
    /// <param name="rules">The permission rules to evaluate the command against.</param>
    /// <returns>A tuple of the process exit code and the stdout text to emit (no trailing newline).</returns>
    internal static (int ExitCode, string Output) Evaluate(string cmd, PermissionRuleSet rules)
    {
        var verdict = Permissions.CheckCommandWithRules(cmd, rules.Deny, rules.Ask, rules.Allow);
        if (verdict == PermissionVerdict.Deny)
        {
            return (2, "");
        }

        if (ShellLexer.ContainsUnattestableConstruct(cmd))
        {
            return (1, "");
        }

        var rewritten = RewriteEngine.RewriteCommand(cmd, [], []);
        if (rewritten is null)
        {
            return (1, "");
        }

        return verdict == PermissionVerdict.Allow ? (0, rewritten) : (3, rewritten);
    }
}
