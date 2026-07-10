using System;
using RtkSharp.Commands.Js;
using Xunit;

namespace RtkSharp.Tests.Commands.Js;

/// <summary>
/// Tests for <see cref="TscCommand"/>. <c>FilterTscOutput</c>/<c>TscErrorRegex</c> now live in
/// <see cref="RtkSharp.Filters.Commands.Js.TscFilters"/> (Task 7 of the filters-library extraction) —
/// see <c>TscFiltersTests</c> in <c>RtkSharp.Filters.Tests</c>. <c>ExecuteAsync</c>/
/// <c>RunTscSafeAsync</c> are not exercised end-to-end here: they construct their own
/// <c>ProcessExecutor</c> via <see cref="RtkSharp.Execution.CommandRunner"/> with no injection seam, so
/// invoking them would risk spawning a real <c>tsc</c>/<c>npx</c> process as a side effect of the test
/// suite (same convention <c>NpmCommandTests</c>/<c>PnpmCommandTests</c> follow for their own
/// <c>CommandRunner</c>-based routes).
/// </summary>
public sealed class TscCommandTests
{
    // -----------------------------------------------------------------------
    // ToolExists - pure PathResolver-based check
    // -----------------------------------------------------------------------

    [Fact]
    public void ToolExists_UnresolvableName_ReturnsFalse() =>
        Assert.False(TscCommand.ToolExists("definitely-not-a-real-binary-xyz123"));
}
