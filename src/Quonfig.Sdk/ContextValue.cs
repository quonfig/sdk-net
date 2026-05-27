using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk;

/// <summary>
/// Discriminated-union base for a single value placed inside a <see cref="ContextProperties"/>.
/// On the wire, every value is tagged with a <c>type</c> field plus a <c>value</c> payload,
/// matching the cross-SDK config wire shape (see <c>integration-test-data</c> per-config files).
///
/// <para>Use the implicit conversion operators (<c>ctx["plan"] = "pro"</c>) for clean call
/// sites; the converter routes serialization through the appropriate concrete sealed record.</para>
/// </summary>
[JsonConverter(typeof(ContextValueJsonConverter))]
public abstract record ContextValue
{
    /// <summary>Wire discriminator: <c>string</c>, <c>int</c>, <c>long</c>, <c>double</c>, <c>bool</c>, <c>string_list</c>.</summary>
    public abstract string Type { get; }

    /// <summary>Implicit lift of a string into a <see cref="ContextValueString"/>.</summary>
    public static implicit operator ContextValue(string s) => new ContextValueString(s);

    /// <summary>Implicit lift of an int into a <see cref="ContextValueInt"/>.</summary>
    public static implicit operator ContextValue(int i) => new ContextValueInt(i);

    /// <summary>Implicit lift of a long into a <see cref="ContextValueLong"/>.</summary>
    public static implicit operator ContextValue(long l) => new ContextValueLong(l);

    /// <summary>Implicit lift of a double into a <see cref="ContextValueDouble"/>.</summary>
    public static implicit operator ContextValue(double d) => new ContextValueDouble(d);

    /// <summary>Implicit lift of a bool into a <see cref="ContextValueBool"/>.</summary>
    public static implicit operator ContextValue(bool b) => new ContextValueBool(b);

    /// <summary>Implicit lift of a string[] into a <see cref="ContextValueStringList"/>.</summary>
    public static implicit operator ContextValue(string[] s) => new ContextValueStringList(s);

    /// <summary>Returns the unboxed CLR payload (string, int, long, double, bool, or IReadOnlyList&lt;string&gt;).</summary>
    public abstract object ToObject();
}

/// <summary>String-valued context property.</summary>
public sealed record ContextValueString(string Value) : ContextValue
{
    /// <inheritdoc/>
    public override string Type => "string";

    /// <inheritdoc/>
    public override object ToObject() => Value;
}

/// <summary>32-bit-int-valued context property.</summary>
public sealed record ContextValueInt(int Value) : ContextValue
{
    /// <inheritdoc/>
    public override string Type => "int";

    /// <inheritdoc/>
    public override object ToObject() => Value;
}

/// <summary>64-bit-int-valued context property.</summary>
public sealed record ContextValueLong(long Value) : ContextValue
{
    /// <inheritdoc/>
    public override string Type => "long";

    /// <inheritdoc/>
    public override object ToObject() => Value;
}

/// <summary>Double-valued context property.</summary>
public sealed record ContextValueDouble(double Value) : ContextValue
{
    /// <inheritdoc/>
    public override string Type => "double";

    /// <inheritdoc/>
    public override object ToObject() => Value;
}

/// <summary>Bool-valued context property.</summary>
public sealed record ContextValueBool(bool Value) : ContextValue
{
    /// <inheritdoc/>
    public override string Type => "bool";

    /// <inheritdoc/>
    public override object ToObject() => Value;
}

/// <summary>String-list-valued context property.</summary>
public sealed record ContextValueStringList : ContextValue
{
    /// <summary>The underlying ordered values. Defensive copy taken on construction.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Initializes a new instance from any string sequence.</summary>
    public ContextValueStringList(IEnumerable<string> values)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(values);
#else
        if (values is null) throw new ArgumentNullException(nameof(values));
#endif
        Values = values.ToArray();
    }

    /// <inheritdoc/>
    public override string Type => "string_list";

    /// <inheritdoc/>
    public override object ToObject() => Values;

    /// <inheritdoc/>
    public bool Equals(ContextValueStringList? other) =>
        other is not null && Values.SequenceEqual(other.Values);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            foreach (var v in Values)
            {
#if NETSTANDARD2_0
                h = (h * 31) + (v?.GetHashCode() ?? 0);
#else
                h = (h * 31) + (v?.GetHashCode(StringComparison.Ordinal) ?? 0);
#endif
            }
            return h;
        }
    }
}
