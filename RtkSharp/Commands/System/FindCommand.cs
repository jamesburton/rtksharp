using System.Text;
using System.Text.RegularExpressions;
using RtkSharp.Filters.Commands.System;

namespace RtkSharp.Commands.System;

/// <summary>
/// Finds files (or directories) and prints them grouped by directory in a compact, token-lean
/// form. Accepts <b>both</b> native <c>find</c> syntax (<c>find src -name "*.rs" -type f
/// -maxdepth 3</c>) and an RTK-simplified syntax (<c>find *.rs src -m 10 -t d</c>). Results are
/// grouped one line per directory, capped at a result budget with a <c>+N more</c> overflow
/// marker, and followed by a per-extension summary when more than one extension is present.
/// </summary>
/// <remarks>
/// Ported from <c>src/cmds/system/find_cmd.rs</c>. Unlike ls/wc/tree this command does not shell
/// out to an external tool — it walks the filesystem itself, so the Rust <c>ignore::WalkBuilder</c>
/// behavior is reproduced directly:
/// <list type="bullet">
///   <item>Hidden entries (names starting with <c>.</c>) are skipped unless the search pattern
///   itself targets a dotfile (starts with <c>.</c>), mirroring find_cmd.rs's <c>search_hidden</c>
///   logic (issue #1101).</item>
///   <item><c>.gitignore</c> files are respected hierarchically (repo-root <c>.git/info/exclude</c>,
///   each ancestor between the git root and the search path, and each directory descended into),
///   mirroring <c>git_ignore(true)</c>. The git root is located by walking up for a <c>.git</c>
///   entry; when none is found no ignore rules apply. A directory named <c>.git</c> is always
///   pruned.</item>
///   <item><b>Concrete divergence:</b> the Rust <c>ignore</c> crate also enables
///   <c>git_global(true)</c>, which consults the user's global <c>core.excludesFile</c> (typically
///   <c>~/.gitconfig</c>'s <c>[core] excludesFile</c>, e.g. a machine-wide <c>~/.gitignore_global</c>).
///   RtkSharp's from-scratch gitignore engine does not read this file at all. On any machine with a
///   global excludesFile configured, this means <b>RtkSharp will list files that the Rust oracle
///   silently omits</b> (e.g. editor swap files, OS metadata like <c>.DS_Store</c>, or IDE
///   directories commonly placed in a global excludesFile rather than per-repo). This is not an
///   exotic edge case — it is host-config-dependent and will reproducibly diverge from the oracle on
///   any developer machine with a global gitignore set up. See
///   <c>docs/parity/compatibility-ledger.md</c> for the ledger entry.</item>
///   <item>Separately, the gitignore <i>pattern engine itself</i> supports the common syntax
///   (comments, negation, leading/trailing/internal slash anchoring, <c>*</c>, <c>?</c>, and a
///   pragmatic <c>**</c>); genuinely exotic pattern forms (e.g. bracket character classes like
///   <c>[abc]</c>) are approximated rather than fully implemented — see
///   <c>FindCommandTests</c> for the pinned current behavior of specific pattern forms.</item>
/// </list>
/// Token-savings tracking (find_cmd.rs's <c>TimedExecution</c>) is omitted, matching the
/// <see cref="ReadCommand"/> precedent: native commands are not wired to a tracker through the
/// registry and tracking is a metrics side effect that does not change output. The verbose
/// <c>eprintln!</c> diagnostics are likewise omitted (the registry handler carries no verbosity).
/// </remarks>
public static class FindCommand
{
    /// <summary>
    /// Native <c>find</c> flags that RTK recognizes and classifies input as native-find syntax.
    /// </summary>
    private static readonly string[] NativeFindFlags = ["-name", "-type", "-maxdepth", "-iname"];

    /// <summary>
    /// Native <c>find</c> flags involving compound predicates, actions, or semantics RTK cannot
    /// faithfully reproduce; their presence is a hard error directing the user to real <c>find</c>.
    /// Mirrors find_cmd.rs's <c>UNSUPPORTED_FIND_FLAGS</c>.
    /// </summary>
    private static readonly string[] UnsupportedFindFlags =
    [
        "-not", "!", "-or", "-o", "-and", "-a", "-exec", "-execdir", "-delete", "-print0", "-newer",
        "-perm", "-size", "-mtime", "-mmin", "-atime", "-amin", "-ctime", "-cmin", "-empty", "-link",
        "-regex", "-iregex"
    ];

