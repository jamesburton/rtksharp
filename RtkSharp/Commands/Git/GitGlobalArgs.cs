namespace RtkSharp.Commands.Git;

/// <summary>
/// The global <c>git</c> options that may precede a subcommand — the ones Rust's
/// <c>Commands::Git</c> variant declares in <c>main.rs</c>: repeated <c>-C &lt;dir&gt;</c>,
/// repeated <c>-c &lt;key=value&gt;</c>, <c>--git-dir</c>, <c>--work-tree</c>, and the four boolean
/// flags <c>--no-pager</c>, <c>--no-optional-locks</c>, <c>--bare</c>, <c>--literal-pathspecs</c>.
/// </summary>
/// <remarks>
/// RtkSharp has no clap, so <see cref="Parse"/> reproduces clap's front-of-line option parsing:
/// it consumes recognized global flags off the head of the argument vector until it reaches the
/// first token that is not one (the subcommand). <see cref="ToArgs"/> then re-serializes them in
/// the exact order Rust's dispatch block builds <c>global_args</c> (main.rs:1561-1589), so the child
/// <c>git</c> process receives <c>[global args] &lt;subcommand&gt; [subcommand args]</c> identically.
/// </remarks>
internal sealed class GitGlobalArgs
{
    /// <summary>The <c>-C &lt;dir&gt;</c> directories, in order (git changes into each before executing).</summary>
    public List<string> Directory { get; } = new();

    /// <summary>The <c>-c &lt;key=value&gt;</c> configuration overrides, in order.</summary>
    public List<string> ConfigOverride { get; } = new();

    /// <summary>The <c>--git-dir</c> value, or null when not supplied.</summary>
    public string? GitDir { get; set; }

    /// <summary>The <c>--work-tree</c> value, or null when not supplied.</summary>
    public string? WorkTree { get; set; }

    /// <summary>Whether <c>--no-pager</c> was supplied.</summary>
    public bool NoPager { get; set; }

    /// <summary>Whether <c>--no-optional-locks</c> was supplied.</summary>
    public bool NoOptionalLocks { get; set; }

    /// <summary>Whether <c>--bare</c> was supplied.</summary>
    public bool Bare { get; set; }

    /// <summary>Whether <c>--literal-pathspecs</c> was supplied.</summary>
    public bool LiteralPathspecs { get; set; }

    /// <summary>
    /// Splits the raw argument vector into the parsed global options and the remainder (the
    /// subcommand token followed by its trailing arguments). Mirrors clap: global flags are only
    /// recognized at the front; the first token that is not a recognized global flag (nor consumed
    /// as one's value) terminates parsing and begins the subcommand. Value-taking flags accept the
    /// space-separated (<c>-C dir</c>), attached (<c>-Cdir</c>), and <c>=</c> (<c>-C=dir</c> /
    /// <c>--git-dir=path</c>) forms.
    /// </summary>
    /// <param name="args">The arguments following the <c>git</c> verb.</param>
    /// <returns>The parsed globals and the remaining tokens (subcommand first, possibly empty).</returns>
    public static (GitGlobalArgs Globals, IReadOnlyList<string> Remaining) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var g = new GitGlobalArgs();
        var i = 0;
        while (i < args.Count)
        {
            var a = args[i];

            if (TryTakeValue(args, ref i, a, "-C", out var dir))
            {
                g.Directory.Add(dir);
                continue;
            }

            if (TryTakeValue(args, ref i, a, "-c", out var cfg))
            {
                g.ConfigOverride.Add(cfg);
                continue;
            }

            if (TryTakeLongValue(args, ref i, a, "--git-dir", out var gitDir))
            {
                g.GitDir = gitDir;
                continue;
            }

            if (TryTakeLongValue(args, ref i, a, "--work-tree", out var workTree))
            {
                g.WorkTree = workTree;
                continue;
            }

            switch (a)
            {
                case "--no-pager":
                    g.NoPager = true;
                    i++;
                    continue;
                case "--no-optional-locks":
                    g.NoOptionalLocks = true;
                    i++;
                    continue;
                case "--bare":
                    g.Bare = true;
                    i++;
                    continue;
                case "--literal-pathspecs":
                    g.LiteralPathspecs = true;
                    i++;
                    continue;
            }

            // Not a recognized global flag: the subcommand starts here.
            break;
        }

        return (g, args.Skip(i).ToList());
    }

    /// <summary>
    /// Serializes the parsed globals into the child <c>git</c> argument prefix, in the exact order
    /// Rust's dispatch block appends them (main.rs:1561-1589): every <c>-C</c>, then every <c>-c</c>,
    /// then <c>--git-dir</c>, <c>--work-tree</c>, and finally the four boolean flags.
    /// </summary>
    /// <returns>The ordered global-argument list to prepend before the subcommand.</returns>
    public List<string> ToArgs()
    {
        var result = new List<string>();

        foreach (var dir in Directory)
        {
            result.Add("-C");
            result.Add(dir);
        }

        foreach (var cfg in ConfigOverride)
        {
            result.Add("-c");
            result.Add(cfg);
        }

        if (GitDir is not null)
        {
            result.Add("--git-dir");
            result.Add(GitDir);
        }

        if (WorkTree is not null)
        {
            result.Add("--work-tree");
            result.Add(WorkTree);
        }

        if (NoPager)
        {
            result.Add("--no-pager");
        }

        if (NoOptionalLocks)
        {
            result.Add("--no-optional-locks");
        }

        if (Bare)
        {
            result.Add("--bare");
        }

        if (LiteralPathspecs)
        {
            result.Add("--literal-pathspecs");
        }

        return result;
    }

    /// <summary>
    /// Recognizes a short value-taking flag (<c>-C</c> / <c>-c</c>) in its space-separated
    /// (<c>-C dir</c>), attached (<c>-Cdir</c>), or <c>=</c> (<c>-C=dir</c>) form, extracting the
    /// value and advancing <paramref name="i"/> past the consumed token(s).
    /// </summary>
    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, string arg, string flag, out string value)
    {
        value = string.Empty;

        if (arg == flag)
        {
            if (i + 1 < args.Count)
            {
                value = args[i + 1];
                i += 2;
                return true;
            }

            // Trailing flag with no value: clap would error; consume it alone with an empty value.
            i++;
            return true;
        }

        var eqForm = flag + "=";
        if (arg.StartsWith(eqForm, StringComparison.Ordinal))
        {
            value = arg[eqForm.Length..];
            i++;
            return true;
        }

        if (arg.StartsWith(flag, StringComparison.Ordinal) && arg.Length > flag.Length)
        {
            value = arg[flag.Length..];
            i++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Recognizes a long value-taking flag (<c>--git-dir</c> / <c>--work-tree</c>) in its
    /// space-separated (<c>--git-dir path</c>) or <c>=</c> (<c>--git-dir=path</c>) form, extracting
    /// the value and advancing <paramref name="i"/> past the consumed token(s).
    /// </summary>
    private static bool TryTakeLongValue(IReadOnlyList<string> args, ref int i, string arg, string flag, out string value)
    {
        value = string.Empty;

        if (arg == flag)
        {
            if (i + 1 < args.Count)
            {
                value = args[i + 1];
                i += 2;
                return true;
            }

            i++;
            return true;
        }

        var eqForm = flag + "=";
        if (arg.StartsWith(eqForm, StringComparison.Ordinal))
        {
            value = arg[eqForm.Length..];
            i++;
            return true;
        }

        return false;
    }
}
