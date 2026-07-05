using System.Globalization;
using System.Text;
using RtkSharp.Core;

namespace RtkSharp.Parser;

/// <summary>
/// Parse result with degradation tier. Faithful port of Rust <c>ParseResult&lt;T&gt;</c>
/// (<c>src/parser/mod.rs:16-77</c>): a three-way discriminated union describing how much of a
/// tool's raw output could be recovered as structured data.
/// </summary>
/// <remarks>
/// Modeled as a closed hierarchy of nested records rather than an <c>enum</c> with payload fields,
/// since C# has no native tagged-union type — this is the idiomatic C# shape for "sealed variant
/// set with per-variant data" and supports exhaustive <c>switch</c> pattern matching the same way
/// Rust's <c>match</c> does. Instances are created via the nested record constructors (e.g.
/// <c>new ParseResult&lt;T&gt;.Full(data)</c>), mirroring Rust's <c>ParseResult::Full(data)</c>.
/// </remarks>
/// <typeparam name="T">The structured data type produced by a successful (Full or Degraded) parse.</typeparam>
public abstract record ParseResult<T>
{
    private ParseResult()
    {
    }

    /// <summary>Tier 1: full parse with complete structured data.</summary>
    /// <param name="Data">The fully parsed structured data.</param>
    public sealed record Full(T Data) : ParseResult<T>;

    /// <summary>Tier 2: degraded parse with partial data and warnings.</summary>
    /// <param name="Data">The partially recovered structured data.</param>
    /// <param name="Warnings">Human-readable warnings describing what could not be recovered.</param>
    public sealed record Degraded(T Data, IReadOnlyList<string> Warnings) : ParseResult<T>;

    /// <summary>Tier 3: passthrough — parsing failed, raw (possibly truncated) output is returned.</summary>
    /// <param name="Raw">The raw, possibly truncated, original output.</param>
    public sealed record Passthrough(string Raw) : ParseResult<T>;

    /// <summary>
    /// The tier level: 1 = <see cref="Full"/>, 2 = <see cref="Degraded"/>, 3 = <see cref="Passthrough"/>.
    /// Faithful port of Rust <c>ParseResult::tier</c> (<c>src/parser/mod.rs:40-48</c>).
    /// </summary>
    public int Tier => this switch
    {
        Full => 1,
        Degraded => 2,
        Passthrough => 3,
        _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
    };

    /// <summary>
    /// Whether parsing succeeded (<see cref="Full"/> or <see cref="Degraded"/>). Faithful port of
    /// Rust <c>ParseResult::is_ok</c> (<c>src/parser/mod.rs:50-54</c>).
    /// </summary>
    public bool IsOk => this is not Passthrough;

    /// <summary>
    /// Returns the warnings if this is a <see cref="Degraded"/> result, otherwise an empty list.
    /// Faithful port of Rust <c>ParseResult::warnings</c> (<c>src/parser/mod.rs:69-76</c>). Named as
    /// a method rather than a property so it does not collide with <see cref="Degraded"/>'s own
    /// <c>Warnings</c> record property.
    /// </summary>
    /// <returns>The warnings list, or an empty list if this is not a <see cref="Degraded"/> result.</returns>
    public IReadOnlyList<string> GetWarnings() => this is Degraded degraded ? degraded.Warnings : [];

    /// <summary>
    /// Unwraps the parsed data, throwing on <see cref="Passthrough"/>. Faithful port of Rust
    /// <c>ParseResult::unwrap</c> (<c>src/parser/mod.rs:29-38</c>), which panics in that case.
    /// </summary>
    /// <returns>The parsed data.</returns>
    /// <exception cref="InvalidOperationException">Thrown when this is a <see cref="Passthrough"/> result.</exception>
    public T Unwrap() => this switch
    {
        Full full => full.Data,
        Degraded degraded => degraded.Data,
        Passthrough => throw new InvalidOperationException("Called Unwrap on Passthrough result"),
        _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
    };

    /// <summary>
    /// Maps the parsed data while preserving tier. Faithful port of Rust <c>ParseResult::map</c>
    /// (<c>src/parser/mod.rs:56-67</c>).
    /// </summary>
    /// <typeparam name="TOut">The mapped data type.</typeparam>
    /// <param name="map">The mapping function, applied only when this is <see cref="Full"/> or <see cref="Degraded"/>.</param>
    /// <returns>A new <see cref="ParseResult{TOut}"/> with the same tier.</returns>
    public ParseResult<TOut> Map<TOut>(Func<T, TOut> map) => this switch
    {
        Full full => new ParseResult<TOut>.Full(map(full.Data)),
        Degraded degraded => new ParseResult<TOut>.Degraded(map(degraded.Data), degraded.Warnings),
        Passthrough passthrough => new ParseResult<TOut>.Passthrough(passthrough.Raw),
        _ => throw new InvalidOperationException("Unreachable: unknown ParseResult variant."),
    };
}

