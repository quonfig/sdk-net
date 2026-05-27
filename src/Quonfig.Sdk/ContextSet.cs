using System;
using System.Collections;
using System.Collections.Generic;

namespace Quonfig.Sdk;

/// <summary>
/// Named-context bag for evaluation. A <see cref="ContextSet"/> holds one or more named contexts
/// (e.g. <c>"user"</c>, <c>"device"</c>) and supports dotted-property lookup (e.g.
/// <c>"user.email"</c>).
///
/// <para>The magic property names <c>prefab.current-time</c>, <c>quonfig.current-time</c>, and
/// <c>reforge.current-time</c> resolve to the current wall-clock time in milliseconds since the
/// epoch — mirrors sdk-java and sdk-go.</para>
/// </summary>
public sealed class ContextSet : IDictionary<string, ContextProperties>
{
    private readonly Dictionary<string, ContextProperties> _data = new(StringComparer.Ordinal);

    /// <summary>
    /// Looks up a property by name. A name with a dot (e.g. <c>"user.email"</c>) selects the
    /// named context before the dot; a bare name selects the unnamed (<c>""</c>) context, matching
    /// sdk-go's <c>splitAtFirstDot</c>. The magic <c>*.current-time</c> properties bypass the
    /// stored data.
    /// </summary>
    public ContextLookup GetContextValue(string? propertyName)
    {
        if (propertyName is null) return ContextLookup.Absent;

        if (propertyName == "prefab.current-time"
            || propertyName == "quonfig.current-time"
            || propertyName == "reforge.current-time")
        {
            return ContextLookup.Present(new ContextValueLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        string contextName;
        string key;
#if NETSTANDARD2_0
        int dot = propertyName.IndexOf('.');
#else
        int dot = propertyName.IndexOf('.', StringComparison.Ordinal);
#endif
        if (dot < 0)
        {
            contextName = "";
            key = propertyName;
        }
        else
        {
            contextName = propertyName.Substring(0, dot);
            key = propertyName.Substring(dot + 1);
        }

        if (!_data.TryGetValue(contextName, out var nc)) return ContextLookup.Absent;
        if (!nc.TryGetValue(key, out var v)) return ContextLookup.Absent;
        return ContextLookup.Present(v);
    }

    /// <inheritdoc/>
    public ContextProperties this[string key]
    {
        get => _data[key];
        set => _data[key] = value;
    }

    /// <inheritdoc/>
    public ICollection<string> Keys => _data.Keys;

    /// <inheritdoc/>
    public ICollection<ContextProperties> Values => _data.Values;

    /// <inheritdoc/>
    public int Count => _data.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public void Add(string key, ContextProperties value) => _data.Add(key, value);

    /// <inheritdoc/>
    public void Add(KeyValuePair<string, ContextProperties> item) => _data.Add(item.Key, item.Value);

    /// <inheritdoc/>
    public void Clear() => _data.Clear();

    /// <inheritdoc/>
    public bool Contains(KeyValuePair<string, ContextProperties> item) =>
        _data.TryGetValue(item.Key, out var v) && Equals(v, item.Value);

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _data.ContainsKey(key);

    /// <inheritdoc/>
    public void CopyTo(KeyValuePair<string, ContextProperties>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<string, ContextProperties>>)_data).CopyTo(array, arrayIndex);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, ContextProperties>> GetEnumerator() => _data.GetEnumerator();

    /// <inheritdoc/>
    public bool Remove(string key) => _data.Remove(key);

    /// <inheritdoc/>
    public bool Remove(KeyValuePair<string, ContextProperties> item) =>
        Contains(item) && _data.Remove(item.Key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out ContextProperties value)
    {
        if (_data.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }
        value = null!;
        return false;
    }

    IEnumerator IEnumerable.GetEnumerator() => _data.GetEnumerator();
}

/// <summary>
/// Properties of a single named context (e.g. all the fields under <c>"user"</c>). Each value
/// is a <see cref="ContextValue"/>; implicit conversions on <see cref="ContextValue"/> let
/// callers write <c>["plan"] = "pro"</c> instead of <c>new ContextValueString("pro")</c>.
/// </summary>
public sealed class ContextProperties : IDictionary<string, ContextValue>
{
    private readonly Dictionary<string, ContextValue> _data = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ContextValue this[string key]
    {
        get => _data[key];
        set => _data[key] = value;
    }

    /// <inheritdoc/>
    public ICollection<string> Keys => _data.Keys;

    /// <inheritdoc/>
    public ICollection<ContextValue> Values => _data.Values;

    /// <inheritdoc/>
    public int Count => _data.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public void Add(string key, ContextValue value) => _data.Add(key, value);

    /// <inheritdoc/>
    public void Add(KeyValuePair<string, ContextValue> item) => _data.Add(item.Key, item.Value);

    /// <inheritdoc/>
    public void Clear() => _data.Clear();

    /// <inheritdoc/>
    public bool Contains(KeyValuePair<string, ContextValue> item) =>
        _data.TryGetValue(item.Key, out var v) && Equals(v, item.Value);

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _data.ContainsKey(key);

    /// <inheritdoc/>
    public void CopyTo(KeyValuePair<string, ContextValue>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<string, ContextValue>>)_data).CopyTo(array, arrayIndex);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, ContextValue>> GetEnumerator() => _data.GetEnumerator();

    /// <inheritdoc/>
    public bool Remove(string key) => _data.Remove(key);

    /// <inheritdoc/>
    public bool Remove(KeyValuePair<string, ContextValue> item) =>
        Contains(item) && _data.Remove(item.Key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out ContextValue value)
    {
        if (_data.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }
        value = null!;
        return false;
    }

    IEnumerator IEnumerable.GetEnumerator() => _data.GetEnumerator();
}
