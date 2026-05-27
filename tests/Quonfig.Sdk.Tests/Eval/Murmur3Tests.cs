using System;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Xunit;

namespace Quonfig.Sdk.Tests.Eval;

/// <summary>
/// Cross-SDK contract tests for <see cref="Murmur3"/>. The 32-bit Murmur3 variant with seed 0
/// is the load-bearing primitive for weighted-value bucketing — if this hash diverges from
/// sdk-java's <c>Hashing.murmur3_32_fixed()</c> or sdk-go's <c>spaolacci/murmur3</c>, then the
/// same <c>(configKey, trackingId)</c> pair will land in a different bucket across SDKs and
/// every A/B test that uses weighted variants silently splits.
/// </summary>
public sealed class Murmur3Tests
{
    // ---- Canonical Murmur3-32 vectors with seed=0 (matches Java's murmur3_32_fixed and Go's spaolacci/murmur3) ----

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0x3c2569b2u)]
    [InlineData("ab", 0x9bbfd75fu)]
    [InlineData("abc", 0xb3dd93fau)]
    [InlineData("abcd", 0x43ed676au)]
    [InlineData("Hello, world!", 0xc0363e43u)]
    public void Hash32_KnownVectors_MatchReference(string input, uint expected)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        Murmur3.Hash32(bytes, 0).Should().Be(expected, "input \"{0}\" must hash identically across SDKs", input);
    }

    // ---- Integration-test-data round trip: tracking_ids → weighted-value bucket ----
    //
    // From integration-test-data/data/integration-tests/feature-flags/feature-flag.weighted.json:
    //   buckets: [1000(=1), 2000(=3), 97000(=2)], totalWeight = 100000
    //   running cumulative: 1000, 3000, 100000
    //
    // From integration-test-data/tests/eval/get_weighted_values.yaml:
    //   "a72c15f5" → value 1 → bucket 0 → threshold ≤ 1000        → fraction ≤ 0.01
    //   "8f414100" → value 3 → bucket 1 → 1000 < threshold ≤ 3000 → 0.01 < fraction ≤ 0.03
    //   "92a202f2" → value 2 → bucket 2 → 3000 < threshold        → fraction > 0.03
    //
    // If our Murmur3 doesn't produce the same fraction sdk-java/sdk-go produce for these inputs,
    // these tracking_ids will land in different buckets than the YAML asserts.

    [Theory]
    [InlineData("a72c15f5", 0.0, 0.01)]
    [InlineData("8f414100", 0.01, 0.03)]
    [InlineData("92a202f2", 0.03, 1.0)]
    public void HashZeroToOne_IntegrationTestFixtures_PickCorrectBucket(string trackingId, double lo, double hi)
    {
        // configKey + propertyValue, matching the cross-SDK contract.
        double fraction = Murmur3.HashZeroToOne("feature-flag.weighted" + trackingId);
        fraction.Should().BeGreaterThanOrEqualTo(lo).And.BeLessThanOrEqualTo(hi);
    }

    // ---- Distribution ----

    [Fact]
    public void HashZeroToOne_Distribution_50_50_IsWithin5PercentOfHalf()
    {
        // Cross-check against sdk-java's distribution_50_50_isWithin5PercentOfHalf test —
        // 10k inputs hashed and bucketed two ways, each bucket within 5% of half.
        const int n = 10_000;
        const int slack = (int)(n * 0.05);
        int half = n / 2;
        int[] counts = new int[2];
        for (int i = 0; i < n; i++)
        {
            double fraction = Murmur3.HashZeroToOne("flag.coin" + "u-" + i);
            int bucket = (fraction * 2.0) >= 1.0 ? 1 : 0;
            counts[bucket]++;
        }

        Math.Abs(counts[0] - half).Should().BeLessThanOrEqualTo(slack);
        Math.Abs(counts[1] - half).Should().BeLessThanOrEqualTo(slack);
    }

    [Fact]
    public void Hash32_NonEmptySeed_DiffersFromZeroSeed()
    {
        // Sanity: the seed parameter actually affects output (so a future caller can request
        // a non-zero seed without it silently no-op'ing). Cross-SDK callers always use seed=0,
        // so this is the only test that exercises a non-zero seed at all.
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
        uint zero = Murmur3.Hash32(bytes, 0);
        uint one = Murmur3.Hash32(bytes, 1);
        zero.Should().NotBe(one);
    }
}
