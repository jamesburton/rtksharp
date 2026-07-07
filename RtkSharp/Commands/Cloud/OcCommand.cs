using System;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk oc</c> CLI verb group (<c>get</c>/<c>pods</c>/<c>services</c>/<c>logs</c>,
/// plus a generic passthrough fallback for any other subcommand). Faithful port of the
/// <c>Commands::Oc</c> dispatch arm (<c>main.rs</c>:1839-1854) and <c>OcCommands</c>
/// (<c>main.rs</c>:991 onward), delegating all shared kubectl/oc filtering logic to
/// <see cref="ContainerFilters"/> with <c>tool = "oc"</c> — Rust's <c>container.rs</c> implements this
/// logic generically over <c>tool: &amp;str</c>, so kubectl and oc share ~100% of it. Note that unlike
/// <c>Commands::Kubectl</c>, Rust's <c>Commands::Oc</c> dispatch arm for <c>Pods</c>/<c>Services</c>/
/// <c>Logs</c> calls <c>container::k8s_pods</c>/<c>k8s_services</c>/<c>k8s_logs</c> directly (not the
/// <c>ContainerCmd</c> enum's <c>run()</c> dispatcher used by Kubectl) — functionally identical, since
/// <c>ContainerCmd::KubectlPods</c> etc. just forward to those same functions.
/// </summary>
public static class OcCommand
{
    private const string Tool = "oc";

    /// <summary>
    /// Registry entry point. Runs <c>rtk oc</c> with the given arguments (the remainder after the
    /// <c>oc</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>oc</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args, int verbose) => RunAsync(args, verbose, new ProcessExecutor());

    /// <summary>
    /// Runs <c>rtk oc</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>oc</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>oc</c> with.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        try
        {
            return await RunCoreAsync(args, verbose, executor).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CommandArgumentParseException)
        {
            Console.Error.Write($"rtk: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// The testable core of <see cref="RunAsync(string[],int,IProcessExecutor)"/>. Faithful port of
    /// the <c>Commands::Oc</c> dispatch arm (<c>main.rs</c>:1839-1854).
    /// </summary>
    /// <param name="args">The arguments following the <c>oc</c> verb.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>oc</c> with.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunCoreAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        if (args.Length == 0)
        {
            // Rust's `OcCommands` is a REQUIRED clap subcommand enum — `rtk oc` alone fails at the
            // clap layer before container.rs's body ever runs. `oc` is Rust-classified PASSTHROUGH,
            // so that clap failure falls back to a raw PATH-exec attempt of "oc", not an
            // oc-specific usage message — this throw lets RtkProgram's dispatch layer re-route
            // there instead of printing this message directly.
            throw new CommandArgumentParseException("'rtk oc' requires a subcommand but one was not provided");
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "get" => ContainerFilters.RunK8sGetAsync(Tool, rest, verbose, executor),
            "pods" => RunPodsAsync(rest, executor),
            "services" => RunServicesAsync(rest, executor),
            "logs" => RunLogsAsync(rest, executor),
            // Passthrough: runs any unsupported oc subcommand directly (main.rs's Other external
            // subcommand) — forwards the FULL original args verbatim, including the subcommand token.
            _ => ContainerFilters.RunPassthroughAsync(Tool, args, verbose, executor),
        };
    }

    /// <summary>Faithful port of the <c>OcCommands::Pods</c> dispatch arm (<c>main.rs</c>:1841-1844).</summary>
    private static Task<int> RunPodsAsync(string[] rest, IProcessExecutor executor)
    {
        var (ns, all) = KubectlCommand.ParseNamespaceArgs(rest);
        var args = ContainerFilters.BuildK8sNamespaceArgs(ns, all);
        return ContainerFilters.RunK8sPodsAsync(Tool, args, executor);
    }

    /// <summary>Faithful port of the <c>OcCommands::Services</c> dispatch arm (<c>main.rs</c>:1845-1848).</summary>
    private static Task<int> RunServicesAsync(string[] rest, IProcessExecutor executor)
    {
        var (ns, all) = KubectlCommand.ParseNamespaceArgs(rest);
        var args = ContainerFilters.BuildK8sNamespaceArgs(ns, all);
        return ContainerFilters.RunK8sServicesAsync(Tool, args, executor);
    }

    /// <summary>Faithful port of the <c>OcCommands::Logs</c> dispatch arm (<c>main.rs</c>:1849-1852).</summary>
    private static Task<int> RunLogsAsync(string[] rest, IProcessExecutor executor)
    {
        if (rest.Length == 0)
        {
            // Rust's `OcCommands::Logs { pod: String }` is a REQUIRED clap positional.
            throw new CommandArgumentParseException(
                "the following required arguments were not provided: <POD>");
        }

        var (pod, container) = KubectlCommand.ParseLogsArgs(rest);
        var args = ContainerFilters.BuildK8sLogsArgs(pod, container);
        return ContainerFilters.RunK8sLogsAsync(Tool, args, executor);
    }
}
