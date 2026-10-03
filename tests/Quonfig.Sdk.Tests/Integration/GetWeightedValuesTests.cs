// AUTO-GENERATED from integration-test-data/tests/eval/get_weighted_values.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class GetWeightedValuesTests
{

    [Fact(DisplayName = "weighted value is consistent 1")]
    public async Task WeightedValueIsConsistent1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "a72c15f5" } });
        Assert.Equal(1L, actual);
    }

    [Fact(DisplayName = "weighted value is consistent 2")]
    public async Task WeightedValueIsConsistent2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "92a202f2" } });
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value is consistent 3")]
    public async Task WeightedValueIsConsistent3()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "8f414100" } });
        Assert.Equal(3L, actual);
    }

    [Fact(DisplayName = "even split ones serves first variant at low hash fraction")]
    public async Task EvenSplitOnesServesFirstVariantAtLowHashFraction()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "b7ff78c8" } });
        Assert.Equal("a", actual);
    }

    [Fact(DisplayName = "even split ones serves first variant at low hash fraction 2")]
    public async Task EvenSplitOnesServesFirstVariantAtLowHashFraction2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "289f4748" } });
        Assert.Equal("a", actual);
    }

    [Fact(DisplayName = "even split ones serves second variant at high hash fraction")]
    public async Task EvenSplitOnesServesSecondVariantAtHighHashFraction()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "d60b2cb6" } });
        Assert.Equal("b", actual);
    }

    [Fact(DisplayName = "even split ones serves second variant at high hash fraction 2")]
    public async Task EvenSplitOnesServesSecondVariantAtHighHashFraction2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "21bcfd13" } });
        Assert.Equal("b", actual);
    }

    [Fact(DisplayName = "non-ascii tracking_id emoji hashes utf-8 bytes")]
    public async Task NonAsciiTrackingIdEmojiHashesUtf8Bytes()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "\ud83d\ude80-rocket" } });
        Assert.Equal("a", actual);
    }

    [Fact(DisplayName = "non-ascii tracking_id latin hashes utf-8 bytes")]
    public async Task NonAsciiTrackingIdLatinHashesUtf8Bytes()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "münchen-7" } });
        Assert.Equal("a", actual);
    }

    [Fact(DisplayName = "non-ascii tracking_id cjk hashes utf-8 bytes")]
    public async Task NonAsciiTrackingIdCjkHashesUtf8Bytes()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("feature-flag.weighted.even-split-ones", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "ユーザー1" } });
        Assert.Equal("b", actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized true bucket")]
    public async Task NonStandardSumStillServesNormalizedTrueBucket()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("feature-flag.weighted.non-standard", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "ff8adf17" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized true bucket 2")]
    public async Task NonStandardSumStillServesNormalizedTrueBucket2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("feature-flag.weighted.non-standard", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "36ef1a7a" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized false bucket")]
    public async Task NonStandardSumStillServesNormalizedFalseBucket()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("feature-flag.weighted.non-standard", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "f667c76a" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "non-standard sum still serves normalized false bucket 2")]
    public async Task NonStandardSumStillServesNormalizedFalseBucket2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("feature-flag.weighted.non-standard", new ContextSet { ["user"] = new ContextProperties { ["tracking_id"] = "7467ca21" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "weighted value with hash property missing from context hashes empty string")]
    public async Task WeightedValueWithHashPropertyMissingFromContextHashesEmptyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted.missing-hash", new ContextSet { ["user"] = new ContextProperties { ["key"] = "no-tracking-id-user" } });
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with no context hashes empty string")]
    public async Task WeightedValueWithNoContextHashesEmptyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted.missing-hash");
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with hash property empty string hashes empty string")]
    public async Task WeightedValueWithHashPropertyEmptyStringHashesEmptyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted.missing-hash", new ContextSet { ["user"] = new ContextProperties { ["key"] = "empty-tracking-id-user", ["tracking_id"] = "" } });
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with zero-weight first variant and hash property missing never serves zero-weight variant")]
    public async Task WeightedValueWithZeroWeightFirstVariantAndHashPropertyMissingNeverServesZeroWeightVariant()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("feature-flag.weighted.zero-first", new ContextSet { ["user"] = new ContextProperties { ["key"] = "no-tracking-id-user" } });
        Assert.Equal(2L, actual);
    }

    [Fact(DisplayName = "weighted value with no hash property is random on every evaluation")]
    public async Task WeightedValueWithNoHashPropertyIsRandomOnEveryEvaluation()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var seen = new HashSet<object?>();
        for (var i = 0; i < 200; i++)
        {
            seen.Add(client.GetLong("feature-flag.weighted.no-hash"));
        }
        Assert.True(
            seen.SetEquals(new object?[] { 1L, 2L }),
            $"values seen over 200 evaluations: {string.Join(", ", seen)}");
    }

    [Fact(DisplayName = "weighted value with no hash property is random on every evaluation with context")]
    public async Task WeightedValueWithNoHashPropertyIsRandomOnEveryEvaluationWithContext()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var seen = new HashSet<object?>();
        for (var i = 0; i < 200; i++)
        {
            seen.Add(client.GetLong("feature-flag.weighted.no-hash", new ContextSet { ["user"] = new ContextProperties { ["key"] = "same-user-every-time", ["tracking_id"] = "same-tracking-id" } }));
        }
        Assert.True(
            seen.SetEquals(new object?[] { 1L, 2L }),
            $"values seen over 200 evaluations: {string.Join(", ", seen)}");
    }
}
