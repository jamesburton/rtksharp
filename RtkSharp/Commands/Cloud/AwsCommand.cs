using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RtkSharp.Cli;
using RtkSharp.Core;
using RtkSharp.Core.Tracking;
using RtkSharp.Execution;

namespace RtkSharp.Commands.Cloud;

/// <summary>
/// Implements the <c>rtk aws</c> CLI verb: replaces verbose <c>--output table</c>/<c>text</c> with
/// JSON, then compresses via one of ~20 specialized filters (STS, S3, EC2, ECS, RDS, CloudFormation,
/// CloudWatch Logs, Lambda, IAM, DynamoDB, Security Groups, S3API, EKS, SQS, SecretsManager), with a
/// generic values-preserving JSON-compaction fallback for any other subcommand. Faithful port of
/// <c>src/cmds/cloud/aws_cmd.rs</c>'s dispatch/execution skeleton (its ~20 pure filter functions live
/// in <see cref="AwsFilters"/> instead, mirroring the <c>CargoCommand</c>/
/// <c>RtkSharp.Filters.Commands.Rust.CargoFilters</c> split).
/// </summary>
/// <remarks>
/// <para>
/// <b>PASSTHROUGH-classified, not a meta-command.</b> Rust's own <c>RTK_META_COMMANDS</c> list does
/// not include <c>"aws"</c> — <c>main.rs</c>'s <c>test_every_subcommand_is_classified</c> lists it
/// under <c>PASSTHROUGH</c>, the same bucket as <c>docker</c>/<c>git</c>/<c>cargo</c>. Rust's
/// <c>Aws { subcommand: String, args: Vec&lt;String&gt; }</c> clap arm has a REQUIRED <c>subcommand</c>
/// positional (<c>main.rs</c>:188-194) — <c>rtk aws</c> with zero arguments fails at the clap layer
/// before <c>aws_cmd::run</c> ever executes, falling back to a raw PATH-exec attempt of <c>aws</c>
/// (not an RTK-specific usage message). This port matches <see cref="DockerCommand"/>'s established
/// precedent for the identical shape: throw <see cref="CommandArgumentParseException"/> so
/// <c>RtkProgram</c>'s dispatch layer can re-route to that same passthrough path.
/// </para>
/// <para>
/// <b>Quirk preserved verbatim: stderr is printed TWICE on a filtered-subcommand failure.</b> Rust's
/// <c>run_aws_json</c> (<c>aws_cmd.rs</c>:280-324) itself does <c>eprintln!("{}", stderr.trim())</c>
/// whenever the child process fails — and its caller, <c>run_aws_filtered</c> (<c>aws_cmd.rs</c>:
/// 328-377), ALSO does <c>eprintln!("{}", stderr.trim())</c> (plus an optional tee hint) on that same
/// failure. This is not a hypothetical: it is the literal, unconditional behavior of the two nested
/// functions as written, so <see cref="RunAwsJsonAsync"/> and <see cref="RunAwsFilteredAsync"/>
/// reproduce the duplicate print exactly rather than "fixing" what looks like an oracle bug.
/// </para>
/// </remarks>
public static class AwsCommand
{
    private const int JsonCompressDepth = 4;

    /// <summary>
    /// Registry entry point. Runs <c>rtk aws</c> with the given arguments (the remainder after the
    /// <c>aws</c> verb).
    /// </summary>
    /// <param name="args">The CLI arguments following <c>aws</c> (subcommand first).</param>
    /// <returns>The exit code.</returns>
    public static Task<int> RunAsync(string[] args) => RunAsync(args, RuntimeOptions.Verbosity, new ProcessExecutor());

