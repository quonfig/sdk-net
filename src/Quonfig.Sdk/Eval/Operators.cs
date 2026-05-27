using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Single-criterion evaluator + operator-name constants. Faithful port of sdk-java's
/// <c>Operators</c> and sdk-go's <c>operators.go</c> — pure logic, no IO. The operator-name
/// strings are the cross-SDK contract (app-quonfig emits them on the wire), so they must stay
/// in lock-step with the sibling SDKs.
///
/// <para>Numeric operators coerce both sides to <see cref="double"/>; coercion failure returns
/// false (we never throw out of <see cref="EvaluateCriterion"/>). String operators take the
/// CLR value as-is — strings, lists, primitives — and use ordinal comparison so locale doesn't
/// affect rule matching.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724:Type names should not match namespaces",
    Justification = "Name is the cross-SDK contract — matches sdk-java's com.quonfig.sdk.eval.Operators.")]
public static class Operators
{
#pragma warning disable CA1707 // operator names are wire-level constants — must match across SDKs
    /// <summary>NOT_SET — never matches (placeholder operator).</summary>
    public const string NOT_SET = "NOT_SET";
    /// <summary>ALWAYS_TRUE — unconditional match.</summary>
    public const string ALWAYS_TRUE = "ALWAYS_TRUE";
    /// <summary>PROP_IS_ONE_OF — exact string match against any value in the list.</summary>
    public const string PROP_IS_ONE_OF = "PROP_IS_ONE_OF";
    /// <summary>PROP_IS_NOT_ONE_OF — inverse of <see cref="PROP_IS_ONE_OF"/>; vacuously true when context absent.</summary>
    public const string PROP_IS_NOT_ONE_OF = "PROP_IS_NOT_ONE_OF";
    /// <summary>PROP_STARTS_WITH_ONE_OF — context string starts with any of the listed prefixes.</summary>
    public const string PROP_STARTS_WITH_ONE_OF = "PROP_STARTS_WITH_ONE_OF";
    /// <summary>PROP_DOES_NOT_START_WITH_ONE_OF — inverse of <see cref="PROP_STARTS_WITH_ONE_OF"/>.</summary>
    public const string PROP_DOES_NOT_START_WITH_ONE_OF = "PROP_DOES_NOT_START_WITH_ONE_OF";
    /// <summary>PROP_ENDS_WITH_ONE_OF — context string ends with any of the listed suffixes.</summary>
    public const string PROP_ENDS_WITH_ONE_OF = "PROP_ENDS_WITH_ONE_OF";
    /// <summary>PROP_DOES_NOT_END_WITH_ONE_OF — inverse of <see cref="PROP_ENDS_WITH_ONE_OF"/>.</summary>
    public const string PROP_DOES_NOT_END_WITH_ONE_OF = "PROP_DOES_NOT_END_WITH_ONE_OF";
    /// <summary>PROP_CONTAINS_ONE_OF — context string contains any of the listed substrings.</summary>
    public const string PROP_CONTAINS_ONE_OF = "PROP_CONTAINS_ONE_OF";
    /// <summary>PROP_DOES_NOT_CONTAIN_ONE_OF — inverse of <see cref="PROP_CONTAINS_ONE_OF"/>.</summary>
    public const string PROP_DOES_NOT_CONTAIN_ONE_OF = "PROP_DOES_NOT_CONTAIN_ONE_OF";
    /// <summary>PROP_MATCHES — regex match (substring, like Java's <c>Pattern.matcher.find</c>).</summary>
    public const string PROP_MATCHES = "PROP_MATCHES";
    /// <summary>PROP_DOES_NOT_MATCH — inverse of <see cref="PROP_MATCHES"/>.</summary>
    public const string PROP_DOES_NOT_MATCH = "PROP_DOES_NOT_MATCH";
    /// <summary>HIERARCHICAL_MATCH — dot-delimited prefix match (<c>a.b</c> matches <c>a.b.c</c>).</summary>
    public const string HIERARCHICAL_MATCH = "HIERARCHICAL_MATCH";
    /// <summary>IN_INT_RANGE — half-open interval [start, end), inclusive lower bound, exclusive upper.</summary>
    public const string IN_INT_RANGE = "IN_INT_RANGE";
    /// <summary>PROP_GREATER_THAN — numeric greater-than.</summary>
    public const string PROP_GREATER_THAN = "PROP_GREATER_THAN";
    /// <summary>PROP_GREATER_THAN_OR_EQUAL — numeric greater-than-or-equal.</summary>
    public const string PROP_GREATER_THAN_OR_EQUAL = "PROP_GREATER_THAN_OR_EQUAL";
    /// <summary>PROP_LESS_THAN — numeric less-than.</summary>
    public const string PROP_LESS_THAN = "PROP_LESS_THAN";
    /// <summary>PROP_LESS_THAN_OR_EQUAL — numeric less-than-or-equal.</summary>
    public const string PROP_LESS_THAN_OR_EQUAL = "PROP_LESS_THAN_OR_EQUAL";
    /// <summary>PROP_BEFORE — Unix-ms timestamp comparison; context strictly before match.</summary>
    public const string PROP_BEFORE = "PROP_BEFORE";
    /// <summary>PROP_AFTER — Unix-ms timestamp comparison; context strictly after match.</summary>
    public const string PROP_AFTER = "PROP_AFTER";
    /// <summary>PROP_SEMVER_LESS_THAN — semver 2.0.0 comparison; fails closed on invalid input.</summary>
    public const string PROP_SEMVER_LESS_THAN = "PROP_SEMVER_LESS_THAN";
    /// <summary>PROP_SEMVER_EQUAL — semver equality (ignores build metadata).</summary>
    public const string PROP_SEMVER_EQUAL = "PROP_SEMVER_EQUAL";
    /// <summary>PROP_SEMVER_GREATER_THAN — semver greater-than.</summary>
    public const string PROP_SEMVER_GREATER_THAN = "PROP_SEMVER_GREATER_THAN";
    /// <summary>IN_SEG — recursive segment membership; resolver returns the segment's evaluated boolean.</summary>
    public const string IN_SEG = "IN_SEG";
    /// <summary>NOT_IN_SEG — inverse of <see cref="IN_SEG"/>; vacuously true when segment is missing.</summary>
    public const string NOT_IN_SEG = "NOT_IN_SEG";
    /// <summary>IS_PRESENT — property exists in context AND its value is non-null (0/""/false still count as present).</summary>
    public const string IS_PRESENT = "IS_PRESENT";
    /// <summary>IS_NOT_PRESENT — inverse of <see cref="IS_PRESENT"/>.</summary>
    public const string IS_NOT_PRESENT = "IS_NOT_PRESENT";
#pragma warning restore CA1707

