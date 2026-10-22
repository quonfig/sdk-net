// AUTO-GENERATED from integration-test-data/tests/eval/enabled.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class EnabledTests
{

    [Fact(DisplayName = "returns the correct value for a simple flag")]
    public async Task ReturnsTheCorrectValueForASimpleFlag()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.simple");
        Assert.True(actual);
    }

    [Fact(DisplayName = "always returns false for a non-boolean flag")]
    public async Task AlwaysReturnsFalseForANonBooleanFlag()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.integer");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for a flag key that does not exist")]
    public async Task ReturnsFalseForAFlagKeyThatDoesNotExist()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("my-missing-key");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for a flag key that does not exist with a context")]
    public async Task ReturnsFalseForAFlagKeyThatDoesNotExistWithAContext()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("my-missing-key", new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael", ["email"] = "michael@example.com" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for a PROP_IS_ONE_OF rule when any prop matches")]
    public async Task ReturnsTrueForAPropIsOneOfRuleWhenAnyPropMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.properties.positive", new ContextSet { [""] = new ContextProperties { ["name"] = "michael", ["domain"] = "something.com" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for a PROP_IS_ONE_OF rule when no prop matches")]
    public async Task ReturnsFalseForAPropIsOneOfRuleWhenNoPropMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.properties.positive", new ContextSet { [""] = new ContextProperties { ["name"] = "lauren", ["domain"] = "something.com" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for a PROP_IS_NOT_ONE_OF rule when any prop doesn't match")]
    public async Task ReturnsTrueForAPropIsNotOneOfRuleWhenAnyPropDoesnTMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.properties.negative", new ContextSet { [""] = new ContextProperties { ["name"] = "lauren", ["domain"] = "prefab.cloud" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for a PROP_IS_NOT_ONE_OF rule when all props match")]
    public async Task ReturnsFalseForAPropIsNotOneOfRuleWhenAllPropsMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.properties.negative", new ContextSet { [""] = new ContextProperties { ["name"] = "michael", ["domain"] = "prefab.cloud" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_ENDS_WITH_ONE_OF rule when the given prop has a matching suffix")]
    public async Task ReturnsTrueForPropEndsWithOneOfRuleWhenTheGivenPropHasAMatchingSuffix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["email"] = "jeff@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.ends-with-one-of.positive");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_ENDS_WITH_ONE_OF rule when the given prop doesn't have a matching suffix")]
    public async Task ReturnsFalseForPropEndsWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSuffix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.ends-with-one-of.positive", new ContextSet { [""] = new ContextProperties { ["email"] = "jeff@test.com" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_END_WITH_ONE_OF rule when the given prop doesn't have a matching suffix")]
    public async Task ReturnsTrueForPropDoesNotEndWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSuffix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { [""] = new ContextProperties { ["email"] = "michael@test.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.ends-with-one-of.negative");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_END_WITH_ONE_OF rule when the given prop has a matching suffix")]
    public async Task ReturnsFalseForPropDoesNotEndWithOneOfRuleWhenTheGivenPropHasAMatchingSuffix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.ends-with-one-of.negative", new ContextSet { [""] = new ContextProperties { ["email"] = "michael@prefab.cloud" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_STARTS_WITH_ONE_OF rule when the given prop has a matching prefix")]
    public async Task ReturnsTrueForPropStartsWithOneOfRuleWhenTheGivenPropHasAMatchingPrefix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "foo@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.starts-with-one-of.positive");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_STARTS_WITH_ONE_OF rule when the given prop doesn't have a matching prefix")]
    public async Task ReturnsFalseForPropStartsWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingPrefix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "notfoo@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.starts-with-one-of.positive");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_START_WITH_ONE_OF rule when the given prop doesn't have a matching prefix")]
    public async Task ReturnsTrueForPropDoesNotStartWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingPrefix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "notfoo@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.starts-with-one-of.negative");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_START_WITH_ONE_OF rule when the given prop has a matching prefix")]
    public async Task ReturnsFalseForPropDoesNotStartWithOneOfRuleWhenTheGivenPropHasAMatchingPrefix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "foo@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.starts-with-one-of.negative");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_CONTAINS_ONE_OF rule when the given prop has a matching substring")]
    public async Task ReturnsTrueForPropContainsOneOfRuleWhenTheGivenPropHasAMatchingSubstring()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "somefoo@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.contains-one-of.positive");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_CONTAINS_ONE_OF rule when the given prop doesn't have a matching substring")]
    public async Task ReturnsFalseForPropContainsOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSubstring()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "info@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.contains-one-of.positive");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_CONTAIN_ONE_OF rule when the given prop doesn't have a matching substring")]
    public async Task ReturnsTrueForPropDoesNotContainOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSubstring()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "info@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.contains-one-of.negative");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_CONTAIN_ONE_OF rule when the given prop has a matching substring")]
    public async Task ReturnsFalseForPropDoesNotContainOneOfRuleWhenTheGivenPropHasAMatchingSubstring()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["email"] = "notfoo@prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.contains-one-of.negative");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG when the segment rule matches")]
    public async Task ReturnsTrueForInSegWhenTheSegmentRuleMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "lauren" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-segment.positive");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for IN_SEG when the segment rule doesn't match")]
    public async Task ReturnsFalseForInSegWhenTheSegmentRuleDoesnTMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.in-segment.positive", new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IN_SEG if any segment rule fails to match")]
    public async Task ReturnsFalseForInSegIfAnySegmentRuleFailsToMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-and");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-and) if all rules matches")]
    public async Task ReturnsTrueForInSegSegmentAndIfAllRulesMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.in-seg.segment-and", new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-or) if any segment rule matches (lookup)")]
    public async Task ReturnsTrueForInSegSegmentOrIfAnySegmentRuleMatchesLookup()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" }, [""] = new ContextProperties { ["domain"] = "example.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-seg.segment-or");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-or) if any segment rule matches (prop)")]
    public async Task ReturnsTrueForInSegSegmentOrIfAnySegmentRuleMatchesProp()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.in-seg.segment-or", new ContextSet { ["user"] = new ContextProperties { ["key"] = "nobody" }, [""] = new ContextProperties { ["domain"] = "gmail.com" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG when the segment rule doesn't match")]
    public async Task ReturnsTrueForNotInSegWhenTheSegmentRuleDoesnTMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-segment.negative");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG when the segment rule matches")]
    public async Task ReturnsFalseForNotInSegWhenTheSegmentRuleMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.in-segment.negative", new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG if any segment rule matches")]
    public async Task ReturnsFalseForNotInSegIfAnySegmentRuleMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-segment.multiple-criteria.negative");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG if no segment rule matches")]
    public async Task ReturnsTrueForNotInSegIfNoSegmentRuleMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.in-segment.multiple-criteria.negative", new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" }, [""] = new ContextProperties { ["domain"] = "something.com" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG (segment-and) if not segment rule fails to match")]
    public async Task ReturnsTrueForNotInSegSegmentAndIfNotSegmentRuleFailsToMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.not-in-seg.segment-and");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-and) if not segment rule fails to match")]
    public async Task ReturnsTrueForInSegSegmentAndIfNotSegmentRuleFailsToMatch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.in-seg.segment-and", new ContextSet { ["user"] = new ContextProperties { ["key"] = "josh" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG (segment-and) if segment rules matches")]
    public async Task ReturnsFalseForNotInSegSegmentAndIfSegmentRulesMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" }, [""] = new ContextProperties { ["domain"] = "prefab.cloud" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.not-in-seg.segment-and");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG (segment-or) if no segment rule matches")]
    public async Task ReturnsTrueForNotInSegSegmentOrIfNoSegmentRuleMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.not-in-seg.segment-or", new ContextSet { ["user"] = new ContextProperties { ["key"] = "nobody" }, [""] = new ContextProperties { ["domain"] = "example.com" } });
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG (segment-or) if one segment rule matches (prop)")]
    public async Task ReturnsFalseForNotInSegSegmentOrIfOneSegmentRuleMatchesProp()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "nobody" }, [""] = new ContextProperties { ["domain"] = "gmail.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.not-in-seg.segment-or");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG (segment-or) if one segment rule matches (lookup)")]
    public async Task ReturnsFalseForNotInSegSegmentOrIfOneSegmentRuleMatchesLookup()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.not-in-seg.segment-or", new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" }, [""] = new ContextProperties { ["domain"] = "example.com" } });
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE rule when the given prop represents a date (string) before the rule's time")]
    public async Task ReturnsTrueForPropBeforeRuleWhenTheGivenPropRepresentsADateStringBeforeTheRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2024-11-01T00:00:00Z" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE rule when the given prop represents a date (number) before the rule's time")]
    public async Task ReturnsTrueForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberBeforeTheRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(1730419200000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop represents a date (number) exactly matching rule's time")]
    public async Task ReturnsFalseForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberExactlyMatchingRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(1733011200000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop represents a date (number) AFTER the rule's time")]
    public async Task ReturnsFalseForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberAfterTheRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2025-01-01T00:00:00Z" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop won't parse as a date")]
    public async Task ReturnsFalseForPropBeforeRuleWhenTheGivenPropWonTParseAsADate()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "not a date" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule using current-time relative to 2050-01-01")]
    public async Task ReturnsFalseForPropBeforeRuleUsingCurrentTimeRelativeTo20500101()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.before.current-time");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_AFTER rule when the given prop represents a date (string) after the rule's time")]
    public async Task ReturnsTrueForPropAfterRuleWhenTheGivenPropRepresentsADateStringAfterTheRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2025-01-01T00:00:00Z" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_AFTER rule when the given prop represents a date (number) after the rule's time")]
    public async Task ReturnsTrueForPropAfterRuleWhenTheGivenPropRepresentsADateNumberAfterTheRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(1735689600000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER rule when the given prop represents a date (number) exactly matching rule's time")]
    public async Task ReturnsFalseForPropAfterRuleWhenTheGivenPropRepresentsADateNumberExactlyMatchingRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(1733011200000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop represents a date (number) BEFORE the rule's time")]
    public async Task ReturnsFalseForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberBeforeTheRuleSTime()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2024-01-01T00:00:00Z" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER rule when the given prop won't parse as a date")]
    public async Task ReturnsFalseForPropAfterRuleWhenTheGivenPropWonTParseAsADate()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "not a date" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER rule using current-time relative to 2025-01-01")]
    public async Task ReturnsFalseForPropAfterRuleUsingCurrentTimeRelativeTo20250101()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.after.current-time");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN rule when the given prop is less than the rule's value")]
    public async Task ReturnsTrueForPropLessThanRuleWhenTheGivenPropIsLessThanTheRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(20L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN rule when the given prop is less than the rule's value (float)")]
    public async Task ReturnsTrueForPropLessThanRuleWhenTheGivenPropIsLessThanTheRuleSValueFloat()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueDouble(20.5d) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN rule when the given prop is equal to rule's value")]
    public async Task ReturnsFalseForPropLessThanRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(30L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN rule when the given prop a string")]
    public async Task ReturnsFalseForPropLessThanRuleWhenTheGivenPropAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = "20" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN_OR_EQUAL rule when the given prop is less than the rule's value")]
    public async Task ReturnsTrueForPropLessThanOrEqualRuleWhenTheGivenPropIsLessThanTheRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(20L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than-or-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN_OR_EQUAL rule when the given prop is less than the rule's value (float)")]
    public async Task ReturnsTrueForPropLessThanOrEqualRuleWhenTheGivenPropIsLessThanTheRuleSValueFloat()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueDouble(20.5d) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than-or-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN_OR_EQUAL rule when the given prop is equal to rule's value")]
    public async Task ReturnsFalseForPropLessThanOrEqualRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(30L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than-or-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN_OR_EQUAL rule when the given prop a string")]
    public async Task ReturnsFalseForPropLessThanOrEqualRuleWhenTheGivenPropAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = "20" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.less-than-or-equal");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's value")]
    public async Task ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(100L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's value (float)")]
    public async Task ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSValueFloat()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueDouble(30.5d) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's float value (float)")]
    public async Task ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSFloatValueFloat()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueDouble(32.7d) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than.double");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's float value (integer)")]
    public async Task ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSFloatValueInteger()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(32L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than.double");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN rule when the given prop is equal to rule's value")]
    public async Task ReturnsFalseForPropGreaterThanRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(30L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN rule when the given prop a string")]
    public async Task ReturnsFalseForPropGreaterThanRuleWhenTheGivenPropAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = "100" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN_OR_EQUAL rule when the given prop is greater than the rule's value")]
    public async Task ReturnsTrueForPropGreaterThanOrEqualRuleWhenTheGivenPropIsGreaterThanTheRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(30L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than-or-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN_OR_EQUAL rule when the given prop is greater than the rule's value (float)")]
    public async Task ReturnsTrueForPropGreaterThanOrEqualRuleWhenTheGivenPropIsGreaterThanTheRuleSValueFloat()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueDouble(30.5d) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than-or-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN_OR_EQUAL rule when the given prop is equal to rule's value")]
    public async Task ReturnsTrueForPropGreaterThanOrEqualRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = new ContextValueLong(30L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than-or-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN_OR_EQUAL rule when the given prop a string")]
    public async Task ReturnsFalseForPropGreaterThanOrEqualRuleWhenTheGivenPropAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = "100" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than-or-equal");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_MATCHES rule when the given prop matches the regex")]
    public async Task ReturnsTrueForPropMatchesRuleWhenTheGivenPropMatchesTheRegex()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "aaaaaab" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES rule when the given prop does not match the regex")]
    public async Task ReturnsFalseForPropMatchesRuleWhenTheGivenPropDoesNotMatchTheRegex()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "aa" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_MATCH rule when the given prop does not match the regex")]
    public async Task ReturnsTrueForPropDoesNotMatchRuleWhenTheGivenPropDoesNotMatchTheRegex()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "b" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH rule when the given prop matches the regex")]
    public async Task ReturnsFalseForPropDoesNotMatchRuleWhenTheGivenPropMatchesTheRegex()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "aabb" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_MATCHES when the regex matches part of the prop (unanchored search)")]
    public async Task ReturnsTrueForPropMatchesWhenTheRegexMatchesPartOfThePropUnanchoredSearch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "xaab" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH when the regex matches part of the prop (unanchored search)")]
    public async Task ReturnsFalseForPropDoesNotMatchWhenTheRegexMatchesPartOfThePropUnanchoredSearch()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "xaab" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_MATCHES with an anchored regex when the whole prop matches")]
    public async Task ReturnsTrueForPropMatchesWithAnAnchoredRegexWhenTheWholePropMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "123" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches.anchored");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES with an anchored regex when the prop is too long")]
    public async Task ReturnsFalseForPropMatchesWithAnAnchoredRegexWhenThePropIsTooLong()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "1234" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES with an anchored regex when the prop has a non-matching character")]
    public async Task ReturnsFalseForPropMatchesWithAnAnchoredRegexWhenThePropHasANonMatchingCharacter()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "12a" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES when the prop is not a string")]
    public async Task ReturnsFalseForPropMatchesWhenThePropIsNotAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = new ContextValueLong(123L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES when the prop is missing")]
    public async Task ReturnsFalseForPropMatchesWhenThePropIsMissing()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_MATCH with an anchored regex when the prop is too long")]
    public async Task ReturnsTrueForPropDoesNotMatchWithAnAnchoredRegexWhenThePropIsTooLong()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "1234" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match.anchored");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_MATCH with an anchored regex when the prop has a non-matching character")]
    public async Task ReturnsTrueForPropDoesNotMatchWithAnAnchoredRegexWhenThePropHasANonMatchingCharacter()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "12a" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match.anchored");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH with an anchored regex when the whole prop matches")]
    public async Task ReturnsFalseForPropDoesNotMatchWithAnAnchoredRegexWhenTheWholePropMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "123" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH when the prop is not a string")]
    public async Task ReturnsFalseForPropDoesNotMatchWhenThePropIsNotAString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = new ContextValueLong(123L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH when the prop is missing")]
    public async Task ReturnsFalseForPropDoesNotMatchWhenThePropIsMissing()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match.anchored");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES when the stored regex is invalid")]
    public async Task ReturnsFalseForPropMatchesWhenTheStoredRegexIsInvalid()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "(" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.matches.invalid-pattern");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH when the stored regex is invalid")]
    public async Task ReturnsFalseForPropDoesNotMatchWhenTheStoredRegexIsInvalid()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "abc" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.does-not-match.invalid-pattern");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG when the segment's PROP_DOES_NOT_MATCH criterion holds")]
    public async Task ReturnsTrueForInSegWhenTheSegmentSPropDoesNotMatchCriterionHolds()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "abc" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-segment.does-not-match");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for IN_SEG when the segment's PROP_DOES_NOT_MATCH regex matches")]
    public async Task ReturnsFalseForInSegWhenTheSegmentSPropDoesNotMatchRegexMatches()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["code"] = "123" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-segment.does-not-match");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IN_SEG when the segment's PROP_DOES_NOT_MATCH prop is missing")]
    public async Task ReturnsFalseForInSegWhenTheSegmentSPropDoesNotMatchPropIsMissing()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["key"] = "michael" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-segment.does-not-match");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is a non-empty string")]
    public async Task ReturnsTrueForIsPresentRuleWhenTheGivenPropIsANonEmptyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["id"] = "abc" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is an empty string")]
    public async Task ReturnsTrueForIsPresentRuleWhenTheGivenPropIsAnEmptyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["id"] = "" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is the integer zero")]
    public async Task ReturnsTrueForIsPresentRuleWhenTheGivenPropIsTheIntegerZero()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["id"] = new ContextValueLong(0L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is boolean false")]
    public async Task ReturnsTrueForIsPresentRuleWhenTheGivenPropIsBooleanFalse()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["id"] = false } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule when the given prop is null")]
    public async Task ReturnsFalseForIsPresentRuleWhenTheGivenPropIsNull()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties() });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule when the given prop key is missing from the context")]
    public async Task ReturnsFalseForIsPresentRuleWhenTheGivenPropKeyIsMissingFromTheContext()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "bob" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule when no contexts are provided at all")]
    public async Task ReturnsFalseForIsPresentRuleWhenNoContextsAreProvidedAtAll()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.IsFeatureEnabled("feature-flag.is-present");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IS_NOT_PRESENT rule when the given prop is a non-empty string")]
    public async Task ReturnsFalseForIsNotPresentRuleWhenTheGivenPropIsANonEmptyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["id"] = "abc" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-not-present");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IS_NOT_PRESENT rule when the given prop is null")]
    public async Task ReturnsTrueForIsNotPresentRuleWhenTheGivenPropIsNull()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties() });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-not-present");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IS_NOT_PRESENT rule when the given prop key is missing from the context")]
    public async Task ReturnsTrueForIsNotPresentRuleWhenTheGivenPropKeyIsMissingFromTheContext()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["name"] = "bob" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-not-present");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule on a nested path when the nested prop is set")]
    public async Task ReturnsTrueForIsPresentRuleOnANestedPathWhenTheNestedPropIsSet()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["organization"] = new ContextProperties { ["domain"] = "example.com" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present-nested");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule on a nested path when the nested key is missing but the parent context exists")]
    public async Task ReturnsFalseForIsPresentRuleOnANestedPathWhenTheNestedKeyIsMissingButTheParentContextExists()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["organization"] = new ContextProperties { ["name"] = "Acme Inc" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present-nested");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule on a nested path when the parent context is entirely absent")]
    public async Task ReturnsFalseForIsPresentRuleOnANestedPathWhenTheParentContextIsEntirelyAbsent()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["id"] = "abc" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-present-nested");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_EQUAL rule when the given prop equals the version")]
    public async Task ReturnsTrueForPropSemverEqualRuleWhenTheGivenPropEqualsTheVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL rule when the given prop does not equal the version")]
    public async Task ReturnsFalseForPropSemverEqualRuleWhenTheGivenPropDoesNotEqualTheVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_EQUAL rule when the given prop is an abbreviated major.minor version")]
    public async Task ReturnsTrueForPropSemverEqualRuleWhenTheGivenPropIsAnAbbreviatedMajorMinorVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_LESS_THAN rule when the given prop is less than 2.0.0")]
    public async Task ReturnsTrueForPropSemverLessThanRuleWhenTheGivenPropIsLessThan200()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "1.5.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-less-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_LESS_THAN rule when the given prop equals the version")]
    public async Task ReturnsFalseForPropSemverLessThanRuleWhenTheGivenPropEqualsTheVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-less-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_LESS_THAN rule when the given prop is greater than the version")]
    public async Task ReturnsFalseForPropSemverLessThanRuleWhenTheGivenPropIsGreaterThanTheVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.2.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-less-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_GREATER_THAN rule when the given prop is greater than 2.0.0")]
    public async Task ReturnsTrueForPropSemverGreaterThanRuleWhenTheGivenPropIsGreaterThan200()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.5.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-greater-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_GREATER_THAN rule when the given prop equals the version")]
    public async Task ReturnsFalseForPropSemverGreaterThanRuleWhenTheGivenPropEqualsTheVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-greater-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL rule when the given prop is less than the version")]
    public async Task ReturnsFalseForPropSemverEqualRuleWhenTheGivenPropIsLessThanTheVersion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "0.0.5" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-greater-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_IS_ONE_OF [\"true\"] when the context value is boolean true")]
    public async Task ReturnsTrueForPropIsOneOfTrueWhenTheContextValueIsBooleanTrue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["beta"] = true } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.bool");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_IS_ONE_OF [\"true\"] when the context value is boolean false")]
    public async Task ReturnsFalseForPropIsOneOfTrueWhenTheContextValueIsBooleanFalse()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["beta"] = false } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.bool");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_IS_ONE_OF [\"true\"] when the context value is the string \"true\"")]
    public async Task ReturnsTrueForPropIsOneOfTrueWhenTheContextValueIsTheStringTrue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["beta"] = "true" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.bool");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_IS_NOT_ONE_OF [\"true\"] when the context value is boolean true")]
    public async Task ReturnsFalseForPropIsNotOneOfTrueWhenTheContextValueIsBooleanTrue()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["beta"] = true } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-not-one-of.bool");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_IS_ONE_OF number strings when the context value is the integer 1")]
    public async Task ReturnsTrueForPropIsOneOfNumberStringsWhenTheContextValueIsTheInteger1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(1L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.number");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_IS_ONE_OF number strings when the context value is the number 1.5")]
    public async Task ReturnsTrueForPropIsOneOfNumberStringsWhenTheContextValueIsTheNumber15()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueDouble(1.5d) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.number");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_IS_ONE_OF number strings when the context value is the integer 1234567")]
    public async Task ReturnsTrueForPropIsOneOfNumberStringsWhenTheContextValueIsTheInteger1234567()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(1234567L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.number");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_IS_ONE_OF number strings when the context value is the integer 2")]
    public async Task ReturnsFalseForPropIsOneOfNumberStringsWhenTheContextValueIsTheInteger2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(2L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.is-one-of.number");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_ENDS_WITH_ONE_OF [\"3\"] when the context value is the integer 123")]
    public async Task ReturnsTrueForPropEndsWithOneOf3WhenTheContextValueIsTheInteger123()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(123L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.ends-with-one-of.number");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_ENDS_WITH_ONE_OF [\"3\"] when the context value is the integer 124")]
    public async Task ReturnsFalseForPropEndsWithOneOf3WhenTheContextValueIsTheInteger124()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(124L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.ends-with-one-of.number");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IN_INT_RANGE [0, 10) when the context value is the start bound 0")]
    public async Task ReturnsTrueForInIntRange010WhenTheContextValueIsTheStartBound0()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(0L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-int-range");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IN_INT_RANGE [0, 10) when the context value is 9")]
    public async Task ReturnsTrueForInIntRange010WhenTheContextValueIs9()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(9L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-int-range");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for IN_INT_RANGE [0, 10) when the context value is the end bound 10")]
    public async Task ReturnsFalseForInIntRange010WhenTheContextValueIsTheEndBound10()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(10L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-int-range");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for IN_INT_RANGE [0, 10) when the context value is -1")]
    public async Task ReturnsFalseForInIntRange010WhenTheContextValueIs1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(-1L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-int-range");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for IN_INT_RANGE with no end when the context value is 5000000000")]
    public async Task ReturnsTrueForInIntRangeWithNoEndWhenTheContextValueIs5000000000()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(5000000000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-int-range.open-end");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for IN_INT_RANGE with no start when the context value is -5")]
    public async Task ReturnsTrueForInIntRangeWithNoStartWhenTheContextValueIs5()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["n"] = new ContextValueLong(-5L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.in-int-range.open-start");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL 2.0.0 when the given prop is the prerelease 2.0.0-beta.1")]
    public async Task ReturnsFalseForPropSemverEqual200WhenTheGivenPropIsThePrerelease200Beta1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0-beta.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_LESS_THAN 2.0.0 when the given prop is the prerelease 2.0.0-beta.1")]
    public async Task ReturnsTrueForPropSemverLessThan200WhenTheGivenPropIsThePrerelease200Beta1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0-beta.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-less-than");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_GREATER_THAN 2.0.0 when the given prop is the prerelease 2.0.0-beta.1")]
    public async Task ReturnsFalseForPropSemverGreaterThan200WhenTheGivenPropIsThePrerelease200Beta1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0-beta.1" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-greater-than");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_EQUAL 2.0.0 when the given prop differs only by build metadata")]
    public async Task ReturnsTrueForPropSemverEqual200WhenTheGivenPropDiffersOnlyByBuildMetadata()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0+build.5" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_LESS_THAN 2.0.0-beta when the given prop is the prerelease 2.0.0-alpha")]
    public async Task ReturnsTrueForPropSemverLessThan200BetaWhenTheGivenPropIsThePrerelease200Alpha()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2.0.0-alpha" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-less-than.prerelease");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_EQUAL 2.0.0 when the given prop is the abbreviated major version 2")]
    public async Task ReturnsTrueForPropSemverEqual200WhenTheGivenPropIsTheAbbreviatedMajorVersion2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "2" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL 2.0.0 when the given prop has a v prefix")]
    public async Task ReturnsFalseForPropSemverEqual200WhenTheGivenPropHasAVPrefix()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["app"] = new ContextProperties { ["version"] = "v2.0.0" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.semver-equal");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE when the given prop is a date-only string")]
    public async Task ReturnsFalseForPropBeforeWhenTheGivenPropIsADateOnlyString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2024-11-01" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE when the given prop is an RFC3339 string without an offset")]
    public async Task ReturnsFalseForPropBeforeWhenTheGivenPropIsAnRfc3339StringWithoutAnOffset()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2024-11-01T00:00:00" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE when the given prop is a free-form date string")]
    public async Task ReturnsFalseForPropBeforeWhenTheGivenPropIsAFreeFormDateString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "Nov 1 2024" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE when the given prop is an RFC3339 string with a numeric offset")]
    public async Task ReturnsTrueForPropBeforeWhenTheGivenPropIsAnRfc3339StringWithANumericOffset()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2024-11-01T02:00:00+02:00" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE when the given prop is an all-digit epoch-ms string")]
    public async Task ReturnsTrueForPropBeforeWhenTheGivenPropIsAnAllDigitEpochMsString()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "1730419200000" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE an RFC3339 criterion when the given prop is an earlier epoch-ms number")]
    public async Task ReturnsTrueForPropBeforeAnRfc3339CriterionWhenTheGivenPropIsAnEarlierEpochMsNumber()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(1730419200000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before.rfc3339-criterion");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE an RFC3339 criterion when the given prop is a later RFC3339 string")]
    public async Task ReturnsFalseForPropBeforeAnRfc3339CriterionWhenTheGivenPropIsALaterRfc3339String()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = "2025-01-01T00:00:00Z" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.before.rfc3339-criterion");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER an all-digit criterion \"999\" when the given prop is 500")]
    public async Task ReturnsFalseForPropAfterAnAllDigitCriterion999WhenTheGivenPropIs500()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(500L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after.numeric-string-criterion");
        Assert.False(actual);
    }

    [Fact(DisplayName = "returns true for PROP_AFTER an all-digit criterion \"999\" when the given prop is 1000")]
    public async Task ReturnsTrueForPropAfterAnAllDigitCriterion999WhenTheGivenPropIs1000()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["creation_date"] = new ContextValueLong(1000L) } });
        var actual = scoped.IsFeatureEnabled("feature-flag.after.numeric-string-criterion");
        Assert.True(actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN 30 when the given prop is a numeric string above the threshold")]
    public async Task ReturnsFalseForPropGreaterThan30WhenTheGivenPropIsANumericStringAboveTheThreshold()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var scoped = client.WithContext(new ContextSet { ["user"] = new ContextProperties { ["age"] = "35" } });
        var actual = scoped.IsFeatureEnabled("feature-flag.greater-than");
        Assert.False(actual);
    }
}
