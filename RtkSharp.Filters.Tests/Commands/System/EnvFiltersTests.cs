using System;
using RtkSharp.Filters.Commands.System;
using Xunit;

namespace RtkSharp.Filters.Tests.Commands.System;

/// <summary>
/// Tests for <see cref="EnvFilters"/>: the value-masking and key-categorization helpers behind
/// <c>rtk env</c>. Moved from <c>RtkSharp.Tests.Commands.System.EnvCommandTests</c> when
/// <c>MaskValue</c>/<c>IsLangVar</c>/<c>IsCloudVar</c>/<c>IsToolVar</c>/<c>IsInterestingVar</c>/
/// <c>GetSensitivePatterns</c> moved from <c>RtkSharp.Commands.System.EnvCommand</c> to
/// <see cref="EnvFilters"/> (Task 14 of the filters-library extraction). Faithful-port target:
/// Rust <c>src/cmds/system/env_cmd.rs</c> (lines 139-295). Argument parsing (<c>ParseArgs</c>)
/// and the full <c>Run</c> flow (now delegating to <see cref="EnvFilters.FormatEnvReport"/>) are
/// not pure and remain in <c>EnvCommand</c> alongside its own tests.
/// </summary>
public sealed class EnvFiltersTests
{
    // -----------------------------------------------------------------------
    // MaskValue - ported from env_cmd.rs's test_mask_value_* (lines 219-243)
    // -----------------------------------------------------------------------

    [Fact]
    public void MaskValue_ShortValue_ReturnsAllAsterisks()
    {
        Assert.Equal("****", EnvFilters.MaskValue("abc"));
        Assert.Equal("****", EnvFilters.MaskValue(""));
    }

    [Fact]
    public void MaskValue_LongValue_PreservesPrefixAndSuffix()
    {
        var result = EnvFilters.MaskValue("supersecrettoken");
        Assert.Contains("****", result, StringComparison.Ordinal);
        Assert.StartsWith("su", result, StringComparison.Ordinal);
        Assert.EndsWith("en", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskValue_ExactlyFourChars_ReturnsAllAsterisks()
    {
        Assert.Equal("****", EnvFilters.MaskValue("abcd"));
    }

    [Fact]
    public void MaskValue_FiveChars_PreservesPrefixAndSuffix()
    {
        var result = EnvFilters.MaskValue("abcde");
        Assert.StartsWith("ab", result, StringComparison.Ordinal);
        Assert.EndsWith("de", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskValue_SurrogatePairCharacters_MaskedByUnicodeScalarValue_NotUtf16CodeUnit()
    {
        // "😀" (U+1F600) is one Rust `char` / one .NET Rune but two UTF-16 code units. A value made
        // of 5 such emoji is > 4 Rune-counted "chars", so it must mask to prefix+****+suffix using
        // whole emoji, not split a surrogate pair the way naive string.Length indexing would.
        const string value = "😀😀😀😀😀";
        var result = EnvFilters.MaskValue(value);
        Assert.Equal("😀😀****😀😀", result);
    }

    // -----------------------------------------------------------------------
    // IsLangVar / IsCloudVar / IsToolVar / IsInterestingVar - ported from env_cmd.rs (lines 245-295)
    // -----------------------------------------------------------------------

    [Fact]
    public void IsLangVar_PositiveCases()
    {
        Assert.True(EnvFilters.IsLangVar("RUST_LOG"));
        Assert.True(EnvFilters.IsLangVar("CARGO_HOME"));
        Assert.True(EnvFilters.IsLangVar("GOPATH"));
        Assert.True(EnvFilters.IsLangVar("NODE_ENV"));
    }

    [Fact]
    public void IsLangVar_NegativeCases()
    {
        Assert.False(EnvFilters.IsLangVar("HOME"));
        Assert.False(EnvFilters.IsLangVar("PATH"));
        Assert.False(EnvFilters.IsLangVar("USER"));
    }

    [Fact]
    public void IsCloudVar_PositiveCases()
    {
        Assert.True(EnvFilters.IsCloudVar("AWS_ACCESS_KEY_ID"));
        Assert.True(EnvFilters.IsCloudVar("AZURE_CLIENT_ID"));
        Assert.True(EnvFilters.IsCloudVar("DOCKER_HOST"));
        Assert.True(EnvFilters.IsCloudVar("KUBERNETES_SERVICE_HOST"));
    }

    [Fact]
    public void IsCloudVar_NegativeCases()
    {
        Assert.False(EnvFilters.IsCloudVar("HOME"));
        Assert.False(EnvFilters.IsCloudVar("RUST_LOG"));
    }

    [Fact]
    public void IsToolVar_PositiveCases()
    {
        Assert.True(EnvFilters.IsToolVar("EDITOR"));
        Assert.True(EnvFilters.IsToolVar("GIT_AUTHOR_NAME"));
        Assert.True(EnvFilters.IsToolVar("SSH_AUTH_SOCK"));
        Assert.True(EnvFilters.IsToolVar("CLAUDE_API_KEY"));
    }

    [Fact]
    public void IsInterestingVar_PositiveCases()
    {
        Assert.True(EnvFilters.IsInterestingVar("HOME"));
        Assert.True(EnvFilters.IsInterestingVar("USER"));
        Assert.True(EnvFilters.IsInterestingVar("LANG"));
        Assert.True(EnvFilters.IsInterestingVar("TZ"));
        Assert.True(EnvFilters.IsInterestingVar("PWD"));
    }

    [Fact]
    public void IsInterestingVar_NegativeCases()
    {
        Assert.False(EnvFilters.IsInterestingVar("RANDOM_VAR"));
        Assert.False(EnvFilters.IsInterestingVar("MY_CUSTOM_VAR"));
    }

    [Fact]
    public void IsInterestingVar_UsesStartsWith_NotContains_UnlikeTheOtherThreeHelpers()
    {
        // "MY_HOME_DIR" contains "HOME" as a substring but does not START with it - the deliberate
        // Contains-vs-StartsWith asymmetry documented in the phase plan and class remarks.
        Assert.False(EnvFilters.IsInterestingVar("MY_HOME_DIR"));
        Assert.True(EnvFilters.IsInterestingVar("HOME_DIR"));
    }

    [Fact]
    public void GetSensitivePatterns_ContainsExpectedKeys()
    {
        var patterns = EnvFilters.GetSensitivePatterns();
        Assert.Contains("key", patterns);
        Assert.Contains("secret", patterns);
        Assert.Contains("password", patterns);
        Assert.Contains("token", patterns);
        Assert.Contains("credential", patterns);
        Assert.Contains("auth", patterns);
        Assert.Contains("private", patterns);
        Assert.Contains("api_key", patterns);
        Assert.Contains("apikey", patterns);
        Assert.Contains("access_key", patterns);
        Assert.Contains("jwt", patterns);
        Assert.Equal(11, patterns.Count);
    }
}
