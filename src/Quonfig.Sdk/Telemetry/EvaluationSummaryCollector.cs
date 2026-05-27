using System;
using System.Collections;
using System.Collections.Generic;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Aggregates evaluation observations into a flush-interval summary.
///
/// <para>A single config evaluated 1000 times collapses into one counter row with
/// <c>count=1000</c>. Distinct <c>(configId, ruleIndex, weightedValueIndex, selectedValue)</c>
/// tuples produce distinct counters; counters are then grouped by <c>(configKey, configType)</c>
/// into summary rows. Mirrors sdk-java's <c>EvaluationSummaryCollector</c>.</para>
/// </summary>
public sealed class EvaluationSummaryCollector
{
    private readonly bool _enabled;
    private readonly int _maxDataSize;
    private readonly object _gate = new();
    private readonly Dictionary<SummaryKey, Dictionary<CounterKey, CounterCell>> _data = new();
    private long? _startAtMs;

    /// <summary>Initializes a new collector with a default 10,000-row cap.</summary>
    public EvaluationSummaryCollector(bool enabled) : this(enabled, 10_000) { }

    /// <summary>Initializes a new collector with the supplied cap on distinct <c>(key,type)</c> rows.</summary>
    public EvaluationSummaryCollector(bool enabled, int maxDataSize)
    {
        _enabled = enabled;
        _maxDataSize = maxDataSize;
    }

    /// <summary>True when this collector accepts pushes; false when constructed with <c>enabled=false</c>.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Records one evaluation observation. No-op when disabled or when <paramref name="stat"/> is null / has no value.</summary>
    public void Push(EvaluationStat? stat)
    {
        if (!_enabled) return;
        if (stat is null || stat.SelectedValue is null) return;
        if (string.Equals(stat.ConfigType, "LOG_LEVEL", StringComparison.OrdinalIgnoreCase)) return;

        var sk = new SummaryKey(stat.ConfigKey, stat.ConfigType);
        lock (_gate)
        {
            if (_data.Count >= _maxDataSize && !_data.ContainsKey(sk)) return;

            _startAtMs ??= NowMs();

            bool redacted = stat.ReportableValue is not null;
            string wrapper = redacted ? "string" : WrapperKeyForValue(stat.SelectedValue);
            object payload = redacted ? stat.ReportableValue! : stat.SelectedValue!;

            var ck = new CounterKey(stat.ConfigId, stat.RuleIndex, wrapper, payload, stat.WeightedValueIndex);

            if (!_data.TryGetValue(sk, out var bucket))
            {
                bucket = new Dictionary<CounterKey, CounterCell>();
                _data[sk] = bucket;
            }

            if (bucket.TryGetValue(ck, out var cell))
            {
                cell.Count++;
            }
            else
            {
                bucket[ck] = new CounterCell { Count = 1, Reason = stat.Reason };
            }
        }
    }

    /// <summary>
    /// Returns the accumulated summary envelope and resets the collector. Returns <c>null</c>
    /// when no data has been collected since the last drain.
    /// </summary>
    public IDictionary<string, object?>? Drain()
    {
        lock (_gate)
        {
            if (_data.Count == 0) return null;

            long end = NowMs();
            long start = _startAtMs ?? end;

            var summaries = new List<Dictionary<string, object?>>(_data.Count);
            foreach (var entry in _data)
            {
                var counters = new List<Dictionary<string, object?>>(entry.Value.Count);
                foreach (var ce in entry.Value)
                {
                    var counter = new Dictionary<string, object?>
                    {
                        ["configId"] = ce.Key.ConfigId,
                        ["conditionalValueIndex"] = ce.Key.RuleIndex,
                        ["configRowIndex"] = 0,
                        ["selectedValue"] = new Dictionary<string, object?> { [ce.Key.Wrapper] = ce.Key.SelectedValue },
                        ["count"] = ce.Value.Count,
                        ["reason"] = ce.Value.Reason,
                    };
                    if (ce.Key.WeightedValueIndex >= 0)
                    {
                        counter["weightedValueIndex"] = ce.Key.WeightedValueIndex;
                    }
                    counters.Add(counter);
                }

                summaries.Add(new Dictionary<string, object?>
                {
                    ["key"] = entry.Key.ConfigKey,
                    ["type"] = entry.Key.ConfigType,
                    ["counters"] = counters,
                });
            }

            var envelope = new Dictionary<string, object?>
            {
                ["start"] = start,
                ["end"] = end,
                ["summaries"] = summaries,
            };
            var ev = new Dictionary<string, object?> { ["summaries"] = envelope };

            _data.Clear();
            _startAtMs = null;
            return ev;
        }
    }

