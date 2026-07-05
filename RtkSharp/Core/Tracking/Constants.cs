namespace RtkSharp.Core.Tracking;

/// <summary>
/// Faithful port of the tracking-related constants in Rust's <c>src/core/constants.rs</c>. Kept as a
/// standalone static class (rather than folded into <see cref="Tracker"/>) so both the schema code
/// and any future query/telemetry code can reference the same literals without duplicating them.
/// </summary>
internal static class TrackingConstants
{
    /// <summary>
    /// The subdirectory name under the platform local-data directory that hosts the tracking
    /// database. Port of Rust <c>RTK_DATA_DIR = "rtk"</c> (<c>constants.rs:1</c>).
    /// </summary>
    public const string RtkDataDir = "rtk";

    /// <summary>
    /// The tracking database's file name. Port of Rust <c>HISTORY_DB = "history.db"</c>
    /// (<c>constants.rs:2</c>). Note: despite a stale Rust doc-comment elsewhere claiming
    /// <c>tracking.db</c>, the actual constant — and its own test at <c>tracking.rs:1565</c> — use
    /// <c>history.db</c>; this port trusts the code, not the comment.
    /// </summary>
    public const string HistoryDb = "history.db";

    /// <summary>
    /// The number of days of tracking history retained by <see cref="Tracker"/>'s cleanup pass. Port
    /// of Rust <c>DEFAULT_HISTORY_DAYS: i64 = 90</c> (<c>constants.rs:6</c>).
    /// </summary>
    /// <remarks>
    /// This is a hardcoded constant in both languages — <c>cleanup_old()</c> in Rust never reads
    /// <c>config.tracking.history_days</c> despite that field existing and being wired through TOML,
    /// which is a real, disclosed Rust quirk (not a bug) this port replicates verbatim rather than
    /// "fixing" by wiring the config value in here instead.
    /// </remarks>
    public const int DefaultHistoryDays = 90;
}
