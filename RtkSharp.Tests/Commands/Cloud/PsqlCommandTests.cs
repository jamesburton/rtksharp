using RtkSharp.Commands.Cloud;
using Xunit;

namespace RtkSharp.Tests.Commands.Cloud;

/// <summary>
/// Ports every test in <c>src/cmds/cloud/psql_cmd.rs</c>'s <c>#[cfg(test)] mod tests</c>.
/// </summary>
public class PsqlCommandTests
{
    [Fact]
    public void FilterTable_BasicTableFormat_ProducesTabSeparatedRows()
    {
        var input = " id | username    | email             | status\n----+-------------+-------------------+--------\n  1 | alice_smith  | alice@example.com | active\n  2 | bob_jones   | bob@example.com   | active\n(2 rows)\n";
        var result = PsqlCommand.FilterTable(input);
        Assert.Contains("id\tusername\temail\tstatus", result);
        Assert.Contains("alice_smith\talice@example.com", result);
        Assert.DoesNotContain("---+---", result);
        Assert.DoesNotContain("(2 rows)", result);
    }

    [Fact]
    public void FilterExpanded_BasicExpandedFormat_ProducesOneLinerRecords()
    {
        var input = "-[ RECORD 1 ]------\nid       | 1\nusername | alice_smith\nemail    | alice@example.com\n-[ RECORD 2 ]------\nid       | 2\nusername | bob_jones\nemail    | bob@example.com\n(2 rows)\n";
        var result = PsqlCommand.FilterExpanded(input);
        Assert.Contains("[1] id=1 username=alice_smith", result);
        Assert.Contains("[2] id=2 username=bob_jones", result);
        Assert.DoesNotContain("-[ RECORD", result);
        Assert.DoesNotContain("(2 rows)", result);
    }

    [Fact]
    public void IsTableFormat_DetectsSeparator()
    {
        var input = " id | name\n----+------\n  1 | foo\n(1 row)\n";
        Assert.True(PsqlCommand.IsTableFormat(input));
    }

    [Fact]
    public void IsTableFormat_RejectsPlainOutput()
    {
        Assert.False(PsqlCommand.IsTableFormat("COPY 5\n"));
        Assert.False(PsqlCommand.IsTableFormat("SET\n"));
    }

    [Fact]
    public void IsExpandedFormat_DetectsRecords()
    {
        var input = "-[ RECORD 1 ]----\nid | 1\nname | foo\n";
        Assert.True(PsqlCommand.IsExpandedFormat(input));
    }

    [Fact]
    public void IsExpandedFormat_RejectsTable()
    {
        var input = " id | name\n----+------\n  1 | foo\n";
        Assert.False(PsqlCommand.IsExpandedFormat(input));
    }

    [Fact]
    public void FilterTable_Basic()
    {
        var input = " id | name  | email\n----+-------+---------\n  1 | alice | a@b.com\n  2 | bob   | b@b.com\n(2 rows)\n";
        var result = PsqlCommand.FilterTable(input);
        Assert.Contains("id\tname\temail", result);
        Assert.Contains("1\talice\ta@b.com", result);
        Assert.Contains("2\tbob\tb@b.com", result);
        Assert.DoesNotContain("----", result);
        Assert.DoesNotContain("(2 rows)", result);
    }

    [Fact]
    public void FilterTable_Overflow_CapsAtTwentyRowsWithCount()
    {
        var lines = new List<string> { " id | val", "----+-----" };
        for (var i = 1; i <= 40; i++)
        {
            lines.Add($"  {i} | row{i}");
        }

        lines.Add("(40 rows)");
        var input = string.Join('\n', lines);

        var result = PsqlCommand.FilterTable(input);
        Assert.Contains("... +20 more rows", result);
        var resultLines = result.Split('\n');
        Assert.Equal(22, resultLines.Length); // 1 header + 20 data + 1 overflow
    }

    [Fact]
    public void FilterPsqlOutput_Empty_ReturnsEmpty()
    {
        var result = PsqlCommand.FilterPsqlOutput("");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FilterExpanded_Basic()
    {
        var input = "-[ RECORD 1 ]----\nid   | 1\nname | alice\n-[ RECORD 2 ]----\nid   | 2\nname | bob\n";
        var result = PsqlCommand.FilterExpanded(input);
        Assert.Contains("[1] id=1 name=alice", result);
        Assert.Contains("[2] id=2 name=bob", result);
    }

    [Fact]
    public void FilterExpanded_Overflow_CapsAtTwentyRecordsWithCount()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 25; i++)
        {
            lines.Add($"-[ RECORD {i} ]----");
            lines.Add($"id   | {i}");
            lines.Add($"name | user{i}");
        }