/// <summary>
/// Template-method base class for the unified three-tier tool-output parser. Faithful port of
/// Rust's <c>OutputParser</c> trait (<c>src/parser/mod.rs:79-101</c>): tier 1 attempts a full parse
/// (typically JSON) into <typeparamref name="T"/>; tier 2 falls back to a degraded parse (typically
/// regex/text extraction) with warnings; tier 3 falls back to a truncated raw passthrough. This
/// three-tier system ensures RTK never returns false data silently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape choice.</b> The brief allowed either an interface-with-a-method-per-tier or a
/// template-method base class. This is the latter: <see cref="Parse"/> is the non-overridable
/// template method that sequences the three tiers exactly as Rust's default fallback order
/// prescribes, while <see cref="TryFull"/> and <see cref="TryDegraded"/> are the two overridable
/// hook points each concrete parser (pnpm's <c>outdated</c> parser, the shared vitest/jest parser,
/// the playwright parser) must implement. This keeps the tier-sequencing logic — and the
/// <see cref="ParseWithTier"/> testing/debugging helper — written exactly once, shared by all
/// three consumers, rather than duplicated per parser as it would be with a bare interface.
/// </para>
/// <para>
/// <b>Constrained to reference types.</b> <typeparamref name="T"/> is constrained to
/// <see langword="class"/> so <see cref="TryFull"/>/<see cref="TryDegraded"/> can signal "fall
/// through to the next tier" with a <see langword="null"/> return, mirroring Rust's internal
/// <c>Option</c>-based tier attempts before they're wrapped in the public <see cref="ParseResult{T}"/>.
/// All three canonical output types this task ports (<see cref="TestResult"/>,
/// <see cref="DependencyState"/>) and Tasks 3/5/6's own DTOs are reference types, so this is not a
/// practical limitation.
/// </para>
/// </remarks>
/// <typeparam name="T">The structured data type this parser produces.</typeparam>
public abstract class OutputParser<T>
    where T : class
{
    /// <summary>
    /// Tier 1: attempt a full parse (typically JSON) of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The raw tool output.</param>
    /// <returns>The fully parsed data, or <see langword="null"/> to fall through to tier 2.</returns>
    protected abstract T? TryFull(string input);

    /// <summary>
    /// Tier 2: attempt a degraded parse (typically regex/text extraction) of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The raw tool output.</param>
    /// <returns>
    /// The partially recovered data and accompanying warnings, or <see langword="null"/> to fall
    /// through to tier 3 (truncated passthrough).
    /// </returns>
    protected abstract (T Data, IReadOnlyList<string> Warnings)? TryDegraded(string input);

    /// <summary>
    /// Parses <paramref name="input"/>, trying tier 1 (<see cref="TryFull"/>), then tier 2
    /// (<see cref="TryDegraded"/>), then falling back to tier 3 (truncated passthrough via
    /// <see cref="OutputParserSupport.TruncatePassthrough"/>). Faithful port of the fallback contract
    /// documented on Rust's <c>OutputParser::parse</c> (<c>src/parser/mod.rs:83-89</c>).
    /// </summary>
    /// <param name="input">The raw tool output to parse.</param>
    /// <returns>The tiered parse result.</returns>
    public ParseResult<T> Parse(string input)
    {
        var full = TryFull(input);
        if (full is not null)
        {
            return new ParseResult<T>.Full(full);
        }

        var degraded = TryDegraded(input);
        if (degraded is { } d)
        {
            return new ParseResult<T>.Degraded(d.Data, d.Warnings);
        }

        return new ParseResult<T>.Passthrough(OutputParserSupport.TruncatePassthrough(input));
    }

    /// <summary>
    /// Parses with an explicit tier ceiling, forcing degradation to passthrough if the natural
    /// result would exceed <paramref name="maxTier"/>. Faithful port of Rust
    /// <c>OutputParser::parse_with_tier</c> (<c>src/parser/mod.rs:93-100</c>), intended for
    /// testing/debugging.
    /// </summary>
    /// <param name="input">The raw tool output to parse.</param>
    /// <param name="maxTier">The maximum tier to allow (1, 2, or 3).</param>
    /// <returns>The tiered parse result, forced to <see cref="ParseResult{T}.Passthrough"/> if it would otherwise exceed <paramref name="maxTier"/>.</returns>
    public ParseResult<T> ParseWithTier(string input, int maxTier)
    {
        var result = Parse(input);
        return result.Tier > maxTier
            ? new ParseResult<T>.Passthrough(OutputParserSupport.TruncatePassthrough(input))
            : result;
    }
}