    /// <summary>
    /// Parses the arguments following the <c>find</c> verb (native or RTK syntax), walks the
    /// filesystem, and prints the grouped results to standard output. On an argument error
    /// (unsupported predicate flags or an invalid numeric value) prints <c>rtk: {message}</c> to
    /// standard error and returns 1.
    /// </summary>
    /// <param name="args">The arguments following the <c>find</c> verb.</param>
    /// <returns>0 on success, 1 on an argument error.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        FindArgs parsed;
        try
        {
            parsed = ParseFindArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"rtk: {ex.Message}");
            return Task.FromResult(1);
        }

        Run(parsed, Console.Out);
        return Task.FromResult(0);
    }

    /// <summary>
    /// Classifies and parses <paramref name="args"/> into a <see cref="FindArgs"/>. Empty input
    /// yields the defaults; unsupported predicate flags throw; args containing a native flag
    /// (<c>-name</c>/<c>-type</c>/<c>-maxdepth</c>/<c>-iname</c>) parse as native syntax, otherwise
    /// as RTK syntax. Mirrors find_cmd.rs's <c>parse_find_args</c>.
    /// </summary>
    /// <param name="args">The raw argument vector following the verb.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="ArgumentException">On unsupported predicate flags or an invalid numeric value.</exception>
    internal static FindArgs ParseFindArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return FindArgs.Default();
        }

        if (args.Any(a => UnsupportedFindFlags.Contains(a)))
        {
            throw new ArgumentException(
                "rtk find does not support compound predicates or actions (e.g. -not, -exec). Use `find` directly.");
        }

        return args.Any(a => NativeFindFlags.Contains(a))
            ? ParseNativeFindArgs(args)
            : ParseRtkFindArgs(args);
    }

    /// <summary>
    /// Parses native <c>find</c> syntax: an optional leading path, then <c>-name</c>/<c>-iname</c>/
    /// <c>-type</c>/<c>-maxdepth</c> flags. Unknown flags are reported to stderr and ignored.
    /// Mirrors find_cmd.rs's <c>parse_native_find_args</c>.
    /// </summary>
    /// <param name="args">The argument vector, known to contain a native flag.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="ArgumentException">On an invalid <c>-maxdepth</c> value.</exception>
    private static FindArgs ParseNativeFindArgs(string[] args)
    {
        var pattern = "*";
        var path = ".";
        int? maxDepth = null;
        var fileType = "f";
        var caseInsensitive = false;

        var i = 0;

        // First non-flag argument is the path (standard find behavior).
        if (!args[0].StartsWith('-'))
        {
            path = args[0];
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-name":
                    if (TryNext(args, ref i, out var name))
                    {
                        pattern = name;
                    }

                    break;
                case "-iname":
                    if (TryNext(args, ref i, out var iname))
                    {
                        pattern = iname;
                        caseInsensitive = true;
                    }

                    break;
                case "-type":
                    if (TryNext(args, ref i, out var type))
                    {
                        fileType = type;
                    }

                    break;
                case "-maxdepth":
                    if (TryNext(args, ref i, out var depth))
                    {
                        maxDepth = ParseCount(depth, "invalid -maxdepth value");
                    }

                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"rtk find: unknown flag '{args[i]}', ignored");
                    }

                    break;
            }
        }

        return new FindArgs(pattern, path, FindArgs.DefaultMaxResults, maxDepth, fileType, caseInsensitive);
    }

    /// <summary>
    /// Parses RTK syntax: <c>&lt;pattern&gt; [path] [-m|--max N] [-t|--file-type T]</c>. Mirrors
    /// find_cmd.rs's <c>parse_rtk_find_args</c>.
    /// </summary>
    /// <param name="args">The argument vector, whose first element is the pattern.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="ArgumentException">On an invalid <c>--max</c> value.</exception>
    private static FindArgs ParseRtkFindArgs(string[] args)
    {
        var pattern = args[0];
        var path = ".";
        var maxResults = FindArgs.DefaultMaxResults;
        var fileType = "f";

        var i = 1;

        // Second positional arg (if not a flag) is the path.
        if (i < args.Length && !args[i].StartsWith('-'))
        {
            path = args[i];
            i++;
        }

        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-m":
                case "--max":
                    if (TryNext(args, ref i, out var max))
                    {
                        maxResults = ParseCount(max, "invalid --max value");
                    }

                    break;
                case "-t":
                case "--file-type":
                    if (TryNext(args, ref i, out var type))
                    {
                        fileType = type;
                    }

                    break;
            }
        }

        return new FindArgs(pattern, path, maxResults, null, fileType, false);
    }

    /// <summary>
    /// Consumes the next argument at position <paramref name="i"/>, advancing it. Returns false
    /// (leaving <paramref name="i"/> advanced past the end) when no argument remains, matching
    /// find_cmd.rs's <c>next_arg</c>.
    /// </summary>
    private static bool TryNext(string[] args, ref int i, out string value)
    {
        i++;
        if (i < args.Length)
        {
            value = args[i];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static int ParseCount(string value, string errorMessage)
    {
        if (!int.TryParse(value, global::System.Globalization.NumberStyles.None,
                global::System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            throw new ArgumentException(errorMessage);
        }

        return count;
    }

    /// <summary>
    /// Walks the filesystem for <paramref name="parsed"/> and writes the grouped result block to
    /// <paramref name="output"/>. Mirrors the body of find_cmd.rs's <c>run</c>. Newlines are
    /// written as <c>\n</c> to match the Rust <c>println!</c> output byte-for-byte.
    /// </summary>
    /// <param name="parsed">The parsed find arguments.</param>
    /// <param name="output">The destination writer (e.g. <see cref="Console.Out"/> or a test sink).</param>
    internal static void Run(FindArgs parsed, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(output);

        // Treat "." as match-all.
        var effectivePattern = parsed.Pattern == "." ? "*" : parsed.Pattern;
        var wantDirs = parsed.FileType == "d";

        // When the pattern targets dotfiles, walk hidden entries; otherwise skip them (#1101).
        var searchHidden = effectivePattern.StartsWith('.');

        var files = Walk(parsed.Path, effectivePattern, wantDirs, parsed.MaxDepth, searchHidden,
            parsed.CaseInsensitive);

        output.Write(FindFilters.FormatFindResults(files, parsed));
    }

    /// <summary>
    /// Matches a filename against a glob pattern supporting <c>*</c> (zero or more characters) and
    /// <c>?</c> (exactly one character). Mirrors find_cmd.rs's <c>glob_match</c>.
    /// </summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <param name="name">The filename to test.</param>
    /// <returns>True if <paramref name="name"/> matches <paramref name="pattern"/>.</returns>
    internal static bool GlobMatch(string pattern, string name) => GlobMatchInner(pattern, 0, name, 0);

    private static bool GlobMatchInner(string pat, int pi, string name, int ni)
    {
        var patEnd = pi >= pat.Length;
        var nameEnd = ni >= name.Length;

        if (patEnd && nameEnd)
        {
            return true;
        }

        if (!patEnd && pat[pi] == '*')
        {
            // '*' matches zero or more characters.
            return GlobMatchInner(pat, pi + 1, name, ni)
                   || (!nameEnd && GlobMatchInner(pat, pi, name, ni + 1));
        }

        if (!patEnd && !nameEnd && pat[pi] == '?')
        {
            return GlobMatchInner(pat, pi + 1, name, ni + 1);
        }

        if (!patEnd && !nameEnd && pat[pi] == name[ni])
        {
            return GlobMatchInner(pat, pi + 1, name, ni + 1);
        }

        return false;
    }

    /// <summary>
    /// Walks <paramref name="path"/> collecting the display paths (relative to the search root,
    /// using the platform separator) of entries whose type and name match. Reproduces the
    /// <c>ignore::WalkBuilder</c> behavior used by find_cmd.rs: hidden-skip and hierarchical
    /// gitignore.
    /// </summary>
    private static List<string> Walk(
        string path, string pattern, bool wantDirs, int? maxDepth, bool searchHidden, bool caseInsensitive)
    {
        var results = new List<string>();

        string root;
        try
        {
            root = global::System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return results;
        }

        if (!Directory.Exists(root))
        {
            return results;
        }

        var loweredPattern = caseInsensitive ? pattern.ToLowerInvariant() : pattern;

        // Build seeds ancestor rules up to (excluding) the search root; descend into the root
        // itself so its own .gitignore governs the root's direct children.
        var ignore = GitignoreStack.Build(root).Descend(root);

        var relParts = new List<string>();
        WalkDir(root, relParts, 1, ignore, wantDirs, loweredPattern, maxDepth, searchHidden,
            caseInsensitive, results);

        return results;
    }

    private static void WalkDir(
        string absDir,
        List<string> relParts,
        int depth,
        GitignoreStack ignore,
        bool wantDirs,
        string pattern,
        int? maxDepth,
        bool searchHidden,
        bool caseInsensitive,
        List<string> results)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(absDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var name = global::System.IO.Path.GetFileName(entry);

            bool isDir;
            try
            {
                isDir = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Always prune the .git metadata directory (matches the ignore crate).
            if (isDir && name == ".git")
            {
                continue;
            }

            // Skip hidden entries unless the pattern targets dotfiles.
            if (!searchHidden && name.StartsWith('.'))
            {
                continue;
            }

            // Respect .gitignore.
            if (ignore.IsIgnored(entry, isDir))
            {
                continue;
            }

            relParts.Add(name);

            var withinDepth = maxDepth is null || depth <= maxDepth;
            if (withinDepth)
            {
                var typeMatches = wantDirs == isDir;
                if (typeMatches)
                {
                    var candidate = caseInsensitive ? name.ToLowerInvariant() : name;
                    if (GlobMatch(pattern, candidate))
                    {
                        results.Add(string.Join(global::System.IO.Path.DirectorySeparatorChar, relParts));
                    }
                }
            }

            if (isDir && (maxDepth is null || depth < maxDepth))
            {
                var childIgnore = ignore.Descend(entry);
                WalkDir(entry, relParts, depth + 1, childIgnore, wantDirs, pattern, maxDepth,
                    searchHidden, caseInsensitive, results);
            }

            relParts.RemoveAt(relParts.Count - 1);
        }
    }

    /// <summary>
    /// A hierarchical stack of <c>.gitignore</c> rules in precedence order (lowest first). Built
    /// from the repo-root <c>.git/info/exclude</c> and every <c>.gitignore</c> from the git root
    /// down to the search path, then extended per directory descended into. Last matching rule
    /// wins, mirroring git's ordering semantics.
    /// </summary>
    private sealed class GitignoreStack
    {
        private static readonly GitignoreStack Empty = new([]);

        private readonly IReadOnlyList<IgnoreRule> _rules;

        private GitignoreStack(IReadOnlyList<IgnoreRule> rules) => _rules = rules;

        /// <summary>
        /// Builds the initial stack for a search rooted at <paramref name="searchRoot"/>: locates
        /// the git root, loads <c>.git/info/exclude</c>, and loads each <c>.gitignore</c> from the
        /// git root down to (but excluding) the search root itself — the search root's own
        /// <c>.gitignore</c> is loaded lazily by <see cref="Descend"/> when its contents are walked.
        /// </summary>
        /// <param name="searchRoot">The absolute search root.</param>
        /// <returns>The seeded stack, or an empty stack when no git root is found.</returns>
        public static GitignoreStack Build(string searchRoot)
        {
            var gitRoot = FindGitRoot(searchRoot);
            if (gitRoot is null)
            {
                return Empty;
            }

            var rules = new List<IgnoreRule>();
            LoadFile(global::System.IO.Path.Combine(gitRoot, ".git", "info", "exclude"), gitRoot, rules);

            // Ancestors from git root down to, but excluding, the search root.
            var chain = new List<string>();
            var current = searchRoot;
            while (current is not null &&
                   !string.Equals(current, gitRoot, StringComparison.OrdinalIgnoreCase))
            {
                var parent = global::System.IO.Path.GetDirectoryName(current);
                if (parent is null)
                {
                    break;
                }

                chain.Add(parent);
                current = parent;
            }

            chain.Reverse();
            foreach (var dir in chain)
            {
                LoadFile(global::System.IO.Path.Combine(dir, ".gitignore"), dir, rules);
            }

            return new GitignoreStack(rules);
        }

        /// <summary>
        /// Returns a stack extended with the <c>.gitignore</c> found in <paramref name="dir"/>, or
        /// this same stack when the directory has none.
        /// </summary>
        /// <param name="dir">The absolute directory being descended into.</param>
        /// <returns>The extended (or unchanged) stack.</returns>
        public GitignoreStack Descend(string dir)
        {
            var file = global::System.IO.Path.Combine(dir, ".gitignore");
            if (!File.Exists(file))
            {
                return this;
            }

            var rules = new List<IgnoreRule>(_rules);
            LoadFile(file, dir, rules);
            return new GitignoreStack(rules);
        }

        /// <summary>
        /// Reports whether <paramref name="absPath"/> is excluded by the accumulated rules
        /// (last match wins; a negated rule re-includes).
        /// </summary>
        /// <param name="absPath">The absolute path of the entry.</param>
        /// <param name="isDir">Whether the entry is a directory.</param>
        /// <returns>True when the entry should be skipped.</returns>
        public bool IsIgnored(string absPath, bool isDir)
        {
            var ignored = false;
            foreach (var rule in _rules)
            {
                if (rule.IsMatch(absPath, isDir))
                {
                    ignored = !rule.Negated;
                }
            }

            return ignored;
        }

        private static string? FindGitRoot(string start)
        {
            var dir = start;
            while (dir is not null)
            {
                if (Directory.Exists(global::System.IO.Path.Combine(dir, ".git")) ||
                    File.Exists(global::System.IO.Path.Combine(dir, ".git")))
                {
                    return dir;
                }

                var parent = global::System.IO.Path.GetDirectoryName(dir);
                if (parent is null || string.Equals(parent, dir, StringComparison.Ordinal))
                {
                    return null;
                }

                dir = parent;
            }

            return null;
        }

        private static void LoadFile(string file, string baseDir, List<IgnoreRule> rules)
        {
            if (!File.Exists(file))
            {
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var raw in lines)
            {
                var rule = IgnoreRule.TryParse(raw, baseDir);
                if (rule is not null)
                {
                    rules.Add(rule);
                }
            }
        }
    }

    /// <summary>A single parsed <c>.gitignore</c> pattern bound to the directory it was declared in.</summary>
    private sealed class IgnoreRule
    {
        private readonly string _baseDir;
        private readonly bool _dirOnly;
        private readonly bool _anchored;
        private readonly Regex _regex;

        private IgnoreRule(string baseDir, bool negated, bool dirOnly, bool anchored, Regex regex)
        {
            _baseDir = baseDir;
            Negated = negated;
            _dirOnly = dirOnly;
            _anchored = anchored;
            _regex = regex;
        }

        /// <summary>Whether this is a negation (<c>!</c>) rule that re-includes a match.</summary>
        public bool Negated { get; }

        /// <summary>
        /// Parses one <c>.gitignore</c> line into a rule, or returns null for blanks and comments.
        /// </summary>
        /// <param name="line">The raw line.</param>
        /// <param name="baseDir">The directory the pattern is relative to.</param>
        /// <returns>The parsed rule, or null.</returns>
        public static IgnoreRule? TryParse(string line, string baseDir)
        {
            var body = line.TrimEnd();
            if (body.Length == 0 || body[0] == '#')
            {
                return null;
            }

            var negated = false;
            if (body[0] == '!')
            {
                negated = true;
                body = body[1..];
            }
            else if (body.StartsWith('\\') && body.Length > 1 && (body[1] == '#' || body[1] == '!'))
            {
                body = body[1..];
            }

            var dirOnly = body.EndsWith('/');
            body = body.TrimEnd('/');
            if (body.Length == 0)
            {
                return null;
            }

            var leadingSlash = body.StartsWith('/');
            if (leadingSlash)
            {
                body = body[1..];
            }

            var anchored = leadingSlash || body.Contains('/');

            // These regexes are built at runtime from arbitrary .gitignore content, so they cannot
            // be the fixed static-readonly GeneratedRegex the project prefers. They are also used
            // for a single walk, so RegexOptions.Compiled (upfront JIT cost per pattern) is the
            // wrong trade-off for a startup-sensitive CLI — the interpreted engine is faster here.
            var regex = new Regex("^" + TranslateGlob(body) + "$", RegexOptions.CultureInvariant);

            return new IgnoreRule(baseDir, negated, dirOnly, anchored, regex);
        }

        /// <summary>Reports whether this rule matches the given entry.</summary>
        /// <param name="absPath">The absolute path of the entry.</param>
        /// <param name="isDir">Whether the entry is a directory.</param>
        /// <returns>True on a match.</returns>
        public bool IsMatch(string absPath, bool isDir)
        {
            if (_dirOnly && !isDir)
            {
                return false;
            }

            string target;
            if (_anchored)
            {
                target = global::System.IO.Path.GetRelativePath(_baseDir, absPath).Replace('\\', '/');
                if (target.StartsWith("../", StringComparison.Ordinal) || target == "..")
                {
                    return false;
                }
            }
            else
            {
                target = global::System.IO.Path.GetFileName(absPath);
            }

            return _regex.IsMatch(target);
        }

        private static string TranslateGlob(string glob)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // '**' crosses directory separators.
                        sb.Append(".*");
                        i++;
                    }
                    else
                    {
                        // '*' matches within a single path segment.
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }

            return sb.ToString();
        }
    }
}
