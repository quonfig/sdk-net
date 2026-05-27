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
    /// <see cref="OnInitFailure.ReturnDefaults"/>, once the init timeout has elapsed and the
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

    // ---- typed getters ----

    /// <summary>Returns the resolved string value of <paramref name="key"/>, or <paramref name="defaultValue"/> when missing.</summary>
    string? GetString(string key, ContextSet? contexts = null, string? defaultValue = null);

    /// <summary>Returns the resolved int value (the wire-format int type is 64-bit; this getter narrows to <see cref="int"/>).</summary>
    int? GetInt(string key, ContextSet? contexts = null, int? defaultValue = null);

    /// <summary>Returns the resolved long (64-bit int) value.</summary>
    long? GetLong(string key, ContextSet? contexts = null, long? defaultValue = null);

    /// <summary>Returns the resolved boolean value.</summary>
    bool? GetBool(string key, ContextSet? contexts = null, bool? defaultValue = null);

    /// <summary>Returns the resolved double value.</summary>
    double? GetDouble(string key, ContextSet? contexts = null, double? defaultValue = null);

    /// <summary>Returns the resolved string-list value.</summary>
    IReadOnlyList<string>? GetStringList(string key, ContextSet? contexts = null, IReadOnlyList<string>? defaultValue = null);

    /// <summary>Returns the resolved JSON value (object → <see cref="IDictionary{TKey,TValue}"/>, array → <see cref="IList{T}"/>, primitives unboxed).</summary>
    object? GetJson(string key, ContextSet? contexts = null, object? defaultValue = null);

    /// <summary>Returns the resolved duration value.</summary>
    TimeSpan? GetDuration(string key, ContextSet? contexts = null, TimeSpan? defaultValue = null);

    /// <summary>
    /// Always returns a bool — never throws. Defaults to <c>false</c> when the key is missing or
    /// evaluation produces a non-bool. Bypasses <see cref="OnNoDefault"/> entirely.
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
