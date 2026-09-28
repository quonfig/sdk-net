// AUTO-GENERATED from integration-test-data/tests/eval/get_weighted_values.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class GetWeightedValuesTests
{

    [Fact(DisplayName = "weighted value is consistent 1")]
    public void WeightedValueIsConsistent1()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted", TestSetup.Map("user", TestSetup.Map("tracking_id", "a72c15f5")));
        Assert.Equal(1L, actual);
    }

    [Fact(DisplayName = "weighted value is consistent 2")]
    public void WeightedValueIsConsistent2()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted", TestSetup.Map("user", TestSetup.Map("tracking_id", "92a202f2")));
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value is consistent 3")]
    public void WeightedValueIsConsistent3()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted", TestSetup.Map("user", TestSetup.Map("tracking_id", "8f414100")));
        Assert.Equal(3L, actual);
    }

    [Fact(DisplayName = "even split ones serves first variant at low hash fraction")]
    public void EvenSplitOnesServesFirstVariantAtLowHashFraction()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.even-split-ones", TestSetup.Map("user", TestSetup.Map("tracking_id", "b7ff78c8")));
        Assert.Equal("a", actual);
    }

    [Fact(DisplayName = "even split ones serves first variant at low hash fraction 2")]
    public void EvenSplitOnesServesFirstVariantAtLowHashFraction2()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.even-split-ones", TestSetup.Map("user", TestSetup.Map("tracking_id", "289f4748")));
        Assert.Equal("a", actual);
    }

    [Fact(DisplayName = "even split ones serves second variant at high hash fraction")]
    public void EvenSplitOnesServesSecondVariantAtHighHashFraction()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.even-split-ones", TestSetup.Map("user", TestSetup.Map("tracking_id", "d60b2cb6")));
        Assert.Equal("b", actual);
    }

    [Fact(DisplayName = "even split ones serves second variant at high hash fraction 2")]
    public void EvenSplitOnesServesSecondVariantAtHighHashFraction2()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.even-split-ones", TestSetup.Map("user", TestSetup.Map("tracking_id", "21bcfd13")));
        Assert.Equal("b", actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized true bucket")]
    public void NonStandardSumStillServesNormalizedTrueBucket()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.non-standard", TestSetup.Map("user", TestSetup.Map("tracking_id", "ff8adf17")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized true bucket 2")]
    public void NonStandardSumStillServesNormalizedTrueBucket2()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.non-standard", TestSetup.Map("user", TestSetup.Map("tracking_id", "36ef1a7a")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized false bucket")]
    public void NonStandardSumStillServesNormalizedFalseBucket()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.non-standard", TestSetup.Map("user", TestSetup.Map("tracking_id", "f667c76a")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized false bucket 2")]
    public void NonStandardSumStillServesNormalizedFalseBucket2()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.non-standard", TestSetup.Map("user", TestSetup.Map("tracking_id", "7467ca21")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "weighted value with hash property missing from context hashes empty string")]
    public void WeightedValueWithHashPropertyMissingFromContextHashesEmptyString()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.missing-hash", TestSetup.Map("user", TestSetup.Map("key", "no-tracking-id-user")));
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with no context hashes empty string")]
    public void WeightedValueWithNoContextHashesEmptyString()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.missing-hash", TestSetup.Map());
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with hash property empty string hashes empty string")]
    public void WeightedValueWithHashPropertyEmptyStringHashesEmptyString()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.missing-hash", TestSetup.Map("user", TestSetup.Map("key", "empty-tracking-id-user", "tracking_id", "")));
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with zero-weight first variant and hash property missing never serves zero-weight variant")]
    public void WeightedValueWithZeroWeightFirstVariantAndHashPropertyMissingNeverServesZeroWeightVariant()
    {
        object? actual = TestSetup.ResolveCase("feature-flag.weighted.zero-first", TestSetup.Map("user", TestSetup.Map("key", "no-tracking-id-user")));
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with no hash property is random on every evaluation")]
    public void WeightedValueWithNoHashPropertyIsRandomOnEveryEvaluation()
    {
        var seen = new System.Collections.Generic.HashSet<object?>();
        for (var i = 0; i < 200; i++)
        {
            seen.Add(TestSetup.ResolveCase("feature-flag.weighted.no-hash", TestSetup.Map()));
        }
        Assert.True(
            seen.SetEquals(new object?[] { 1L, 2L }),
            $"values seen over 200 evaluations: {string.Join(", ", seen)}");
    }

    [Fact(DisplayName = "weighted value with no hash property is random on every evaluation with context")]
    public void WeightedValueWithNoHashPropertyIsRandomOnEveryEvaluationWithContext()
    {
        var seen = new System.Collections.Generic.HashSet<object?>();
        for (var i = 0; i < 200; i++)
        {
            seen.Add(TestSetup.ResolveCase("feature-flag.weighted.no-hash", TestSetup.Map("user", TestSetup.Map("key", "same-user-every-time", "tracking_id", "same-tracking-id"))));
        }
        Assert.True(
            seen.SetEquals(new object?[] { 1L, 2L }),
            $"values seen over 200 evaluations: {string.Join(", ", seen)}");
    }
}
