using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;
using RtkSharp.Filters.Commands.Cloud;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk az</c> CLI verb: compresses <c>az</c> JSON output via 15 named filters
/// (account, group, deployment group, webapp, storage account — each list/show; functionapp
/// list/show; acr list, acr repository list, acr repository show-tags) plus a generic
/// values-preserving JSON-compaction fallback, with secret redaction applied throughout. A pure
/// RtkSharp superset feature — no Rust oracle exists for <c>az</c>. Design source of truth:
/// <c>docs/superpowers/specs/2026-07-08-az-command-module-design.md</c> (MVP) and
/// <c>docs/superpowers/specs/2026-07-09-az-functionapp-acr-design.md</c> (functionapp/acr).
/// Mirrors <see cref="AwsCommand"/>'s dispatch/execution skeleton.
/// </summary>
/// <remarks>
/// <b>Unlike <c>aws</c>, no <c>--output json</c> injection is needed</b> — <c>az</c>'s factory
/// default output format is already JSON (confirmed via a live <c>az account show</c>/
/// <c>az config get core.output</c> capture this session). Filtering therefore applies whenever
/// the effective format is JSON (the default, or an explicit <c>--output json</c>); an explicit
/// non-JSON format (<c>table</c>/<c>tsv</c>/<c>yaml</c>/etc.) passes through completely
/// unfiltered via <see cref="RequestsNonJsonOutput"/>, mirroring <see cref="AwsCommand"/>'s
/// policy of never corrupting a deliberately-requested format.
/// </remarks>
public static class AzCommand
{
    private const int JsonCompressDepth = 4;

