using System;
using System.Collections.Generic;

namespace Quonfig.Sdk;

/// <summary>
/// A <see cref="Quonfig"/> view with a fixed <see cref="ContextSet"/> baked in. Returned by
/// <see cref="IQuonfig.WithContext"/>. Mirrors sdk-java's <c>BoundQuonfig</c> and sdk-go's
/// <c>ContextBoundClient</c>: every typed getter forwards to the underlying client with the bound
/// context merged onto any per-call context (per-call wins on key collision).
/// </summary>
public interface IBoundQuonfig
{
    /// <summary>The context bag that this view applies to every evaluation.</summary>
    ContextSet BoundContext { get; }

    /// <summary>
    /// Returns a new bound view with <paramref name="contexts"/> merged onto the existing bound
    /// context (per-call wins on key collision). Lightweight wrapper — no data copy.
    /// </summary>
    IBoundQuonfig WithContext(ContextSet contexts);

    /// <summary>String-valued getter; bound context is applied.</summary>
    string? GetString(string key, string? defaultValue = null);

    /// <summary>Int-valued getter; bound context is applied.</summary>
    int? GetInt(string key, int? defaultValue = null);

    /// <summary>Long-valued getter; bound context is applied.</summary>
    long? GetLong(string key, long? defaultValue = null);

    /// <summary>Bool-valued getter; bound context is applied.</summary>
    bool? GetBool(string key, bool? defaultValue = null);

    /// <summary>Double-valued getter; bound context is applied.</summary>
    double? GetDouble(string key, double? defaultValue = null);

    /// <summary>String-list getter; bound context is applied.</summary>
    IReadOnlyList<string>? GetStringList(string key, IReadOnlyList<string>? defaultValue = null);

    /// <summary>JSON getter; bound context is applied.</summary>
    object? GetJson(string key, object? defaultValue = null);

    /// <summary>Duration getter; bound context is applied.</summary>
    TimeSpan? GetDuration(string key, TimeSpan? defaultValue = null);

    /// <summary>Boolean feature-flag getter; bound context is applied. Never throws.</summary>
    bool IsFeatureEnabled(string key);

    /// <summary>Logging filter helper; bound context is applied. Never throws.</summary>
    bool ShouldLog(string loggerPath, LogLevel desired, LogLevel? defaultLevel = null);

    /// <summary>Resolves a logger-path's configured level; bound context is applied.</summary>
    LogLevel? GetLogLevel(string loggerPath);

    /// <summary>Detail variant of <see cref="GetString"/>.</summary>
    EvaluationDetails<string?> GetStringDetails(string key, string? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetInt"/>.</summary>
    EvaluationDetails<int?> GetIntDetails(string key, int? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetLong"/>.</summary>
    EvaluationDetails<long?> GetLongDetails(string key, long? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetBool"/>.</summary>
    EvaluationDetails<bool?> GetBoolDetails(string key, bool? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetDouble"/>.</summary>
    EvaluationDetails<double?> GetDoubleDetails(string key, double? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetStringList"/>.</summary>
    EvaluationDetails<IReadOnlyList<string>?> GetStringListDetails(string key, IReadOnlyList<string>? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetJson"/>.</summary>
    EvaluationDetails<object?> GetJsonDetails(string key, object? defaultValue = null);

    /// <summary>Detail variant of <see cref="GetDuration"/>.</summary>
    EvaluationDetails<TimeSpan?> GetDurationDetails(string key, TimeSpan? defaultValue = null);
}