        var input = string.Join('\n', lines);
        var result = PsqlCommand.FilterExpanded(input);
        Assert.Contains("... +5 more records", result);
    }

    [Fact]
    public void FilterPsqlOutput_Passthrough_ForNonTableOutput()
    {
        var input = "COPY 5\n";
        var result = PsqlCommand.FilterPsqlOutput(input);
        Assert.Equal("COPY 5\n", result);
    }

    [Fact]
    public void FilterPsqlOutput_RoutesToTable()
    {
        var input = " id | name\n----+------\n  1 | foo\n(1 row)\n";
        var result = PsqlCommand.FilterPsqlOutput(input);
        Assert.Contains("id\tname", result);
        Assert.DoesNotContain("----", result);
    }

    [Fact]
    public void FilterPsqlOutput_RoutesToExpanded()
    {
        var input = "-[ RECORD 1 ]----\nid | 1\nname | foo\n";
        var result = PsqlCommand.FilterPsqlOutput(input);
        Assert.Contains("[1]", result);
        Assert.Contains("id=1", result);
    }

    [Fact]
    public void FilterTable_StripsRowCount()
    {
        var input = " c\n---\n 1\n(1 row)\n";
        var result = PsqlCommand.FilterTable(input);
        Assert.DoesNotContain("(1 row)", result);
    }

    [Fact]
    public void FilterExpanded_StripsRowCount()
    {
        var input = "-[ RECORD 1 ]----\nid | 1\n(1 row)\n";
        var result = PsqlCommand.FilterExpanded(input);
        Assert.DoesNotContain("(1 row)", result);
    }

    private static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void FilterTable_TokenSavings_MeetsFortyPercentTarget()
    {
        var input = " id | username          | email                          | status    | created_at          | updated_at          | role\n-------------+-------------------+--------------------------------+-----------+---------------------+---------------------+------------\n           1 | alice_smith       | alice@example.com              | active    | 2024-01-01 09:00:00 | 2024-01-15 14:30:00 | admin\n           2 | bob_jones         | bob.jones@company.org          | active    | 2024-01-02 10:15:00 | 2024-01-16 09:00:00 | user\n           3 | carol_white       | carol.white@example.com        | inactive  | 2024-01-03 11:30:00 | 2024-01-17 11:00:00 | user\n           4 | dave_brown        | dave@business.net              | active    | 2024-01-04 08:45:00 | 2024-01-18 16:00:00 | moderator\n           5 | eve_davis         | eve.davis@example.com          | active    | 2024-01-05 13:00:00 | 2024-01-19 10:30:00 | user\n(5 rows)\n";
        var result = PsqlCommand.FilterTable(input);
        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(result);
        var savings = 100.0 - (double)outputTokens / inputTokens * 100.0;
        Assert.True(savings >= 40.0, $"Table filter: expected >=40% savings, got {savings:F1}%");
    }

    [Fact]
    public void FilterExpanded_TokenSavings_MeetsSixtyPercentTarget()
    {
        var input = "-[ RECORD 1 ]-------------------------------\nid            | 1\nusername      | alice_smith\nemail         | alice@example.com\nstatus        | active\nrole          | admin\ncreated_at    | 2024-01-01 09:00:00\nupdated_at    | 2024-01-15 14:30:00\nlast_login    | 2024-02-01 08:00:00\nlogin_count   | 42\npreferences   | {\"theme\":\"dark\",\"notifications\":true}\n-[ RECORD 2 ]-------------------------------\nid            | 2\nusername      | bob_jones\nemail         | bob.jones@company.org\nstatus        | active\nrole          | user\ncreated_at    | 2024-01-02 10:15:00\nupdated_at    | 2024-01-16 09:00:00\nlast_login    | 2024-02-02 09:30:00\nlogin_count   | 17\npreferences   | {\"theme\":\"light\",\"notifications\":false}\n(2 rows)\n";
        var result = PsqlCommand.FilterExpanded(input);
        var inputTokens = CountTokens(input);
        var outputTokens = CountTokens(result);
        var savings = 100.0 - (double)outputTokens / inputTokens * 100.0;
        Assert.True(savings >= 60.0, $"Expanded filter: expected >=60% savings, got {savings:F1}%");
    }
}
