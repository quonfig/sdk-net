// AUTO-GENERATED from integration-test-data/tests/eval/enabled.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public class EnabledTests
{

    [Fact(DisplayName = "returns the correct value for a simple flag")]
    public void ReturnsTheCorrectValueForASimpleFlag()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.simple", TestSetup.Map());
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "always returns false for a non-boolean flag")]
    public void AlwaysReturnsFalseForANonBooleanFlag()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.integer", TestSetup.Map());
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for a PROP_IS_ONE_OF rule when any prop matches")]
    public void ReturnsTrueForAPropIsOneOfRuleWhenAnyPropMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.properties.positive", TestSetup.Map("", TestSetup.Map("name", "michael", "domain", "something.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for a PROP_IS_ONE_OF rule when no prop matches")]
    public void ReturnsFalseForAPropIsOneOfRuleWhenNoPropMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.properties.positive", TestSetup.Map("", TestSetup.Map("name", "lauren", "domain", "something.com")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for a PROP_IS_NOT_ONE_OF rule when any prop doesn't match")]
    public void ReturnsTrueForAPropIsNotOneOfRuleWhenAnyPropDoesnTMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.properties.negative", TestSetup.Map("", TestSetup.Map("name", "lauren", "domain", "prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for a PROP_IS_NOT_ONE_OF rule when all props match")]
    public void ReturnsFalseForAPropIsNotOneOfRuleWhenAllPropsMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.properties.negative", TestSetup.Map("", TestSetup.Map("name", "michael", "domain", "prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_ENDS_WITH_ONE_OF rule when the given prop has a matching suffix")]
    public void ReturnsTrueForPropEndsWithOneOfRuleWhenTheGivenPropHasAMatchingSuffix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.ends-with-one-of.positive", TestSetup.Map("", TestSetup.Map("email", "jeff@prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_ENDS_WITH_ONE_OF rule when the given prop doesn't have a matching suffix")]
    public void ReturnsFalseForPropEndsWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSuffix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.ends-with-one-of.positive", TestSetup.Map("", TestSetup.Map("email", "jeff@test.com")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_END_WITH_ONE_OF rule when the given prop doesn't have a matching suffix")]
    public void ReturnsTrueForPropDoesNotEndWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSuffix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.ends-with-one-of.negative", TestSetup.Map("", TestSetup.Map("email", "michael@test.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_END_WITH_ONE_OF rule when the given prop has a matching suffix")]
    public void ReturnsFalseForPropDoesNotEndWithOneOfRuleWhenTheGivenPropHasAMatchingSuffix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.ends-with-one-of.negative", TestSetup.Map("", TestSetup.Map("email", "michael@prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_STARTS_WITH_ONE_OF rule when the given prop has a matching prefix")]
    public void ReturnsTrueForPropStartsWithOneOfRuleWhenTheGivenPropHasAMatchingPrefix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.starts-with-one-of.positive", TestSetup.Map("user", TestSetup.Map("email", "foo@prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_STARTS_WITH_ONE_OF rule when the given prop doesn't have a matching prefix")]
    public void ReturnsFalseForPropStartsWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingPrefix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.starts-with-one-of.positive", TestSetup.Map("user", TestSetup.Map("email", "notfoo@prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_START_WITH_ONE_OF rule when the given prop doesn't have a matching prefix")]
    public void ReturnsTrueForPropDoesNotStartWithOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingPrefix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.starts-with-one-of.negative", TestSetup.Map("user", TestSetup.Map("email", "notfoo@prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_START_WITH_ONE_OF rule when the given prop has a matching prefix")]
    public void ReturnsFalseForPropDoesNotStartWithOneOfRuleWhenTheGivenPropHasAMatchingPrefix()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.starts-with-one-of.negative", TestSetup.Map("user", TestSetup.Map("email", "foo@prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_CONTAINS_ONE_OF rule when the given prop has a matching substring")]
    public void ReturnsTrueForPropContainsOneOfRuleWhenTheGivenPropHasAMatchingSubstring()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.contains-one-of.positive", TestSetup.Map("user", TestSetup.Map("email", "somefoo@prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_CONTAINS_ONE_OF rule when the given prop doesn't have a matching substring")]
    public void ReturnsFalseForPropContainsOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSubstring()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.contains-one-of.positive", TestSetup.Map("user", TestSetup.Map("email", "info@prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_CONTAIN_ONE_OF rule when the given prop doesn't have a matching substring")]
    public void ReturnsTrueForPropDoesNotContainOneOfRuleWhenTheGivenPropDoesnTHaveAMatchingSubstring()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.contains-one-of.negative", TestSetup.Map("user", TestSetup.Map("email", "info@prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_CONTAIN_ONE_OF rule when the given prop has a matching substring")]
    public void ReturnsFalseForPropDoesNotContainOneOfRuleWhenTheGivenPropHasAMatchingSubstring()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.contains-one-of.negative", TestSetup.Map("user", TestSetup.Map("email", "notfoo@prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG when the segment rule matches")]
    public void ReturnsTrueForInSegWhenTheSegmentRuleMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-segment.positive", TestSetup.Map("user", TestSetup.Map("key", "lauren")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for IN_SEG when the segment rule doesn't match")]
    public void ReturnsFalseForInSegWhenTheSegmentRuleDoesnTMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-segment.positive", TestSetup.Map("user", TestSetup.Map("key", "josh")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for IN_SEG if any segment rule fails to match")]
    public void ReturnsFalseForInSegIfAnySegmentRuleFailsToMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("user", TestSetup.Map("key", "josh"), "", TestSetup.Map("domain", "prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-and) if all rules matches")]
    public void ReturnsTrueForInSegSegmentAndIfAllRulesMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("user", TestSetup.Map("key", "michael"), "", TestSetup.Map("domain", "prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-or) if any segment rule matches (lookup)")]
    public void ReturnsTrueForInSegSegmentOrIfAnySegmentRuleMatchesLookup()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-or", TestSetup.Map("user", TestSetup.Map("key", "michael"), "", TestSetup.Map("domain", "example.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-or) if any segment rule matches (prop)")]
    public void ReturnsTrueForInSegSegmentOrIfAnySegmentRuleMatchesProp()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-or", TestSetup.Map("user", TestSetup.Map("key", "nobody"), "", TestSetup.Map("domain", "gmail.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG when the segment rule doesn't match")]
    public void ReturnsTrueForNotInSegWhenTheSegmentRuleDoesnTMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-segment.negative", TestSetup.Map("user", TestSetup.Map("key", "josh")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG when the segment rule matches")]
    public void ReturnsFalseForNotInSegWhenTheSegmentRuleMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-segment.negative", TestSetup.Map("user", TestSetup.Map("key", "michael")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG if any segment rule matches")]
    public void ReturnsFalseForNotInSegIfAnySegmentRuleMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-segment.multiple-criteria.negative", TestSetup.Map("user", TestSetup.Map("key", "josh"), "", TestSetup.Map("domain", "prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG if no segment rule matches")]
    public void ReturnsTrueForNotInSegIfNoSegmentRuleMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-segment.multiple-criteria.negative", TestSetup.Map("user", TestSetup.Map("key", "josh"), "", TestSetup.Map("domain", "something.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG (segment-and) if not segment rule fails to match")]
    public void ReturnsTrueForNotInSegSegmentAndIfNotSegmentRuleFailsToMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.not-in-seg.segment-and", TestSetup.Map("user", TestSetup.Map("key", "josh"), "", TestSetup.Map("domain", "prefab.cloud")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IN_SEG (segment-and) if not segment rule fails to match")]
    public void ReturnsTrueForInSegSegmentAndIfNotSegmentRuleFailsToMatch()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.in-seg.segment-and", TestSetup.Map("user", TestSetup.Map("key", "josh"), "", TestSetup.Map("domain", "prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG (segment-and) if segment rules matches")]
    public void ReturnsFalseForNotInSegSegmentAndIfSegmentRulesMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.not-in-seg.segment-and", TestSetup.Map("user", TestSetup.Map("key", "michael"), "", TestSetup.Map("domain", "prefab.cloud")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for NOT_IN_SEG (segment-or) if no segment rule matches")]
    public void ReturnsTrueForNotInSegSegmentOrIfNoSegmentRuleMatches()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.not-in-seg.segment-or", TestSetup.Map("user", TestSetup.Map("key", "nobody"), "", TestSetup.Map("domain", "example.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG (segment-or) if one segment rule matches (prop)")]
    public void ReturnsFalseForNotInSegSegmentOrIfOneSegmentRuleMatchesProp()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.not-in-seg.segment-or", TestSetup.Map("user", TestSetup.Map("key", "nobody"), "", TestSetup.Map("domain", "gmail.com")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for NOT_IN_SEG (segment-or) if one segment rule matches (lookup)")]
    public void ReturnsFalseForNotInSegSegmentOrIfOneSegmentRuleMatchesLookup()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.not-in-seg.segment-or", TestSetup.Map("user", TestSetup.Map("key", "michael"), "", TestSetup.Map("domain", "example.com")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE rule when the given prop represents a date (string) before the rule's time")]
    public void ReturnsTrueForPropBeforeRuleWhenTheGivenPropRepresentsADateStringBeforeTheRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.before", TestSetup.Map("user", TestSetup.Map("creation_date", "2024-11-01T00:00:00Z")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_BEFORE rule when the given prop represents a date (number) before the rule's time")]
    public void ReturnsTrueForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberBeforeTheRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.before", TestSetup.Map("user", TestSetup.Map("creation_date", 1730419200000L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop represents a date (number) exactly matching rule's time")]
    public void ReturnsFalseForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberExactlyMatchingRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.before", TestSetup.Map("user", TestSetup.Map("creation_date", 1733011200000L)));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop represents a date (number) AFTER the rule's time")]
    public void ReturnsFalseForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberAfterTheRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.before", TestSetup.Map("user", TestSetup.Map("creation_date", "2025-01-01T00:00:00Z")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop won't parse as a date")]
    public void ReturnsFalseForPropBeforeRuleWhenTheGivenPropWonTParseAsADate()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.before", TestSetup.Map("user", TestSetup.Map("creation_date", "not a date")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule using current-time relative to 2050-01-01")]
    public void ReturnsFalseForPropBeforeRuleUsingCurrentTimeRelativeTo20500101()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.before.current-time", TestSetup.Map());
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_AFTER rule when the given prop represents a date (string) after the rule's time")]
    public void ReturnsTrueForPropAfterRuleWhenTheGivenPropRepresentsADateStringAfterTheRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.after", TestSetup.Map("user", TestSetup.Map("creation_date", "2025-01-01T00:00:00Z")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_AFTER rule when the given prop represents a date (number) after the rule's time")]
    public void ReturnsTrueForPropAfterRuleWhenTheGivenPropRepresentsADateNumberAfterTheRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.after", TestSetup.Map("user", TestSetup.Map("creation_date", 1735689600000L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER rule when the given prop represents a date (number) exactly matching rule's time")]
    public void ReturnsFalseForPropAfterRuleWhenTheGivenPropRepresentsADateNumberExactlyMatchingRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.after", TestSetup.Map("user", TestSetup.Map("creation_date", 1733011200000L)));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_BEFORE rule when the given prop represents a date (number) BEFORE the rule's time")]
    public void ReturnsFalseForPropBeforeRuleWhenTheGivenPropRepresentsADateNumberBeforeTheRuleSTime()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.after", TestSetup.Map("user", TestSetup.Map("creation_date", "2024-01-01T00:00:00Z")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER rule when the given prop won't parse as a date")]
    public void ReturnsFalseForPropAfterRuleWhenTheGivenPropWonTParseAsADate()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.after", TestSetup.Map("user", TestSetup.Map("creation_date", "not a date")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_AFTER rule using current-time relative to 2025-01-01")]
    public void ReturnsFalseForPropAfterRuleUsingCurrentTimeRelativeTo20250101()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.after.current-time", TestSetup.Map());
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN rule when the given prop is less than the rule's value")]
    public void ReturnsTrueForPropLessThanRuleWhenTheGivenPropIsLessThanTheRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than", TestSetup.Map("user", TestSetup.Map("age", 20L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN rule when the given prop is less than the rule's value (float)")]
    public void ReturnsTrueForPropLessThanRuleWhenTheGivenPropIsLessThanTheRuleSValueFloat()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than", TestSetup.Map("user", TestSetup.Map("age", 20.5d)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN rule when the given prop is equal to rule's value")]
    public void ReturnsFalseForPropLessThanRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than", TestSetup.Map("user", TestSetup.Map("age", 30L)));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN rule when the given prop a string")]
    public void ReturnsFalseForPropLessThanRuleWhenTheGivenPropAString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than", TestSetup.Map("user", TestSetup.Map("age", "20")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN_OR_EQUAL rule when the given prop is less than the rule's value")]
    public void ReturnsTrueForPropLessThanOrEqualRuleWhenTheGivenPropIsLessThanTheRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", 20L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_LESS_THAN_OR_EQUAL rule when the given prop is less than the rule's value (float)")]
    public void ReturnsTrueForPropLessThanOrEqualRuleWhenTheGivenPropIsLessThanTheRuleSValueFloat()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", 20.5d)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN_OR_EQUAL rule when the given prop is equal to rule's value")]
    public void ReturnsFalseForPropLessThanOrEqualRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", 30L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_LESS_THAN_OR_EQUAL rule when the given prop a string")]
    public void ReturnsFalseForPropLessThanOrEqualRuleWhenTheGivenPropAString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.less-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", "20")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's value")]
    public void ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than", TestSetup.Map("user", TestSetup.Map("age", 100L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's value (float)")]
    public void ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSValueFloat()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than", TestSetup.Map("user", TestSetup.Map("age", 30.5d)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's float value (float)")]
    public void ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSFloatValueFloat()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than.double", TestSetup.Map("user", TestSetup.Map("age", 32.7d)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN rule when the given prop is greater than the rule's float value (integer)")]
    public void ReturnsTrueForPropGreaterThanRuleWhenTheGivenPropIsGreaterThanTheRuleSFloatValueInteger()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than.double", TestSetup.Map("user", TestSetup.Map("age", 32L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN rule when the given prop is equal to rule's value")]
    public void ReturnsFalseForPropGreaterThanRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than", TestSetup.Map("user", TestSetup.Map("age", 30L)));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN rule when the given prop a string")]
    public void ReturnsFalseForPropGreaterThanRuleWhenTheGivenPropAString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than", TestSetup.Map("user", TestSetup.Map("age", "100")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN_OR_EQUAL rule when the given prop is greater than the rule's value")]
    public void ReturnsTrueForPropGreaterThanOrEqualRuleWhenTheGivenPropIsGreaterThanTheRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", 30L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN_OR_EQUAL rule when the given prop is greater than the rule's value (float)")]
    public void ReturnsTrueForPropGreaterThanOrEqualRuleWhenTheGivenPropIsGreaterThanTheRuleSValueFloat()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", 30.5d)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for PROP_GREATER_THAN_OR_EQUAL rule when the given prop is equal to rule's value")]
    public void ReturnsTrueForPropGreaterThanOrEqualRuleWhenTheGivenPropIsEqualToRuleSValue()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", 30L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_GREATER_THAN_OR_EQUAL rule when the given prop a string")]
    public void ReturnsFalseForPropGreaterThanOrEqualRuleWhenTheGivenPropAString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.greater-than-or-equal", TestSetup.Map("user", TestSetup.Map("age", "100")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_MATCHES rule when the given prop matches the regex")]
    public void ReturnsTrueForPropMatchesRuleWhenTheGivenPropMatchesTheRegex()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.matches", TestSetup.Map("user", TestSetup.Map("code", "aaaaaab")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_MATCHES rule when the given prop does not match the regex")]
    public void ReturnsFalseForPropMatchesRuleWhenTheGivenPropDoesNotMatchTheRegex()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.matches", TestSetup.Map("user", TestSetup.Map("code", "aa")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_DOES_NOT_MATCH rule when the given prop does not match the regex")]
    public void ReturnsTrueForPropDoesNotMatchRuleWhenTheGivenPropDoesNotMatchTheRegex()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.does-not-match", TestSetup.Map("user", TestSetup.Map("code", "b")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_DOES_NOT_MATCH rule when the given prop matches the regex")]
    public void ReturnsFalseForPropDoesNotMatchRuleWhenTheGivenPropMatchesTheRegex()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.does-not-match", TestSetup.Map("user", TestSetup.Map("code", "aabb")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is a non-empty string")]
    public void ReturnsTrueForIsPresentRuleWhenTheGivenPropIsANonEmptyString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map("user", TestSetup.Map("id", "abc")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is an empty string")]
    public void ReturnsTrueForIsPresentRuleWhenTheGivenPropIsAnEmptyString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map("user", TestSetup.Map("id", "")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is the integer zero")]
    public void ReturnsTrueForIsPresentRuleWhenTheGivenPropIsTheIntegerZero()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map("user", TestSetup.Map("id", 0L)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule when the given prop is boolean false")]
    public void ReturnsTrueForIsPresentRuleWhenTheGivenPropIsBooleanFalse()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map("user", TestSetup.Map("id", false)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule when the given prop is null")]
    public void ReturnsFalseForIsPresentRuleWhenTheGivenPropIsNull()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map("user", TestSetup.Map("id", null)));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule when the given prop key is missing from the context")]
    public void ReturnsFalseForIsPresentRuleWhenTheGivenPropKeyIsMissingFromTheContext()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map("user", TestSetup.Map("name", "bob")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule when no contexts are provided at all")]
    public void ReturnsFalseForIsPresentRuleWhenNoContextsAreProvidedAtAll()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present", TestSetup.Map());
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for IS_NOT_PRESENT rule when the given prop is a non-empty string")]
    public void ReturnsFalseForIsNotPresentRuleWhenTheGivenPropIsANonEmptyString()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-not-present", TestSetup.Map("user", TestSetup.Map("id", "abc")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for IS_NOT_PRESENT rule when the given prop is null")]
    public void ReturnsTrueForIsNotPresentRuleWhenTheGivenPropIsNull()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-not-present", TestSetup.Map("user", TestSetup.Map("id", null)));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IS_NOT_PRESENT rule when the given prop key is missing from the context")]
    public void ReturnsTrueForIsNotPresentRuleWhenTheGivenPropKeyIsMissingFromTheContext()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-not-present", TestSetup.Map("user", TestSetup.Map("name", "bob")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns true for IS_PRESENT rule on a nested path when the nested prop is set")]
    public void ReturnsTrueForIsPresentRuleOnANestedPathWhenTheNestedPropIsSet()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present-nested", TestSetup.Map("organization", TestSetup.Map("domain", "example.com")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule on a nested path when the nested key is missing but the parent context exists")]
    public void ReturnsFalseForIsPresentRuleOnANestedPathWhenTheNestedKeyIsMissingButTheParentContextExists()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present-nested", TestSetup.Map("organization", TestSetup.Map("name", "Acme Inc")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for IS_PRESENT rule on a nested path when the parent context is entirely absent")]
    public void ReturnsFalseForIsPresentRuleOnANestedPathWhenTheParentContextIsEntirelyAbsent()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.is-present-nested", TestSetup.Map("user", TestSetup.Map("id", "abc")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_EQUAL rule when the given prop equals the version")]
    public void ReturnsTrueForPropSemverEqualRuleWhenTheGivenPropEqualsTheVersion()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-equal", TestSetup.Map("app", TestSetup.Map("version", "2.0.0")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL rule when the given prop does not equal the version")]
    public void ReturnsFalseForPropSemverEqualRuleWhenTheGivenPropDoesNotEqualTheVersion()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-equal", TestSetup.Map("app", TestSetup.Map("version", "2.0.1")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL rule when the given prop is not a valid semver")]
    public void ReturnsFalseForPropSemverEqualRuleWhenTheGivenPropIsNotAValidSemver()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-equal", TestSetup.Map("app", TestSetup.Map("version", "2.0")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_LESS_THAN rule when the given prop is less than 2.0.0")]
    public void ReturnsTrueForPropSemverLessThanRuleWhenTheGivenPropIsLessThan200()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-less-than", TestSetup.Map("app", TestSetup.Map("version", "1.5.1")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_LESS_THAN rule when the given prop equals the version")]
    public void ReturnsFalseForPropSemverLessThanRuleWhenTheGivenPropEqualsTheVersion()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-less-than", TestSetup.Map("app", TestSetup.Map("version", "2.0.0")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_LESS_THAN rule when the given prop is greater than the version")]
    public void ReturnsFalseForPropSemverLessThanRuleWhenTheGivenPropIsGreaterThanTheVersion()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-less-than", TestSetup.Map("app", TestSetup.Map("version", "2.2.1")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns true for PROP_SEMVER_GREATER_THAN rule when the given prop is greater than 2.0.0")]
    public void ReturnsTrueForPropSemverGreaterThanRuleWhenTheGivenPropIsGreaterThan200()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-greater-than", TestSetup.Map("app", TestSetup.Map("version", "2.5.1")));
        Assert.Equal(true, actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_GREATER_THAN rule when the given prop equals the version")]
    public void ReturnsFalseForPropSemverGreaterThanRuleWhenTheGivenPropEqualsTheVersion()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-greater-than", TestSetup.Map("app", TestSetup.Map("version", "2.0.0")));
        Assert.Equal(false, actual);
    }

    [Fact(DisplayName = "returns false for PROP_SEMVER_EQUAL rule when the given prop is less than the version")]
    public void ReturnsFalseForPropSemverEqualRuleWhenTheGivenPropIsLessThanTheVersion()
    {
        object? actual = TestSetup.EnabledCase("feature-flag.semver-greater-than", TestSetup.Map("app", TestSetup.Map("version", "0.0.5")));
        Assert.Equal(false, actual);
    }
}
