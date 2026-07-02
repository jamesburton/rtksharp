using System;
using System.Collections.Generic;
using System.Linq;

namespace RtkSharp.Rewrite;

/// <summary>
/// Classifies a lexed shell token.
/// </summary>
public enum TokenKind
{
    /// <summary>A plain argument or word.</summary>
    Arg,

    /// <summary>A control operator such as <c>;</c>, <c>&amp;&amp;</c>, or <c>||</c>.</summary>
    Operator,

    /// <summary>A pipe operator (<c>|</c>).</summary>
    Pipe,

    /// <summary>A redirect operator such as <c>&gt;</c>, <c>&gt;&gt;</c>, or <c>&lt;</c>.</summary>
    Redirect,

    /// <summary>A shell metacharacter with special meaning (e.g. <c>*</c>, <c>$</c>, backtick, <c>(</c>, <c>)</c>).</summary>
    Shellism
}

/// <summary>
/// A single lexed token from a shell command string.
/// </summary>
/// <param name="Kind">The token's classification.</param>
/// <param name="Value">The token's raw text value.</param>
/// <param name="Offset">The character offset of the token within the original input.</param>
public record ParsedToken(
    TokenKind Kind,
    string Value,
    int Offset
);

/// <summary>
/// A lightweight, quote- and escape-aware shell command lexer used to split and
/// classify command strings without invoking an actual shell.
/// </summary>
public static class ShellLexer
{
    /// <summary>
    /// Tokenizes a shell command string, treating newlines as whitespace.
    /// </summary>
    /// <param name="input">The command string to tokenize.</param>
    /// <returns>The list of parsed tokens.</returns>
    public static List<ParsedToken> Tokenize(string input)
    {
        return TokenizeInner(input, false);
    }

