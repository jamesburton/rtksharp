using System;
using System.Linq;
using System.Text.Json;
using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Covers <see cref="AwsFilters"/>, a test-for-test port of Rust <c>src/cmds/cloud/aws_cmd.rs</c>'s own
/// <c>#[cfg(test)] mod tests</c> block. Every fixture JSON string below is copied verbatim from the
/// corresponding Rust test (same field values, same field ordering).
/// </summary>
public sealed class AwsCommandTests
{
    /// <summary>Mirrors Rust's own test-local <c>count_tokens</c> helper (<c>core::utils::count_tokens</c>): <c>text.split_whitespace().count()</c>.</summary>
    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ===================== sts get-caller-identity =====================

    [Fact]
    public void FilterStsIdentity_SnapshotFields_FormatsAccountAndArn()
    {
        const string json = """
            {
                "UserId": "AIDAEXAMPLEUSERID1234",
                "Account": "123456789012",
                "Arn": "arn:aws:iam::123456789012:user/dev-user"
            }
            """;
        var result = AwsFilters.FilterStsIdentity(json)!;
        Assert.Equal("AWS: 123456789012 arn:aws:iam::123456789012:user/dev-user", result.Text);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterStsIdentity_Basic_FormatsAccountAndArn()
    {
        const string json = """
            {
                "UserId": "AIDAEXAMPLE",
                "Account": "123456789012",
                "Arn": "arn:aws:iam::123456789012:user/dev"
            }
            """;
        var result = AwsFilters.FilterStsIdentity(json)!;
        Assert.Equal("AWS: 123456789012 arn:aws:iam::123456789012:user/dev", result.Text);
    }

    [Fact]
    public void FilterStsIdentity_MissingFields_UsesQuestionMarks()
    {
        var result = AwsFilters.FilterStsIdentity("{}")!;
        Assert.Equal("AWS: ? ?", result.Text);
    }

    [Fact]
    public void FilterStsIdentity_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterStsIdentity("not json"));
    }

    [Fact]
    public void FilterStsIdentity_TokenSavings_MeetsSixtyPercent()
    {
        const string json = """
            {
                "UserId": "AIDAEXAMPLEUSERID1234",
                "Account": "123456789012",
                "Arn": "arn:aws:iam::123456789012:user/dev-user"
            }
            """;
        var result = AwsFilters.FilterStsIdentity(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 60.0, $"STS identity filter: expected >=60% savings, got {savings:F1}%");
    }

    // ===================== s3 ls =====================

    [Fact]
    public void FilterS3Ls_Basic_ContainsAllLines()
    {
        const string output = "2024-01-01 bucket1\n2024-01-02 bucket2\n2024-01-03 bucket3\n";
        var result = AwsFilters.FilterS3Ls(output);
        Assert.Contains("bucket1", result.Text, StringComparison.Ordinal);
        Assert.Contains("bucket3", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterS3Ls_Overflow_TruncatesAfterThirty()
    {
        var input = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"2024-01-01 bucket{i}"));
        var result = AwsFilters.FilterS3Ls(input);
        Assert.Contains("… +20 more items", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

    // ===================== ec2 describe-instances =====================

    [Fact]
    public void FilterEc2Instances_Snapshot_FormatsBothInstances()
    {
        const string json = """
            {"Reservations":[{"Instances":[{"InstanceId":"i-0a1b2c3d4e5f00001","InstanceType":"t3.micro","PrivateIpAddress":"10.0.1.10","PublicIpAddress":"54.1.2.3","VpcId":"vpc-123","SubnetId":"subnet-a","State":{"Code":16,"Name":"running"},"Tags":[{"Key":"Name","Value":"web-server-1"}],"BlockDeviceMappings":[],"SecurityGroups":[{"GroupId":"sg-001"}]},{"InstanceId":"i-0a1b2c3d4e5f00002","InstanceType":"t3.large","PrivateIpAddress":"10.0.2.20","VpcId":"vpc-123","SubnetId":"subnet-b","State":{"Code":80,"Name":"stopped"},"Tags":[{"Key":"Name","Value":"worker-1"}],"BlockDeviceMappings":[],"SecurityGroups":[{"GroupId":"sg-002"}]}]}]}
            """;
        var result = AwsFilters.FilterEc2Instances(json)!;
        Assert.Contains("EC2: 2 instances", result.Text, StringComparison.Ordinal);
        Assert.Contains(
            "i-0a1b2c3d4e5f00001 running t3.micro 10.0.1.10 pub:54.1.2.3 vpc:vpc-123 subnet:subnet-a sg:[sg-001] (web-server-1)",
            result.Text, StringComparison.Ordinal);
        Assert.Contains("i-0a1b2c3d4e5f00002 stopped t3.large 10.0.2.20", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterEc2Instances_Basic_FormatsBothInstances()
    {
        const string json = """
            {
                "Reservations": [{
                    "Instances": [{
                        "InstanceId": "i-abc123",
                        "State": {"Name": "running"},
                        "InstanceType": "t3.micro",
                        "PrivateIpAddress": "10.0.1.5",
                        "PublicIpAddress": "54.1.2.3",
                        "VpcId": "vpc-001",
                        "SubnetId": "subnet-001",
                        "SecurityGroups": [{"GroupId": "sg-001", "GroupName": "web"}],
                        "Tags": [{"Key": "Name", "Value": "web-server"}]
                    }, {
                        "InstanceId": "i-def456",
                        "State": {"Name": "stopped"},
                        "InstanceType": "t3.large",
                        "PrivateIpAddress": "10.0.1.6",
                        "VpcId": "vpc-001",
                        "SubnetId": "subnet-002",
                        "SecurityGroups": [{"GroupId": "sg-002", "GroupName": "worker"}],
                        "Tags": [{"Key": "Name", "Value": "worker"}]
                    }]
                }]
            }
            """;
        var result = AwsFilters.FilterEc2Instances(json)!;
        Assert.Contains("EC2: 2 instances", result.Text, StringComparison.Ordinal);
        Assert.Contains(
            "i-abc123 running t3.micro 10.0.1.5 pub:54.1.2.3 vpc:vpc-001 subnet:subnet-001 sg:[sg-001] (web-server)",
            result.Text, StringComparison.Ordinal);
        Assert.Contains("i-def456 stopped t3.large 10.0.1.6", result.Text, StringComparison.Ordinal);
        Assert.Contains("sg:[sg-002]", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEc2Instances_NoNameTag_ShowsDash()
    {
        const string json = """
            {
                "Reservations": [{
                    "Instances": [{
                        "InstanceId": "i-abc123",
                        "State": {"Name": "running"},
                        "InstanceType": "t3.micro",
                        "PrivateIpAddress": "10.0.1.5",
                        "Tags": []
                    }]
                }]
            }
            """;
        var result = AwsFilters.FilterEc2Instances(json)!;
        Assert.Contains("(-)", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEc2Instances_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterEc2Instances("not json"));
    }

    [Fact]
    public void FilterEc2Instances_Empty_ShowsZero()
    {
        var result = AwsFilters.FilterEc2Instances("""{"Reservations": []}""")!;
        Assert.Equal("EC2: 0 instances", result.Text);
    }

    [Fact]
    public void FilterEc2Instances_TokenSavings_MeetsSixtyPercent()
    {
        const string json = """
            {
                "Reservations": [{
                    "ReservationId": "r-001",
                    "OwnerId": "123456789012",
                    "Groups": [],
                    "Instances": [{
                        "InstanceId": "i-0a1b2c3d4e5f00001",
                        "ImageId": "ami-0abcdef1234567890",
                        "InstanceType": "t3.micro",
                        "KeyName": "my-key-pair",
                        "LaunchTime": "2024-01-15T10:30:00+00:00",
                        "Placement": { "AvailabilityZone": "us-east-1a", "GroupName": "", "Tenancy": "default" },
                        "PrivateDnsName": "ip-10-0-1-10.ec2.internal",
                        "PrivateIpAddress": "10.0.1.10",
                        "PublicDnsName": "ec2-54-0-0-10.compute-1.amazonaws.com",
                        "PublicIpAddress": "54.0.0.10",
                        "State": { "Code": 16, "Name": "running" },
                        "SubnetId": "subnet-0abc123def456001",
                        "VpcId": "vpc-0abc123def456001",
                        "Architecture": "x86_64",
                        "BlockDeviceMappings": [{ "DeviceName": "/dev/xvda", "Ebs": { "AttachTime": "2024-01-15T10:30:05+00:00", "DeleteOnTermination": true, "Status": "attached", "VolumeId": "vol-001" } }],
                        "EbsOptimized": false,
                        "EnaSupport": true,
                        "Hypervisor": "xen",
                        "NetworkInterfaces": [{ "NetworkInterfaceId": "eni-001", "PrivateIpAddress": "10.0.1.10", "Status": "in-use" }],
                        "RootDeviceName": "/dev/xvda",
                        "RootDeviceType": "ebs",
                        "SecurityGroups": [{ "GroupId": "sg-001", "GroupName": "web-server-sg" }],
                        "SourceDestCheck": true,
                        "Tags": [{ "Key": "Name", "Value": "web-server-1" }, { "Key": "Environment", "Value": "production" }, { "Key": "Team", "Value": "backend" }],
                        "VirtualizationType": "hvm",
                        "CpuOptions": { "CoreCount": 1, "ThreadsPerCore": 2 },
                        "MetadataOptions": { "State": "applied", "HttpTokens": "required", "HttpEndpoint": "enabled" }
                    }]
                }]
            }
            """;
        var result = AwsFilters.FilterEc2Instances(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 60.0, $"EC2 filter: expected >=60% savings, got {savings:F1}%");
    }

    // ===================== ecs list-services / describe-services =====================

    [Fact]
    public void FilterEcsListServices_ShortensArns()
    {
        const string json = """
            {
                "serviceArns": [
                    "arn:aws:ecs:us-east-1:123:service/cluster/api-service",
                    "arn:aws:ecs:us-east-1:123:service/cluster/worker-service"
                ]
            }
            """;
        var result = AwsFilters.FilterEcsListServices(json)!;
        Assert.Contains("api-service", result.Text, StringComparison.Ordinal);
        Assert.Contains("worker-service", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("arn:aws", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEcsDescribeServices_FormatsCountsAndLaunchType()
    {
        const string json = """
            {
                "services": [{
                    "serviceName": "api",
                    "status": "ACTIVE",
                    "runningCount": 3,
                    "desiredCount": 3,
                    "launchType": "FARGATE"
                }]
            }
            """;
        var result = AwsFilters.FilterEcsDescribeServices(json)!;
        Assert.Equal("api ACTIVE 3/3 (FARGATE)", result.Text);
    }

    // ===================== rds =====================

    [Fact]
    public void FilterRdsInstances_FormatsEndpointAndPort()
    {
        const string json = """
            {
                "DBInstances": [{
                    "DBInstanceIdentifier": "mydb",
                    "Engine": "postgres",
                    "EngineVersion": "15.4",
                    "DBInstanceClass": "db.t3.micro",
                    "DBInstanceStatus": "available",
                    "Endpoint": {"Address": "mydb.cluster-abc.us-east-1.rds.amazonaws.com", "Port": 5432}
                }]
            }
            """;
        var result = AwsFilters.FilterRdsInstances(json)!;
        Assert.Equal("mydb postgres 15.4 db.t3.micro available mydb.cluster-abc.us-east-1.rds.amazonaws.com:5432", result.Text);
    }

    [Fact]
    public void FilterRdsInstances_Overflow_TruncatesAfterTwenty()
    {
        var dbs = string.Join(",", Enumerable.Range(1, 25).Select(i =>
            $$"""{"DBInstanceIdentifier": "db-{{i}}", "Engine": "postgres", "EngineVersion": "15.4", "DBInstanceClass": "db.t3.micro", "DBInstanceStatus": "available"}"""));
        var json = $$"""{"DBInstances": [{{dbs}}]}""";
        var result = AwsFilters.FilterRdsInstances(json)!;
        Assert.Contains("… +5 more instances", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

    // ===================== cloudformation list-stacks / describe-stacks =====================

    [Fact]
    public void FilterCfnListStacks_PrefersLastUpdatedOverCreation()
    {
        const string json = """
            {
                "StackSummaries": [{
                    "StackName": "my-stack",
                    "StackStatus": "CREATE_COMPLETE",
                    "CreationTime": "2024-01-15T10:30:00Z"
                }, {
                    "StackName": "other-stack",
                    "StackStatus": "UPDATE_COMPLETE",
                    "LastUpdatedTime": "2024-02-20T14:00:00Z",
                    "CreationTime": "2024-01-01T00:00:00Z"
                }]
            }
            """;
        var result = AwsFilters.FilterCfnListStacks(json)!;
        Assert.Contains("my-stack CREATE_COMPLETE 2024-01-15", result.Text, StringComparison.Ordinal);
        Assert.Contains("other-stack UPDATE_COMPLETE 2024-02-20", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCfnDescribeStacks_WithOutputs_ListsKeyValuePairs()
    {
        const string json = """
            {
                "Stacks": [{
                    "StackName": "my-stack",
                    "StackStatus": "CREATE_COMPLETE",
                    "CreationTime": "2024-01-15T10:30:00Z",
                    "Outputs": [
                        {"OutputKey": "ApiUrl", "OutputValue": "https://api.example.com"},
                        {"OutputKey": "BucketName", "OutputValue": "my-bucket"}
                    ]
                }]
            }
            """;
        var result = AwsFilters.FilterCfnDescribeStacks(json)!;
        Assert.Contains("my-stack CREATE_COMPLETE 2024-01-15", result.Text, StringComparison.Ordinal);
        Assert.Contains("ApiUrl=https://api.example.com", result.Text, StringComparison.Ordinal);
        Assert.Contains("BucketName=my-bucket", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCfnDescribeStacks_NoOutputs_OmitsEqualsSign()
    {
        const string json = """
            {
                "Stacks": [{
                    "StackName": "my-stack",
                    "StackStatus": "CREATE_COMPLETE",
                    "CreationTime": "2024-01-15T10:30:00Z"
                }]
            }
            """;
        var result = AwsFilters.FilterCfnDescribeStacks(json)!;
        Assert.Contains("my-stack CREATE_COMPLETE 2024-01-15", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("=", result.Text, StringComparison.Ordinal);
    }

    // ===================== P0: CloudWatch Logs =====================

    [Fact]
    public void FilterLogsEvents_CompactsJsonMessageAndStripsPaginationTokens()
    {
        const string json = """
            {
                "events": [
                    {"timestamp": 1705312200000, "message": "INFO: Starting service\n", "ingestionTime": 1705312201000},
                    {"timestamp": 1705312260000, "message": "ERROR: Connection refused\n", "ingestionTime": 1705312261000},
                    {"timestamp": 1705312320000, "message": "{\"level\":\"warn\",\"msg\":\"retrying\"}\n", "ingestionTime": 1705312321000}
                ],
                "nextForwardToken": "f/1234567890abcdef1234567890abcdef",
                "nextBackwardToken": "b/1234567890abcdef1234567890abcdef"
            }
            """;
        var result = AwsFilters.FilterLogsEvents(json)!;
        Assert.Contains("INFO: Starting service", result.Text, StringComparison.Ordinal);
        Assert.Contains("ERROR: Connection refused", result.Text, StringComparison.Ordinal);
        Assert.Contains("retrying", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("nextForwardToken", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("f/1234567890", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterLogsEvents_Overflow_TruncatesAfterFifty()
    {
        var events = string.Join(",", Enumerable.Range(0, 60).Select(i =>
            $$"""{"timestamp": {{1705312200000L + (i * 1000)}}, "message": "line {{i}}", "ingestionTime": {{1705312200000L + (i * 1000) + 100}}}"""));
        var json = $$"""{"events": [{{events}}]}""";
        var result = AwsFilters.FilterLogsEvents(json)!;
        Assert.Contains("… +10 more events", result.Text, StringComparison.Ordinal);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void FilterLogsEvents_TokenSavings_MeetsFifteenPercent()
    {
        var events = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{"timestamp": {{1705312200000L + (i * 1000)}}, "message": "2024-01-15T10:30:{{i:D2}}Z INFO [com.example.service.Handler] Processing request id={{1000 + i}} user=admin@example.com action=GET /api/v1/items?limit=100&offset=0 duration={{50 + (i * 10)}}ms", "ingestionTime": {{1705312200000L + (i * 1000) + 100}}}"""));
        var json = $$"""{"events": [{{events}}], "nextForwardToken": "f/abcdef1234567890abcdef1234567890abcdef1234567890", "nextBackwardToken": "b/abcdef1234567890abcdef1234567890abcdef1234567890"}""";
        var result = AwsFilters.FilterLogsEvents(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 15.0, $"Logs filter: expected >=15% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterLogsEvents_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterLogsEvents("not json"));
    }

    [Fact]
    public void FilterLogsEvents_Empty_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterLogsEvents("""{"events": []}""")!;
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void FilterLogsEvents_Snapshot_ExactFormat()
    {
        const string json = """
            {
                "events": [
                    {"timestamp": 1705312200000, "message": "INFO: server started\n", "ingestionTime": 1705312201000},
                    {"timestamp": 1705312260000, "message": "ERROR: connection lost\n", "ingestionTime": 1705312261000}
                ],
                "nextForwardToken": "f/token123"
            }
            """;
        var result = AwsFilters.FilterLogsEvents(json)!;
        Assert.Equal(
            "2024-01-15 09:50:00 INFO: server started\n2024-01-15 09:51:00 ERROR: connection lost",
            result.Text);
    }

    // ===================== CloudFormation events =====================

    [Fact]
    public void FilterCfnEvents_SplitsFailedAndSuccessfulAndStripsProperties()
    {
        const string json = """
            {
                "StackEvents": [
                    {
                        "Timestamp": "2024-01-15T10:30:00Z",
                        "LogicalResourceId": "MyBucket",
                        "ResourceType": "AWS::S3::Bucket",
                        "ResourceStatus": "CREATE_FAILED",
                        "ResourceStatusReason": "Bucket already exists",
                        "ResourceProperties": "{\"BucketName\":\"my-bucket\",\"VersioningConfiguration\":{\"Status\":\"Enabled\"},\"Tags\":[{\"Key\":\"Env\",\"Value\":\"prod\"}]}"
                    },
                    {
                        "Timestamp": "2024-01-15T10:29:00Z",
                        "LogicalResourceId": "MyVpc",
                        "ResourceType": "AWS::EC2::VPC",
                        "ResourceStatus": "CREATE_COMPLETE",
                        "ResourceProperties": "{\"CidrBlock\":\"10.0.0.0/16\"}"
                    },
                    {
                        "Timestamp": "2024-01-15T10:28:00Z",
                        "LogicalResourceId": "MyStack",
                        "ResourceType": "AWS::CloudFormation::Stack",
                        "ResourceStatus": "ROLLBACK_IN_PROGRESS",
                        "ResourceStatusReason": "The following resource(s) failed to create: [MyBucket]"
                    }
                ]
            }
            """;
        var result = AwsFilters.FilterCfnEvents(json)!;
        Assert.Contains("3 events", result.Text, StringComparison.Ordinal);
        Assert.Contains("2 failed", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 successful", result.Text, StringComparison.Ordinal);
        Assert.Contains("FAILURES", result.Text, StringComparison.Ordinal);
        Assert.Contains("MyBucket", result.Text, StringComparison.Ordinal);
        Assert.Contains("Bucket already exists", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BucketName", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CidrBlock", result.Text, StringComparison.Ordinal);
        Assert.Contains("S3::Bucket", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AWS::S3", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCfnEvents_TokenSavings_MeetsFortyPercent()
    {
        const string json = """
            {
                "StackEvents": [
                    {"Timestamp": "2024-01-15T10:30:00Z", "LogicalResourceId": "Res1", "ResourceType": "AWS::Lambda::Function", "ResourceStatus": "CREATE_FAILED", "ResourceStatusReason": "Error", "ResourceProperties": "{\"FunctionName\":\"my-fn\",\"Runtime\":\"python3.12\",\"Handler\":\"index.handler\",\"MemorySize\":512,\"Timeout\":30,\"Role\":\"arn:aws:iam::123:role/my-role\",\"Environment\":{\"Variables\":{\"TABLE\":\"my-table\"}}}"},
                    {"Timestamp": "2024-01-15T10:29:00Z", "LogicalResourceId": "Res2", "ResourceType": "AWS::EC2::VPC", "ResourceStatus": "CREATE_COMPLETE", "ResourceProperties": "{\"CidrBlock\":\"10.0.0.0/16\",\"EnableDnsSupport\":true,\"EnableDnsHostnames\":true}"},
                    {"Timestamp": "2024-01-15T10:28:00Z", "LogicalResourceId": "Res3", "ResourceType": "AWS::S3::Bucket", "ResourceStatus": "CREATE_COMPLETE", "ResourceProperties": "{\"BucketName\":\"my-bucket\",\"VersioningConfiguration\":{\"Status\":\"Enabled\"}}"}
                ]
            }
            """;
        var result = AwsFilters.FilterCfnEvents(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 40.0, $"CFN events filter: expected >=40% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterCfnEvents_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterCfnEvents("not json"));
    }

    [Fact]
    public void FilterCfnEvents_Empty_ShowsZeroCounts()
    {
        var result = AwsFilters.FilterCfnEvents("""{"StackEvents": []}""")!;
        Assert.Equal("CloudFormation: 0 events (0 failed, 0 successful)", result.Text);
    }

    [Fact]
    public void FilterCfnEvents_FailureCountExceedsMaxItems_ReportsRealCount()
    {
        var events = string.Join(",", Enumerable.Range(0, 30).Select(i =>
            $$"""{"Timestamp": "2024-01-15T10:30:00Z", "LogicalResourceId": "Res{{i}}", "ResourceType": "AWS::Lambda::Function", "ResourceStatus": "CREATE_FAILED", "ResourceStatusReason": "Error {{i}}", "ResourceProperties": "{}"}"""));
        var json = $$"""{"StackEvents": [{{events}}]}""";
        var result = AwsFilters.FilterCfnEvents(json)!;
        Assert.Contains("30 failed", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterCfnEvents_Snapshot_ExactHeaderAndFailureLine()
    {
        const string json = """
            {"StackEvents": [
                {"Timestamp": "2024-01-15T10:30:00Z", "LogicalResourceId": "Bucket", "ResourceType": "AWS::S3::Bucket", "ResourceStatus": "CREATE_FAILED", "ResourceStatusReason": "Already exists"},
                {"Timestamp": "2024-01-15T10:29:00Z", "LogicalResourceId": "VPC", "ResourceType": "AWS::EC2::VPC", "ResourceStatus": "CREATE_COMPLETE"}
            ]}
            """;
        var result = AwsFilters.FilterCfnEvents(json)!;
        Assert.StartsWith("CloudFormation: 2 events (1 failed, 1 successful)", result.Text, StringComparison.Ordinal);
        Assert.Contains("--- FAILURES ---", result.Text, StringComparison.Ordinal);
        Assert.Contains("Bucket S3::Bucket CREATE_FAILED REASON: Already exists", result.Text, StringComparison.Ordinal);
    }

    // ===================== Lambda =====================

    [Fact]
    public void FilterLambdaList_RedactsEnvironmentSecrets()
    {
        const string json = """
            {
                "Functions": [
                    {"FunctionName": "my-api", "Runtime": "python3.12", "MemorySize": 512, "Timeout": 30, "State": "Active", "Environment": {"Variables": {"SECRET_KEY": "s3cr3t", "DB_PASSWORD": "hunter2"}}},
                    {"FunctionName": "my-worker", "Runtime": "nodejs20.x", "MemorySize": 256, "Timeout": 60, "State": "Active"}
                ]
            }
            """;
        var result = AwsFilters.FilterLambdaList(json)!;
        Assert.Contains("my-api python3.12 512MB 30s Active", result.Text, StringComparison.Ordinal);
        Assert.Contains("my-worker nodejs20.x 256MB 60s Active", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_KEY", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("DB_PASSWORD", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.Text, StringComparison.Ordinal);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void FilterLambdaList_TokenSavings_MeetsSixtyPercent()
    {
        const string json = """
            {
                "Functions": [
                    {"FunctionName": "fn-1", "FunctionArn": "arn:aws:lambda:us-east-1:123:function:fn-1", "Runtime": "python3.12", "Role": "arn:aws:iam::123:role/role-1", "Handler": "index.handler", "CodeSize": 5242880, "Description": "A function", "Timeout": 30, "MemorySize": 512, "LastModified": "2024-01-15T10:30:00.000+0000", "CodeSha256": "abc123def456", "Version": "$LATEST", "TracingConfig": {"Mode": "Active"}, "RevisionId": "rev-123", "State": "Active", "LastUpdateStatus": "Successful", "PackageType": "Zip", "Architectures": ["x86_64"], "EphemeralStorage": {"Size": 512}, "Environment": {"Variables": {"TABLE_NAME": "my-table", "API_KEY": "secret-api-key-12345"}}}
                ]
            }
            """;
        var result = AwsFilters.FilterLambdaList(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 60.0, $"Lambda list filter: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterLambdaGet_RedactsSecretsAndShortensLayerArns()
    {
        const string json = """
            {
                "Configuration": {
                    "FunctionName": "my-api",
                    "Runtime": "python3.12",
                    "Handler": "app.handler",
                    "MemorySize": 512,
                    "Timeout": 30,
                    "State": "Active",
                    "LastModified": "2024-01-15T10:30:00.000+0000",
                    "Environment": {"Variables": {"SECRET": "hunter2"}},
                    "Layers": [
                        {"Arn": "arn:aws:lambda:us-east-1:123:layer:my-layer:5"},
                        {"Arn": "arn:aws:lambda:us-east-1:123:layer:common-utils:3"}
                    ]
                },
                "Code": {"Location": "https://awslambda-us-east-1-tasks.s3.amazonaws.com/snapshots/123/my-func?versionId=abc&X-Amz-Security-Token=very-long-token"},
                "Tags": {"Team": "backend"}
            }
            """;
        var result = AwsFilters.FilterLambdaGet(json)!;
        Assert.Contains("my-api python3.12 app.handler 512MB 30s Active 2024-01-15", result.Text, StringComparison.Ordinal);
        Assert.Contains("layers: my-layer:5, common-utils:3", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("awslambda", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Amz-Security-Token", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterLambdaGet_NoLayers_OmitsLayersLine()
    {
        const string json = """
            {
                "Configuration": {
                    "FunctionName": "simple-fn",
                    "Runtime": "nodejs20.x",
                    "Handler": "index.handler",
                    "MemorySize": 128,
                    "Timeout": 10,
                    "State": "Active",
                    "LastModified": "2024-02-20T14:00:00.000+0000"
                },
                "Code": {"Location": "https://example.com/code"}
            }
            """;
        var result = AwsFilters.FilterLambdaGet(json)!;
        Assert.Contains("simple-fn", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("layers", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterLambdaList_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterLambdaList("not json"));
    }

    [Fact]
    public void FilterLambdaList_Empty_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterLambdaList("""{"Functions": []}""")!;
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void FilterLambdaList_Snapshot_ExactFormat()
    {
        const string json = """
            {"Functions": [
                {"FunctionName": "api", "Runtime": "python3.12", "MemorySize": 512, "Timeout": 30, "State": "Active"}
            ]}
            """;
        var result = AwsFilters.FilterLambdaList(json)!;
        Assert.Equal("api python3.12 512MB 30s Active", result.Text);
    }

    // ===================== P1: IAM =====================

    [Fact]
    public void FilterIamRoles_ExtractsAssumePrincipalsOnly()
    {
        const string json = """
            {
                "Roles": [
                    {"RoleName": "admin-role", "CreateDate": "2024-01-15T10:30:00Z", "Description": "Admin access", "AssumeRolePolicyDocument": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"Service\":\"lambda.amazonaws.com\"},\"Action\":\"sts:AssumeRole\"}]}"},
                    {"RoleName": "lambda-exec", "CreateDate": "2024-02-20T14:00:00Z", "AssumeRolePolicyDocument": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"Service\":\"lambda.amazonaws.com\"},\"Action\":\"sts:AssumeRole\"}]}"}
                ]
            }
            """;
        var result = AwsFilters.FilterIamRoles(json)!;
        Assert.Contains("admin-role 2024-01-15 [Admin access] assume:[lambda.amazonaws.com]", result.Text, StringComparison.Ordinal);
        Assert.Contains("lambda-exec 2024-02-20 assume:[lambda.amazonaws.com]", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Statement", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Version", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterIamRoles_TokenSavings_MeetsSixtyPercent()
    {
        const string json = """
            {
                "Roles": [
                    {"RoleName": "role-1", "RoleId": "AROA1234567890", "Arn": "arn:aws:iam::123:role/role-1", "Path": "/", "CreateDate": "2024-01-15T10:30:00Z", "MaxSessionDuration": 3600, "Description": "Test role", "AssumeRolePolicyDocument": "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"Service\":\"lambda.amazonaws.com\"},\"Action\":\"sts:AssumeRole\"}]}", "Tags": [{"Key": "Team", "Value": "backend"}]}
                ]
            }
            """;
        var result = AwsFilters.FilterIamRoles(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 60.0, $"IAM roles filter: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterIamUsers_ShowsCreationDateOnly()
    {
        const string json = """
            {
                "Users": [
                    {"UserName": "alice", "UserId": "AIDA1234", "Arn": "arn:aws:iam::123:user/alice", "Path": "/", "CreateDate": "2024-01-15T10:30:00Z"},
                    {"UserName": "bob", "UserId": "AIDA5678", "Arn": "arn:aws:iam::123:user/bob", "Path": "/", "CreateDate": "2024-02-20T14:00:00Z"}
                ]
            }
            """;
        var result = AwsFilters.FilterIamUsers(json)!;
        Assert.Contains("alice created:2024-01-15", result.Text, StringComparison.Ordinal);
        Assert.Contains("bob created:2024-02-20", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AIDA", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("arn:aws", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterIamRoles_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterIamRoles("not json"));
    }

    [Fact]
    public void FilterIamRoles_Empty_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterIamRoles("""{"Roles": []}""")!;
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void FilterIamUsers_Empty_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterIamUsers("""{"Users": []}""")!;
        Assert.Equal(string.Empty, result.Text);
    }

    // ===================== DynamoDB scan/query/get-item =====================

    [Fact]
    public void FilterDynamoDbItems_UnwrapsTypedValuesIncludingNested()
    {
        const string json = """
            {
                "Items": [
                    {"id": {"S": "user-1"}, "name": {"S": "Alice"}, "age": {"N": "30"}, "active": {"BOOL": true}},
                    {"id": {"S": "user-2"}, "name": {"S": "Bob"}, "scores": {"L": [{"N": "100"}, {"N": "95"}]}, "meta": {"M": {"role": {"S": "admin"}}}}
                ],
                "Count": 2,
                "ScannedCount": 100
            }
            """;
        var result = AwsFilters.FilterDynamoDbItems(json)!;
        Assert.Contains("Count: 2/100", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"Alice\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"Bob\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"S\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"N\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"BOOL\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"admin\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDynamoDbItems_TokenSavings_MeetsThirtyPercent()
    {
        const string json = """
            {
                "Items": [
                    {"pk": {"S": "USER#1"}, "sk": {"S": "PROFILE"}, "name": {"S": "Alice"}, "email": {"S": "alice@example.com"}, "age": {"N": "30"}, "active": {"BOOL": true}, "tags": {"SS": ["admin", "user"]}, "meta": {"M": {"role": {"S": "admin"}, "team": {"S": "backend"}}}, "scores": {"L": [{"N": "100"}, {"N": "95"}, {"N": "88"}]}},
                    {"pk": {"S": "USER#2"}, "sk": {"S": "PROFILE"}, "name": {"S": "Bob"}, "email": {"S": "bob@example.com"}, "age": {"N": "25"}, "active": {"BOOL": false}, "tags": {"SS": ["user"]}, "meta": {"M": {"role": {"S": "viewer"}, "team": {"S": "frontend"}}}, "scores": {"L": [{"N": "80"}, {"N": "75"}]}}
                ],
                "Count": 2,
                "ScannedCount": 2
            }
            """;
        var result = AwsFilters.FilterDynamoDbItems(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 30.0, $"DynamoDB filter: expected >=30% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterDynamoDbItems_NullType_UnwrapsToJsonNull()
    {
        const string json = """
            {
                "Items": [{"id": {"S": "1"}, "deleted_at": {"NULL": true}}],
                "Count": 1,
                "ScannedCount": 1
            }
            """;
        var result = AwsFilters.FilterDynamoDbItems(json)!;
        Assert.Contains("null", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("NULL", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDynamoDbItems_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterDynamoDbItems("not json"));
    }

    [Fact]
    public void FilterDynamoDbItems_WithConsumedCapacity_ShowsRcu()
    {
        const string json = """
            {
                "Items": [
                    {"id": {"N": "1"}, "name": {"S": "item1"}}
                ],
                "Count": 1,
                "ScannedCount": 1,
                "ConsumedCapacity": {
                    "CapacityUnits": 2.5
                }
            }
            """;
        var result = AwsFilters.FilterDynamoDbItems(json)!;
        Assert.Contains("Count: 1/1", result.Text, StringComparison.Ordinal);
        Assert.Contains("Capacity: 2.5 RCU", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDynamoDbItems_WithPagination_ShowsHint()
    {
        const string json = """
            {
                "Items": [
                    {"id": {"N": "1"}, "name": {"S": "item1"}}
                ],
                "Count": 1,
                "ScannedCount": 1,
                "LastEvaluatedKey": {
                    "id": {"N": "1"}
                }
            }
            """;
        var result = AwsFilters.FilterDynamoDbItems(json)!;
        Assert.Contains("Count: 1/1", result.Text, StringComparison.Ordinal);
        Assert.Contains("(paginated — more results available)", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDynamoDbItems_Empty_ShowsZeroCounts()
    {
        var result = AwsFilters.FilterDynamoDbItems("""{"Items": [], "Count": 0, "ScannedCount": 0}""")!;
        Assert.Equal("Count: 0/0", result.Text);
    }

    [Fact]
    public void FilterDynamoDbItems_Snapshot_ExactFormat()
    {
        const string json = """{"Items": [{"id": {"N": "1"}, "name": {"S": "Alice"}}], "Count": 1, "ScannedCount": 1}""";
        var result = AwsFilters.FilterDynamoDbItems(json)!;
        Assert.Equal("Count: 1/1\n{\"id\":1,\"name\":\"Alice\"}", result.Text);
    }

    [Fact]
    public void FilterDynamoDbGetItem_UnwrapsItemAndShowsCapacity()
    {
        const string json = """
            {
                "Item": {
                    "id": {"N": "123"},
                    "name": {"S": "test-item"},
                    "price": {"N": "19.99"},
                    "tags": {"L": [{"S": "new"}, {"S": "sale"}]},
                    "metadata": {"M": {"key": {"S": "value"}}}
                },
                "ConsumedCapacity": {
                    "CapacityUnits": 1.0
                }
            }
            """;
        var result = AwsFilters.FilterDynamoDbGetItem(json)!;
        Assert.Contains("\"id\":123", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"test-item\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("Capacity: 1 RCU", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDynamoDbGetItem_NoItem_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterDynamoDbGetItem("{}"));
    }

    [Fact]
    public void FilterDynamoDbGetItem_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterDynamoDbGetItem("not json"));
    }

    [Fact]
    public void UnwrapDynamoDbValue_NType_ParsesIntThenFloat()
    {
        using var intDoc = JsonDocument.Parse("""{"N": "123"}""");
        var intResult = AwsFilters.UnwrapDynamoDbValue(intDoc.RootElement, 0);
        Assert.Equal("123", intResult!.ToJsonString());

        using var floatDoc = JsonDocument.Parse("""{"N": "123.45"}""");
        var floatResult = AwsFilters.UnwrapDynamoDbValue(floatDoc.RootElement, 0);
        Assert.True(double.TryParse(floatResult!.ToJsonString(), out _));
    }

    [Fact]
    public void UnwrapDynamoDbValue_NsType_ParsesIntsAndFloats()
    {
        using var doc = JsonDocument.Parse("""{"NS": ["123", "456", "78.9"]}""");
        var result = AwsFilters.UnwrapDynamoDbValue(doc.RootElement, 0)!.AsArray();
        Assert.Equal(3, result.Count);
        Assert.Equal("123", result[0]!.ToJsonString());
        Assert.Equal("456", result[1]!.ToJsonString());
        Assert.True(double.TryParse(result[2]!.ToJsonString(), out _));
    }

    // ===================== ECS describe-tasks =====================

    [Fact]
    public void FilterEcsTasks_ShowsContainerStatusAndStopReason()
    {
        const string json = """
            {
                "tasks": [
                    {
                        "taskArn": "arn:aws:ecs:us-east-1:123:task/my-cluster/abc123def456",
                        "lastStatus": "RUNNING",
                        "desiredStatus": "RUNNING",
                        "containers": [
                            {"name": "web", "lastStatus": "RUNNING"},
                            {"name": "sidecar", "lastStatus": "RUNNING"}
                        ],
                        "attachments": [{"id": "eni-123", "type": "ElasticNetworkInterface", "status": "ATTACHED", "details": []}],
                        "overrides": {"containerOverrides": []}
                    },
                    {
                        "taskArn": "arn:aws:ecs:us-east-1:123:task/my-cluster/def789ghi012",
                        "lastStatus": "STOPPED",
                        "stoppedReason": "Essential container in task exited",
                        "containers": [
                            {"name": "worker", "lastStatus": "STOPPED", "exitCode": 1}
                        ],
                        "attachments": [],
                        "overrides": {}
                    }
                ]
            }
            """;
        var result = AwsFilters.FilterEcsTasks(json)!;
        Assert.Contains("abc123def456 RUNNING containers:[web:RUNNING, sidecar:RUNNING]", result.Text, StringComparison.Ordinal);
        Assert.Contains("def789ghi012 STOPPED containers:[worker:STOPPED(exit:1)]", result.Text, StringComparison.Ordinal);
        Assert.Contains("reason:Essential container in task exited", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ElasticNetworkInterface", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("containerOverrides", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEcsTasks_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterEcsTasks("not json"));
    }

    [Fact]
    public void FilterEcsTasks_Empty_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterEcsTasks("""{"tasks": []}""")!;
        Assert.Equal(string.Empty, result.Text);
    }

    // ===================== P2: Security Groups, S3 objects, EKS, SQS =====================

    [Fact]
    public void FilterSecurityGroups_FormatsIngressAndEgressRules()
    {
        const string json = """
            {
                "SecurityGroups": [{
                    "GroupName": "web-sg",
                    "GroupId": "sg-001",
                    "IpPermissions": [
                        {"IpProtocol": "tcp", "FromPort": 443, "ToPort": 443, "IpRanges": [{"CidrIp": "0.0.0.0/0"}], "Ipv6Ranges": [], "UserIdGroupPairs": []},
                        {"IpProtocol": "tcp", "FromPort": 22, "ToPort": 22, "IpRanges": [{"CidrIp": "10.0.0.0/8"}], "Ipv6Ranges": [], "UserIdGroupPairs": []}
                    ],
                    "IpPermissionsEgress": [
                        {"IpProtocol": "-1", "IpRanges": [{"CidrIp": "0.0.0.0/0"}], "Ipv6Ranges": [], "UserIdGroupPairs": []}
                    ]
                }]
            }
            """;
        var result = AwsFilters.FilterSecurityGroups(json)!;
        Assert.Contains("web-sg (sg-001)", result.Text, StringComparison.Ordinal);
        Assert.Contains("tcp/443<-0.0.0.0/0", result.Text, StringComparison.Ordinal);
        Assert.Contains("tcp/22<-10.0.0.0/8", result.Text, StringComparison.Ordinal);
        Assert.Contains("all<-0.0.0.0/0", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterSecurityGroups_TokenSavings_MeetsSixtyPercent()
    {
        const string json = """
            {
                "SecurityGroups": [{
                    "GroupName": "web-sg", "GroupId": "sg-001", "Description": "Web server security group", "VpcId": "vpc-001", "OwnerId": "123456789012",
                    "IpPermissions": [
                        {"IpProtocol": "tcp", "FromPort": 443, "ToPort": 443, "IpRanges": [{"CidrIp": "0.0.0.0/0", "Description": "HTTPS from anywhere"}], "Ipv6Ranges": [{"CidrIpv6": "::/0", "Description": "HTTPS IPv6"}], "PrefixListIds": [], "UserIdGroupPairs": []},
                        {"IpProtocol": "tcp", "FromPort": 80, "ToPort": 80, "IpRanges": [{"CidrIp": "0.0.0.0/0", "Description": "HTTP from anywhere"}], "Ipv6Ranges": [], "PrefixListIds": [], "UserIdGroupPairs": []}
                    ],
                    "IpPermissionsEgress": [{"IpProtocol": "-1", "IpRanges": [{"CidrIp": "0.0.0.0/0"}], "Ipv6Ranges": [{"CidrIpv6": "::/0"}], "PrefixListIds": [], "UserIdGroupPairs": []}],
                    "Tags": [{"Key": "Name", "Value": "web-sg"}, {"Key": "Environment", "Value": "production"}]
                }]
            }
            """;
        var result = AwsFilters.FilterSecurityGroups(json)!;
        var savings = 100.0 - ((double)CountTokens(result.Text) / CountTokens(json) * 100.0);
        Assert.True(savings >= 60.0, $"SG filter: expected >=60% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterS3Objects_FormatsHumanSizesAndTruncatedDates()
    {
        const string json = """
            {
                "Contents": [
                    {"Key": "data/users.csv", "Size": 5242880, "LastModified": "2024-01-15T10:30:00Z", "ETag": "\"abc123\"", "StorageClass": "STANDARD"},
                    {"Key": "logs/app.log", "Size": 1024, "LastModified": "2024-02-20T14:00:00Z", "ETag": "\"def456\"", "StorageClass": "STANDARD"}
                ]
            }
            """;
        var result = AwsFilters.FilterS3Objects(json)!;
        Assert.Contains("data/users.csv 5.0 MB 2024-01-15", result.Text, StringComparison.Ordinal);
        Assert.Contains("logs/app.log 1.0 KB 2024-02-20", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("STANDARD", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterEksCluster_OmitsCertificateAuthority()
    {
        const string json = """
            {
                "cluster": {
                    "name": "my-cluster",
                    "status": "ACTIVE",
                    "version": "1.28",
                    "endpoint": "https://ABC123.gr7.us-east-1.eks.amazonaws.com",
                    "certificateAuthority": {"data": "LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSUN5RENDQWJDZ0F3SUJBZ0lCQURBTkJna3Foa2lHOXcwQkFRc0ZBREFWTVJNd0VRWURWUVFERXdwcmRXSmwKY21...VERY_LONG_BASE64_CERT_DATA"},
                    "logging": {"clusterLogging": [{"types": ["api","audit","authenticator","controllerManager","scheduler"], "enabled": true}]},
                    "platformVersion": "eks.5"
                }
            }
            """;
        var result = AwsFilters.FilterEksCluster(json)!;
        Assert.Contains("my-cluster ACTIVE k8s/1.28 https://ABC123.gr7.us-east-1.eks.amazonaws.com", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("LS0tLS1CRUdJTi", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("VERY_LONG", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterSqsMessages_OmitsReceiptHandleAndMd5()
    {
        const string json = """
            {
                "Messages": [
                    {
                        "MessageId": "12345678-abcd-efgh-ijkl-1234567890ab",
                        "ReceiptHandle": "AQEBwJnKyrHigUMZj6rYigCgxlaS3SLy0a...VERY_LONG_RECEIPT_HANDLE_200_CHARS_OF_OPAQUE_GARBAGE_THAT_NOBODY_NEEDS",
                        "MD5OfBody": "abc123",
                        "Body": "{\"orderId\": 42, \"status\": \"pending\"}"
                    }
                ]
            }
            """;
        var result = AwsFilters.FilterSqsMessages(json)!;
        Assert.Contains("12345678", result.Text, StringComparison.Ordinal);
        Assert.Contains("orderId", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AQEBwJnK", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OPAQUE_GARBAGE", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("MD5OfBody", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterSecurityGroups_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterSecurityGroups("not json"));
    }

    [Fact]
    public void FilterS3Objects_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterS3Objects("not json"));
    }

    [Fact]
    public void FilterEksCluster_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterEksCluster("not json"));
    }

    [Fact]
    public void FilterSqsMessages_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterSqsMessages("not json"));
    }

    [Fact]
    public void FilterSecurityGroups_Empty_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterSecurityGroups("""{"SecurityGroups": []}""")!;
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void FilterS3Objects_MissingContents_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterS3Objects("{}")!;
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void FilterSqsMessages_MissingMessages_ReturnsEmptyString()
    {
        var result = AwsFilters.FilterSqsMessages("{}")!;
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void FilterSecurityGroups_Snapshot_ExactFormat()
    {
        const string json = """
            {"SecurityGroups": [{
                "GroupName": "web", "GroupId": "sg-1",
                "IpPermissions": [{"IpProtocol": "tcp", "FromPort": 443, "ToPort": 443, "IpRanges": [{"CidrIp": "0.0.0.0/0"}], "Ipv6Ranges": [], "UserIdGroupPairs": []}],
                "IpPermissionsEgress": [{"IpProtocol": "-1", "IpRanges": [{"CidrIp": "0.0.0.0/0"}], "Ipv6Ranges": [], "UserIdGroupPairs": []}]
            }]}
            """;
        var result = AwsFilters.FilterSecurityGroups(json)!;
        Assert.Equal("web (sg-1) ingress: tcp/443<-0.0.0.0/0 | egress: all<-0.0.0.0/0", result.Text);
    }

    // ===================== logs get-query-results =====================

    [Fact]
    public void FilterLogsQueryResults_StripsInternalPtrField()
    {
        const string json = """
            {
                "status": "Complete",
                "results": [
                    [
                        {"field": "@timestamp", "value": "2024-01-01 12:00:00"},
                        {"field": "@message", "value": "Error occurred"},
                        {"field": "@ptr", "value": "internal-pointer"}
                    ],
                    [
                        {"field": "@timestamp", "value": "2024-01-01 12:01:00"},
                        {"field": "@message", "value": "Another error"}
                    ]
                ]
            }
            """;
        var result = AwsFilters.FilterLogsQueryResults(json)!;
        Assert.Contains("Status: Complete", result.Text, StringComparison.Ordinal);
        Assert.Contains("@timestamp=2024-01-01 12:00:00", result.Text, StringComparison.Ordinal);
        Assert.Contains("@message=Error occurred", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@ptr", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterLogsQueryResults_EmptyResults_ShowsStatusOnly()
    {
        var result = AwsFilters.FilterLogsQueryResults("""{"status": "Complete", "results": []}""")!;
        Assert.Equal("Status: Complete", result.Text);
    }

    [Fact]
    public void FilterLogsQueryResults_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterLogsQueryResults("not json"));
    }

    // ===================== s3 sync/cp (text transfer) =====================

    [Fact]
    public void FilterS3Transfer_ShortOutput_PassesThroughUnchanged()
    {
        const string output = "upload: file1.txt to s3://bucket/file1.txt\n";
        var result = AwsFilters.FilterS3Transfer(output);
        Assert.Equal(output, result.Text);
    }

    [Fact]
    public void FilterS3Transfer_WithOperations_SummarizesCountsAndKeepsErrors()
    {
        const string output = """
            upload: file1.txt to s3://bucket/file1.txt
            upload: file2.txt to s3://bucket/file2.txt
            download: s3://bucket/file3.txt to file3.txt
            delete: s3://bucket/old.txt
            upload: file4.txt to s3://bucket/file4.txt
            upload: file5.txt to s3://bucket/file5.txt
            download: s3://bucket/file6.txt to file6.txt
            copy: s3://bucket/a.txt to s3://bucket/b.txt
            error: failed to upload file7.txt
            upload: file8.txt to s3://bucket/file8.txt
            upload: file9.txt to s3://bucket/file9.txt
            upload: file10.txt to s3://bucket/file10.txt

            """;
        var result = AwsFilters.FilterS3Transfer(output);
        Assert.Contains("7 uploaded", result.Text, StringComparison.Ordinal);
        Assert.Contains("2 downloaded", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 deleted", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 copied", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 errors", result.Text, StringComparison.Ordinal);
        Assert.Contains("error: failed to upload file7.txt", result.Text, StringComparison.Ordinal);
    }

    // ===================== secretsmanager get-secret-value =====================

    [Fact]
    public void FilterSecretsGet_JsonSecret_CompactsAndOmitsMetadata()
    {
        const string json = """
            {
                "Name": "my-secret",
                "SecretString": "{\"username\":\"admin\",\"password\":\"secret123\"}",
                "ARN": "arn:aws:secretsmanager:us-east-1:123456789012:secret:my-secret-AbCdEf",
                "VersionId": "version-uuid",
                "CreatedDate": "2024-01-01T00:00:00Z"
            }
            """;
        var result = AwsFilters.FilterSecretsGet(json)!;
        Assert.Contains("Name: my-secret", result.Text, StringComparison.Ordinal);
        Assert.Contains("""{"username":"admin","password":"secret123"}""", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ARN", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionId", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterSecretsGet_PlainTextSecret_PassesThroughAsIs()
    {
        const string json = """
            {
                "Name": "my-secret",
                "SecretString": "plain-text-password"
            }
            """;
        var result = AwsFilters.FilterSecretsGet(json)!;
        Assert.Contains("Name: my-secret", result.Text, StringComparison.Ordinal);
        Assert.Contains("Secret: plain-text-password", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterSecretsGet_InvalidJson_ReturnsNull()
    {
        Assert.Null(AwsFilters.FilterSecretsGet("not json"));
    }

    // ===================== generic fallback: values-preserving JSON compaction =====================

    // Regression: generic AWS path (unsupported subcommand returning JSON) must compress responses
    // while preserving values, not collapse them to schema type names. Mirrors the fixture at
    // tests/fixtures/aws_backup_describe_global_settings.json (aws_cmd.rs's own
    // test_aws_unsupported_subcommand_json_preserves_values, which calls
    // json_cmd::filter_json_compact directly).
    [Fact]
    public void FilterJsonCompact_UnsupportedSubcommandJson_PreservesValues()
    {
        const string fixture = """
            {
                "GlobalSettings": {
                    "isCrossAccountBackupEnabled": "false",
                    "isDelegatedAdministratorEnabled": "false",
                    "isMpaEnabled": "false"
                },
                "LastUpdateTime": "2026-05-28T09:52:17.525000+02:00"
            }
            """;

        var output = AwsFilters.FilterJsonCompact(fixture, 4);

        Assert.Contains("\"false\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain(": string", output, StringComparison.Ordinal);
        Assert.Contains("isMpaEnabled", output, StringComparison.Ordinal);
    }
}