/// <summary>
/// Free functions shared by every <see cref="OutputParser{T}"/> implementation and by callers that
/// need to truncate raw output outside the tier system. Faithful port of the free functions in
/// <c>src/parser/mod.rs</c> that sit alongside (but are not part of) the <c>OutputParser</c> trait.
/// </summary>
public static class OutputParserSupport
{
    /// <summary>Marker prefix used on truncated/degraded output, matching Rust's literal marker text.</summary>
    public const string PassthroughMarker = "[RTK:PASSTHROUGH]";

    /// <summary>Marker prefix used on degradation warnings, matching Rust's literal marker text.</summary>
    public const string DegradedMarker = "[RTK:DEGRADED]";

    /// <summary>
    /// Truncates <paramref name="output"/> using the configured passthrough character limit
    /// (<see cref="LimitsConfig.PassthroughMaxChars"/>, default 2000). Faithful port of Rust
    /// <c>truncate_passthrough</c> (<c>src/parser/mod.rs:104-107</c>), which reads
    /// <c>config::limits().passthrough_max_chars</c>.
    /// </summary>
    /// <param name="output">The raw output to truncate.</param>
    /// <returns>The (possibly) truncated output.</returns>
    public static string TruncatePassthrough(string output)
    {
        var maxChars = Config.LoadOrDefault().Limits.PassthroughMaxChars;
        return TruncateOutput(output, maxChars);
    }

    /// <summary>
    /// Truncates <paramref name="output"/> to at most <paramref name="maxChars"/> Unicode scalar
    /// values (counted the same way as <see cref="Utils.Truncate"/>), appending a
    /// <c>[RTK:PASSTHROUGH]</c> marker with the before/after character counts when truncation
    /// occurs. Faithful port of Rust <c>truncate_output</c> (<c>src/parser/mod.rs:110-123</c>), which
    /// counts and slices by <c>char</c> (a Unicode scalar value).
    /// </summary>
    /// <param name="output">The raw output to truncate.</param>
    /// <param name="maxChars">The maximum number of Unicode scalar values to keep before truncation kicks in.</param>
    /// <returns><paramref name="output"/> unchanged if within the limit; otherwise the truncated prefix plus a marker.</returns>
    public static string TruncateOutput(string output, int maxChars)
    {
        var runes = new List<Rune>();
        foreach (var rune in output.EnumerateRunes())
        {
            runes.Add(rune);
        }

        if (runes.Count <= maxChars)
        {
            return output;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < maxChars; i++)
        {
            sb.Append(runes[i].ToString());
        }

        sb.Append('\n').Append('\n')
            .Append(PassthroughMarker)
            .Append(" Output truncated (")
            .Append(runes.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" chars → ")
            .Append(maxChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars)");

        return sb.ToString();
    }

    /// <summary>
    /// Emits a degradation warning to stderr. Faithful port of Rust <c>emit_degradation_warning</c>
    /// (<c>src/parser/mod.rs:126-128</c>).
    /// </summary>
    /// <param name="tool">The tool name (e.g. <c>"vitest"</c>).</param>
    /// <param name="reason">The reason for the degradation.</param>
    public static void EmitDegradationWarning(string tool, string reason) =>
        Console.Error.Write($"{DegradedMarker} {tool} parser: {reason}\n");

    /// <summary>
    /// Emits a passthrough warning to stderr. Faithful port of Rust <c>emit_passthrough_warning</c>
    /// (<c>src/parser/mod.rs:131-133</c>).
    /// </summary>
    /// <param name="tool">The tool name (e.g. <c>"vitest"</c>).</param>
    /// <param name="reason">The reason parsing fell all the way through to passthrough.</param>
    public static void EmitPassthroughWarning(string tool, string reason) =>
        Console.Error.Write($"{PassthroughMarker} {tool} parser: {reason}\n");
}
