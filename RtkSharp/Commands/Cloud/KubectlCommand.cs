using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk kubectl</c> CLI verb group (<c>get</c>/<c>pods</c>/<c>services</c>/<c>logs</c>,
/// plus a generic passthrough fallback for any other subcommand). Faithful port of the
/// <c>Commands::Kubectl</c> dispatch arm (<c>main.rs</c>:1822-1837) and <c>KubectlCommands</c>
/// (<c>main.rs</c>:957-989), delegating all shared kubectl/oc filtering logic to
/// <see cref="ContainerFilters"/> with <c>tool = "kubectl"</c> — Rust's <c>container.rs</c> implements
/// this logic generically over <c>tool: &amp;str</c>, so kubectl and oc share ~100% of it.
/// </summary>
public static class KubectlCommand
{
    private const string Tool = "kubectl";

    /// <summary>
    /// Registry entry point. Runs <c>rtk kubectl</c> with the given arguments (the remainder after the
    /// <c>kubectl</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>kubectl</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args, int verbose) => RunAsync(args, verbose, new ProcessExecutor());

    /// <summary>
    /// Runs <c>rtk kubectl</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>kubectl</c>.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>kubectl</c> with.</param>
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
    /// the <c>Commands::Kubectl</c> dispatch arm (<c>main.rs</c>:1822-1837).
    /// </summary>
    /// <param name="args">The arguments following the <c>kubectl</c> verb.</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>kubectl</c> with.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunCoreAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        if (args.Length == 0)
        {
            // Rust's `KubectlCommands` is a REQUIRED clap subcommand enum — `rtk kubectl` alone fails
            // at the clap layer before container.rs's body ever runs. `kubectl` is Rust-classified
            // PASSTHROUGH, so that clap failure falls back to a raw PATH-exec attempt of "kubectl",
            // not a kubectl-specific usage message — this throw lets RtkProgram's dispatch layer
            // re-route there instead of printing this message directly.
            throw new CommandArgumentParseException("'rtk kubectl' requires a subcommand but one was not provided");
        }

        var subcommand = args[0];
        var rest = args[1..];

        return subcommand switch
        {
            "get" => ContainerFilters.RunK8sGetAsync(Tool, rest, verbose, executor),
            "pods" => RunPodsAsync(rest, executor),
            "services" => RunServicesAsync(rest, executor),
            "logs" => RunLogsAsync(rest, executor),
            // Passthrough: runs any unsupported kubectl subcommand directly (main.rs's Other external
            // subcommand) — forwards the FULL original args verbatim, including the subcommand token.
            _ => ContainerFilters.RunPassthroughAsync(Tool, args, verbose, executor),
        };
    }

    /// <summary>Faithful port of the <c>KubectlCommands::Pods</c> dispatch arm (<c>main.rs</c>:1824-1827),
    /// which builds args via <c>build_k8s_namespace_args</c> then calls <c>k8s_pods</c>.</summary>
    private static Task<int> RunPodsAsync(string[] rest, IProcessExecutor executor)
    {
        var (ns, all) = ParseNamespaceArgs(rest);
        var args = ContainerFilters.BuildK8sNamespaceArgs(ns, all);
        return ContainerFilters.RunK8sPodsAsync(Tool, args, executor);
    }

    /// <summary>Faithful port of the <c>KubectlCommands::Services</c> dispatch arm (<c>main.rs</c>:1828-1831).</summary>
    private static Task<int> RunServicesAsync(string[] rest, IProcessExecutor executor)
    {
        var (ns, all) = ParseNamespaceArgs(rest);
        var args = ContainerFilters.BuildK8sNamespaceArgs(ns, all);
        return ContainerFilters.RunK8sServicesAsync(Tool, args, executor);
    }

    /// <summary>Faithful port of the <c>KubectlCommands::Logs</c> dispatch arm (<c>main.rs</c>:1832-1835),
    /// which builds args via <c>build_k8s_logs_args</c> then calls <c>k8s_logs</c>.</summary>
    private static Task<int> RunLogsAsync(string[] rest, IProcessExecutor executor)
    {
        if (rest.Length == 0)
        {
            // Rust's `KubectlCommands::Logs { pod: String }` is a REQUIRED clap positional.
            throw new CommandArgumentParseException(
                "the following required arguments were not provided: <POD>");
        }

        var (pod, container) = ParseLogsArgs(rest);
        var args = ContainerFilters.BuildK8sLogsArgs(pod, container);
        return ContainerFilters.RunK8sLogsAsync(Tool, args, executor);
    }

    /// <summary>Parses <c>-n/--namespace &lt;value&gt;</c> and <c>-A/--all</c> from a <c>pods</c>/<c>services</c>
    /// subcommand's argument list, mirroring clap's <c>KubectlCommands::Pods</c>/<c>Services</c> option shape.</summary>
    internal static (string? Namespace, bool All) ParseNamespaceArgs(IReadOnlyList<string> args)
    {
        string? ns = null;
        var all = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-A" or "--all":
                    all = true;
                    break;
                case "-n" or "--namespace" when i + 1 < args.Count:
                    ns = args[++i];
                    break;
            }
        }

        return (ns, all);
    }

    /// <summary>Parses the pod positional plus an optional <c>-c/--container &lt;value&gt;</c> from a
    /// <c>logs</c> subcommand's argument list, mirroring clap's <c>KubectlCommands::Logs</c> option shape.</summary>
    internal static (string Pod, string? Container) ParseLogsArgs(IReadOnlyList<string> args)
    {
        var pod = args.Count > 0 ? args[0] : "";
        string? container = null;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "-c" or "--container" && i + 1 < args.Count)
            {
                container = args[++i];
            }
        }

        return (pod, container);
    }
}
