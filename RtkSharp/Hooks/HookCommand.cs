using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RtkSharp.Rewrite;

namespace RtkSharp.Hooks;

/// <summary>
/// Implements the <c>rtk hook</c> CLI verb: the native hook processors for AI-agent CLIs
/// (<c>claude</c>, <c>cursor</c>, <c>gemini</c>, <c>copilot</c>) plus the <c>check</c> dry-run.
/// Each processor reads an agent-specific PreToolUse JSON payload from stdin, extracts the shell
/// command, runs it through the rewrite/permission pipeline, and emits an agent-specific JSON
/// decision on stdout. Faithful port of Rust <c>src/hooks/hook_cmd.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never-block contract.</b> A hook must never break the host: malformed or missing input is
/// tolerated. Claude / Cursor / Copilot always exit 0 (Cursor emits <c>{}</c>, the others emit
/// nothing) on bad input. Gemini is the lone exception — like the Rust source it propagates a JSON
/// parse error as exit 1 — so that behaviour is ported exactly rather than smoothed over.
/// </para>
/// <para>
/// <b>Byte-exact output.</b> rtk builds serde_json <c>Value</c>s with the <c>preserve_order</c>
/// feature, so response key order is insertion order (not alphabetical). This port constructs
/// <see cref="JsonObject"/>s in the same insertion order and serializes with
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> (which escapes only what serde
/// escapes) so the emitted bytes match the oracle.
/// </para>
/// </remarks>
public static class HookCommand
{
    /// <summary>Stdin read cap, matching Rust <c>STDIN_CAP</c> (1 MiB).</summary>
    private const int StdinCap = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// Dispatches <c>rtk hook &lt;subcommand&gt;</c> to the matching processor.
    /// </summary>
    /// <param name="args">The arguments following <c>hook</c> (subcommand + remainder).</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("rtk hook: missing subcommand (claude|cursor|gemini|copilot|check)");
            return 1;
        }

        var sub = args[0];
        var rest = args[1..];
        return sub switch
        {
            "claude" => RunClaude(),
            "cursor" => RunCursor(),
            "gemini" => RunGemini(),
            "copilot" => RunCopilot(),
            "check" => RunCheck(rest),
            _ => UnknownSubcommand(sub),
        };
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"rtk hook: unknown subcommand '{sub}'");
        return 1;
    }

    // ── Live entry points ────────────────────────────────────────────────

    /// <summary>Runs the Claude Code PreToolUse hook (stdin JSON → stdout decision).</summary>
    private static int RunClaude()
    {
        var status = TryReadStdin(out var input);
        if (status == StdinReadStatus.TooLarge)
        {
            return ReportStdinCapExceeded();
        }

        if (status != StdinReadStatus.Ok)
        {
            return 0;
        }

        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        var output = ProcessClaude(trimmed, PermissionRules.Default);
        if (output is not null)
        {
            WriteLine(output);
        }

        return 0;
    }

    /// <summary>Runs the Cursor Agent hook. Always emits exactly one JSON line (<c>{}</c> when deferring).</summary>
    private static int RunCursor()
    {
        var status = TryReadStdin(out var raw);
        if (status == StdinReadStatus.TooLarge)
        {
            return ReportStdinCapExceeded();
        }

        var input = status == StdinReadStatus.Ok ? raw : "";
        WriteLine(ProcessCursor(input, PermissionRules.ForHost(PermissionHost.Cursor)));
        return 0;
    }

    /// <summary>Runs the Gemini CLI BeforeTool hook. Exits 1 on unparseable input (mirrors Rust).</summary>
    private static int RunGemini()
    {
        var status = TryReadStdin(out var input);
        if (status == StdinReadStatus.TooLarge)
        {
            return ReportStdinCapExceeded();
        }

        if (status != StdinReadStatus.Ok)
        {
            return 0;
        }

        var (output, exit) = ProcessGemini(input, PermissionRules.ForHost(PermissionHost.Gemini));
        if (output is not null)
        {
            WriteLine(output);
        }

        return exit;
    }

    /// <summary>Runs the Copilot preToolUse hook (auto-detects VS Code Copilot Chat vs Copilot CLI).</summary>
    private static int RunCopilot()
    {
        var status = TryReadStdin(out var input);
        if (status == StdinReadStatus.TooLarge)
        {
            return ReportStdinCapExceeded();
        }

        if (status != StdinReadStatus.Ok)
        {
            return 0;
        }

        var cleaned = StripLeadingBom(input).Trim();
        if (cleaned.Length == 0)
        {
            return 0;
        }

        var output = ProcessCopilot(cleaned, PermissionRules.Default);
        if (output is not null)
        {
            WriteLine(output);
        }

        return 0;
    }

    /// <summary>
    /// Runs <c>rtk hook check [--agent &lt;a&gt;] &lt;command...&gt;</c>: a dry-run that prints the
    /// rewritten command (exit 0) or reports no rewrite (exit 1). The agent is accepted but ignored,
    /// matching Rust (<c>HookCommands::Check { agent: _, .. }</c>, main.rs:2279) — it only runs the
    /// rewrite engine, never the permission verdict.
    /// </summary>
    /// <param name="args">The <c>check</c> remainder: optional <c>--agent &lt;value&gt;</c> then the command words.</param>
    /// <returns>0 if a rewrite was produced; otherwise 1.</returns>
    private static int RunCheck(string[] args)
    {
        var raw = string.Join(' ', StripAgentFlag(args));
        var rewritten = RewriteEngine.RewriteCommand(raw, [], []);
        if (rewritten is not null)
        {
            Console.Out.Write(rewritten);
            Console.Out.Write('\n');
            return 0;
        }

        Console.Error.WriteLine($"No rewrite for: {raw}");
        return 1;
    }

    /// <summary>Drops a leading <c>--agent &lt;value&gt;</c> / <c>--agent=value</c> option from the check args.</summary>
    private static string[] StripAgentFlag(string[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        if (args[0] == "--agent")
        {
            return args.Length >= 2 ? args[2..] : [];
        }

        if (args[0].StartsWith("--agent=", StringComparison.Ordinal))
        {
            return args[1..];
        }

        return args;
    }

    // ── Processors (pure; rules injected so tests stay config-independent) ─

    /// <summary>
    /// Processes a Claude Code payload. Returns the response JSON, or <see langword="null"/> when
    /// the hook defers (non-rewritable, deny, unattestable, non-Bash, malformed, or no command).
    /// </summary>
    /// <param name="input">The raw stdin JSON.</param>
    /// <param name="rules">The permission rules to evaluate against.</param>
    /// <returns>The response JSON string, or <see langword="null"/> to emit nothing.</returns>
    internal static string? ProcessClaude(string input, PermissionRuleSet rules)
    {
        if (ParseWithDiagnostic(input) is not JsonObject root ||
            root["tool_input"] is not JsonObject toolInput ||
            CommandOf(toolInput) is not { } cmd)
        {
            return null;
        }

        var decision = Decide(cmd, rules);
        if (decision.Kind is HookDecisionKind.Deny or HookDecisionKind.Defer)
        {
            return null;
        }

        var updatedInput = (JsonObject)toolInput.DeepClone();
        updatedInput["command"] = decision.Rewritten;

        var hookOutput = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecisionReason"] = "RTK auto-rewrite",
            ["updatedInput"] = updatedInput,
        };
        if (decision.Kind == HookDecisionKind.AllowRewrite)
        {
            hookOutput["permissionDecision"] = "allow";
        }

        return Serialize(new JsonObject { ["hookSpecificOutput"] = hookOutput });
    }

    /// <summary>
    /// Processes a Cursor payload. Cursor applies a rewrite only on an explicit allow; every other
    /// outcome (defer, deny, empty, malformed) collapses to <c>{}</c>. Always returns a string.
    /// </summary>
    /// <param name="input">The raw stdin JSON (BOM/whitespace tolerated).</param>
    /// <param name="rules">The permission rules to evaluate against.</param>
    /// <returns>The response JSON string (never <see langword="null"/>).</returns>
    internal static string ProcessCursor(string input, PermissionRuleSet rules)
    {
        var cleaned = StripLeadingBom(input).Trim();
        if (cleaned.Length == 0)
        {
            return "{}";
        }

        if (Parse(input: cleaned) is not JsonObject root ||
            root["tool_input"] is not JsonObject toolInput ||
            CommandOf(toolInput) is not { } cmd)
        {
            return "{}";
        }

        var decision = Decide(cmd, rules);
        if (decision.Kind != HookDecisionKind.AllowRewrite)
        {
            return "{}";
        }

        var response = new JsonObject
        {
            ["continue"] = true,
            ["permission"] = "allow",
            ["updated_input"] = new JsonObject { ["command"] = decision.Rewritten },
        };
        return Serialize(response);
    }

    /// <summary>
    /// Processes a Gemini payload. Returns the response JSON and exit code. Unparseable input yields
    /// <c>(null, 1)</c> — Gemini is the one processor that propagates a parse failure as exit 1.
    /// </summary>
    /// <param name="input">The raw stdin JSON.</param>
    /// <param name="rules">The permission rules to evaluate against.</param>
    /// <returns>The response JSON (or <see langword="null"/>) paired with the exit code.</returns>
    internal static (string? Output, int Exit) ProcessGemini(string input, PermissionRuleSet rules)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(input);
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"rtk: Failed to parse hook input as JSON: {e.Message}");
            return (null, 1);
        }

        var toolName = (root as JsonObject) is { } obj && StringOf(obj["tool_name"]) is { } t ? t : "";
        if (toolName != "run_shell_command")
        {
            return (GeminiJson("allow", null), 0);
        }

        var cmd = CommandOf((root as JsonObject)?["tool_input"] as JsonObject);
        if (cmd is null)
        {
            return (GeminiJson("allow", null), 0);
        }

        var decision = Decide(cmd, rules);
        return decision.Kind switch
        {
            HookDecisionKind.Deny => ("{\"decision\":\"deny\",\"reason\":\"Blocked by RTK permission rule\"}", 0),
            HookDecisionKind.AllowRewrite => (GeminiJson("allow", decision.Rewritten), 0),
            HookDecisionKind.AskRewrite => (GeminiJson("ask_user", decision.Rewritten), 0),
            _ => (GeminiJson("ask_user", null), 0),
        };
    }

    /// <summary>
    /// Processes a Copilot payload, auto-detecting the VS Code Copilot Chat (snake_case
    /// <c>tool_name</c> + <c>tool_input</c>) vs GitHub Copilot CLI (camelCase <c>toolName</c> +
    /// JSON-string <c>toolArgs</c>) format. Returns the response JSON, or <see langword="null"/> to
    /// emit nothing (non-bash tool, deferral, deny, or unrecognized format).
    /// </summary>
    /// <param name="input">The raw stdin JSON (BOM/whitespace already stripped by the caller).</param>
    /// <param name="rules">The permission rules to evaluate against.</param>
    /// <returns>The response JSON string, or <see langword="null"/>.</returns>
    internal static string? ProcessCopilot(string input, PermissionRuleSet rules)
    {
        if (ParseWithDiagnostic(input) is not JsonObject root)
        {
            return null;
        }

        // VS Code Copilot Chat / Claude Code: snake_case keys.
        if (StringOf(root["tool_name"]) is { } toolName)
        {
            if (toolName is "runTerminalCommand" or "Bash" or "bash" &&
                CommandOf(root["tool_input"] as JsonObject) is { } cmd)
            {
                return CopilotVsCode(cmd, rules);
            }

            return null;
        }

        // GitHub Copilot CLI: camelCase keys, toolArgs is a JSON-encoded string.
        if (StringOf(root["toolName"]) is { } cliTool)
        {
            if (cliTool == "bash" && StringOf(root["toolArgs"]) is { } toolArgsStr &&
                Parse(toolArgsStr) is JsonObject toolArgs &&
                CommandOf(toolArgs) is { } cliCmd)
            {
                return CopilotCli(cliCmd, toolArgs, rules);
            }
        }

        return null;
    }

    private static string? CopilotVsCode(string cmd, PermissionRuleSet rules)
    {
        var decision = Decide(cmd, rules);
        var permission = decision.Kind switch
        {
            HookDecisionKind.AllowRewrite => "allow",
            HookDecisionKind.AskRewrite => "ask",
            _ => null,
        };
        if (permission is null)
        {
            return null;
        }

        var hookOutput = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = permission,
            ["permissionDecisionReason"] = "RTK auto-rewrite",
            ["updatedInput"] = new JsonObject { ["command"] = decision.Rewritten },
        };
        return Serialize(new JsonObject { ["hookSpecificOutput"] = hookOutput });
    }

    private static string? CopilotCli(string cmd, JsonObject toolArgs, PermissionRuleSet rules)
    {
        var decision = Decide(cmd, rules);
        var allow = decision.Kind switch
        {
            HookDecisionKind.AllowRewrite => true,
            HookDecisionKind.AskRewrite => false,
            _ => (bool?)null,
        };
        if (allow is null)
        {
            return null;
        }

        var modified = (JsonObject)toolArgs.DeepClone();
        modified["command"] = decision.Rewritten;

        var response = new JsonObject
        {
            ["permissionDecisionReason"] = "RTK auto-rewrite",
            ["modifiedArgs"] = modified,
        };
        if (allow.Value)
        {
            response["permissionDecision"] = "allow";
        }

        return Serialize(response);
    }

    // ── Shared decision flow ─────────────────────────────────────────────

    private enum HookDecisionKind
    {
        AllowRewrite,
        AskRewrite,
        Defer,
        Deny,
    }

    private readonly record struct HookDecision(HookDecisionKind Kind, string Rewritten);

    /// <summary>
    /// Maps a permission verdict + rewrite availability to a hook decision. Port of Rust
    /// <c>decide_from_verdict</c> (hook_cmd.rs:135): deny wins; an unattestable construct defers;
    /// otherwise a found rewrite is auto-allowed only when the verdict is Allow, else it is an ask.
    /// </summary>
    private static HookDecision Decide(string cmd, PermissionRuleSet rules)
    {
        var verdict = Permissions.CheckCommandWithRules(cmd, rules.Deny, rules.Ask, rules.Allow);
        if (verdict == PermissionVerdict.Deny)
        {
            return new HookDecision(HookDecisionKind.Deny, "");
        }

        if (ShellLexer.ContainsUnattestableConstruct(cmd))
        {
            return new HookDecision(HookDecisionKind.Defer, "");
        }

        var rewritten = GetRewritten(cmd);
        if (rewritten is null)
        {
            return new HookDecision(HookDecisionKind.Defer, "");
        }

        return verdict == PermissionVerdict.Allow
            ? new HookDecision(HookDecisionKind.AllowRewrite, rewritten)
            : new HookDecision(HookDecisionKind.AskRewrite, rewritten);
    }

    /// <summary>
    /// Returns the rewritten command, or <see langword="null"/> when no rewrite applies (or the
    /// rewrite is a no-op). Port of Rust <c>get_rewritten</c> (hook_cmd.rs:110); config-driven
    /// exclude/transparent-prefix lists remain out of scope so empty lists are passed.
    /// </summary>
    private static string? GetRewritten(string cmd)
    {
        var rewritten = RewriteEngine.RewriteCommand(cmd, [], []);
        return rewritten is null || rewritten == cmd ? null : rewritten;
    }

    // ── JSON / stdin helpers ─────────────────────────────────────────────

    private static string GeminiJson(string decision, string? rewrite)
    {
        var output = new JsonObject { ["decision"] = decision };
        if (rewrite is not null)
        {
            output["hookSpecificOutput"] = new JsonObject
            {
                ["tool_input"] = new JsonObject { ["command"] = rewrite },
            };
        }

        return Serialize(output);
    }

    /// <summary>Parses JSON, returning <see langword="null"/> on any parse error (never throws).</summary>
    private static JsonNode? Parse(string input)
    {
        try
        {
            return JsonNode.Parse(input);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses JSON, writing the Rust-equivalent diagnostic to stderr on failure. Port of the
    /// explicit <c>match serde_json::from_str(input)</c> in Rust <c>run_claude</c>
    /// (hook_cmd.rs:406-412) and <c>run_copilot</c> (hook_cmd.rs:54-60), which write
    /// <c>[rtk hook] Failed to parse JSON input: {e}</c> to stderr before deferring silently.
    /// Only the top-level Claude/Copilot payload parse gets this diagnostic — Cursor and the
    /// Copilot-CLI <c>toolArgs</c> re-parse stay silent via <see cref="Parse"/>, matching Rust.
    /// </summary>
    /// <param name="input">The raw JSON text to parse.</param>
    /// <returns>The parsed node, or <see langword="null"/> on failure.</returns>
    private static JsonNode? ParseWithDiagnostic(string input)
    {
        try
        {
            return JsonNode.Parse(input);
        }
        catch (JsonException e)
        {
            Console.Error.Write($"[rtk hook] Failed to parse JSON input: {e.Message}\n");
            return null;
        }
    }

    /// <summary>Extracts a non-empty string <c>command</c> from a <c>tool_input</c>/args object.</summary>
    private static string? CommandOf(JsonObject? toolInput)
    {
        if (toolInput is null || StringOf(toolInput["command"]) is not { } cmd || cmd.Length == 0)
        {
            return null;
        }

        return cmd;
    }

    /// <summary>Returns the string value of a node, or <see langword="null"/> if it is not a JSON string.</summary>
    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static string Serialize(JsonNode node) => node.ToJsonString(JsonOptions);

    /// <summary>
    /// Strips one or more leading UTF-8 BOMs. Some Windows hosts (confirmed for Cursor) prepend
    /// BOMs to hook stdin, which the JSON parser rejects. Port of Rust <c>strip_leading_bom</c>.
    /// </summary>
    private static string StripLeadingBom(string input)
    {
        var i = 0;
        while (i < input.Length && input[i] == '﻿')
        {
            i++;
        }

        return i == 0 ? input : input[i..];
    }

    /// <summary>Writes a JSON line terminated by a bare <c>\n</c> (matching Rust <c>writeln!</c>).</summary>
    private static void WriteLine(string json)
    {
        Console.Out.Write(json);
        Console.Out.Write('\n');
    }

    /// <summary>
    /// Outcome of a stdin read. Port of the Rust distinction between a hard cap overflow
    /// (<c>read_stdin_limited</c>, hook_cmd.rs:16-26, which <c>bail!</c>s and propagates to
    /// <c>main</c>'s <c>exit 1</c>) and any other read failure, which the never-block contract
    /// tolerates as if stdin were empty.
    /// </summary>
    internal enum StdinReadStatus
    {
        /// <summary>Stdin was read successfully (may be empty).</summary>
        Ok,

        /// <summary>The underlying stream read failed; treated as no input (exit 0).</summary>
        IoError,

        /// <summary>Stdin exceeded <see cref="StdinCap"/> bytes; the caller must exit 1.</summary>
        TooLarge,
    }

    /// <summary>
    /// Writes the Rust <c>read_stdin_limited</c> overflow diagnostic to stderr and returns the
    /// exit-1 code. Port of the <c>bail!("hook stdin exceeds {} byte limit", STDIN_CAP)</c> in
    /// hook_cmd.rs:23, formatted by <c>main</c> (main.rs:1439) as <c>rtk: {:#}</c>.
    /// </summary>
    /// <returns>Always <c>1</c>.</returns>
    internal static int ReportStdinCapExceeded()
    {
        Console.Error.Write($"rtk: hook stdin exceeds {StdinCap} byte limit\n");
        return 1;
    }

    /// <summary>
    /// Reads stdin as raw UTF-8 without stripping a BOM (so BOM-aware processors can handle it).
    /// </summary>
    /// <param name="input">The stdin text read so far (empty unless the status is <see cref="StdinReadStatus.Ok"/>).</param>
    /// <returns>The read outcome; see <see cref="StdinReadStatus"/>.</returns>
    private static StdinReadStatus TryReadStdin(out string input)
    {
        using var stream = Console.OpenStandardInput();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false);
        return TryReadStdin(reader, out input);
    }

    /// <summary>
    /// Reads to the end of <paramref name="reader"/>, applying the same cap/error semantics as
    /// <see cref="TryReadStdin(out string)"/>. Split out as an internal overload so the overflow
    /// path is unit-testable without a real stdin stream.
    /// </summary>
    /// <param name="reader">The text reader to consume.</param>
    /// <param name="input">The text read (empty unless the status is <see cref="StdinReadStatus.Ok"/>).</param>
    /// <returns>The read outcome; see <see cref="StdinReadStatus"/>.</returns>
    internal static StdinReadStatus TryReadStdin(TextReader reader, out string input)
    {
        input = "";
        try
        {
            var text = reader.ReadToEnd();
            if (text.Length > StdinCap)
            {
                return StdinReadStatus.TooLarge;
            }

            input = text;
            return StdinReadStatus.Ok;
        }
        catch (IOException)
        {
            return StdinReadStatus.IoError;
        }
    }
}
