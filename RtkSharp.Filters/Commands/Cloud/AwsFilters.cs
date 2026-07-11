using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RtkSharp.Core;

namespace RtkSharp.Filters.Commands.Cloud;

/// <summary>
/// Pure filter functions for <c>rtk aws</c>: each parses a raw AWS CLI JSON (or, for
/// <c>s3 ls</c>/<c>s3 sync</c>/<c>s3 cp</c>, plain-text) response and produces a compact
/// token-optimized rendering. Faithful port of the filter functions in
/// <c>src/cmds/cloud/aws_cmd.rs</c> (everything below its <c>run</c>/<c>run_aws_filtered</c>
/// dispatch skeleton, which lives in <see cref="AwsCommand"/> instead).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="FilterResult"/> mirrors Rust's own <c>FilterResult</c> struct</b> (<c>text</c> +
/// <c>truncated</c>) exactly: when <c>Truncated</c> is true, the shared runner force-tees the full
/// raw output so the LLM has a recovery path to the untruncated data.
/// </para>
/// <para>
/// <b><see cref="DaysToYmd"/> is Howard Hinnant's civil-calendar algorithm</b>, ported bit-for-bit
/// (same operator sequence, same truncating integer division) from
/// <c>aws_cmd.rs</c>:706-719, which itself credits
/// http://howardhinnant.github.io/date_algorithms.html. This is used (not
/// <see cref="DateTimeOffset"/>) so behavior at negative epoch-day and year-boundary edge cases is
/// bit-identical to the Rust oracle rather than merely "close enough".
/// </para>
/// <para>
/// <b><see cref="UnwrapDynamoDbValue"/> preserves source key order</b>, matching Rust's
/// <c>serde_json</c> which is built with the <c>preserve_order</c> Cargo feature (<c>Cargo.toml</c>:23)
/// — so its <c>Map</c> is insertion-ordered, not alphabetically sorted. <see cref="JsonObject"/> (from
/// <c>System.Text.Json.Nodes</c>) has the same insertion-order guarantee, so building the unwrapped
/// tree with it and calling <c>ToJsonString()</c> reproduces the same key ordering as Rust's
/// <c>serde_json::to_string</c>. The recursion depth guard matches exactly: <c>depth &gt; 10</c> stops
/// further unwrapping and clones the value as-is.
/// </para>
/// </remarks>
public static class AwsFilters
{
    // Rust's MAX_ITEMS = CAP_LIST (core/truncate.rs).
    private const int MaxItems = 20;

    // Rust's MAX_LOG_EVENTS = CAP_INVENTORY (core/truncate.rs).
    private const int MaxLogEvents = 50;

    private static readonly global::System.Text.RegularExpressions.Regex S3TransferRegex =
        new(@"^(upload|download|delete|copy|move):", global::System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Result of a filter function: filtered text + whether items were truncated. Faithful port of
    /// Rust's <c>FilterResult</c> struct (<c>aws_cmd.rs</c>:24-43).
    /// </summary>
    public sealed class FilterResult
    {
        private FilterResult(string text, bool isTruncated)
        {
            Text = text;
            IsTruncated = isTruncated;
        }

        /// <summary>The filtered, token-optimized text.</summary>
        public string Text { get; }

        /// <summary>Whether items were truncated (triggers a force-tee of the full raw output).</summary>
        public bool IsTruncated { get; }

        /// <summary>Creates a non-truncated result.</summary>
        public static FilterResult New(string text) => new(text, false);

        /// <summary>Creates a truncated result.</summary>
        public static FilterResult Truncated(string text) => new(text, true);
    }

    // ===================== sts =====================

    /// <summary>Faithful port of <c>filter_sts_identity</c> (<c>aws_cmd.rs</c>:483-488).</summary>
    public static FilterResult? FilterStsIdentity(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            var account = JStr(v, "Account", "?");
            var arn = JStr(v, "Arn", "?");
            return FilterResult.New($"AWS: {account} {arn}");
        }
    }

    // ===================== s3 ls =====================

    /// <summary>Faithful port of <c>filter_s3_ls</c> (<c>aws_cmd.rs</c>:490-505).</summary>
    public static FilterResult FilterS3Ls(string output)
    {
        var lines = SourceFilterLineSplitter.SplitLines(output);
        var total = lines.Count;
        var limit = MaxItems + 10;

        if (total > limit)
        {
            var text = $"{string.Join('\n', lines.Take(limit))}\n… +{total - limit} more items";
            return FilterResult.Truncated(text);
        }

        return FilterResult.New(string.Join('\n', lines));
    }

    // ===================== ec2 describe-instances =====================

    /// <summary>Faithful port of <c>filter_ec2_instances</c> (<c>aws_cmd.rs</c>:507-565).</summary>
    public static FilterResult? FilterEc2Instances(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "Reservations", out var reservations) || reservations.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var instances = new List<string>();
            foreach (var res in reservations.EnumerateArray())
            {
                if (!TryGetProp(res, "Instances", out var insts) || insts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var inst in insts.EnumerateArray())
                {
                    var id = JStr(inst, "InstanceId", "?");
                    var state = JNestedStr(inst, "State", "Name", "?");
                    var itype = JStr(inst, "InstanceType", "?");
                    var privateIp = JStr(inst, "PrivateIpAddress", "-");
                    var publicIp = JStr(inst, "PublicIpAddress", "-");
                    var subnet = JStr(inst, "SubnetId", "-");
                    var vpc = JStr(inst, "VpcId", "-");

                    var name = "-";
                    if (TryGetProp(inst, "Tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tags.EnumerateArray())
                        {
                            if (JStr(t, "Key", string.Empty) == "Name")
                            {
                                name = JStr(t, "Value", "-");
                                break;
                            }
                        }
                    }

                    var sgs = new List<string>();
                    if (TryGetProp(inst, "SecurityGroups", out var sgArr) && sgArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sg in sgArr.EnumerateArray())
                        {
                            if (TryGetProp(sg, "GroupId", out var gid) && gid.ValueKind == JsonValueKind.String)
                            {
                                sgs.Add(gid.GetString()!);
                            }
                        }
                    }

                    var sgStr = sgs.Count == 0 ? "-" : string.Join(',', sgs);

                    instances.Add(
                        $"{id} {state} {itype} {privateIp} pub:{publicIp} vpc:{vpc} subnet:{subnet} sg:[{sgStr}] ({name})");
                }
            }

