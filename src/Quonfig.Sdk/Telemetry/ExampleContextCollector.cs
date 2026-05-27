using System;
using System.Collections.Generic;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Collects sampled full-context examples (with values) for telemetry drill-down.
///
/// <para>Only enabled when <see cref="ContextUploadMode.PeriodicExample"/>. Within the rate-limit
/// window, the same grouped key (named contexts × <c>key</c>/<c>trackingId</c>) is recorded only
/// once. Mirrors sdk-java's <c>ExampleContextCollector</c>.</para>
/// </summary>
public sealed class ExampleContextCollector
{
    private static readonly TimeSpan DefaultRateLimit = TimeSpan.FromHours(1);

    private readonly bool _enabled;
    private readonly int _maxDataSize;
    private readonly long _rateLimitMs;
    private readonly object _gate = new();
    private readonly List<long> _timestamps = new();
    private readonly List<ContextSet> _data = new();
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);

    /// <summary>Initializes a collector with the default 1h rate-limit and 10,000-row cap.</summary>
    public ExampleContextCollector(ContextUploadMode mode) : this(mode, 10_000, DefaultRateLimit) { }

    /// <summary>Initializes a collector with explicit cap + rate-limit window.</summary>
    public ExampleContextCollector(ContextUploadMode mode, int maxDataSize, TimeSpan rateLimit)
    {
        _enabled = mode == ContextUploadMode.PeriodicExample;
        _maxDataSize = maxDataSize;
        _rateLimitMs = (long)rateLimit.TotalMilliseconds;
    }

    /// <summary>True iff the collector was constructed with <see cref="ContextUploadMode.PeriodicExample"/>.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Records one full context example (subject to rate-limit and size cap).</summary>
    public void Push(ContextSet? contexts)
    {
        if (!_enabled || contexts is null) return;

        string key = GroupedKey(contexts);
        if (key.Length == 0) return;

        long now = NowMs();
        lock (_gate)
        {
            if (_data.Count >= _maxDataSize) return;
            if (_seen.TryGetValue(key, out long lastSeen) && (now - lastSeen) < _rateLimitMs) return;

            _timestamps.Add(now);
            _data.Add(contexts);
            _seen[key] = now;
        }
    }

    /// <summary>
    /// Returns the accumulated example-context envelope and resets the collector. Returns
    /// <c>null</c> when nothing has been pushed since the last drain.
    /// </summary>
    public IDictionary<string, object?>? Drain()
    {
        lock (_gate)
        {
            if (_data.Count == 0) return null;

            var examples = new List<Dictionary<string, object?>>(_data.Count);
            for (int i = 0; i < _data.Count; i++)
            {
                var ctx = _data[i];
                long ts = _timestamps[i];

                var contexts = new List<Dictionary<string, object?>>(ctx.Count);
                foreach (var named in ctx)
                {
                    var values = new Dictionary<string, object?>(named.Value.Count);
                    foreach (var p in named.Value)
                    {
                        values[p.Key] = p.Value?.ToObject();
                    }
                    contexts.Add(new Dictionary<string, object?>
                    {
                        ["type"] = named.Key,
                        ["values"] = values,
                    });
                }

                examples.Add(new Dictionary<string, object?>
                {
                    ["timestamp"] = ts,
                    ["contextSet"] = new Dictionary<string, object?> { ["contexts"] = contexts },
                });
            }

            var envelope = new Dictionary<string, object?> { ["examples"] = examples };
            var ev = new Dictionary<string, object?> { ["exampleContexts"] = envelope };

            _data.Clear();
            _timestamps.Clear();
            PruneCache();
            return ev;
        }
    }

    private static string GroupedKey(ContextSet contexts)
    {
        var parts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var named in contexts)
        {
            string? id = ExtractIdentifier(named.Value);
            if (!string.IsNullOrEmpty(id)) parts.Add(id!);
        }
        return string.Join("|", parts);
    }

    private static string? ExtractIdentifier(ContextProperties props)
    {
        if (props.TryGetValue("key", out var k) && k is not null)
        {
            string s = StringifyContextValue(k);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        if (props.TryGetValue("trackingId", out var t) && t is not null)
        {
            string s = StringifyContextValue(t);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    private static string StringifyContextValue(ContextValue v)
    {
        var o = v.ToObject();
        return o?.ToString() ?? string.Empty;
    }

    private void PruneCache()
    {
        long now = NowMs();
        var stale = new List<string>();
        foreach (var entry in _seen)
        {
            if (now - entry.Value > _rateLimitMs) stale.Add(entry.Key);
        }
        foreach (var k in stale) _seen.Remove(k);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
