using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Aggregates evaluation observations into a flush-interval summary.
///
/// <para>A single config evaluated 1000 times collapses into one counter row with
/// <c>count=1000</c>. Distinct <c>(configId, ruleIndex, weightedValueIndex, selectedValue)</c>
/// tuples produce distinct counters; counters are then grouped by <c>(configKey, configType)</c>
/// into summary rows. Mirrors sdk-java's <c>EvaluationSummaryCollector</c>.</para>
///
/// <para>Non-scalar selected values (JSON objects/arrays, string lists) are grouped by their
/// canonical JSON text, not by reference, so equal values parsed fresh on every evaluation (an
/// ENV_VAR-provided json config) share one counter. The number of distinct counters per window is
/// capped (P6, as sdk-go's <c>maxKeys</c>): a new counter past the cap is dropped, existing
/// counters keep incrementing.</para>
/// </summary>
public sealed class EvaluationSummaryCollector
{
    private readonly int _maxDataSize;
    private volatile bool _enabled;
    private readonly object _gate = new();
    private readonly Dictionary<SummaryKey, Dictionary<CounterKey, CounterCell>> _data = new();
    private int _counterCount;
    private long? _startAtMs;
    private int _canonicalFailureReported;

    /// <summary>Initializes a new collector with a default 10,000-row cap.</summary>
    public EvaluationSummaryCollector(bool enabled) : this(enabled, 10_000) { }

    /// <summary>Initializes a new collector with the supplied cap on distinct counters per window.</summary>
    public EvaluationSummaryCollector(bool enabled, int maxDataSize)
    {
        _enabled = enabled;
        _maxDataSize = maxDataSize;
    }

    /// <summary>True when this collector accepts pushes; false when constructed with <c>enabled=false</c>.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// Called at most once per collector when the canonical-JSON walk of a selected value throws
    /// (qfg-goi1.2.15); the client logs it. The value is then counted by reference.
    /// </summary>
    internal Action<Exception>? OnCanonicalFailure { get; set; }

    /// <summary>Cap on distinct counters per window; existing counters keep counting at the cap.</summary>
    internal int MaxDataSize => _maxDataSize;

    /// <summary>Stops collecting and clears pending data (telemetry disabled for the process, P3).</summary>
    internal void Disable()
    {
        _enabled = false;
        lock (_gate)
        {
            _data.Clear();
            _counterCount = 0;
            _startAtMs = null;
        }
    }

