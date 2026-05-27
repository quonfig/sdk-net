using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Strict semver 2.0.0 parser and comparator. Mirrors sdk-java's <c>SemanticVersion</c> and
/// sdk-go's <c>semver.go</c> — same regex, same precedence rules — so the
/// <c>PROP_SEMVER_LESS_THAN</c> / <c>PROP_SEMVER_EQUAL</c> / <c>PROP_SEMVER_GREATER_THAN</c>
/// operators order versions identically across SDKs. Invalid input either throws
/// (<see cref="Parse"/>) or yields false (<see cref="TryParse"/>); the evaluator uses TryParse
/// so an unparseable context or match value just fails the criterion (matches sdk-java/sdk-go
/// "string-comparison fallback" semantics — which in practice means both sides fail to parse
/// and the criterion returns false).
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex SemverRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)" +
        @"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        @"(?:\+(?<buildmetadata>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled);

    /// <summary>Major version component.</summary>
    public int Major { get; }

    /// <summary>Minor version component.</summary>
    public int Minor { get; }

    /// <summary>Patch version component.</summary>
    public int Patch { get; }

    /// <summary>Prerelease identifier (without the leading <c>-</c>); empty if absent.</summary>
    public string Prerelease { get; }

    /// <summary>Build metadata (without the leading <c>+</c>); empty if absent. Ignored by <see cref="CompareTo"/>.</summary>
    public string BuildMetadata { get; }

    private SemanticVersion(int major, int minor, int patch, string prerelease, string buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? "";
        BuildMetadata = buildMetadata ?? "";
    }

    /// <summary>Parses <paramref name="version"/>; throws <see cref="FormatException"/> on invalid input.</summary>
    public static SemanticVersion Parse(string version)
    {
        if (TryParse(version, out var sv)) return sv!;
        throw new FormatException($"invalid semantic version format: {version}");
    }

    /// <summary>Parses <paramref name="version"/>; returns false on invalid input.</summary>
    public static bool TryParse(string? version, out SemanticVersion? result)
    {
        if (string.IsNullOrEmpty(version))
        {
            result = null;
            return false;
        }
        var m = SemverRegex.Match(version!);
        if (!m.Success)
        {
            result = null;
            return false;
        }
        result = new SemanticVersion(
            int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["patch"].Value, CultureInfo.InvariantCulture),
            m.Groups["prerelease"].Success ? m.Groups["prerelease"].Value : "",
            m.Groups["buildmetadata"].Success ? m.Groups["buildmetadata"].Value : "");
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        // A version without prerelease has higher precedence than one with.
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;
        var idsA = a.Split('.');
        var idsB = b.Split('.');
        int min = Math.Min(idsA.Length, idsB.Length);
        for (int i = 0; i < min; i++)
        {
            int c = CompareIdentifier(idsA[i], idsB[i]);
            if (c != 0) return c;
        }
        return idsA.Length.CompareTo(idsB.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool aNum = IsNumeric(a);
        bool bNum = IsNumeric(b);
        if (aNum && bNum)
        {
            // Both numeric — compare numerically (no leading zeros allowed per semver, so length+lex works for non-negative).
            return long.Parse(a, CultureInfo.InvariantCulture)
                .CompareTo(long.Parse(b, CultureInfo.InvariantCulture));
        }
        if (aNum) return -1; // numeric identifiers always have lower precedence
        if (bNum) return 1;
        return string.CompareOrdinal(a, b);
    }

    private static bool IsNumeric(string s)
    {
        if (s.Length == 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i])) return false;
        }
        return true;
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    /// <summary>Less-than operator.</summary>
    public static bool operator <(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) =>
        left is null || left.CompareTo(right) <= 0;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(SemanticVersion? left, SemanticVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public bool Equals(SemanticVersion? other) =>
        other is not null
        && Major == other.Major
        && Minor == other.Minor
        && Patch == other.Patch
        && string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SemanticVersion sv && Equals(sv);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int h = Major;
            h = (h * 31) + Minor;
            h = (h * 31) + Patch;
#if NETSTANDARD2_0
            h = (h * 31) + (Prerelease?.GetHashCode() ?? 0);
#else
            h = (h * 31) + Prerelease.GetHashCode(StringComparison.Ordinal);
#endif
            return h;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var s = $"{Major}.{Minor}.{Patch}";
        if (Prerelease.Length > 0) s += "-" + Prerelease;
        if (BuildMetadata.Length > 0) s += "+" + BuildMetadata;
        return s;
    }
}