    /// <summary>
    /// Resolves a named segment reference for <see cref="IN_SEG"/> / <see cref="NOT_IN_SEG"/>.
    /// The evaluator wires this to a callback that recursively evaluates the
    /// segment config (segments are themselves config rows of type <c>segment</c>).
    /// </summary>
    public delegate SegmentResolverResult SegmentResolver(string segmentKey);

    /// <summary>
    /// Evaluate a single criterion against the resolved context value.
    /// </summary>
    /// <param name="contextValue">The resolved context value (may be null).</param>
    /// <param name="contextExists">Whether the dotted property actually exists in the context.</param>
    /// <param name="criterion">The criterion to evaluate.</param>
    /// <param name="segmentResolver">Resolver for <see cref="IN_SEG"/>/<see cref="NOT_IN_SEG"/>; may be null.</param>
    /// <returns>Whether the criterion is satisfied.</returns>
#pragma warning disable CA1062 // false positive: contextValue is object? and may be null by design
    public static bool EvaluateCriterion(
        object? contextValue,
        bool contextExists,
        Criterion criterion,
        SegmentResolver? segmentResolver)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(criterion);
#else
        if (criterion is null) throw new ArgumentNullException(nameof(criterion));
#endif
        var matchValue = criterion.ValueToMatch;
        var op = criterion.Operator;