            var total = instances.Count;
            var truncated = total > MaxItems;
            var sb = new StringBuilder();
            sb.Append($"EC2: {total} instances\n");
            foreach (var inst in instances.Take(MaxItems))
            {
                sb.Append($"  {inst}\n");
            }

            if (truncated)
            {
                sb.Append($"  … +{total - MaxItems} more\n");
            }

            var text = sb.ToString().TrimEnd();
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    // ===================== ecs list-services / describe-services =====================

    /// <summary>Faithful port of <c>filter_ecs_list_services</c> (<c>aws_cmd.rs</c>:567-585).</summary>
    public static FilterResult? FilterEcsListServices(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "serviceArns", out var arns) || arns.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = arns.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var arn in arns.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var arnStr = arn.ValueKind == JsonValueKind.String ? arn.GetString()! : "?";
                result.Add(ShortenArn(arnStr));
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "services");
            return total > MaxItems ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_ecs_describe_services</c> (<c>aws_cmd.rs</c>:587-612).</summary>
    public static FilterResult? FilterEcsDescribeServices(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "services", out var services) || services.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = services.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var svc in services.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(svc, "serviceName", "?");
                var status = JStr(svc, "status", "?");
                var running = JInt(svc, "runningCount", 0);
                var desired = JInt(svc, "desiredCount", 0);
                var launch = JStr(svc, "launchType", "?");
                result.Add($"{name} {status} {running}/{desired} ({launch})");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "services");
            return total > MaxItems ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    // ===================== rds =====================

    /// <summary>Faithful port of <c>filter_rds_instances</c> (<c>aws_cmd.rs</c>:614-641).</summary>
    public static FilterResult? FilterRdsInstances(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "DBInstances", out var dbs) || dbs.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = dbs.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var db in dbs.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(db, "DBInstanceIdentifier", "?");
                var engine = JStr(db, "Engine", "?");
                var version = JStr(db, "EngineVersion", "?");
                var dbClass = JStr(db, "DBInstanceClass", "?");
                var status = JStr(db, "DBInstanceStatus", "?");
                var endpoint = JNestedStr(db, "Endpoint", "Address", "-");
                var port = JNestedInt(db, "Endpoint", "Port", 0);
                result.Add($"{name} {engine} {version} {dbClass} {status} {endpoint}:{port}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "instances");
            return total > MaxItems ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    // ===================== cloudformation =====================

    /// <summary>Faithful port of <c>filter_cfn_list_stacks</c> (<c>aws_cmd.rs</c>:643-666).</summary>
    public static FilterResult? FilterCfnListStacks(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "StackSummaries", out var stacks) || stacks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = stacks.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var stack in stacks.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(stack, "StackName", "?");
                var status = JStr(stack, "StackStatus", "?");
                var date = TruncateIsoDate(JStrOr(stack, "LastUpdatedTime", "CreationTime", "?"));
                result.Add($"{name} {status} {date}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "stacks");
            return total > MaxItems ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_cfn_describe_stacks</c> (<c>aws_cmd.rs</c>:668-699).</summary>
    public static FilterResult? FilterCfnDescribeStacks(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "Stacks", out var stacks) || stacks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = stacks.GetArrayLength();
            var result = new List<string>();
            var i = 0;
            foreach (var stack in stacks.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(stack, "StackName", "?");
                var status = JStr(stack, "StackStatus", "?");
                var date = TruncateIsoDate(JStrOr(stack, "LastUpdatedTime", "CreationTime", "?"));
                result.Add($"{name} {status} {date}");

                if (TryGetProp(stack, "Outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var outp in outputs.EnumerateArray())
                    {
                        var key = JStr(outp, "OutputKey", "?");
                        var val = JStr(outp, "OutputValue", "?");
                        result.Add($"  {key}={val}");
                    }
                }

                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "stacks");
            return total > MaxItems ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    // ===================== P0: CloudWatch Logs, CloudFormation Events, Lambda =====================

    /// <summary>
    /// Convert days since Unix epoch to (year, month, day). Civil calendar, UTC. Bit-for-bit port of
    /// <c>days_to_ymd</c> (<c>aws_cmd.rs</c>:706-719); algorithm from
    /// http://howardhinnant.github.io/date_algorithms.html.
    /// </summary>
    internal static (long Year, long Month, long Day) DaysToYmd(long days)
    {
        var z = days + 719468;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var doe = z - (era * 146097);
        var yoe = (doe - (doe / 1460) + (doe / 36524) - (doe / 146096)) / 365;
        var y = yoe + (era * 400);
        var doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        var mp = ((5 * doy) + 2) / 153;
        var d = doy - (((153 * mp) + 2) / 5) + 1;
        var m = mp < 10 ? mp + 3 : mp - 9;
        y = m <= 2 ? y + 1 : y;
        return (y, m, d);
    }

    /// <summary>Faithful port of <c>filter_logs_events</c> (<c>aws_cmd.rs</c>:721-771).</summary>
    public static FilterResult? FilterLogsEvents(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = events.GetArrayLength();
            var truncated = total > MaxLogEvents;
            var lines = new List<string>();

            var i = 0;
            foreach (var evt in events.EnumerateArray())
            {
                if (i >= MaxLogEvents)
                {
                    break;
                }

                i++;

                string timeStr;
                if (TryGetProp(evt, "timestamp", out var tsEl)
                    && tsEl.ValueKind == JsonValueKind.Number
                    && tsEl.TryGetInt64(out var ts)
                    && ts > 0)
                {
                    var epochSecs = ts / 1000;
                    var days = epochSecs / 86400;
                    var timeOfDay = epochSecs % 86400;
                    var h = timeOfDay / 3600;
                    var m = (timeOfDay % 3600) / 60;
                    var s = timeOfDay % 60;
                    var (y, mo, d) = DaysToYmd(days);
                    timeStr = string.Create(CultureInfo.InvariantCulture, $"{y:0000}-{mo:00}-{d:00} {h:00}:{m:00}:{s:00}");
                }
                else
                {
                    timeStr = "??:??:??";
                }

                var msg = JStr(evt, "message", string.Empty).TrimEnd();
                var compactMsg = msg;
                if (msg.StartsWith('{'))
                {
                    try
                    {
                        using var msgDoc = JsonDocument.Parse(msg);
                        compactMsg = CompactSerialize(msgDoc.RootElement);
                    }
                    catch (JsonException)
                    {
                        compactMsg = msg;
                    }
                }

                lines.Add($"{timeStr} {compactMsg}");
            }

            if (truncated)
            {
                lines.Add($"… +{total - MaxLogEvents} more events");
            }

            var text = string.Join('\n', lines);
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_cfn_events</c> (<c>aws_cmd.rs</c>:773-834).</summary>
    public static FilterResult? FilterCfnEvents(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "StackEvents", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var failed = new List<string>();
            var failedCount = 0;
            var successCount = 0;

            foreach (var evt in events.EnumerateArray())
            {
                var status = JStr(evt, "ResourceStatus", "?");
                var logicalId = JStr(evt, "LogicalResourceId", "?");
                var resourceTypeRaw = JStr(evt, "ResourceType", "?");
                var resourceType = resourceTypeRaw.StartsWith("AWS::", StringComparison.Ordinal)
                    ? resourceTypeRaw["AWS::".Length..]
                    : resourceTypeRaw;
                var ts = TruncateIsoDate(JStr(evt, "Timestamp", "?"));

                if (status.Contains("FAILED", StringComparison.Ordinal) || status.Contains("ROLLBACK", StringComparison.Ordinal))
                {
                    failedCount++;
                    if (failed.Count < MaxItems)
                    {
                        var reason = JStr(evt, "ResourceStatusReason", string.Empty);
                        var line = $"{ts} {logicalId} {resourceType} {status}";
                        if (reason.Length > 0)
                        {
                            line += $" REASON: {reason}";
                        }

                        failed.Add(line);
                    }
                }
                else
                {
                    successCount++;
                }
            }

            var totalEvents = events.GetArrayLength();
            var lines = new List<string> { $"CloudFormation: {totalEvents} events ({failedCount} failed, {successCount} successful)" };

            if (failed.Count > 0)
            {
                lines.Add("--- FAILURES ---");
                foreach (var f in failed)
                {
                    lines.Add($"  {f}");
                }
            }

            if (successCount > 0)
            {
                lines.Add($"+ {successCount} successful resources");
            }

            var truncated = totalEvents > MaxItems * 5;
            var text = string.Join('\n', lines);
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_lambda_list</c> (<c>aws_cmd.rs</c>:836-863).</summary>
    public static FilterResult? FilterLambdaList(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "Functions", out var functions) || functions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = functions.GetArrayLength();
            var truncated = total > MaxItems;
            var result = new List<string>();
            var i = 0;
            foreach (var func in functions.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(func, "FunctionName", "?");
                var runtime = JStr(func, "Runtime", "?");
                var memory = JInt(func, "MemorySize", 0);
                var timeout = JInt(func, "Timeout", 0);
                var state = JStr(func, "State", "active");

                // SECURITY: Environment is intentionally NOT read (may contain secrets)
                result.Add($"{name} {runtime} {memory}MB {timeout}s {state}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "functions");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_lambda_get</c> (<c>aws_cmd.rs</c>:865-907).</summary>
    public static FilterResult? FilterLambdaGet(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            TryGetProp(v, "Configuration", out var config);

            var name = JStr(config, "FunctionName", "?");
            var runtime = JStr(config, "Runtime", "?");
            var handler = JStr(config, "Handler", "?");
            var memory = JInt(config, "MemorySize", 0);
            var timeout = JInt(config, "Timeout", 0);
            var state = JStr(config, "State", "active");
            var lastModified = TruncateIsoDate(JStr(config, "LastModified", "?"));

            // SECURITY: Environment and Code.Location intentionally NOT read
            var text = $"{name} {runtime} {handler} {memory}MB {timeout}s {state} {lastModified}";

            // Show layer names if present. Layer ARNs use colons:
            // arn:aws:lambda:region:acct:layer:name:version
            if (TryGetProp(config, "Layers", out var layers) && layers.ValueKind == JsonValueKind.Array && layers.GetArrayLength() > 0)
            {
                var layerNames = new List<string>();
                foreach (var l in layers.EnumerateArray())
                {
                    if (!TryGetProp(l, "Arn", out var arnEl) || arnEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var arn = arnEl.GetString()!;
                    var parts = arn.Split(':');
                    if (parts.Length >= 2)
                    {
                        layerNames.Add($"{parts[^2]}:{parts[^1]}");
                    }
                    else
                    {
                        layerNames.Add(arn);
                    }
                }

                if (layerNames.Count > 0)
                {
                    text += $"\n  layers: {string.Join(", ", layerNames)}";
                }
            }

            return FilterResult.New(text);
        }
    }

    // ===================== P1: IAM, DynamoDB, ECS tasks =====================

    /// <summary>
    /// Extract principal services/accounts from AssumeRolePolicyDocument. Returns a compact list like
    /// <c>["lambda.amazonaws.com", "ecs-tasks.amazonaws.com"]</c> instead of the full 200+ token JSON
    /// policy document. Faithful port of <c>extract_assume_principals</c> (<c>aws_cmd.rs</c>:914-954).
    /// </summary>
    internal static List<string> ExtractAssumePrincipals(JsonElement role)
    {
        var principals = new List<string>();

        JsonElement doc = default;
        var hasDoc = false;
        if (TryGetProp(role, "AssumeRolePolicyDocument", out var docEl))
        {
            if (docEl.ValueKind == JsonValueKind.String)
            {
                if (TryParse(docEl.GetString()!, out var parsed))
                {
                    using (parsed)
                    {
                        doc = parsed.RootElement.Clone();
                        hasDoc = true;
                    }
                }
            }
            else if (docEl.ValueKind == JsonValueKind.Object)
            {
                doc = docEl;
                hasDoc = true;
            }
        }

        if (hasDoc && TryGetProp(doc, "Statement", out var stmts) && stmts.ValueKind == JsonValueKind.Array)
        {
            foreach (var stmt in stmts.EnumerateArray())
            {
                if (!TryGetProp(stmt, "Principal", out var principal))
                {
                    continue;
                }

                if (principal.ValueKind == JsonValueKind.String)
                {
                    principals.Add(principal.GetString()!);
                }
                else if (TryGetProp(principal, "Service", out var svcS) && svcS.ValueKind == JsonValueKind.String)
                {
                    principals.Add(svcS.GetString()!);
                }
                else if (TryGetProp(principal, "Service", out var svcA) && svcA.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in svcA.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.String)
                        {
                            principals.Add(s.GetString()!);
                        }
                    }
                }
                else if (TryGetProp(principal, "AWS", out var awsS) && awsS.ValueKind == JsonValueKind.String)
                {
                    principals.Add(ShortenArn(awsS.GetString()!));
                }
                else if (TryGetProp(principal, "AWS", out var awsA) && awsA.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in awsA.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                        {
                            principals.Add(ShortenArn(a.GetString()!));
                        }
                    }
                }
            }
        }

        // Rust's Vec::dedup removes only consecutive duplicates.
        var deduped = new List<string>();
        foreach (var p in principals)
        {
            if (deduped.Count == 0 || deduped[^1] != p)
            {
                deduped.Add(p);
            }
        }

        return deduped;
    }

    /// <summary>Faithful port of <c>filter_iam_roles</c> (<c>aws_cmd.rs</c>:956-993).</summary>
    public static FilterResult? FilterIamRoles(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "Roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = roles.GetArrayLength();
            var truncated = total > MaxItems;
            var result = new List<string>();
            var i = 0;
            foreach (var role in roles.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(role, "RoleName", "?");
                var date = TruncateIsoDate(JStr(role, "CreateDate", "?"));
                var desc = JStr(role, "Description", string.Empty);

                var principals = ExtractAssumePrincipals(role);
                var principalStr = principals.Count == 0 ? string.Empty : $" assume:[{string.Join(',', principals)}]";

                result.Add(desc.Length == 0
                    ? $"{name} {date}{principalStr}"
                    : $"{name} {date} [{desc}]{principalStr}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "roles");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_iam_users</c> (<c>aws_cmd.rs</c>:995-1018).</summary>
    public static FilterResult? FilterIamUsers(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "Users", out var users) || users.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = users.GetArrayLength();
            var truncated = total > MaxItems;
            var result = new List<string>();
            var i = 0;
            foreach (var user in users.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(user, "UserName", "?");
                var date = TruncateIsoDate(JStr(user, "CreateDate", "?"));
                result.Add($"{name} created:{date}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "users");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>
    /// Recursively unwrap DynamoDB typed values to plain JSON: <c>{"S": "foo"}</c> -&gt; <c>"foo"</c>,
    /// <c>{"N": "42"}</c> -&gt; <c>42</c>, <c>{"M": {...}}</c> -&gt; unwrapped object, etc. Faithful port
    /// of <c>unwrap_dynamodb_value</c> (<c>aws_cmd.rs</c>:1020-1102), including the exact
    /// <c>depth &gt; 10</c> recursion cutoff.
    /// </summary>
    internal static JsonNode? UnwrapDynamoDbValue(JsonElement val, int depth)
    {
        if (depth > 10)
        {
            return CloneToNode(val);
        }

        if (val.ValueKind == JsonValueKind.Object)
        {
            var props = val.EnumerateObject().ToList();
            if (props.Count == 1)
            {
                var key = props[0].Name;
                var inner = props[0].Value;

                switch (key)
                {
                    case "S":
                    case "B":
                        return CloneToNode(inner);
                    case "N":
                        return UnwrapNumber(inner);
                    case "BOOL":
                        return CloneToNode(inner);
                    case "NULL":
                        return null;
                    case "L":
                        if (inner.ValueKind == JsonValueKind.Array)
                        {
                            var arr = new JsonArray();
                            foreach (var item in inner.EnumerateArray())
                            {
                                arr.Add(UnwrapDynamoDbValue(item, depth + 1));
                            }

                            return arr;
                        }

                        break;
                    case "M":
                        if (inner.ValueKind == JsonValueKind.Object)
                        {
                            var obj = new JsonObject();
                            foreach (var p in inner.EnumerateObject())
                            {
                                obj[p.Name] = UnwrapDynamoDbValue(p.Value, depth + 1);
                            }

                            return obj;
                        }

                        break;
                    case "SS":
                        return CloneToNode(inner);
                    case "NS":
                        if (inner.ValueKind == JsonValueKind.Array)
                        {
                            var nums = new JsonArray();
                            foreach (var item in inner.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.String)
                                {
                                    continue;
                                }

                                nums.Add(UnwrapNumber(item));
                            }

                            return nums;
                        }

                        return CloneToNode(inner);
                    case "BS":
                        return CloneToNode(inner);
                }
            }

            // Not a DynamoDB type wrapper — unwrap each field as a potential item.
            var unwrapped = new JsonObject();
            foreach (var p in val.EnumerateObject())
            {
                unwrapped[p.Name] = UnwrapDynamoDbValue(p.Value, depth + 1);
            }

            return unwrapped;
        }

        return CloneToNode(val);
    }

    /// <summary>
    /// Parses a DynamoDB <c>N</c>/<c>NS</c>-member string as i64 first, then f64, falling back to the
    /// original string when both parses fail (matches Rust's <c>str::parse::&lt;i64&gt;</c> /
    /// <c>str::parse::&lt;f64&gt;</c> / string fallback chain exactly).
    /// </summary>
    private static JsonNode? UnwrapNumber(JsonElement inner)
    {
        if (inner.ValueKind != JsonValueKind.String)
        {
            return CloneToNode(inner);
        }

        var s = inner.GetString()!;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return JsonValue.Create(n);
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            && !double.IsNaN(f) && !double.IsInfinity(f))
        {
            return JsonValue.Create(f);
        }

        return JsonValue.Create(s);
    }

    private static JsonNode? CloneToNode(JsonElement el) =>
        el.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(el.GetRawText());

    /// <summary>Faithful port of <c>filter_dynamodb_items</c> (<c>aws_cmd.rs</c>:1104-1144).</summary>
    public static FilterResult? FilterDynamoDbItems(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "Items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = items.GetArrayLength();
            var count = JInt(v, "Count", total);
            var scanned = TryGetProp(v, "ScannedCount", out var scEl) && scEl.ValueKind == JsonValueKind.Number && scEl.TryGetInt64(out var sc)
                ? sc
                : count;
            var truncated = total > MaxItems;

            var lines = new List<string> { $"Count: {count}/{scanned}" };

            if (TryGetProp(v, "ConsumedCapacity", out var capacity) && capacity.ValueKind == JsonValueKind.Object
                && TryGetProp(capacity, "CapacityUnits", out var cu) && cu.ValueKind == JsonValueKind.Number
                && cu.TryGetDouble(out var units))
            {
                lines.Add($"Capacity: {FormatNum(units)} RCU");
            }

            if (TryGetProp(v, "LastEvaluatedKey", out var lek) && lek.ValueKind == JsonValueKind.Object)
            {
                lines.Add("(paginated — more results available)");
            }

            var i = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                i++;
                var unwrapped = UnwrapDynamoDbValue(item, 0);
                lines.Add(unwrapped?.ToJsonString() ?? "?");
            }

            if (truncated)
            {
                lines.Add($"… +{total - MaxItems} more items");
            }

            var text = string.Join('\n', lines);
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_ecs_tasks</c> (<c>aws_cmd.rs</c>:1146-1198).</summary>
    public static FilterResult? FilterEcsTasks(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = tasks.GetArrayLength();
            var truncated = total > MaxItems;
            var result = new List<string>();
            var i = 0;
            foreach (var task in tasks.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var taskArn = JStr(task, "taskArn", "?");
                var taskId = ShortenArn(taskArn);
                var status = JStr(task, "lastStatus", "?");

                var containers = new List<string>();
                if (TryGetProp(task, "containers", out var containersEl) && containersEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in containersEl.EnumerateArray())
                    {
                        var cname = JStr(c, "name", "?");
                        var cstatus = JStr(c, "lastStatus", "?");
                        if (TryGetProp(c, "exitCode", out var exitEl) && exitEl.ValueKind == JsonValueKind.Number && exitEl.TryGetInt64(out var exit))
                        {
                            containers.Add($"{cname}:{cstatus}(exit:{exit})");
                        }
                        else
                        {
                            containers.Add($"{cname}:{cstatus}");
                        }
                    }
                }

                var stoppedReason = JStr(task, "stoppedReason", string.Empty);
                var reasonStr = stoppedReason.Length == 0 ? string.Empty : $" reason:{stoppedReason}";

                result.Add($"{taskId} {status} containers:[{string.Join(", ", containers)}]{reasonStr}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "tasks");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    // ===================== P2: Security Groups, S3 objects, EKS, SQS =====================

    /// <summary>Faithful port of <c>format_sg_rule</c> (<c>aws_cmd.rs</c>:1202-1247).</summary>
    internal static string FormatSgRule(JsonElement perm)
    {
        var protocol = JStr(perm, "IpProtocol", "?");
        var proto = protocol == "-1" ? "all" : protocol;

        long fromPort = 0;
        long toPort = 0;
        var hasFrom = TryGetProp(perm, "FromPort", out var fromEl) && fromEl.ValueKind == JsonValueKind.Number && fromEl.TryGetInt64(out fromPort);
        var hasTo = TryGetProp(perm, "ToPort", out var toEl) && toEl.ValueKind == JsonValueKind.Number && toEl.TryGetInt64(out toPort);
        string port;
        if (hasFrom && hasTo && fromPort == toPort)
        {
            port = fromPort.ToString(CultureInfo.InvariantCulture);
        }
        else if (hasFrom && hasTo)
        {
            port = $"{fromPort}-{toPort}";
        }
        else
        {
            port = "*";
        }

        var sources = new List<string>();
        if (TryGetProp(perm, "IpRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in ranges.EnumerateArray())
            {
                if (TryGetProp(r, "CidrIp", out var cidr) && cidr.ValueKind == JsonValueKind.String)
                {
                    sources.Add(cidr.GetString()!);
                }
            }
        }

        if (TryGetProp(perm, "Ipv6Ranges", out var ranges6) && ranges6.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in ranges6.EnumerateArray())
            {
                if (TryGetProp(r, "CidrIpv6", out var cidr6) && cidr6.ValueKind == JsonValueKind.String)
                {
                    sources.Add(cidr6.GetString()!);
                }
            }
        }

        if (TryGetProp(perm, "UserIdGroupPairs", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in groups.EnumerateArray())
            {
                sources.Add(JStr(g, "GroupId", "?"));
            }
        }

        var src = sources.Count == 0 ? "?" : string.Join(',', sources);

        return proto == "all" ? $"all<-{src}" : $"{proto}/{port}<-{src}";
    }

    /// <summary>Faithful port of <c>filter_security_groups</c> (<c>aws_cmd.rs</c>:1249-1293).</summary>
    public static FilterResult? FilterSecurityGroups(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            if (!TryGetProp(v, "SecurityGroups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = groups.GetArrayLength();
            var truncated = total > MaxItems;
            var result = new List<string>();
            var i = 0;
            foreach (var sg in groups.EnumerateArray())
            {
                if (i >= MaxItems)
                {
                    break;
                }

                var name = JStr(sg, "GroupName", "?");
                var id = JStr(sg, "GroupId", "?");

                var ingress = new List<string>();
                if (TryGetProp(sg, "IpPermissions", out var ingressEl) && ingressEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var perm in ingressEl.EnumerateArray())
                    {
                        ingress.Add(FormatSgRule(perm));
                    }
                }

                var egress = new List<string>();
                if (TryGetProp(sg, "IpPermissionsEgress", out var egressEl) && egressEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var perm in egressEl.EnumerateArray())
                    {
                        egress.Add(FormatSgRule(perm));
                    }
                }

                var ingressStr = ingress.Count == 0 ? "none" : string.Join(", ", ingress);
                var egressStr = egress.Count == 0 ? "none" : string.Join(", ", egress);

                result.Add($"{name} ({id}) ingress: {ingressStr} | egress: {egressStr}");
                i++;
            }

            var text = JoinWithOverflow(result, total, MaxItems, "groups");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_s3_objects</c> (<c>aws_cmd.rs</c>:1295-1320).</summary>
    public static FilterResult? FilterS3Objects(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            var hasContents = TryGetProp(v, "Contents", out var contents) && contents.ValueKind == JsonValueKind.Array;
            var total = hasContents ? contents.GetArrayLength() : 0;
            var truncated = total > MaxItems;
            var result = new List<string>();

            if (hasContents)
            {
                var i = 0;
                foreach (var obj in contents.EnumerateArray())
                {
                    if (i >= MaxItems)
                    {
                        break;
                    }

                    var key = JStr(obj, "Key", "?");
                    var size = TryGetProp(obj, "Size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetUInt64(out var sz)
                        ? sz
                        : 0UL;
                    var modified = TruncateIsoDate(JStr(obj, "LastModified", "?"));
                    result.Add($"{key} {HumanBytes(size)} {modified}");
                    i++;
                }
            }

            var text = JoinWithOverflow(result, total, MaxItems, "objects");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_eks_cluster</c> (<c>aws_cmd.rs</c>:1322-1334).</summary>
    public static FilterResult? FilterEksCluster(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            TryGetProp(v, "cluster", out var cluster);

            var name = JStr(cluster, "name", "?");
            var status = JStr(cluster, "status", "?");
            var version = JStr(cluster, "version", "?");
            var endpoint = JStr(cluster, "endpoint", "?");

            // certificateAuthority intentionally NOT read (base64 cert, 1000+ chars)
            return FilterResult.New($"{name} {status} k8s/{version} {endpoint}");
        }
    }

    /// <summary>Faithful port of <c>filter_sqs_messages</c> (<c>aws_cmd.rs</c>:1340-1364).</summary>
    public static FilterResult? FilterSqsMessages(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            var hasMessages = TryGetProp(v, "Messages", out var messages) && messages.ValueKind == JsonValueKind.Array;
            var total = hasMessages ? messages.GetArrayLength() : 0;
            var truncated = total > MaxItems;
            var result = new List<string>();

            if (hasMessages)
            {
                var i = 0;
                foreach (var msg in messages.EnumerateArray())
                {
                    if (i >= MaxItems)
                    {
                        break;
                    }

                    var id = JStr(msg, "MessageId", "?");
                    var idShort = id[..Math.Min(8, id.Length)];
                    var body = JStr(msg, "Body", "?");
                    var bodyTruncated = Truncate(body, 200);

                    // ReceiptHandle intentionally NOT read (200+ chars of opaque garbage)
                    result.Add($"{idShort} {bodyTruncated}");
                    i++;
                }
            }

            var text = JoinWithOverflow(result, total, MaxItems, "messages");
            return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
        }
    }

    /// <summary>Faithful port of <c>filter_dynamodb_get_item</c> (<c>aws_cmd.rs</c>:1366-1390).</summary>
    public static FilterResult? FilterDynamoDbGetItem(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            var lines = new List<string>();

            if (TryGetProp(v, "Item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                var unwrapped = UnwrapDynamoDbValue(item, 0);
                lines.Add(unwrapped?.ToJsonString() ?? "?");
            }

            if (TryGetProp(v, "ConsumedCapacity", out var capacity) && capacity.ValueKind == JsonValueKind.Object
                && TryGetProp(capacity, "CapacityUnits", out var cu) && cu.ValueKind == JsonValueKind.Number
                && cu.TryGetDouble(out var units))
            {
                lines.Add($"Capacity: {FormatNum(units)} RCU");
            }

            if (lines.Count == 0)
            {
                return null;
            }

            return FilterResult.New(string.Join('\n', lines));
        }
    }

    /// <summary>Faithful port of <c>filter_logs_query_results</c> (<c>aws_cmd.rs</c>:1392-1442).</summary>
    public static FilterResult? FilterLogsQueryResults(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            var lines = new List<string>();

            if (TryGetProp(v, "status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            {
                lines.Add($"Status: {statusEl.GetString()}");
            }

            if (TryGetProp(v, "results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                var total = results.GetArrayLength();
                var truncated = total > MaxItems;

                var i = 0;
                foreach (var row in results.EnumerateArray())
                {
                    if (i >= MaxItems)
                    {
                        break;
                    }

                    i++;

                    if (row.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var fieldPairs = new List<string>();
                    foreach (var field in row.EnumerateArray())
                    {
                        if (!TryGetProp(field, "field", out var fieldNameEl) || fieldNameEl.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var fieldName = fieldNameEl.GetString()!;
                        if (fieldName == "@ptr")
                        {
                            continue;
                        }

                        string fieldValue;
                        if (TryGetProp(field, "value", out var valueEl))
                        {
                            fieldValue = valueEl.ValueKind == JsonValueKind.String
                                ? valueEl.GetString()!
                                : CompactSerialize(valueEl);
                        }
                        else
                        {
                            fieldValue = "null";
                        }

                        fieldPairs.Add($"{fieldName}={fieldValue}");
                    }

                    lines.Add(string.Join(' ', fieldPairs));
                }

                if (truncated)
                {
                    lines.Add($"… +{total - MaxItems} more rows");
                }

                var text = string.Join('\n', lines);
                return truncated ? FilterResult.Truncated(text) : FilterResult.New(text);
            }

            return null;
        }
    }

    /// <summary>Faithful port of <c>filter_s3_transfer</c> (<c>aws_cmd.rs</c>:1444-1513).</summary>
    public static FilterResult FilterS3Transfer(string output)
    {
        var lines = SourceFilterLineSplitter.SplitLines(output);
        var total = lines.Count;

        // Pass through short output unchanged.
        if (total < 10)
        {
            return FilterResult.New(output);
        }

        var uploaded = 0;
        var downloaded = 0;
        var deleted = 0;
        var copied = 0;
        var moved = 0;
        var errors = new List<string>();

        foreach (var line in lines)
        {
            var m = S3TransferRegex.Match(line);
            if (m.Success)
            {
                switch (m.Groups[1].Value)
                {
                    case "upload": uploaded++; break;
                    case "download": downloaded++; break;
                    case "delete": deleted++; break;
                    case "copy": copied++; break;
                    case "move": moved++; break;
                }
            }
            else if (line.Contains("error", StringComparison.Ordinal) || line.Contains("failed", StringComparison.Ordinal))
            {
                errors.Add(line);
            }
        }

        var summaryParts = new List<string>();
        if (uploaded > 0)
        {
            summaryParts.Add($"{uploaded} uploaded");
        }

        if (downloaded > 0)
        {
            summaryParts.Add($"{downloaded} downloaded");
        }

        if (deleted > 0)
        {
            summaryParts.Add($"{deleted} deleted");
        }

        if (copied > 0)
        {
            summaryParts.Add($"{copied} copied");
        }

        if (moved > 0)
        {
            summaryParts.Add($"{moved} moved");
        }

        var resultLines = new List<string>();

        if (summaryParts.Count > 0)
        {
            resultLines.Add($"S3 transfer: {string.Join(", ", summaryParts)}, {errors.Count} errors");
        }

        foreach (var error in errors.Take(10))
        {
            resultLines.Add(error);
        }

        if (resultLines.Count == 0)
        {
            return FilterResult.New(output);
        }

        return FilterResult.New(string.Join('\n', resultLines));
    }

    /// <summary>Faithful port of <c>filter_secrets_get</c> (<c>aws_cmd.rs</c>:1515-1542).</summary>
    public static FilterResult? FilterSecretsGet(string jsonStr)
    {
        if (!TryParse(jsonStr, out var doc))
        {
            return null;
        }

        using (doc)
        {
            var v = doc.RootElement;
            var lines = new List<string>();

            if (TryGetProp(v, "Name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                lines.Add($"Name: {nameEl.GetString()}");
            }

            if (TryGetProp(v, "SecretString", out var secretEl) && secretEl.ValueKind == JsonValueKind.String)
            {
                var secretStr = secretEl.GetString()!;
                if (TryParse(secretStr, out var secretDoc))
                {
                    using (secretDoc)
                    {
                        lines.Add($"Secret: {CompactSerialize(secretDoc.RootElement)}");
                    }
                }
                else
                {
                    lines.Add($"Secret: {secretStr}");
                }
            }

            if (lines.Count == 0)
            {
                return null;
            }

            return FilterResult.New(string.Join('\n', lines));
        }
    }

    // ===================== Generic fallback: values-preserving JSON compaction =====================

    /// <summary>
    /// Parse a JSON string and return compact representation with values preserved. Faithful port
    /// of <c>json_cmd::filter_json_compact</c> (<c>src/cmds/system/json_cmd.rs</c>:91-178), used by
    /// <c>run_generic</c> for any AWS subcommand without a specialized filter. Delegates to the
    /// shared <see cref="RtkSharp.Core.JsonCompaction.Compact"/> (extracted so <c>rtk az</c>'s
    /// generic fallback shares the identical algorithm) — preserves this method's pre-extraction,
    /// byte-identical output, verified by <c>AwsCommandTests.FilterJsonCompact_UnsupportedSubcommandJson_PreservesValues</c>.
    /// </summary>
    public static string FilterJsonCompact(string jsonStr, int maxDepth) =>
        RtkSharp.Core.JsonCompaction.Compact(jsonStr, maxDepth);

    // ===================== shared JSON/string helpers =====================

    private static bool TryParse(string jsonStr, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(jsonStr);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static bool TryGetProp(JsonElement el, string prop, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Mirrors serde_json's <c>json[prop].as_str().unwrap_or(dflt)</c>.</summary>
    private static string JStr(JsonElement el, string prop, string dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : dflt;

    /// <summary>Mirrors <c>json[a].as_str().or_else(|| json[b].as_str()).unwrap_or(dflt)</c>.</summary>
    private static string JStrOr(JsonElement el, string propA, string propB, string dflt)
    {
        if (TryGetProp(el, propA, out var a) && a.ValueKind == JsonValueKind.String)
        {
            return a.GetString()!;
        }

        if (TryGetProp(el, propB, out var b) && b.ValueKind == JsonValueKind.String)
        {
            return b.GetString()!;
        }

        return dflt;
    }

    /// <summary>Mirrors <c>json[prop][inner].as_str().unwrap_or(dflt)</c>.</summary>
    private static string JNestedStr(JsonElement el, string prop, string inner, string dflt) =>
        TryGetProp(el, prop, out var v) ? JStr(v, inner, dflt) : dflt;

    /// <summary>Mirrors <c>json[prop].as_i64().unwrap_or(dflt)</c>.</summary>
    private static long JInt(JsonElement el, string prop, long dflt) =>
        TryGetProp(el, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : dflt;

    /// <summary>Mirrors <c>json[prop][inner].as_i64().unwrap_or(dflt)</c>.</summary>
    private static long JNestedInt(JsonElement el, string prop, string inner, long dflt) =>
        TryGetProp(el, prop, out var v) ? JInt(v, inner, dflt) : dflt;

    /// <summary>Faithful port of <c>join_with_overflow</c> (<c>core/utils.rs</c>:147-153).</summary>
    private static string JoinWithOverflow(List<string> items, int total, int max, string label)
    {
        var text = string.Join('\n', items);
        if (total > max)
        {
            text += $"\n… +{total - max} more {label}";
        }

        return text;
    }

    /// <summary>Faithful port of <c>truncate_iso_date</c> (<c>core/utils.rs</c>:164-170).</summary>
    private static string TruncateIsoDate(string date) => date.Length >= 10 ? date[..10] : date;

    /// <summary>Faithful port of <c>shorten_arn</c> (<c>core/utils.rs</c>:368-378).</summary>
    private static string ShortenArn(string arn)
    {
        var slashIdx = arn.LastIndexOf('/');
        var slashResult = slashIdx >= 0 ? arn[(slashIdx + 1)..] : arn;
        if (slashResult == arn)
        {
            var colonIdx = arn.LastIndexOf(':');
            return colonIdx >= 0 ? arn[(colonIdx + 1)..] : arn;
        }

        return slashResult;
    }

    /// <summary>Faithful port of <c>human_bytes</c> (<c>core/utils.rs</c>:382-399).</summary>
    private static string HumanBytes(ulong bytes)
    {
        const ulong Kb = 1024;
        const ulong Mb = Kb * 1024;
        const ulong Gb = Mb * 1024;
        const ulong Tb = Gb * 1024;

        if (bytes >= Tb)
        {
            return $"{(double)bytes / Tb:F1} TB";
        }

        if (bytes >= Gb)
        {
            return $"{(double)bytes / Gb:F1} GB";
        }

        if (bytes >= Mb)
        {
            return $"{(double)bytes / Mb:F1} MB";
        }

        if (bytes >= Kb)
        {
            return $"{(double)bytes / Kb:F1} KB";
        }

        return $"{bytes} B";
    }

    /// <summary>Faithful port of <c>utils::truncate</c> (<c>core/utils.rs</c>:25-35).</summary>
    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen)
        {
            return s;
        }

        if (maxLen < 3)
        {
            return "...";
        }

        return s[..(maxLen - 3)] + "...";
    }

    /// <summary>
    /// Formats a double the way Rust's <c>{}</c> Display formats an <c>f64</c>: whole values print
    /// without a trailing <c>.0</c> (e.g. <c>1.0</c> -&gt; <c>"1"</c>), fractional values print their
    /// shortest round-trippable form (e.g. <c>2.5</c> -&gt; <c>"2.5"</c>).
    /// </summary>
    private static string FormatNum(double d)
    {
        if (!double.IsInfinity(d) && !double.IsNaN(d) && d == Math.Floor(d) && Math.Abs(d) < 1e15)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        return d.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Compact (no-whitespace) re-serialization of a parsed <see cref="JsonElement"/>.</summary>
    private static string CompactSerialize(JsonElement el)
    {
        using var stream = new global::System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            el.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
