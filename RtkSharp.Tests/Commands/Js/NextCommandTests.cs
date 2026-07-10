using System;
using RtkSharp.Commands.Js;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="NextCommand"/>. <c>FilterNextBuild</c>/<c>ExtractTime</c> now live in
/// <see cref="RtkSharp.Filters.Commands.Js.NextFilters"/> (Task 7 of the filters-library extraction) —
/// see <c>NextFiltersTests</c> in <c>RtkSharp.Filters.Tests</c>. <c>ExecuteAsync</c>/
/// <c>RunNextSafeAsync</c> are not exercised end-to-end here: they construct their own process
/// invocation via <see cref="RtkSharp.Execution.CommandRunner"/> with no injection seam, so invoking
/// them would risk spawning a real <c>next</c>/<c>npx</c> process as a side effect of the test suite
/// (same convention <c>TscCommandTests</c> follows for its own <c>CommandRunner</c>-based route).
/// </summary>
public sealed class NextCommandTests
{
    // -----------------------------------------------------------------------
    // ToolExists - pure PathResolver-based check
    // -----------------------------------------------------------------------

    [Fact]
    public void ToolExists_UnresolvableName_ReturnsFalse() =>
        Assert.False(NextCommand.ToolExists("definitely-not-a-real-binary-xyz123"));
}