        switch (op)
        {
            case NOT_SET:
                return false;

            case ALWAYS_TRUE:
                return true;

            case PROP_IS_ONE_OF:
            case PROP_IS_NOT_ONE_OF:
            {
                if (contextExists && matchValue is not null)
                {
                    var matchStrings = StringListOf(matchValue);
                    if (matchStrings is not null)
                    {
                        var contextStrings = ToStringList(contextValue);
                        bool matchFound = false;
                        foreach (var cv in contextStrings)
                        {
                            if (matchStrings.Contains(cv))
                            {
                                matchFound = true;
                                break;
                            }
                        }
                        return matchFound == (op == PROP_IS_ONE_OF);
                    }
                }
                return op == PROP_IS_NOT_ONE_OF;
            }

            case PROP_STARTS_WITH_ONE_OF:
            case PROP_DOES_NOT_START_WITH_ONE_OF:
            {
                if (contextExists && matchValue is not null)
                {
                    var matchStrings = StringListOf(matchValue);
                    if (matchStrings is not null)
                    {
                        var cv = ToStringOrEmpty(contextValue);
                        bool matchFound = AnyMatch(matchStrings, cv, MatchKind.Starts);
                        return matchFound == (op == PROP_STARTS_WITH_ONE_OF);
                    }
                }
                return op == PROP_DOES_NOT_START_WITH_ONE_OF;
            }

            case PROP_ENDS_WITH_ONE_OF:
            case PROP_DOES_NOT_END_WITH_ONE_OF:
            {
                if (contextExists && matchValue is not null)
                {
                    var matchStrings = StringListOf(matchValue);
                    if (matchStrings is not null)
                    {
                        var cv = ToStringOrEmpty(contextValue);
                        bool matchFound = AnyMatch(matchStrings, cv, MatchKind.Ends);
                        return matchFound == (op == PROP_ENDS_WITH_ONE_OF);
                    }
                }
                return op == PROP_DOES_NOT_END_WITH_ONE_OF;
            }

            case PROP_CONTAINS_ONE_OF:
            case PROP_DOES_NOT_CONTAIN_ONE_OF:
            {
                if (contextExists && matchValue is not null)
                {
                    var matchStrings = StringListOf(matchValue);
                    if (matchStrings is not null)
                    {
                        var cv = ToStringOrEmpty(contextValue);
                        bool matchFound = AnyMatch(matchStrings, cv, MatchKind.Contains);
                        return matchFound == (op == PROP_CONTAINS_ONE_OF);
                    }
                }
                return op == PROP_DOES_NOT_CONTAIN_ONE_OF;
            }

            case PROP_MATCHES:
            case PROP_DOES_NOT_MATCH:
            {
                if (contextExists
                    && matchValue is not null
                    && contextValue is string cv
                    && matchValue.Payload is string pattern)
                {
                    try
                    {
                        var matched = Regex.IsMatch(cv, pattern);
                        return matched == (op == PROP_MATCHES);
                    }
                    catch (ArgumentException)
                    {
                        // Invalid regex pattern — fail closed (matches sdk-java/sdk-go).
                        return false;
                    }
                }
                return false;
            }

            case HIERARCHICAL_MATCH:
            {
                if (contextExists && matchValue is not null)
                {
                    var cv = ToStringOrEmpty(contextValue);
                    var mv = ToStringOrEmpty(matchValue.Payload);
                    return cv.StartsWith(mv, StringComparison.Ordinal);
                }
                return false;
            }

            case IN_INT_RANGE:
            {
                if (contextExists && matchValue is not null)
                {
                    var (start, end) = ExtractIntRange(matchValue);
                    if (TryToDouble(contextValue, out double n))
                    {
                        return n >= start && n < end;
                    }
                }
                return false;
            }

            case PROP_GREATER_THAN:
            case PROP_GREATER_THAN_OR_EQUAL:
            case PROP_LESS_THAN:
            case PROP_LESS_THAN_OR_EQUAL:
            {
                if (contextExists
                    && matchValue is not null
                    && IsNumber(contextValue)
                    && IsNumber(matchValue.Payload)
                    && TryToDouble(contextValue, out double a)
                    && TryToDouble(matchValue.Payload, out double b))
                {
                    int cmp = a.CompareTo(b);
                    return op switch
                    {
                        PROP_GREATER_THAN => cmp > 0,
                        PROP_GREATER_THAN_OR_EQUAL => cmp >= 0,
                        PROP_LESS_THAN => cmp < 0,
                        PROP_LESS_THAN_OR_EQUAL => cmp <= 0,
                        _ => false,
                    };
                }
                return false;
            }

            case PROP_BEFORE:
            case PROP_AFTER:
            {
                if (contextExists && matchValue is not null)
                {
                    if (TryDateToMillis(contextValue, out long ctxMillis)
                        && TryDateToMillis(matchValue.Payload, out long matchMillis))
                    {
                        return op == PROP_BEFORE ? ctxMillis < matchMillis : ctxMillis > matchMillis;
                    }
                }
                return false;
            }

            case PROP_SEMVER_LESS_THAN:
            case PROP_SEMVER_EQUAL:
            case PROP_SEMVER_GREATER_THAN:
            {
                if (contextExists
                    && matchValue is not null
                    && contextValue is string ctxStr
                    && matchValue.Payload is string matchStr
                    && SemanticVersion.TryParse(ctxStr, out var ctxSv)
                    && SemanticVersion.TryParse(matchStr, out var matchSv))
                {
                    int cmp = ctxSv!.CompareTo(matchSv);
                    return op switch
                    {
                        PROP_SEMVER_LESS_THAN => cmp < 0,
                        PROP_SEMVER_EQUAL => cmp == 0,
                        PROP_SEMVER_GREATER_THAN => cmp > 0,
                        _ => false,
                    };
                }
                return false;
            }

            case IS_PRESENT:
            case IS_NOT_PRESENT:
            {
                // A property is "present" iff the dotted path resolved AND the value is non-null.
                // Empty string, 0, and false are intentionally treated as present (matches sdk-java/sdk-go).
                bool present = contextExists && contextValue is not null;
                return present == (op == IS_PRESENT);
            }

            case IN_SEG:
            case NOT_IN_SEG:
            {
                if (matchValue is not null && segmentResolver is not null)
                {
                    var segKey = ToStringOrEmpty(matchValue.Payload);
                    var r = segmentResolver(segKey);
                    if (!r.Found)
                    {
                        return op == NOT_IN_SEG;
                    }
                    return r.Value == (op == IN_SEG);
                }
                return op == NOT_IN_SEG;
            }

            default:
                return false;
        }
    }
