using System;
using System.Collections.Generic;

namespace Quonfig.Sdk;

/// <summary>
/// Concrete <see cref="IBoundQuonfig"/>. Lightweight wrapper around a <see cref="Quonfig"/>
/// instance + a fixed <see cref="ContextSet"/>; no data copy. Per-call contexts override the
/// bound context key-by-key (same precedence as sdk-go / sdk-java).
/// </summary>
public sealed class BoundQuonfig : IBoundQuonfig
{
    private readonly Quonfig _client;
    private readonly ContextSet _bound;

    internal BoundQuonfig(Quonfig client, ContextSet bound)
    {
        _client = client;
        _bound = bound;
    }

    /// <inheritdoc/>
    public ContextSet BoundContext => _bound;

    /// <inheritdoc/>
    public IBoundQuonfig WithContext(ContextSet contexts) =>
        new BoundQuonfig(_client, Quonfig.MergeContexts(_bound, contexts) ?? new ContextSet());

    /// <inheritdoc/>
    public string? GetString(string key, string? defaultValue = null) =>
        _client.GetString(key, _bound, defaultValue);

    /// <inheritdoc/>
    public int? GetInt(string key, int? defaultValue = null) =>
        _client.GetInt(key, _bound, defaultValue);

    /// <inheritdoc/>
    public long? GetLong(string key, long? defaultValue = null) =>
        _client.GetLong(key, _bound, defaultValue);

    /// <inheritdoc/>
    public bool? GetBool(string key, bool? defaultValue = null) =>
        _client.GetBool(key, _bound, defaultValue);

    /// <inheritdoc/>
    public double? GetDouble(string key, double? defaultValue = null) =>
        _client.GetDouble(key, _bound, defaultValue);

    /// <inheritdoc/>
    public IReadOnlyList<string>? GetStringList(string key, IReadOnlyList<string>? defaultValue = null) =>
        _client.GetStringList(key, _bound, defaultValue);

    /// <inheritdoc/>
    public object? GetJson(string key, object? defaultValue = null) =>
        _client.GetJson(key, _bound, defaultValue);

    /// <inheritdoc/>
    public TimeSpan? GetDuration(string key, TimeSpan? defaultValue = null) =>
        _client.GetDuration(key, _bound, defaultValue);

    /// <inheritdoc/>
    public bool IsFeatureEnabled(string key) =>
        _client.IsFeatureEnabled(key, _bound);

    /// <inheritdoc/>
    public bool ShouldLog(string loggerPath, LogLevel desired, LogLevel? defaultLevel = null) =>
        _client.ShouldLog(loggerPath, desired, _bound, defaultLevel);

    /// <inheritdoc/>
    public LogLevel? GetLogLevel(string loggerPath) =>
        _client.GetLogLevel(loggerPath, _bound);

    /// <inheritdoc/>
    public EvaluationDetails<string?> GetStringDetails(string key, string? defaultValue = null) =>
        _client.GetStringDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<int?> GetIntDetails(string key, int? defaultValue = null) =>
        _client.GetIntDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<long?> GetLongDetails(string key, long? defaultValue = null) =>
        _client.GetLongDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<bool?> GetBoolDetails(string key, bool? defaultValue = null) =>
        _client.GetBoolDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<double?> GetDoubleDetails(string key, double? defaultValue = null) =>
        _client.GetDoubleDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<IReadOnlyList<string>?> GetStringListDetails(string key, IReadOnlyList<string>? defaultValue = null) =>
        _client.GetStringListDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<object?> GetJsonDetails(string key, object? defaultValue = null) =>
        _client.GetJsonDetails(key, _bound, defaultValue);

    /// <inheritdoc/>
    public EvaluationDetails<TimeSpan?> GetDurationDetails(string key, TimeSpan? defaultValue = null) =>
        _client.GetDurationDetails(key, _bound, defaultValue);
}
