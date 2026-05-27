using System.Collections.Generic;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Xunit;
using ValueType = Quonfig.Sdk.Eval.ValueType;

namespace Quonfig.Sdk.Tests.Eval;

/// <summary>
/// Single-criterion operator contract — case-for-case port of sdk-java's <c>OperatorsTest</c>
/// (and the matching sdk-go suite). Each operator gets at least a positive and a negative case
/// plus a "fail closed" check when the context value type doesn't match what the operator expects.
/// The .NET surface differs from Java only in the CLR types fed in: where Java passes
/// <c>Object</c>, .NET passes the result of <see cref="ContextValue.ToObject"/> (or a raw CLR
/// value for unit-test convenience).
/// </summary>
public sealed class OperatorsTests
{
    private static Criterion Crit(string op, string? prop, Value? match) =>
        new Criterion(prop, op, match);

    private static Value StrList(params string[] vs) =>
        new Value(ValueType.StringList, (System.Collections.Generic.IReadOnlyList<string>)vs);

    private static Value Str(string s) => new Value(ValueType.String, s);
    private static Value LongVal(long v) => new Value(ValueType.Int, v);
    private static Value DoubleVal(double v) => new Value(ValueType.Double, v);

    // ----- ALWAYS_TRUE / NOT_SET -----