    /// <summary>Registry entry point. Runs <c>rtk az</c> with the given arguments (the remainder after the <c>az</c> verb).</summary>
    /// <param name="args">The CLI arguments following <c>az</c> (subcommand first).</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>Runs <c>rtk az</c> with an injectable <see cref="IProcessExecutor"/>, for testing.</summary>
    /// <param name="args">The CLI arguments following <c>az</c> (subcommand first).</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>az</c> with.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            // No Rust oracle constrains zero-args behavior for `az` (unlike `aws`'s clap-required-
            // positional quirk) — simplest sensible behavior: pass through raw so the real `az`
            // binary prints its own help/usage.
            var passthroughResult = await executor.ExecuteAsync(
                new ExecutionRequest(PathResolver.Resolve("az"), args, CaptureMode: ExecutionCaptureMode.Inherit)).ConfigureAwait(false);
            return passthroughResult.ExitCode;
        }

        var subcommand = args[0];
        var rest = args[1..];
        var op = rest.Length > 0 ? rest[0] : null;
        var opArgs = rest.Length > 0 ? rest[1..] : rest;
        var fullSub = rest.Length == 0 ? subcommand : $"{subcommand} {string.Join(' ', rest)}";

        if (RequestsNonJsonOutput(rest))
        {
            return await RunPassthroughAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false);
        }

        return (subcommand, op) switch
        {
            ("account", "show") => await RunAzFilteredAsync(
                ["account", "show"], opArgs, verbose, executor, AzFilters.FilterAccountShow).ConfigureAwait(false),
            ("account", "list") => await RunAzFilteredAsync(
                ["account", "list"], opArgs, verbose, executor, AzFilters.FilterAccountList).ConfigureAwait(false),
            ("group", "list") => await RunAzFilteredAsync(
                ["group", "list"], opArgs, verbose, executor, AzFilters.FilterGroupList).ConfigureAwait(false),
            ("group", "show") => await RunAzFilteredAsync(
                ["group", "show"], opArgs, verbose, executor, AzFilters.FilterGroupShow).ConfigureAwait(false),
            ("deployment", "group") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["deployment", "group", "list"], opArgs[1..], verbose, executor, AzFilters.FilterDeploymentGroupList).ConfigureAwait(false),
            ("deployment", "group") when opArgs.Length > 0 && opArgs[0] == "show" => await RunAzFilteredAsync(
                ["deployment", "group", "show"], opArgs[1..], verbose, executor, AzFilters.FilterDeploymentGroupShow).ConfigureAwait(false),
            ("webapp", "list") => await RunAzFilteredAsync(
                ["webapp", "list"], opArgs, verbose, executor, AzFilters.FilterWebappList).ConfigureAwait(false),
            ("webapp", "show") => await RunAzFilteredAsync(
                ["webapp", "show"], opArgs, verbose, executor, AzFilters.FilterWebappShow).ConfigureAwait(false),
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["storage", "account", "list"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountList).ConfigureAwait(false),
            ("storage", "account") when opArgs.Length > 0 && opArgs[0] == "show" => await RunAzFilteredAsync(
                ["storage", "account", "show"], opArgs[1..], verbose, executor, AzFilters.FilterStorageAccountShow).ConfigureAwait(false),
            ("functionapp", "list") => await RunAzFilteredAsync(
                ["functionapp", "list"], opArgs, verbose, executor, AzFilters.FilterFunctionAppList).ConfigureAwait(false),
            ("functionapp", "show") => await RunAzFilteredAsync(
                ["functionapp", "show"], opArgs, verbose, executor, AzFilters.FilterFunctionAppShow).ConfigureAwait(false),
            ("acr", "list") => await RunAzFilteredAsync(
                ["acr", "list"], opArgs, verbose, executor, AzFilters.FilterAcrList).ConfigureAwait(false),
            ("acr", "repository") when opArgs.Length > 0 && opArgs[0] == "list" => await RunAzFilteredAsync(
                ["acr", "repository", "list"], opArgs[1..], verbose, executor, AzFilters.FilterAcrRepositoryList).ConfigureAwait(false),
            ("acr", "repository") when opArgs.Length > 0 && opArgs[0] == "show-tags" => await RunAzFilteredAsync(
                ["acr", "repository", "show-tags"], opArgs[1..], verbose, executor, AzFilters.FilterAcrRepositoryShowTags).ConfigureAwait(false),
            _ => await RunGenericAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// True when <paramref name="args"/> explicitly requests a non-JSON output format
    /// (<c>--output table</c>/<c>tsv</c>/<c>yaml</c>/etc., or their <c>-o</c>/<c>--output=</c>
    /// shorthand). <c>json</c>/<c>jsonc</c> (explicit or absent, since <c>az</c> already defaults
    /// to JSON) return false.
    /// </summary>
    private static bool RequestsNonJsonOutput(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;

            if (arg is "--output" or "-o" && i + 1 < args.Count)
            {
                value = args[i + 1];
            }
            else if (arg.StartsWith("--output=", StringComparison.Ordinal))
            {
                value = arg["--output=".Length..];
            }
            else if (arg.StartsWith("-o=", StringComparison.Ordinal))
            {
                value = arg["-o=".Length..];
            }

            if (value is not null
                && !value.Equals("json", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("jsonc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs <c>az</c> with the original arguments unchanged, inheriting the console (no filtering, no capture). Used for zero-args and explicit-non-JSON-format cases.</summary>
    private static async Task<int> RunPassthroughAsync(
        string subcommand, IReadOnlyList<string> rest, int verbose, string fullSub, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();
        var cmdArgs = new List<string> { subcommand };
        cmdArgs.AddRange(rest);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: az {fullSub}\n");
        }

        var result = await executor.ExecuteAsync(
            new ExecutionRequest(PathResolver.Resolve("az"), cmdArgs, CaptureMode: ExecutionCaptureMode.Inherit)).ConfigureAwait(false);

        timer.TrackPassthrough($"az {fullSub}", $"rtk az {fullSub}");
        return result.ExitCode;
    }

    /// <summary>Generic fallback strategy for any az subcommand without a dedicated filter: values-preserving JSON compaction + redaction.</summary>
    private static async Task<int> RunGenericAsync(
        string subcommand, IReadOnlyList<string> args, int verbose, string fullSub, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var cmdArgs = new List<string> { subcommand };
        cmdArgs.AddRange(args);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: az {fullSub}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("az"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run az CLI{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        var raw = result.Stdout;
        var stderr = result.Stderr;

        if (result.ExitCode != 0)
        {
            timer.Track($"az {fullSub}", $"rtk az {fullSub}", stderr, stderr);
            Console.Error.Write(stderr.Trim() + "\n");
            return result.ExitCode;
        }

        string filtered;
        try
        {
            filtered = AzFilters.Redact(JsonCompaction.Compact(raw, JsonCompressDepth));
            Console.Out.Write(filtered + "\n");
        }
        catch (global::System.Text.Json.JsonException)
        {
            Console.Out.Write(raw);
            filtered = raw;
        }

        timer.Track($"az {fullSub}", $"rtk az {fullSub}", raw, filtered);
        return 0;
    }

    /// <summary>Shared runner for az commands with a dedicated named filter. Follows the six-phase contract: timer → execute → filter (fallback) → redact → tee → track → exit code.</summary>
    private static async Task<int> RunAzFilteredAsync(
        string[] subArgs,
        IReadOnlyList<string> extraArgs,
        int verbose,
        IProcessExecutor executor,
        Func<string, AwsFilters.FilterResult?> filterFn)
    {
        var cmdLabel = $"az {string.Join(' ', subArgs)}";
        var rtkLabel = $"rtk {cmdLabel}";
        var slug = cmdLabel.Replace(' ', '_');
        var timer = TimedExecution.Start();

        var cmdArgs = new List<string>(subArgs);
        cmdArgs.AddRange(extraArgs);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {cmdLabel} {string.Join(' ', extraArgs)}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("az"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run {cmdLabel}{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        var stdout = result.Stdout;
        var stderr = result.Stderr;
        var raw = stderr.Length == 0 ? stdout : $"{stdout}\n{stderr}";

        if (result.ExitCode != 0)
        {
            var hint = Tee.TeeAndHint(raw, slug, result.ExitCode);
            Console.Error.Write(hint is not null ? $"{stderr.Trim()}\n{hint}\n" : $"{stderr.Trim()}\n");
            timer.Track(cmdLabel, rtkLabel, raw, stderr);
            return result.ExitCode;
        }

        var filterResult = filterFn(stdout);
        if (filterResult is null)
        {
            Console.Error.Write("rtk: filter warning: az filter returned None, passing through raw output\n");
            filterResult = AwsFilters.FilterResult.New(stdout);
        }

        var redactedText = AzFilters.Redact(filterResult.Text);
        filterResult = filterResult.IsTruncated
            ? AwsFilters.FilterResult.Truncated(redactedText)
            : AwsFilters.FilterResult.New(redactedText);

        if (filterResult.IsTruncated)
        {
            var hint = Tee.ForceTeeHint(raw, slug);
            Console.Out.Write(hint is not null ? $"{filterResult.Text}\n{hint}\n" : $"{filterResult.Text}\n");
        }
        else
        {
            Console.Out.Write(filterResult.Text + "\n");
        }

        timer.Track(cmdLabel, rtkLabel, raw, filterResult.Text);
        return 0;
    }
}
