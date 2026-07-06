using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RtkSharp.Hooks;

namespace RtkSharp.Discover;

/// <summary>
/// A single Bash-tool invocation extracted from a Claude Code session transcript, paired with its
/// tool result (if any). Faithful port of Rust <c>ExtractedCommand</c> (<c>discover/provider.rs</c>:14-26).
/// </summary>
/// <param name="Command">The raw shell command string passed to the <c>Bash</c> tool.</param>
/// <param name="OutputLen">The tool result's content length in UTF-16 code units, or <see langword="null"/> if no matching result was found (mirrors Rust's byte-length <c>content.len()</c> — see class remarks on this port's one disclosed unit difference).</param>
/// <param name="SessionId">The session transcript's file stem (unused by any current caller, kept for parity with the Rust struct's field).</param>
/// <param name="OutputContent">The first 1000 characters of the tool result's content, or <see langword="null"/> if none.</param>
/// <param name="IsError">Whether the tool result's <c>is_error</c> flag was set.</param>
/// <param name="SequenceIndex">The command's chronological order within the session (0-based, assigned as <c>tool_use</c> blocks are encountered).</param>
public sealed record ExtractedCommand(
    string Command,
    int? OutputLen,
    string SessionId,
    string? OutputContent,
    bool IsError,
    int SequenceIndex);

/// <summary>
/// Reads Claude Code session transcripts (<c>~/.claude/projects/**/*.jsonl</c>) from disk and
/// extracts their <c>Bash</c> tool-use/tool-result command history. Faithful port of Rust
/// <c>discover::provider::ClaudeProvider</c> (<c>discover/provider.rs</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Disclosed unit difference: UTF-16 code units vs. UTF-8 bytes for <see cref="ExtractedCommand.OutputLen"/>.</b>
/// Rust's <c>content.len()</c> (<c>provider.rs</c>:226) is the tool result string's UTF-8 byte length;
/// this port uses .NET <see cref="string.Length"/> (UTF-16 code units) for the same field. The two
/// units only diverge for non-ASCII content, and <see cref="ExtractedCommand.OutputLen"/> is only
/// ever summed into <c>rtk session</c>'s "Output" column via <see cref="Core.Utils.FormatTokens"/> —
/// a rough token-count display, not a byte-exact metric — so this divergence is accepted rather than
/// hand-rolling a UTF-8 byte-count pass over content this port never otherwise needs as UTF-8.
/// </para>
/// </remarks>
public static class ClaudeProvider
{
    /// <summary>
    /// Resolves <c>{resolve_claude_dir()}/projects</c> — the base directory Claude Code stores
    /// per-project session transcripts under. Faithful port of <c>ClaudeProvider::projects_dir</c>
    /// (<c>provider.rs</c>:45-49).
    /// </summary>
    /// <returns>The resolved projects directory path.</returns>
    public static string ProjectsDir() => Path.Combine(SettingsPatcher.ResolveClaudeDir(), "projects");

    /// <summary>
    /// Discovers every <c>.jsonl</c> session-transcript file under <see cref="ProjectsDir"/>,
    /// recursing into subdirectories (so per-agent <c>subagents/</c> files are found too), optionally
    /// filtered by a case-sensitive substring match on the project directory name and/or a
    /// last-modified-within-N-days cutoff. Faithful port of the <c>SessionProvider::discover_sessions</c>
    /// trait method (<c>provider.rs</c>:143-150).
    /// </summary>
    /// <param name="projectFilter">A substring to match against each project directory's name, or <see langword="null"/> for no filter.</param>
    /// <param name="sinceDays">Only include files modified within this many days, or <see langword="null"/> for no cutoff.</param>
    /// <returns>The matching session-transcript file paths, in no particular order.</returns>
    public static List<string> DiscoverSessions(string? projectFilter, ulong? sinceDays) =>
        DiscoverSessionsInProjectsDir(ProjectsDir(), projectFilter, sinceDays);

    /// <summary>
    /// The testable core of <see cref="DiscoverSessions"/>, taking the projects directory explicitly.
    /// Faithful port of <c>ClaudeProvider::discover_sessions_in_projects_dir</c>
    /// (<c>provider.rs</c>:51-116).
    /// </summary>
    /// <param name="projectsDir">The projects directory to scan.</param>
    /// <param name="projectFilter">A substring to match against each project directory's name, or <see langword="null"/> for no filter.</param>
    /// <param name="sinceDays">Only include files modified within this many days, or <see langword="null"/> for no cutoff.</param>
    /// <returns>The matching session-transcript file paths, or an empty list if <paramref name="projectsDir"/> doesn't exist.</returns>
    internal static List<string> DiscoverSessionsInProjectsDir(string projectsDir, string? projectFilter, ulong? sinceDays)
    {
        if (!Directory.Exists(projectsDir))
        {
            return [];
        }

        var cutoff = sinceDays is { } days ? DateTime.UtcNow - TimeSpan.FromDays(days) : (DateTime?)null;
        var sessions = new List<string>();

        foreach (var projectDir in Directory.EnumerateDirectories(projectsDir))
        {
            var dirName = Path.GetFileName(projectDir);
            if (projectFilter is not null && !dirName.Contains(projectFilter, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.AllDirectories))
            {
                if (cutoff is { } cutoffTime && File.GetLastWriteTimeUtc(filePath) < cutoffTime)
                {
                    continue;
                }

                sessions.Add(filePath);
            }
        }

        return sessions;
    }