#pragma warning restore CA1062

    // ---------- helpers ----------

    private enum MatchKind { Starts, Ends, Contains }

    private static bool AnyMatch(IList<string> needles, string haystack, MatchKind kind)
    {
        foreach (var n in needles)
        {
            bool hit = kind switch
            {
                MatchKind.Starts => haystack.StartsWith(n, StringComparison.Ordinal),
                MatchKind.Ends => haystack.EndsWith(n, StringComparison.Ordinal),
#if NETSTANDARD2_0
                MatchKind.Contains => haystack.IndexOf(n, StringComparison.Ordinal) >= 0,
#else
                MatchKind.Contains => haystack.Contains(n, StringComparison.Ordinal),
#endif
                _ => false,
            };
            if (hit) return true;
        }
        return false;
    }

    private static IList<string>? StringListOf(Value v)
    {
        var raw = v.Payload;
        if (raw is IReadOnlyList<string> rs)
        {
            return new List<string>(rs);
        }
        if (raw is IEnumerable seq and not string)
        {
            var list = new List<string>();
            foreach (var item in seq) list.Add(ToStringOrEmpty(item));
            return list;
        }
        return null;
    }

    private static string ToStringOrEmpty(object? v)
    {
        if (v is null) return "";
        if (v is string s) return s;
        if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
        if (v is float f) return f.ToString("R", CultureInfo.InvariantCulture);
        if (v is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
        return v.ToString() ?? "";
    }

    private static IList<string> ToStringList(object? v)
    {
        if (v is null) return Array.Empty<string>();
        if (v is string s) return new[] { s };
        if (v is IEnumerable seq)
        {
            var list = new List<string>();
            foreach (var item in seq) list.Add(ToStringOrEmpty(item));
            return list;
        }
        return new[] { ToStringOrEmpty(v) };
    }

    private static bool IsNumber(object? v) =>
        v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool TryToDouble(object? v, out double result)
    {
        switch (v)
        {
            case sbyte b: result = b; return true;
            case byte b: result = b; return true;
            case short b: result = b; return true;
            case ushort b: result = b; return true;
            case int b: result = b; return true;
            case uint b: result = b; return true;
            case long b: result = b; return true;
            case ulong b: result = b; return true;
            case float b: result = b; return true;
            case double b: result = b; return true;
            case decimal b: result = (double)b; return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p):
                result = p;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static (long Start, long End) ExtractIntRange(Value v)
    {
        long start = long.MinValue;
        long end = long.MaxValue;
        if (v?.Payload is IDictionary<string, object?> d)
        {
            if (d.TryGetValue("start", out var s) && s is not null) start = ToLongOrZero(s);
            if (d.TryGetValue("end", out var e) && e is not null) end = ToLongOrZero(e);
        }
        else if (v?.Payload is IDictionary dict)
        {
            if (dict.Contains("start") && dict["start"] is not null) start = ToLongOrZero(dict["start"]!);
            if (dict.Contains("end") && dict["end"] is not null) end = ToLongOrZero(dict["end"]!);
        }
        return (start, end);
    }

    private static long ToLongOrZero(object v)
    {
        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case short s: return s;
            case byte b: return b;
            case sbyte sb: return sb;
            case ushort us: return us;
            case uint ui: return ui;
            case ulong ul: return (long)ul;
            case float f: return (long)f;
            case double d: return (long)d;
            case decimal dec: return (long)dec;
            case string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p):
                return p;
            default: return 0;
        }
    }

    private static bool TryDateToMillis(object? val, out long millis)
    {
        if (IsNumber(val))
        {
            if (TryToDouble(val, out var d))
            {
                millis = (long)d;
                return true;
            }
        }
        if (val is string s)
        {
            // ISO-8601: prefer DateTimeOffset for round-trip-with-zone parsing.
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                millis = dto.ToUnixTimeMilliseconds();
                return true;
            }
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                millis = l;
                return true;
            }
        }
        millis = 0;
        return false;
    }
}