    /// <summary>
    /// Tokenizes a shell command string, optionally emitting newlines as explicit
    /// <see cref="TokenKind.Operator"/> tokens instead of treating them as whitespace.
    /// </summary>
    /// <param name="input">The command string to tokenize.</param>
    /// <param name="emitNewline">Whether newlines should be emitted as operator tokens.</param>
    /// <returns>The list of parsed tokens.</returns>
    public static List<ParsedToken> TokenizeInner(string input, bool emitNewline)
    {
        var tokens = new List<ParsedToken>();
        var current = new System.Text.StringBuilder();
        int currentStart = 0;
        int charPos = 0;

        char? quote = null;
        bool escaped = false;

        while (charPos < input.Length)
        {
            char c = input[charPos];
            int charLen = 1;

            if (escaped)
            {
                current.Append('\\');
                current.Append(c);
                charPos += charLen;
                escaped = false;
                continue;
            }

            if (c == '\\' && quote != '\'')
            {
                escaped = true;
                if (current.Length == 0)
                {
                    currentStart = charPos;
                }
                charPos += charLen;
                continue;
            }

            if (quote.HasValue)
            {
                if (c == quote.Value)
                {
                    quote = null;
                }
                current.Append(c);
                charPos += charLen;
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                if (current.Length == 0)
                {
                    currentStart = charPos;
                }
                current.Append(c);
                charPos += charLen;
                continue;
            }

            switch (c)
            {
                case '$':
                    FlushArg(tokens, current, currentStart);
                    int startDollar = charPos;
                    charPos += charLen;
                    if (charPos < input.Length && (char.IsAsciiLetter(input[charPos]) || input[charPos] == '_'))
                    {
                        var name = new System.Text.StringBuilder("$");
                        while (charPos < input.Length)
                        {
                            char nc = input[charPos];
                            if (!char.IsAsciiLetterOrDigit(nc) && nc != '_')
                            {
                                break;
                            }
                            charPos++;
                            name.Append(nc);
                        }
                        tokens.Add(new ParsedToken(TokenKind.Arg, name.ToString(), startDollar));
                    }
                    else
                    {
                        tokens.Add(new ParsedToken(TokenKind.Shellism, "$", startDollar));
                    }
                    currentStart = charPos;
                    break;

                case '*':
                case '?':
                case '`':
                case '(':
                case ')':
                case '{':
                case '}':
                case '!':
                    FlushArg(tokens, current, currentStart);
                    tokens.Add(new ParsedToken(TokenKind.Shellism, c.ToString(), charPos));
                    charPos += charLen;
                    currentStart = charPos;
                    break;

                case '|':
                    FlushArg(tokens, current, currentStart);
                    int startPipe = charPos;
                    charPos += charLen;
                    if (charPos < input.Length && input[charPos] == '|')
                    {
                        charPos++;
                        tokens.Add(new ParsedToken(TokenKind.Operator, "||", startPipe));
                    }
                    else
                    {
                        tokens.Add(new ParsedToken(TokenKind.Pipe, "|", startPipe));
                    }
                    currentStart = charPos;
                    break;

                case ';':
                    FlushArg(tokens, current, currentStart);
                    tokens.Add(new ParsedToken(TokenKind.Operator, ";", charPos));
                    charPos += charLen;
                    currentStart = charPos;
                    break;

                case '&':
                    FlushArg(tokens, current, currentStart);
                    int startAmp = charPos;
                    charPos += charLen;
                    if (charPos < input.Length && input[charPos] == '&')
                    {
                        charPos++;
                        tokens.Add(new ParsedToken(TokenKind.Operator, "&&", startAmp));
                    }
                    else if (charPos < input.Length && input[charPos] == '>')
                    {
                        charPos++;
                        var val = new System.Text.StringBuilder("&>");
                        if (charPos < input.Length && input[charPos] == '>')
                        {
                            charPos++;
                            val.Append('>');
                        }
                        tokens.Add(new ParsedToken(TokenKind.Redirect, val.ToString(), startAmp));
                    }
                    else
                    {
                        tokens.Add(new ParsedToken(TokenKind.Shellism, "&", startAmp));
                    }
                    currentStart = charPos;
                    break;

                case '>':
                    string fdPrefix = "";
                    var currentStr = current.ToString();
                    if (current.Length > 0 && currentStr.All(char.IsAsciiDigit))
                    {
                        fdPrefix = currentStr;
                        current.Clear();
                    }
                    else
                    {
                        FlushArg(tokens, current, currentStart);
                    }

                    int redirStart = fdPrefix.Length > 0 ? currentStart : charPos;
                    var valRedir = new System.Text.StringBuilder(fdPrefix);
                    valRedir.Append('>');
                    charPos += charLen;

                    if (charPos < input.Length && input[charPos] == '>')
                    {
                        charPos++;
                        valRedir.Append('>');
                    }
                    if (charPos < input.Length && input[charPos] == '&')
                    {
                        charPos++;
                        valRedir.Append('&');
                        while (charPos < input.Length)
                        {
                            char nc = input[charPos];
                            if (!char.IsAsciiDigit(nc) && nc != '-')
                            {
                                break;
                            }
                            charPos++;
                            valRedir.Append(nc);
                        }
                    }
                    tokens.Add(new ParsedToken(TokenKind.Redirect, valRedir.ToString(), redirStart));
                    currentStart = charPos;
                    break;

                case '<':
                    FlushArg(tokens, current, currentStart);
                    int startLess = charPos;
                    var valLess = new System.Text.StringBuilder("<");
                    charPos += charLen;
                    if (charPos < input.Length && input[charPos] == '<')
                    {
                        charPos++;
                        valLess.Append('<');
                    }
                    tokens.Add(new ParsedToken(TokenKind.Redirect, valLess.ToString(), startLess));
                    currentStart = charPos;
                    break;

                case '\n':
                case '\r':
                    if (emitNewline)
                    {
                        FlushArg(tokens, current, currentStart);
                        tokens.Add(new ParsedToken(TokenKind.Operator, "\n", charPos));
                        charPos += charLen;
                        currentStart = charPos;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        FlushArg(tokens, current, currentStart);
                        charPos += charLen;
                        currentStart = charPos;
                    }
                    else
                    {
                        if (current.Length == 0)
                        {
                            currentStart = charPos;
                        }
                        current.Append(c);
                        charPos += charLen;
                    }
                    break;

                default:
                    if (char.IsWhiteSpace(c))
                    {
                        FlushArg(tokens, current, currentStart);
                        charPos += charLen;
                        currentStart = charPos;
                    }
                    else
                    {
                        if (current.Length == 0)
                        {
                            currentStart = charPos;
                        }
                        current.Append(c);
                        charPos += charLen;
                    }
                    break;
            }
        }

        if (escaped)
        {
            current.Append('\\');
        }
        FlushArg(tokens, current, currentStart);
        return tokens;
    }

    private static void FlushArg(List<ParsedToken> tokens, System.Text.StringBuilder current, int offset)
    {
        if (current.Length > 0)
        {
            tokens.Add(new ParsedToken(TokenKind.Arg, current.ToString(), offset));
            current.Clear();
        }
    }

