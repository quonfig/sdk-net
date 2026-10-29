using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk;

/// <summary>
/// Public surface of the Quonfig client. Mirrors the <see cref="Quonfig"/> concrete impl one-to-one
/// so DI containers can register the abstract type and tests can substitute a mock. The interface
/// is the same shape exposed by sdk-java's <c>Quonfig</c> (sans Java-isms) — every typed getter,
/// lifecycle method, health primitive, and <c>WithContext</c> overload.
/// </summary>
public interface IQuonfig : IAsyncDisposable
{
    /// <summary>
    /// Completes once the first envelope has been installed (or, under
    /// <see cref="OnInitFailure.Continue"/>, once the init timeout has elapsed and the
    /// background load is still pending). Idempotent.
    /// </summary>
    Task InitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops all background workers (SSE, fallback poller, telemetry, datadir watcher) and waits
    /// for them to wind down. Idempotent. Equivalent to <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Task CloseAsync();

    /// <summary>All config keys currently in the in-memory store.</summary>
    IReadOnlyList<string> Keys();

    /// <summary>Returns a bound view of this client with <paramref name="contexts"/> applied to every call.</summary>
    IBoundQuonfig WithContext(ContextSet contexts);

    /// <summary>Wall-clock time of the most recent successful envelope install. <c>null</c> before the first install.</summary>
    DateTimeOffset? LastSuccessfulRefresh { get; }

    /// <summary>Transport state. Diagnostic surface only — do NOT wire into a Kubernetes liveness probe.</summary>
    ConnectionState ConnectionState { get; }

    /// <summary>Fires when <see cref="ConnectionState"/> transitions. Subscribers must not throw.</summary>
    event Action<ConnectionState>? OnConnectionStateChange;

    /// <summary>
    /// Fires after a new envelope has been installed (initial load and every subsequent SSE /
    /// fallback / datadir-watcher refresh). Subscribers must not throw. Handlers run after the SDK
    /// releases its install lock, one at a time, in install order. Used by the
    /// <c>Quonfig.Sdk.Serilog</c> companion package to re-evaluate <c>LoggingLevelSwitch</c>
    /// values reactively on config changes.
    /// </summary>
    event Action? OnConfigChange;

    // ---- typed getters ----
    //
    // Typed-getter contract (qfg-zvyy), shared by every Quonfig SDK. A getter returns the value
    // only when it has the getter's type. Strings are never coerced: a stored "false" is not a
    // bool and "3" is not an int. The only conversions are int -> double (30 -> 30.0) and a whole
    // double within +/-(2^53-1) -> int (3.0 -> 3); 3.9 or 1e300 is a type mismatch, never
    // truncated. A wrong-type, malformed, unset-ENV_VAR or undecryptable value returns
    // defaultValue (or null) and logs one warning per key per client. With no defaultValue under
    // OnNoDefault.Throw the getter throws instead: QuonfigTypeMismatchException,
    // QuonfigCoercionException, QuonfigEnvVarNotSetException or QuonfigDecryptionException.

    /// <summary>Returns the value of a string (or log_level) config, or <paramref name="defaultValue"/> when missing or of another type.</summary>
    string? GetString(string key, ContextSet? contexts = null, string? defaultValue = null);

    /// <summary>Returns the value of an int config, or of a double config holding a whole number (the wire-format int type is 64-bit; this getter narrows to <see cref="int"/>).</summary>
    int? GetInt(string key, ContextSet? contexts = null, int? defaultValue = null);

    /// <summary>Returns the value of an int config, or of a double config holding a whole number within +/-(2^53-1). Never truncates.</summary>
    long? GetLong(string key, ContextSet? contexts = null, long? defaultValue = null);

    /// <summary>Returns the resolved boolean value.</summary>
    bool? GetBool(string key, ContextSet? contexts = null, bool? defaultValue = null);

    /// <summary>Returns the value of a double config, or of an int config converted to double.</summary>
    double? GetDouble(string key, ContextSet? contexts = null, double? defaultValue = null);

    /// <summary>Returns the resolved string-list value.</summary>
    IReadOnlyList<string>? GetStringList(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null);

    /// <summary>Returns the resolved JSON value (object → <see cref="IDictionary{TKey,TValue}"/>, array → <see cref="IList{T}"/>, primitives unboxed).</summary>
    object? GetJson(string key, ContextSet? contexts = null, object? defaultValue = null);

    /// <summary>Returns the resolved duration value.</summary>
    TimeSpan? GetDuration(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null);

    /// <summary>
    /// Always returns a bool — never throws. True only for a resolved bool <c>true</c>. Returns
    /// <c>false</c>, and logs a warning once per key, when the key is missing, no rule matches, the
    /// value is not a bool, or the value cannot be resolved (unset ENV_VAR, decryption failure).
    /// Bypasses <see cref="OnNoDefault"/> entirely.
    /// </summary>
    bool IsFeatureEnabled(string key, ContextSet? contexts = null);

    /// <summary>
    /// Returns whether a log at <paramref name="desired"/> for <paramref name="loggerPath"/> should
    /// be emitted, walking dotted parents on miss. Always returns a bool — never throws.
    /// </summary>
    bool ShouldLog(string loggerPath, LogLevel desired, ContextSet? contexts = null, LogLevel? defaultLevel = null);

    /// <summary>
    /// Returns the configured <see cref="LogLevel"/> for <paramref name="loggerPath"/> (walking dotted
    /// parents), or <c>null</c> when no rule matches.
    /// </summary>
    LogLevel? GetLogLevel(string loggerPath, ContextSet? contexts = null);

    // ---- detail variants (OpenFeature-ready) ----
    // The Get*Details getters never throw, whatever OnNoDefault is set to: a missing key or a
    // value that cannot be resolved returns details with Reason.Error, an ErrorCode and the
    // default (or null).

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetString"/>.</summary>
    EvaluationDetails<string?> GetStringDetails(string key, ContextSet? contexts = null, string? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetInt"/>.</summary>
    EvaluationDetails<int?> GetIntDetails(string key, ContextSet? contexts = null, int? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetLong"/>.</summary>
    EvaluationDetails<long?> GetLongDetails(string key, ContextSet? contexts = null, long? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetBool"/>.</summary>
    EvaluationDetails<bool?> GetBoolDetails(string key, ContextSet? contexts = null, bool? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetDouble"/>.</summary>
    EvaluationDetails<double?> GetDoubleDetails(string key, ContextSet? contexts = null, double? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetStringList"/>.</summary>
    EvaluationDetails<IReadOnlyList<string>?> GetStringListDetails(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetJson"/>.</summary>
    EvaluationDetails<object?> GetJsonDetails(string key, ContextSet? contexts = null, object? defaultValue = null);

    /// <summary>Full <see cref="EvaluationDetails{T}"/> for <see cref="GetDuration"/>.</summary>
    EvaluationDetails<TimeSpan?> GetDurationDetails(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null);
}