    [Fact]
    public void AlwaysTrue_IsAlwaysTrue()
    {
        var c = Crit(Operators.ALWAYS_TRUE, null, null);
        Operators.EvaluateCriterion(null, false, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("x", true, c, null).Should().BeTrue();
    }

    [Fact]
    public void NotSet_IsAlwaysFalse()
    {
        var c = Crit(Operators.NOT_SET, null, null);
        Operators.EvaluateCriterion("anything", true, c, null).Should().BeFalse();
        Operators.EvaluateCriterion(null, false, c, null).Should().BeFalse();
    }

    // ----- PROP_IS_ONE_OF / NOT_ONE_OF -----

    [Fact]
    public void PropIsOneOf_MatchesWhenContextValueInList()
    {
        var c = Crit(Operators.PROP_IS_ONE_OF, "user.email", StrList("a@b.com", "c@d.com"));
        Operators.EvaluateCriterion("c@d.com", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("nope@e.com", true, c, null).Should().BeFalse();
    }

    [Fact]
    public void PropIsNotOneOf_InverseAndDefaultsTrueWhenMissing()
    {
        var c = Crit(Operators.PROP_IS_NOT_ONE_OF, "user.email", StrList("a@b.com"));
        Operators.EvaluateCriterion("a@b.com", true, c, null).Should().BeFalse();
        Operators.EvaluateCriterion("z@z.com", true, c, null).Should().BeTrue();
        // missing context: NOT_ONE_OF returns true (vacuous)
        Operators.EvaluateCriterion(null, false, c, null).Should().BeTrue();
    }

    private static readonly string[] UserAdmin = { "user", "admin" };
    private static readonly string[] UserGuest = { "user", "guest" };

    [Fact]
    public void PropIsOneOf_ListContext_AnyOverlapMatches()
    {
        var c = Crit(Operators.PROP_IS_ONE_OF, "user.roles", StrList("admin", "owner"));
        Operators.EvaluateCriterion(UserAdmin, true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion(UserGuest, true, c, null).Should().BeFalse();
    }

    // ----- STARTS / ENDS / CONTAINS -----

    [Fact]
    public void PropStartsWithOneOf()
    {
        var c = Crit(Operators.PROP_STARTS_WITH_ONE_OF, "user.email", StrList("admin", "ceo"));
        Operators.EvaluateCriterion("admin@x.com", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("user@x.com", true, c, null).Should().BeFalse();
    }

    [Fact]
    public void PropDoesNotStartWithOneOf()
    {
        var c = Crit(Operators.PROP_DOES_NOT_START_WITH_ONE_OF, "user.email", StrList("admin"));
        Operators.EvaluateCriterion("admin@x.com", true, c, null).Should().BeFalse();
        Operators.EvaluateCriterion("user@x.com", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion(null, false, c, null).Should().BeTrue();
    }

    [Fact]
    public void PropEndsWithOneOf()
    {
        var c = Crit(Operators.PROP_ENDS_WITH_ONE_OF, "user.email", StrList("@quonfig.com"));
        Operators.EvaluateCriterion("a@quonfig.com", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("a@other.com", true, c, null).Should().BeFalse();
    }

    [Fact]
    public void PropContainsOneOf()
    {
        var c = Crit(Operators.PROP_CONTAINS_ONE_OF, "user.email", StrList("internal"));
        Operators.EvaluateCriterion("foo-internal-bar", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("foo-bar", true, c, null).Should().BeFalse();
    }

    [Fact]
    public void PropDoesNotContainOneOf_InverseAndTrueWhenMissing()
    {
        var c = Crit(Operators.PROP_DOES_NOT_CONTAIN_ONE_OF, "user.email", StrList("internal"));
        Operators.EvaluateCriterion("internal-thing", true, c, null).Should().BeFalse();
        Operators.EvaluateCriterion(null, false, c, null).Should().BeTrue();
    }

    // ----- PROP_MATCHES / DOES_NOT_MATCH -----

    [Fact]
    public void PropMatches_Regex()
    {
        var c = Crit(Operators.PROP_MATCHES, "user.email", Str("^foo.*$"));
        Operators.EvaluateCriterion("foobar", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("nope", true, c, null).Should().BeFalse();
    }

    [Fact]
    public void PropDoesNotMatch_Regex()
    {
        var c = Crit(Operators.PROP_DOES_NOT_MATCH, "user.email", Str("^foo.*$"));
        Operators.EvaluateCriterion("foobar", true, c, null).Should().BeFalse();
        Operators.EvaluateCriterion("nope", true, c, null).Should().BeTrue();
    }

    [Fact]
    public void PropMatches_InvalidRegex_FailsClosed()
    {
        var c = Crit(Operators.PROP_MATCHES, "x", Str("[unterminated"));
        Operators.EvaluateCriterion("anything", true, c, null).Should().BeFalse();
    }

    // ----- HIERARCHICAL_MATCH -----

    [Fact]
    public void HierarchicalMatch_IsPrefixCheck()
    {
        var c = Crit(Operators.HIERARCHICAL_MATCH, "logger.path", Str("com.quonfig"));
        Operators.EvaluateCriterion("com.quonfig.sdk", true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion("org.something", true, c, null).Should().BeFalse();
    }

    // ----- IN_INT_RANGE -----

    [Fact]
    public void InIntRange_HalfOpenInterval()
    {
        var range = new Dictionary<string, object?> { ["start"] = 10L, ["end"] = 20L };
        var c = Crit(Operators.IN_INT_RANGE, "user.age", new Value(ValueType.Json, range));
        Operators.EvaluateCriterion(10, true, c, null).Should().BeTrue("start inclusive");
        Operators.EvaluateCriterion(19, true, c, null).Should().BeTrue();
        Operators.EvaluateCriterion(20, true, c, null).Should().BeFalse("end exclusive");
        Operators.EvaluateCriterion(9, true, c, null).Should().BeFalse();
    }

    // ----- COMPARISON -----

    [Fact]
    public void GreaterThan_AndOrEqual()
    {
        var gt = Crit(Operators.PROP_GREATER_THAN, "n", LongVal(5));
        var gte = Crit(Operators.PROP_GREATER_THAN_OR_EQUAL, "n", LongVal(5));
        Operators.EvaluateCriterion(6L, true, gt, null).Should().BeTrue();
        Operators.EvaluateCriterion(5L, true, gt, null).Should().BeFalse();
        Operators.EvaluateCriterion(5L, true, gte, null).Should().BeTrue();
    }

    [Fact]
    public void LessThan_AndOrEqual_DoublesAndInts()
    {
        var lt = Crit(Operators.PROP_LESS_THAN, "n", DoubleVal(5.5));
        Operators.EvaluateCriterion(5, true, lt, null).Should().BeTrue();
        Operators.EvaluateCriterion(5.5, true, lt, null).Should().BeFalse();
        var lte = Crit(Operators.PROP_LESS_THAN_OR_EQUAL, "n", DoubleVal(5.5));
        Operators.EvaluateCriterion(5.5, true, lte, null).Should().BeTrue();
    }

    [Fact]
    public void Comparison_FailsClosed_WhenContextNotNumeric()
    {
        var gt = Crit(Operators.PROP_GREATER_THAN, "n", LongVal(5));
        Operators.EvaluateCriterion("not-a-number", true, gt, null).Should().BeFalse();
    }

    // ----- BEFORE / AFTER -----

    [Fact]
    public void PropBefore_AcceptsMillisAndIso()
    {
        long matchMillis = 1700000000000L;
        var before = Crit(Operators.PROP_BEFORE, "user.created_at", LongVal(matchMillis));
        Operators.EvaluateCriterion(matchMillis - 1, true, before, null).Should().BeTrue();
        Operators.EvaluateCriterion(matchMillis + 1, true, before, null).Should().BeFalse();
        Operators.EvaluateCriterion("2020-01-01T00:00:00Z", true, before, null).Should().BeTrue("ISO before match");
    }

    [Fact]
    public void PropAfter_IsInverseOfBefore()
    {
        long matchMillis = 1700000000000L;
        var after = Crit(Operators.PROP_AFTER, "user.created_at", LongVal(matchMillis));
        Operators.EvaluateCriterion(matchMillis + 1, true, after, null).Should().BeTrue();
        Operators.EvaluateCriterion(matchMillis - 1, true, after, null).Should().BeFalse();
    }

    // ----- SEMVER -----

    [Fact]
    public void Semver_LessThan_Equal_GreaterThan()
    {
        var lt = Crit(Operators.PROP_SEMVER_LESS_THAN, "v", Str("2.0.0"));
        var eq = Crit(Operators.PROP_SEMVER_EQUAL, "v", Str("2.0.0"));
        var gt = Crit(Operators.PROP_SEMVER_GREATER_THAN, "v", Str("2.0.0"));
        Operators.EvaluateCriterion("1.99.99", true, lt, null).Should().BeTrue();
        Operators.EvaluateCriterion("2.0.0", true, eq, null).Should().BeTrue();
        Operators.EvaluateCriterion("2.0.1", true, gt, null).Should().BeTrue();
        // pre-release: 2.0.0-rc1 < 2.0.0
        Operators.EvaluateCriterion("2.0.0-rc1", true, lt, null).Should().BeTrue();
    }

    [Fact]
    public void Semver_FailsClosedOnInvalidInput()
    {
        var lt = Crit(Operators.PROP_SEMVER_LESS_THAN, "v", Str("2.0.0"));
        Operators.EvaluateCriterion("not-a-semver", true, lt, null).Should().BeFalse();
    }

    // ----- IS_PRESENT / IS_NOT_PRESENT -----

    [Fact]
    public void IsPresent_EmptyStringAndZeroAreStillPresent()
    {
        var present = Crit(Operators.IS_PRESENT, "user.email", null);
        Operators.EvaluateCriterion("", true, present, null).Should().BeTrue();
        Operators.EvaluateCriterion(0, true, present, null).Should().BeTrue();
        Operators.EvaluateCriterion(false, true, present, null).Should().BeTrue();
        Operators.EvaluateCriterion(null, false, present, null).Should().BeFalse();
        Operators.EvaluateCriterion(null, true, present, null).Should().BeFalse("null is absent");
    }

    [Fact]
    public void IsNotPresent_IsInverse()
    {
        var notPresent = Crit(Operators.IS_NOT_PRESENT, "user.email", null);
        Operators.EvaluateCriterion(null, false, notPresent, null).Should().BeTrue();
        Operators.EvaluateCriterion("x", true, notPresent, null).Should().BeFalse();
    }

    // ----- IN_SEG / NOT_IN_SEG -----

    [Fact]
    public void InSeg_CallsResolverAndReturnsResult()
    {
        var c = Crit(Operators.IN_SEG, null, Str("seg-key"));
        Operators.SegmentResolver yes = key =>
            key == "seg-key" ? SegmentResolverResult.FromValue(true) : SegmentResolverResult.NotFound;
        Operators.EvaluateCriterion(null, false, c, yes).Should().BeTrue();
    }

    [Fact]
    public void InSeg_SegmentNotFound_ReturnsFalse()
    {
        var c = Crit(Operators.IN_SEG, null, Str("missing"));
        Operators.SegmentResolver missing = _ => SegmentResolverResult.NotFound;
        Operators.EvaluateCriterion(null, false, c, missing).Should().BeFalse();
    }

    [Fact]
    public void NotInSeg_SegmentNotFound_ReturnsTrue()
    {
        var c = Crit(Operators.NOT_IN_SEG, null, Str("missing"));
        Operators.SegmentResolver missing = _ => SegmentResolverResult.NotFound;
        Operators.EvaluateCriterion(null, false, c, missing).Should().BeTrue();
    }

    [Fact]
    public void UnknownOperator_ReturnsFalse()
    {
        var c = Crit("MADE_UP_OPERATOR", "x", Str("y"));
        Operators.EvaluateCriterion("y", true, c, null).Should().BeFalse();
    }

    [Fact]
    public void OperatorConstants_MatchExpectedStringValues()
    {
        Operators.NOT_SET.Should().Be("NOT_SET");
        Operators.ALWAYS_TRUE.Should().Be("ALWAYS_TRUE");
        Operators.PROP_IS_ONE_OF.Should().Be("PROP_IS_ONE_OF");
        Operators.PROP_IS_NOT_ONE_OF.Should().Be("PROP_IS_NOT_ONE_OF");
        Operators.PROP_STARTS_WITH_ONE_OF.Should().Be("PROP_STARTS_WITH_ONE_OF");
        Operators.PROP_DOES_NOT_START_WITH_ONE_OF.Should().Be("PROP_DOES_NOT_START_WITH_ONE_OF");
        Operators.PROP_ENDS_WITH_ONE_OF.Should().Be("PROP_ENDS_WITH_ONE_OF");
        Operators.PROP_DOES_NOT_END_WITH_ONE_OF.Should().Be("PROP_DOES_NOT_END_WITH_ONE_OF");
        Operators.PROP_CONTAINS_ONE_OF.Should().Be("PROP_CONTAINS_ONE_OF");
        Operators.PROP_DOES_NOT_CONTAIN_ONE_OF.Should().Be("PROP_DOES_NOT_CONTAIN_ONE_OF");
        Operators.PROP_MATCHES.Should().Be("PROP_MATCHES");
        Operators.PROP_DOES_NOT_MATCH.Should().Be("PROP_DOES_NOT_MATCH");
        Operators.HIERARCHICAL_MATCH.Should().Be("HIERARCHICAL_MATCH");
        Operators.IN_INT_RANGE.Should().Be("IN_INT_RANGE");
        Operators.PROP_GREATER_THAN.Should().Be("PROP_GREATER_THAN");
        Operators.PROP_GREATER_THAN_OR_EQUAL.Should().Be("PROP_GREATER_THAN_OR_EQUAL");
        Operators.PROP_LESS_THAN.Should().Be("PROP_LESS_THAN");
        Operators.PROP_LESS_THAN_OR_EQUAL.Should().Be("PROP_LESS_THAN_OR_EQUAL");
        Operators.PROP_BEFORE.Should().Be("PROP_BEFORE");
        Operators.PROP_AFTER.Should().Be("PROP_AFTER");
        Operators.PROP_SEMVER_LESS_THAN.Should().Be("PROP_SEMVER_LESS_THAN");
        Operators.PROP_SEMVER_EQUAL.Should().Be("PROP_SEMVER_EQUAL");
        Operators.PROP_SEMVER_GREATER_THAN.Should().Be("PROP_SEMVER_GREATER_THAN");
        Operators.IN_SEG.Should().Be("IN_SEG");
        Operators.NOT_IN_SEG.Should().Be("NOT_IN_SEG");
        Operators.IS_PRESENT.Should().Be("IS_PRESENT");
        Operators.IS_NOT_PRESENT.Should().Be("IS_NOT_PRESENT");
    }
}