    /// <summary>
    /// Runs <c>rtk aws</c> with an injectable <see cref="IProcessExecutor"/>, for testing.
    /// </summary>
    /// <param name="args">The CLI arguments following <c>aws</c> (subcommand first).</param>
    /// <param name="verbose">The top-level verbosity count.</param>
    /// <param name="executor">The process executor to spawn <c>aws</c> with.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args, int verbose, IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            // Rust's `subcommand: String` positional is REQUIRED at the clap layer — zero args
            // never reaches aws_cmd::run at all. `aws` is Rust-classified PASSTHROUGH, so the real
            // oracle falls back to running the REAL aws binary with the original (empty) argv,
            // not a clean clap-style exit 2. Matches DockerCommand's identical zero-args handling.
            throw new CommandArgumentParseException("'rtk aws' requires a subcommand but one was not provided");
        }

        var subcommand = args[0];
        var rest = args[1..];
        var op = rest.Length > 0 ? rest[0] : null;
        var opArgs = rest.Length > 0 ? rest[1..] : rest;
        var fullSub = rest.Length == 0 ? subcommand : $"{subcommand} {string.Join(' ', rest)}";

        return (subcommand, op) switch
        {
            ("sts", "get-caller-identity") => await RunAwsFilteredAsync(
                ["sts", "get-caller-identity"], opArgs, verbose, executor, AwsFilters.FilterStsIdentity).ConfigureAwait(false),
            ("s3", "ls") => await RunS3LsAsync(opArgs, verbose, executor).ConfigureAwait(false),
            ("ec2", "describe-instances") => await RunAwsFilteredAsync(
                ["ec2", "describe-instances"], opArgs, verbose, executor, AwsFilters.FilterEc2Instances).ConfigureAwait(false),
            ("ecs", "list-services") => await RunAwsFilteredAsync(
                ["ecs", "list-services"], opArgs, verbose, executor, AwsFilters.FilterEcsListServices).ConfigureAwait(false),
            ("ecs", "describe-services") => await RunAwsFilteredAsync(
                ["ecs", "describe-services"], opArgs, verbose, executor, AwsFilters.FilterEcsDescribeServices).ConfigureAwait(false),
            ("rds", "describe-db-instances") => await RunAwsFilteredAsync(
                ["rds", "describe-db-instances"], opArgs, verbose, executor, AwsFilters.FilterRdsInstances).ConfigureAwait(false),
            ("cloudformation", "list-stacks") => await RunAwsFilteredAsync(
                ["cloudformation", "list-stacks"], opArgs, verbose, executor, AwsFilters.FilterCfnListStacks).ConfigureAwait(false),
            ("cloudformation", "describe-stacks") => await RunAwsFilteredAsync(
                ["cloudformation", "describe-stacks"], opArgs, verbose, executor, AwsFilters.FilterCfnDescribeStacks).ConfigureAwait(false),
            ("cloudformation", "describe-stack-events") => await RunAwsFilteredAsync(
                ["cloudformation", "describe-stack-events"], opArgs, verbose, executor, AwsFilters.FilterCfnEvents).ConfigureAwait(false),
            ("logs", "get-log-events") => await RunAwsFilteredAsync(
                ["logs", "get-log-events"], opArgs, verbose, executor, AwsFilters.FilterLogsEvents).ConfigureAwait(false),
            ("logs", "filter-log-events") => await RunAwsFilteredAsync(
                ["logs", "filter-log-events"], opArgs, verbose, executor, AwsFilters.FilterLogsEvents).ConfigureAwait(false),
            ("lambda", "list-functions") => await RunAwsFilteredAsync(
                ["lambda", "list-functions"], opArgs, verbose, executor, AwsFilters.FilterLambdaList).ConfigureAwait(false),
            ("lambda", "get-function") => await RunAwsFilteredAsync(
                ["lambda", "get-function"], opArgs, verbose, executor, AwsFilters.FilterLambdaGet).ConfigureAwait(false),
            ("iam", "list-roles") => await RunAwsFilteredAsync(
                ["iam", "list-roles"], opArgs, verbose, executor, AwsFilters.FilterIamRoles).ConfigureAwait(false),
            ("iam", "list-users") => await RunAwsFilteredAsync(
                ["iam", "list-users"], opArgs, verbose, executor, AwsFilters.FilterIamUsers).ConfigureAwait(false),
            ("dynamodb", "scan") => await RunAwsFilteredAsync(
                ["dynamodb", "scan"], opArgs, verbose, executor, AwsFilters.FilterDynamoDbItems).ConfigureAwait(false),
            ("dynamodb", "query") => await RunAwsFilteredAsync(
                ["dynamodb", "query"], opArgs, verbose, executor, AwsFilters.FilterDynamoDbItems).ConfigureAwait(false),
            ("ecs", "describe-tasks") => await RunAwsFilteredAsync(
                ["ecs", "describe-tasks"], opArgs, verbose, executor, AwsFilters.FilterEcsTasks).ConfigureAwait(false),
            ("ec2", "describe-security-groups") => await RunAwsFilteredAsync(
                ["ec2", "describe-security-groups"], opArgs, verbose, executor, AwsFilters.FilterSecurityGroups).ConfigureAwait(false),
            ("s3api", "list-objects-v2") => await RunAwsFilteredAsync(
                ["s3api", "list-objects-v2"], opArgs, verbose, executor, AwsFilters.FilterS3Objects).ConfigureAwait(false),
            ("eks", "describe-cluster") => await RunAwsFilteredAsync(
                ["eks", "describe-cluster"], opArgs, verbose, executor, AwsFilters.FilterEksCluster).ConfigureAwait(false),
            ("sqs", "receive-message") => await RunAwsFilteredAsync(
                ["sqs", "receive-message"], opArgs, verbose, executor, AwsFilters.FilterSqsMessages).ConfigureAwait(false),
            ("dynamodb", "get-item") => await RunAwsFilteredAsync(
                ["dynamodb", "get-item"], opArgs, verbose, executor, AwsFilters.FilterDynamoDbGetItem).ConfigureAwait(false),
            ("logs", "get-query-results") => await RunAwsFilteredAsync(
                ["logs", "get-query-results"], opArgs, verbose, executor, AwsFilters.FilterLogsQueryResults).ConfigureAwait(false),
            ("s3", "sync") => await RunS3TransferAsync("sync", opArgs, verbose, executor).ConfigureAwait(false),
            ("s3", "cp") => await RunS3TransferAsync("cp", opArgs, verbose, executor).ConfigureAwait(false),
            ("secretsmanager", "get-secret-value") => await RunAwsFilteredAsync(
                ["secretsmanager", "get-secret-value"], opArgs, verbose, executor, AwsFilters.FilterSecretsGet).ConfigureAwait(false),
            _ => await RunGenericAsync(subcommand, rest, verbose, fullSub, executor).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Returns true for operations that return structured JSON (<c>describe-*</c>/<c>list-*</c>/
    /// <c>get-*</c>/<c>scan</c>/<c>query</c>/<c>receive-message</c>). Mutating/transfer operations
    /// (<c>s3 cp</c>, <c>s3 sync</c>, <c>s3 mb</c>, etc.) emit plain-text progress and do not accept
    /// <c>--output json</c>, so it must not be injected for them. Faithful port of
    /// <c>is_structured_operation</c> (<c>aws_cmd.rs</c>:203-215).
    /// </summary>
    private static bool IsStructuredOperation(IReadOnlyList<string> args)
    {
        var op = args.Count > 0 ? args[0] : string.Empty;
        if (op is "sync" or "cp")
        {
            return false;
        }

        return op.StartsWith("describe-", StringComparison.Ordinal)
            || op.StartsWith("list-", StringComparison.Ordinal)
            || op.StartsWith("get-", StringComparison.Ordinal)
            || op is "scan" or "query" or "receive-message";
    }

    /// <summary>
    /// Generic strategy: force <c>--output json</c> for structured ops, compress via values-preserving
    /// JSON compaction. Faithful port of <c>run_generic</c> (<c>aws_cmd.rs</c>:218-278).
    /// </summary>
    private static async Task<int> RunGenericAsync(
        string subcommand, IReadOnlyList<string> args, int verbose, string fullSub, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var cmdArgs = new List<string> { subcommand };
        var hasOutputFlag = false;
        foreach (var arg in args)
        {
            if (arg == "--output")
            {
                hasOutputFlag = true;
            }

            cmdArgs.Add(arg);
        }

        // Only inject --output json for structured read operations. Mutating/transfer operations
        // (s3 cp, s3 sync, s3 mb, cloudformation deploy…) emit plain-text progress and reject
        // --output json.
        if (!hasOutputFlag && IsStructuredOperation(args))
        {
            cmdArgs.Add("--output");
            cmdArgs.Add("json");
        }

        if (verbose > 0)
        {
            Console.Error.Write($"Running: aws {fullSub}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("aws"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run aws CLI{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        var raw = result.Stdout;
        var stderr = result.Stderr;

        if (result.ExitCode != 0)
        {
            timer.Track($"aws {fullSub}", $"rtk aws {fullSub}", stderr, stderr);
            Console.Error.Write(stderr.Trim() + "\n");
            return result.ExitCode;
        }

        string filtered;
        try
        {
            filtered = AwsFilters.FilterJsonCompact(raw, JsonCompressDepth);
            Console.Out.Write(filtered + "\n");
        }
        catch (global::System.Text.Json.JsonException)
        {
            // Fallback: print raw (maybe not JSON).
            Console.Out.Write(raw);
            filtered = raw;
        }

        timer.Track($"aws {fullSub}", $"rtk aws {fullSub}", raw, filtered);
        return 0;
    }

    /// <summary>
    /// Runs the AWS CLI, forcing <c>--output json</c> (stripping any user-supplied
    /// <c>--output ...</c>). Faithful port of <c>run_aws_json</c> (<c>aws_cmd.rs</c>:280-324).
    /// </summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAwsJsonAsync(
        IReadOnlyList<string> subArgs, IReadOnlyList<string> extraArgs, int verbose, IProcessExecutor executor)
    {
        var cmdArgs = new List<string>(subArgs);

        // Replace --output table/text with --output json.
        for (var i = 0; i < extraArgs.Count; i++)
        {
            var arg = extraArgs[i];
            if (arg == "--output")
            {
                i++; // also skip its value
                continue;
            }

            if (arg.StartsWith("--output=", StringComparison.Ordinal))
            {
                continue;
            }

            cmdArgs.Add(arg);
        }

        cmdArgs.Add("--output");
        cmdArgs.Add("json");

        var cmdDesc = $"aws {string.Join(' ', subArgs)}";
        if (verbose > 0)
        {
            Console.Error.Write($"Running: {cmdDesc}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("aws"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run {cmdDesc}{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        if (result.ExitCode != 0)
        {
            // Faithful duplicate: run_aws_filtered ALSO prints stderr.trim() on this same failure
            // (see the class remarks on the preserved quirk).
            Console.Error.Write(result.Stderr.Trim() + "\n");
        }

        return (result.Stdout, result.Stderr, result.ExitCode);
    }

    /// <summary>
    /// Shared runner for AWS commands that return JSON. Follows the six-phase contract: timer →
    /// execute → filter (fallback) → tee → track → exit code. Faithful port of
    /// <c>run_aws_filtered</c> (<c>aws_cmd.rs</c>:328-377).
    /// </summary>
    private static async Task<int> RunAwsFilteredAsync(
        string[] subArgs,
        IReadOnlyList<string> extraArgs,
        int verbose,
        IProcessExecutor executor,
        Func<string, AwsFilters.FilterResult?> filterFn)
    {
        var cmdLabel = $"aws {string.Join(' ', subArgs)}";
        var rtkLabel = $"rtk {cmdLabel}";
        var slug = cmdLabel.Replace(' ', '_');
        var timer = TimedExecution.Start();

        var (stdout, stderr, exitCode) = await RunAwsJsonAsync(subArgs, extraArgs, verbose, executor).ConfigureAwait(false);

        // Combine stdout+stderr for accurate tracking (per contract).
        var raw = stderr.Length == 0 ? stdout : $"{stdout}\n{stderr}";

        if (exitCode != 0)
        {
            var hint = Tee.TeeAndHint(raw, slug, exitCode);
            Console.Error.Write(hint is not null ? $"{stderr.Trim()}\n{hint}\n" : $"{stderr.Trim()}\n");
            timer.Track(cmdLabel, rtkLabel, raw, stderr);
            return exitCode;
        }

        var result = filterFn(stdout);
        if (result is null)
        {
            Console.Error.Write("rtk: filter warning: aws filter returned None, passing through raw output\n");
            result = AwsFilters.FilterResult.New(stdout);
        }

        if (result.IsTruncated)
        {
            var hint = Tee.ForceTeeHint(raw, slug);
            Console.Out.Write(hint is not null ? $"{result.Text}\n{hint}\n" : $"{result.Text}\n");
        }
        else
        {
            var hint = Tee.TeeAndHint(raw, slug, 0);
            Console.Out.Write(hint is not null ? $"{result.Text}\n{hint}\n" : $"{result.Text}\n");
        }

        timer.Track(cmdLabel, rtkLabel, raw, result.Text);
        return 0;
    }

    /// <summary>Faithful port of <c>run_s3_ls</c> (<c>aws_cmd.rs</c>:379-424).</summary>
    private static async Task<int> RunS3LsAsync(IReadOnlyList<string> extraArgs, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();

        var cmdArgs = new List<string> { "s3", "ls" };
        cmdArgs.AddRange(extraArgs);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: aws s3 ls {string.Join(' ', extraArgs)}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("aws"), cmdArgs)).ConfigureAwait(false);
        if (!result.WasStarted)
        {
            throw new InvalidOperationException(
                $"Failed to run aws s3 ls{(string.IsNullOrEmpty(result.Failure) ? string.Empty : $": {result.Failure}")}");
        }

        var stdout = result.Stdout;
        var stderr = result.Stderr;
        var raw = stderr.Length == 0 ? stdout : $"{stdout}\n{stderr}";

        if (result.ExitCode != 0)
        {
            var hint = Tee.TeeAndHint(raw, "aws_s3_ls", result.ExitCode);
            Console.Error.Write(hint is not null ? $"{stderr.Trim()}\n{hint}\n" : $"{stderr.Trim()}\n");
            timer.Track("aws s3 ls", "rtk aws s3 ls", raw, stderr);
            return result.ExitCode;
        }

        var filterResult = AwsFilters.FilterS3Ls(stdout);
        if (filterResult.IsTruncated)
        {
            var hint = Tee.ForceTeeHint(raw, "aws_s3_ls");
            Console.Out.Write(hint is not null ? $"{filterResult.Text}\n{hint}\n" : $"{filterResult.Text}\n");
        }
        else
        {
            Console.Out.Write(filterResult.Text + "\n");
        }

        timer.Track("aws s3 ls", "rtk aws s3 ls", raw, filterResult.Text);
        return 0;
    }

    /// <summary>Run s3 sync/cp (text output, not JSON). Faithful port of <c>run_s3_transfer</c> (<c>aws_cmd.rs</c>:427-477).</summary>
    private static async Task<int> RunS3TransferAsync(string operation, IReadOnlyList<string> extraArgs, int verbose, IProcessExecutor executor)
    {
        var timer = TimedExecution.Start();
        var cmdLabel = $"aws s3 {operation}";
        var rtkLabel = $"rtk aws s3 {operation}";
        var slug = $"aws_s3_{operation}";

        var cmdArgs = new List<string> { "s3", operation };
        cmdArgs.AddRange(extraArgs);

        if (verbose > 0)
        {
            Console.Error.Write($"Running: {cmdLabel} {string.Join(' ', extraArgs)}\n");
        }

        var result = await executor.ExecuteAsync(new ExecutionRequest(PathResolver.Resolve("aws"), cmdArgs)).ConfigureAwait(false);
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

        var filterResult = AwsFilters.FilterS3Transfer(stdout);
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
