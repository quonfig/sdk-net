using FluentAssertions;
using Quonfig.Sdk.Eval;
using Xunit;

namespace Quonfig.Sdk.Tests.Eval;

/// <summary>
/// Strict semver 2.0.0 parser/comparator. Mirrors sdk-java's <c>SemanticVersionTest</c> and
/// sdk-go's <c>semver_test.go</c> so the .NET SDK orders versions identically to its siblings —
/// the <c>PROP_SEMVER_*</c> operators rely on this.
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("10.20.30")]
    [InlineData("1.2.3-rc1")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3-rc.1+build.42")]
    [InlineData("1.2.3+build.42")]
    public void TryParse_ValidVersion_ReturnsTrue(string input)
    {
        SemanticVersion.TryParse(input, out var sv).Should().BeTrue($"\"{input}\" is a valid semver");
        sv.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("not-a-semver")]
    public void TryParse_InvalidVersion_ReturnsFalse(string input)
    {
        SemanticVersion.TryParse(input, out var sv).Should().BeFalse();
        sv.Should().BeNull();
    }

    [Fact]
    public void Compare_MajorMinorPatch_OrdersNumerically()
    {
        var a = SemanticVersion.Parse("1.2.3");
        var b = SemanticVersion.Parse("1.2.4");
        a.CompareTo(b).Should().BeNegative();
        b.CompareTo(a).Should().BePositive();
        a.CompareTo(SemanticVersion.Parse("1.2.3")).Should().Be(0);
    }

    [Fact]
    public void Compare_MajorVsMinor_MajorWins()
    {
        var a = SemanticVersion.Parse("1.99.99");
        var b = SemanticVersion.Parse("2.0.0");
        a.CompareTo(b).Should().BeNegative();
    }

    [Fact]
    public void Compare_PrereleaseHasLowerPrecedenceThanRelease()
    {
        // semver 2.0.0 §11: a normal version has higher precedence than a pre-release version
        var rc = SemanticVersion.Parse("1.0.0-rc1");
        var release = SemanticVersion.Parse("1.0.0");
        rc.CompareTo(release).Should().BeNegative();
    }

    [Fact]
    public void Compare_PrereleaseIdentifiers_NumericLowerThanAlphanumeric()
    {
        var num = SemanticVersion.Parse("1.0.0-1");
        var alpha = SemanticVersion.Parse("1.0.0-alpha");
        num.CompareTo(alpha).Should().BeNegative();
    }

    [Fact]
    public void Compare_PrereleaseLength_LongerWins_WhenPrefixesEqual()
    {
        // 1.0.0-alpha < 1.0.0-alpha.1 (§11 example).
        var shorter = SemanticVersion.Parse("1.0.0-alpha");
        var longer = SemanticVersion.Parse("1.0.0-alpha.1");
        shorter.CompareTo(longer).Should().BeNegative();
    }

    [Fact]
    public void Compare_BuildMetadata_IsIgnored()
    {
        var a = SemanticVersion.Parse("1.0.0+build.1");
        var b = SemanticVersion.Parse("1.0.0+build.2");
        a.CompareTo(b).Should().Be(0);
    }

    [Fact]
    public void Compare_OrderFromSemverSpec()
    {
        // Spec §11 example chain:
        // 1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta < 1.0.0-beta.2
        //   < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0
        string[] ordered =
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        };
        for (int i = 0; i < ordered.Length - 1; i++)
        {
            var a = SemanticVersion.Parse(ordered[i]);
            var b = SemanticVersion.Parse(ordered[i + 1]);
            a.CompareTo(b).Should().BeNegative($"{ordered[i]} < {ordered[i + 1]}");
        }
    }
}