/// <summary>
/// Result of resolving a segment reference: <see cref="Found"/> distinguishes "segment exists,
/// here's its boolean" from "segment is missing in the store" — the operator handles both with
/// different fallback semantics.
/// </summary>
public readonly struct SegmentResolverResult : IEquatable<SegmentResolverResult>
{
    /// <summary>Whether the segment was found in the store.</summary>
    public bool Found { get; }

    /// <summary>The segment's evaluated boolean value; only meaningful when <see cref="Found"/> is true.</summary>
    public bool Value { get; }

    private SegmentResolverResult(bool found, bool value)
    {
        Found = found;
        Value = value;
    }

    /// <summary>The "segment not found" singleton.</summary>
    public static SegmentResolverResult NotFound { get; } = new(false, false);

    /// <summary>Builds a found result with the given boolean value.</summary>
    public static SegmentResolverResult FromValue(bool value) => new(true, value);

    /// <inheritdoc/>
    public bool Equals(SegmentResolverResult other) => Found == other.Found && Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SegmentResolverResult o && Equals(o);

    /// <inheritdoc/>
    public override int GetHashCode() => (Found.GetHashCode() * 397) ^ Value.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SegmentResolverResult a, SegmentResolverResult b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SegmentResolverResult a, SegmentResolverResult b) => !a.Equals(b);
}