    internal static string WrapperKeyForValue(object value)
    {
        switch (value)
        {
            case bool _: return "bool";
            case sbyte _:
            case byte _:
            case short _:
            case ushort _:
            case int _:
            case uint _:
            case long _:
            case ulong _:
                return "int";
            case float _:
            case double _:
            case decimal _:
                return "double";
            case string _: return "string";
        }
        if (value is IEnumerable enumerable && value is not string) return "stringList";
        return "string";
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly struct SummaryKey : IEquatable<SummaryKey>
    {
        public SummaryKey(string configKey, string configType)
        {
            ConfigKey = configKey;
            ConfigType = configType;
        }

        public string ConfigKey { get; }
        public string ConfigType { get; }

        public bool Equals(SummaryKey other) =>
            string.Equals(ConfigKey, other.ConfigKey, StringComparison.Ordinal)
            && string.Equals(ConfigType, other.ConfigType, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SummaryKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
#if NETSTANDARD2_0
                h = (h * 31) + (ConfigKey?.GetHashCode() ?? 0);
                h = (h * 31) + (ConfigType?.GetHashCode() ?? 0);
#else
                h = (h * 31) + (ConfigKey?.GetHashCode(StringComparison.Ordinal) ?? 0);
                h = (h * 31) + (ConfigType?.GetHashCode(StringComparison.Ordinal) ?? 0);
#endif
                return h;
            }
        }
    }

    private readonly struct CounterKey : IEquatable<CounterKey>
    {
        public CounterKey(string configId, int ruleIndex, string wrapper, object selectedValue, int weightedValueIndex)
        {
            ConfigId = configId;
            RuleIndex = ruleIndex;
            Wrapper = wrapper;
            SelectedValue = selectedValue;
            WeightedValueIndex = weightedValueIndex;
        }

        public string ConfigId { get; }
        public int RuleIndex { get; }
        public string Wrapper { get; }
        public object SelectedValue { get; }
        public int WeightedValueIndex { get; }

        public bool Equals(CounterKey other) =>
            RuleIndex == other.RuleIndex
            && WeightedValueIndex == other.WeightedValueIndex
            && string.Equals(ConfigId, other.ConfigId, StringComparison.Ordinal)
            && string.Equals(Wrapper, other.Wrapper, StringComparison.Ordinal)
            && SelectedValueEquals(SelectedValue, other.SelectedValue);

        public override bool Equals(object? obj) => obj is CounterKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
#if NETSTANDARD2_0
                h = (h * 31) + (ConfigId?.GetHashCode() ?? 0);
                h = (h * 31) + RuleIndex;
                h = (h * 31) + (Wrapper?.GetHashCode() ?? 0);
#else
                h = (h * 31) + (ConfigId?.GetHashCode(StringComparison.Ordinal) ?? 0);
                h = (h * 31) + RuleIndex;
                h = (h * 31) + (Wrapper?.GetHashCode(StringComparison.Ordinal) ?? 0);
#endif
                h = (h * 31) + SelectedValueHash(SelectedValue);
                h = (h * 31) + WeightedValueIndex;
                return h;
            }
        }

        private static bool SelectedValueEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (a is IList<string> la && b is IList<string> lb)
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                {
                    if (!string.Equals(la[i], lb[i], StringComparison.Ordinal)) return false;
                }
                return true;
            }
            return a.Equals(b);
        }

        private static int SelectedValueHash(object v)
        {
            if (v is null) return 0;
            if (v is IList<string> list)
            {
                unchecked
                {
                    int h = 17;
                    foreach (var s in list)
                    {
#if NETSTANDARD2_0
                        h = (h * 31) + (s?.GetHashCode() ?? 0);
#else
                        h = (h * 31) + (s?.GetHashCode(StringComparison.Ordinal) ?? 0);
#endif
                    }
                    return h;
                }
            }
            if (v is string vs)
            {
#if NETSTANDARD2_0
                return vs.GetHashCode();
#else
                return vs.GetHashCode(StringComparison.Ordinal);
#endif
            }
            return v.GetHashCode();
        }
    }

    private sealed class CounterCell
    {
        public long Count { get; set; }
        public int Reason { get; set; }
    }
}
