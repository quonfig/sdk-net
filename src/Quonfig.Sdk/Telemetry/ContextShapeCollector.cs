using System.Collections;
using System.Collections.Generic;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Collects per-named-context property shapes (name → field-type code) for telemetry.
///
/// <para>Field-type codes match the rest of the SDK family: 1=int, 2=string, 4=double, 5=bool,
/// 10=string list / array. Disabled when <see cref="ContextUploadMode.None"/>.</para>
/// </summary>
public sealed class ContextShapeCollector
{
    internal const int FieldTypeInt = 1;
    internal const int FieldTypeString = 2;
    internal const int FieldTypeDouble = 4;
    internal const int FieldTypeBool = 5;
    internal const int FieldTypeArray = 10;

    private readonly bool _enabled;
    private readonly int _maxDataSize;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, int>> _shapes = new();

    /// <summary>Initializes a collector with a default 10,000-row cap.</summary>
    public ContextShapeCollector(ContextUploadMode mode) : this(mode, 10_000) { }

    /// <summary>Initializes a collector with the supplied cap on named-context rows.</summary>
    public ContextShapeCollector(ContextUploadMode mode, int maxDataSize)
    {
        _enabled = mode != ContextUploadMode.None;
        _maxDataSize = maxDataSize;
    }

    /// <summary>True when this collector accepts pushes (any mode other than <see cref="ContextUploadMode.None"/>).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Records the property-name shape of every named context in <paramref name="contexts"/>.</summary>
    public void Push(ContextSet? contexts)
    {
        if (!_enabled || contexts is null) return;
        lock (_gate)
        {
            foreach (var named in contexts)
            {
                string name = named.Key;
                if (!_shapes.TryGetValue(name, out var shape))
                {
                    if (_shapes.Count >= _maxDataSize) continue;
                    shape = new Dictionary<string, int>();
                    _shapes[name] = shape;
                }
                foreach (var prop in named.Value)
                {
                    if (!shape.ContainsKey(prop.Key))
                    {
                        shape[prop.Key] = FieldTypeForValue(prop.Value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns the accumulated context-shape envelope and resets the collector. Returns
    /// <c>null</c> when nothing has been pushed since the last drain.
    /// </summary>
    public IDictionary<string, object?>? Drain()
    {
        lock (_gate)
        {
            if (_shapes.Count == 0) return null;

            var list = new List<Dictionary<string, object?>>(_shapes.Count);
            foreach (var entry in _shapes)
            {
                // Copy fieldTypes so the returned envelope can't mutate our internal state and
                // vice versa.
                var fieldTypes = new Dictionary<string, object?>(entry.Value.Count);
                foreach (var p in entry.Value) fieldTypes[p.Key] = p.Value;

                list.Add(new Dictionary<string, object?>
                {
                    ["name"] = entry.Key,
                    ["fieldTypes"] = fieldTypes,
                });
            }

            var envelope = new Dictionary<string, object?> { ["shapes"] = list };
            var ev = new Dictionary<string, object?> { ["contextShapes"] = envelope };

            _shapes.Clear();
            return ev;
        }
    }

    internal static int FieldTypeForValue(ContextValue? value)
    {
        if (value is null) return FieldTypeString;
        switch (value.Type)
        {
            case "bool": return FieldTypeBool;
            case "int":
            case "long":
                return FieldTypeInt;
            case "double": return FieldTypeDouble;
            case "string_list": return FieldTypeArray;
            default: return FieldTypeString;
        }
    }

    internal static int FieldTypeForRaw(object? raw)
    {
        switch (raw)
        {
            case bool _: return FieldTypeBool;
            case sbyte _:
            case byte _:
            case short _:
            case ushort _:
            case int _:
            case uint _:
            case long _:
            case ulong _:
                return FieldTypeInt;
            case float _:
            case double _:
            case decimal _:
                return FieldTypeDouble;
            case string _: return FieldTypeString;
        }
        if (raw is IEnumerable && !(raw is string)) return FieldTypeArray;
        return FieldTypeString;
    }
}