    /// <summary>
    /// Determines whether a command string contains constructs (command/process substitution,
    /// or redirects to a file target) that cannot be safely attested to without a real shell.
    /// </summary>
    /// <param name="cmd">The command string to inspect.</param>
    /// <returns>True if the command contains an unattestable construct.</returns>
    public static bool ContainsUnattestableConstruct(string cmd)
    {
        if (ContainsSubstitution(cmd))
        {
            return true;
        }

        var tokens = Tokenize(cmd);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Redirect && RedirectHasFileTarget(tokens, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSubstitution(string cmd)
    {
        bool inSingle = false;
        bool inDouble = false;
        int i = 0;
        while (i < cmd.Length)
        {
            char c = cmd[i];
            if (c == '\\' && !inSingle)
            {
                i += 2;
                continue;
            }
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '`' && !inSingle)
            {
                return true;
            }
            else if (c == '$' && !inSingle && i + 1 < cmd.Length && cmd[i + 1] == '(')
            {
                return true;
            }
            else if ((c == '<' || c == '>') && !inSingle && !inDouble && i + 1 < cmd.Length && cmd[i + 1] == '(')
            {
                return true;
            }
            i += 1;
        }
        return false;
    }

    private static bool RedirectHasFileTarget(List<ParsedToken> tokens, int i)
    {
        string value = tokens[i].Value;
        int pos = value.IndexOf(">&");
        if (pos >= 0)
        {
            string tail = value.Substring(pos + 2);
            if (tail.Length > 0 && tail.All(c => char.IsAsciiDigit(c) || c == '-'))
            {
                return false;
            }
        }
        if (i + 1 < tokens.Count)
        {
            var next = tokens[i + 1];
            if (next.Kind == TokenKind.Arg)
            {
                return next.Value != "/dev/null";
            }
        }
        return true;
    }

    /// <summary>
    /// Splits a command string into segments at operator/pipe/background boundaries, for
    /// per-segment permission checks. Redirect targets are excluded from returned segments.
    /// </summary>
    /// <param name="cmd">The command string to split.</param>
    /// <returns>The list of command segments.</returns>
    public static List<string> SplitForPermissions(string cmd)
    {
        string trimmed = cmd.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new List<string>();
        }

        var tokens = TokenizeInner(trimmed, true);
        var results = new List<string>();
        int segStart = 0;
        int? segEnd = null;

        foreach (var tok in tokens)
        {
            bool isBoundary = false;
            if (tok.Kind == TokenKind.Operator || tok.Kind == TokenKind.Pipe)
            {
                isBoundary = true;
            }
            else if (tok.Kind == TokenKind.Shellism)
            {
                isBoundary = tok.Value == "&" || tok.Value == "(" || tok.Value == ")";
            }

            if (isBoundary)
            {
                int end = segEnd ?? tok.Offset;
                segEnd = null;
                string segment = trimmed.Substring(segStart, end - segStart).Trim();
                if (!string.IsNullOrEmpty(segment))
                {
                    results.Add(segment);
                }
                segStart = tok.Offset + tok.Value.Length;
            }
            else if (tok.Kind == TokenKind.Redirect && !segEnd.HasValue)
            {
                segEnd = tok.Offset;
            }
        }

        int finalEnd = segEnd ?? trimmed.Length;
        string tail = trimmed.Substring(segStart, finalEnd - segStart).Trim();
        if (!string.IsNullOrEmpty(tail))
        {
            results.Add(tail);
        }

        return results;
    }

    /// <summary>
    /// Splits a command string into segments at control-operator boundaries, optionally
    /// stopping at the first pipe.
    /// </summary>
    /// <param name="cmd">The command string to split.</param>
    /// <param name="stopAtPipe">Whether to stop splitting (and return early) at the first pipe operator.</param>
    /// <returns>The list of command segments.</returns>
    public static List<string> SplitOnOperators(string cmd, bool stopAtPipe)
    {
        string trimmed = cmd.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new List<string>();
        }

        var tokens = Tokenize(trimmed);
        var results = new List<string>();
        int segStart = 0;

        foreach (var tok in tokens)
        {
            if (tok.Kind == TokenKind.Operator)
            {
                string segment = trimmed.Substring(segStart, tok.Offset - segStart).Trim();
                if (!string.IsNullOrEmpty(segment))
                {
                    results.Add(segment);
                }
                segStart = tok.Offset + tok.Value.Length;
            }
            else if (tok.Kind == TokenKind.Pipe)
            {
                string segment = trimmed.Substring(segStart, tok.Offset - segStart).Trim();
                if (!string.IsNullOrEmpty(segment))
                {
                    results.Add(segment);
                }
                if (stopAtPipe)
                {
                    return results;
                }
                segStart = tok.Offset + tok.Value.Length;
            }
        }

        string tail = trimmed.Substring(segStart).Trim();
        if (!string.IsNullOrEmpty(tail))
        {
            results.Add(tail);
        }

        return results;
    }

    /// <summary>
    /// Removes a single matching pair of surrounding single or double quotes from a string, if present.
    /// </summary>
    /// <param name="s">The string to strip quotes from.</param>
    /// <returns>The string without surrounding quotes, or unchanged if not quoted.</returns>
    public static string StripQuotes(string s)
    {
        if (s.Length >= 2 && 
            ((s[0] == '"' && s[s.Length - 1] == '"') || 
             (s[0] == '\'' && s[s.Length - 1] == '\'')))
        {
            return s.Substring(1, s.Length - 2);
        }
        return s;
    }

    /// <summary>
    /// Splits a string on whitespace, honoring single/double quoting and backslash escapes,
    /// similar to POSIX shell word-splitting.
    /// </summary>
    /// <param name="input">The string to split.</param>
    /// <returns>The list of split, unescaped words.</returns>
    public static List<string> ShellSplit(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingle = false;
        bool inDouble = false;
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];
            if (c == '\\' && !inSingle)
            {
                i++;
                if (i < input.Length)
                {
                    current.Append(input[i]);
                }
            }
            else if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if ((c == ' ' || c == '\t') && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
            i++;
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