    /// <summary>Records one evaluation observation. No-op when disabled or when <paramref name="stat"/> is null / has no value.</summary>
    public void Push(EvaluationStat? stat)
    {
        if (!_enabled) return;
        if (stat is null || stat.SelectedValue is null) return;
        if (string.Equals(stat.ConfigType, "LOG_LEVEL", StringComparison.OrdinalIgnoreCase)) return;

        var sk = new SummaryKey(stat.ConfigKey, stat.ConfigType);

        bool redacted = stat.ReportableValue is not null;
        string wrapper = redacted ? "string" : WrapperKeyForValue(stat.SelectedValue);
        object payload = redacted ? stat.ReportableValue! : stat.SelectedValue!;
        // Canonical text is computed outside the lock: it is per-evaluation work for JSON values.
        string? canonical = IsScalar(payload) ? null : TryCanonicalJson(payload);

        var ck = new CounterKey(stat.ConfigId, stat.RuleIndex, wrapper, canonical ?? payload, canonical is not null, stat.WeightedValueIndex);

        lock (_gate)
        {
            if (_data.TryGetValue(sk, out var bucket) && bucket.TryGetValue(ck, out var cell))
            {
                cell.Count++;
                return;
            }

            // Cap on distinct counters (P6): drop a NEW counter at the cap; existing ones keep counting.
            if (_counterCount >= _maxDataSize) return;

            _startAtMs ??= NowMs();

            if (bucket is null)
            {
                bucket = new Dictionary<CounterKey, CounterCell>();
                _data[sk] = bucket;
            }

            bucket[ck] = new CounterCell { Count = 1, Reason = stat.Reason, Payload = payload };
            _counterCount++;
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
                        ["selectedValue"] = new Dictionary<string, object?> { [ce.Key.Wrapper] = ce.Value.Payload },
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
            _counterCount = 0;
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

    private static bool IsScalar(object value) => value is string || value is not IEnumerable;

    // Canonical JSON text for a non-scalar value: object keys sorted ordinally, so equal values
    // built as fresh objects (or with a different key order) produce the same text.
    internal static string CanonicalJson(object value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value, depth: 0);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Push runs on the evaluation path, so the walk must not throw into a getter (qfg-goi1.2.15). If it
    // does (for example a caller mutates the value during the walk), report it once and return null:
    // the value is then keyed by reference, as it was before canonical grouping.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Telemetry must never throw into a getter; the failure is reported, not swallowed.")]
    private string? TryCanonicalJson(object value)
    {
        try
        {
            return CanonicalJson(value);
        }
        catch (Exception ex)
        {
            if (System.Threading.Interlocked.Exchange(ref _canonicalFailureReported, 1) == 0)
            {
                OnCanonicalFailure?.Invoke(ex);
            }
            return null;
        }
    }

    // Depth guard: a self-referencing collection must not overflow the stack.
    private const int MaxCanonicalDepth = 64;

    private static void WriteCanonical(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > MaxCanonicalDepth)
        {
            writer.WriteStringValue("...");
            return;
        }
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                writer.WriteNumberValue(d);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case JsonElement el:
                el.WriteTo(writer);
                return;
            case IDictionary dict:
                var entries = new List<KeyValuePair<string, object?>>(dict.Count);
                foreach (DictionaryEntry e in dict)
                {
                    entries.Add(new KeyValuePair<string, object?>(
                        Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? string.Empty, e.Value));
                }
                writer.WriteStartObject();
                foreach (var e in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(e.Key);
                    WriteCanonical(writer, e.Value, depth + 1);
                }
                writer.WriteEndObject();
                return;
            case IEnumerable seq:
                writer.WriteStartArray();
                foreach (var item in seq) WriteCanonical(writer, item, depth + 1);
                writer.WriteEndArray();
                return;
            default:
                // Other scalars (float, other integer widths, NaN/Infinity, unknown types): their
                // invariant text is stable across evaluations, which is all grouping needs.
                writer.WriteStringValue(value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
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
        // GroupValue is the selected value itself for scalars, or its canonical JSON text for
        // non-scalars (IsCanonical = true), so equality is by value. The one exception: a non-scalar
        // whose canonical walk threw is keyed by reference (IsCanonical = false; qfg-goi1.2.15).
        public CounterKey(string configId, int ruleIndex, string wrapper, object groupValue, bool isCanonical, int weightedValueIndex)
        {
            ConfigId = configId;
            RuleIndex = ruleIndex;
            Wrapper = wrapper;
            GroupValue = groupValue;
            IsCanonical = isCanonical;
            WeightedValueIndex = weightedValueIndex;
        }

        public string ConfigId { get; }
        public int RuleIndex { get; }
        public string Wrapper { get; }
        public object GroupValue { get; }
        public bool IsCanonical { get; }
        public int WeightedValueIndex { get; }

        public bool Equals(CounterKey other) =>
            RuleIndex == other.RuleIndex
            && WeightedValueIndex == other.WeightedValueIndex
            && IsCanonical == other.IsCanonical
            && string.Equals(ConfigId, other.ConfigId, StringComparison.Ordinal)
            && string.Equals(Wrapper, other.Wrapper, StringComparison.Ordinal)
            && GroupValueEquals(GroupValue, other.GroupValue);

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
                h = (h * 31) + GroupValueHash(GroupValue);
                h = (h * 31) + (IsCanonical ? 1 : 0);
                h = (h * 31) + WeightedValueIndex;
                return h;
            }
        }

        private static bool GroupValueEquals(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
            return a.Equals(b);
        }

        private static int GroupValueHash(object v)
        {
            if (v is null) return 0;
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
        public object Payload { get; set; } = string.Empty;
    }
}