    /// <summary>
    /// Encodes a filesystem path to Claude Code's directory-name slug format: every <c>/</c>, <c>.</c>,
    /// <c>_</c>, <c>\</c>, space, <c>[</c>, <c>]</c>, or non-ASCII character becomes <c>-</c>. Faithful
    /// port of <c>ClaudeProvider::encode_project_path</c> (<c>provider.rs</c>:127-139).
    /// </summary>
    /// <param name="path">The filesystem path to encode.</param>
    /// <returns>The encoded project-directory slug.</returns>
    public static string EncodeProjectPath(string path)
    {
        const string sanitizedChars = "/.\\_ []";
        var chars = new char[path.Length];
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            chars[i] = !char.IsAscii(c) || sanitizedChars.Contains(c) ? '-' : c;
        }

        return new string(chars);
    }

    /// <summary>
    /// Parses a single session-transcript file, extracting every <c>Bash</c> tool-use call paired
    /// with its tool result (if any). Faithful port of <c>SessionProvider::extract_commands</c>
    /// (<c>provider.rs</c>:152-267): a single forward pass collects <c>tool_use</c> Bash commands (in
    /// encounter order) and <c>tool_result</c> blocks (keyed by <c>tool_use_id</c>) into two
    /// collections, then a second pass joins them — matching Rust's own two-collection design rather
    /// than a naive single-pass join, since a <c>tool_result</c> can appear on a later line than its
    /// <c>tool_use</c>. Malformed JSON lines and lines that cannot possibly contain a relevant block
    /// (pre-filtered via a substring check, matching Rust's own optimization) are silently skipped —
    /// never thrown, matching the "never block the user" fallback pattern for what is ultimately a
    /// best-effort analytics read.
    /// </summary>
    /// <param name="path">The session-transcript file to parse.</param>
    /// <returns>The extracted commands, in their original chronological order.</returns>
    public static List<ExtractedCommand> ExtractCommands(string path)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = "unknown";
        }

        var pendingToolUses = new List<(string ToolUseId, string Command, int SequenceIndex)>();
        var toolResults = new Dictionary<string, (int Length, string Content, bool IsError)>(StringComparer.Ordinal);
        var sequenceCounter = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("\"Bash\"", StringComparison.Ordinal) && !line.Contains("\"tool_result\"", StringComparison.Ordinal))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entryType = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                    ? typeProp.GetString()
                    : "";

                if (entryType == "assistant")
                {
                    foreach (var block in EnumerateContentBlocks(root))
                    {
                        if (GetString(block, "type") == "tool_use" && GetString(block, "name") == "Bash")
                        {
                            var id = GetString(block, "id");
                            var command = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                                ? GetString(input, "command")
                                : null;

                            if (id is not null && command is not null)
                            {
                                pendingToolUses.Add((id, command, sequenceCounter));
                                sequenceCounter++;
                            }
                        }
                    }
                }
                else if (entryType == "user")
                {
                    foreach (var block in EnumerateContentBlocks(root))
                    {
                        if (GetString(block, "type") != "tool_result")
                        {
                            continue;
                        }

                        var id = GetString(block, "tool_use_id");
                        if (id is null)
                        {
                            continue;
                        }

                        var content = GetString(block, "content") ?? "";
                        var isError = block.TryGetProperty("is_error", out var isErrorProp) && isErrorProp.ValueKind == JsonValueKind.True;
                        var contentPreview = content.Length > 1000 ? content[..1000] : content;

                        toolResults[id] = (content.Length, contentPreview, isError);
                    }
                }
            }
        }

        var commands = new List<ExtractedCommand>(pendingToolUses.Count);
        foreach (var (toolId, command, sequenceIndex) in pendingToolUses)
        {
            var (outputLen, outputContent, isError) = toolResults.TryGetValue(toolId, out var result)
                ? ((int?)result.Length, (string?)result.Content, result.IsError)
                : (null, null, false);

            commands.Add(new ExtractedCommand(command, outputLen, sessionId, outputContent, isError, sequenceIndex));
        }

        return commands;
    }

    private static IEnumerable<JsonElement> EnumerateContentBlocks(JsonElement root)
    {
        if (!TryGetPointer(root, "message", "content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            yield return block;
        }
    }

    private static bool TryGetPointer(JsonElement root, string first, string second, out JsonElement result)
    {
        // The ValueKind guard on the first hop matters: JsonElement.TryGetProperty throws
        // InvalidOperationException when called on a non-object element, whereas Rust's
        // serde_json::Value::pointer (provider.rs:192/216) simply returns None for the same shape
        // (e.g. a line where "message" is a bare string, not an object) — without this guard, a line
        // that happens to contain the pre-filter substring but doesn't match the expected shape would
        // crash this parse instead of being skipped like every other malformed/unexpected line.
        if (root.TryGetProperty(first, out var firstProp)
            && firstProp.ValueKind == JsonValueKind.Object
            && firstProp.TryGetProperty(second, out result))
        {
            return true;
        }

        result = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
