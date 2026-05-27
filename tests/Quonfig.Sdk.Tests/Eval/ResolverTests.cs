using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Exceptions;
using Xunit;
using ValueType = Quonfig.Sdk.Eval.ValueType;

namespace Quonfig.Sdk.Tests.Eval;

/// <summary>
/// Resolver contract — ENV_VAR provisioning, weighted-value bucketing, AES-GCM decryption, and
/// env-var-string → typed coercion. Mirrors sdk-java's <c>ResolverTest</c> case-for-case so the
/// .NET SDK passes the same integration-test-data YAML cases. Where Java has a separate
/// <c>configRow</c> argument carrying the value-type, the .NET surface threads the configKey and
/// target <see cref="ValueType"/> in directly.
/// </summary>
public sealed class ResolverTests
{
    // Same fixture as AesGcmCompatTests / sdk-java's ResolverTest.
    private const string FixtureKeyHex =
        "c87ba22d8662282abe8a0e4651327b579cb64a454ab0f4c170b45b15f049a221";
    private const string FixtureCiphertext =
        "875247386844c18c58a97c--b307b97a8288ac9da3ce0cf2--7ab0c32e044869e355586ed653a435de";
    private const string FixturePlaintext = "hello.world";

    private static Value Provided(string lookup) =>
        new Value(ValueType.Provided, new ProvidedValue("ENV_VAR", lookup), false, null);

    private static Value WeightedInt(string hashByPropertyName, params (int weight, long value)[] entries)
    {
        var list = new List<WeightedVariant>();
        foreach (var (w, v) in entries)
        {
            list.Add(new WeightedVariant(w, new Value(ValueType.Int, v, false, null)));
        }
        return new Value(ValueType.WeightedValues, new WeightedValuesPayload(hashByPropertyName, list), false, null);
    }

    private static ContextSet UserCtx(string key, string val)
    {
        var ctx = new ContextSet
        {
            ["user"] = new ContextProperties()
        };
        ctx["user"][key] = val;
        return ctx;
    }

    // ----- Plain pass-through -----

    [Fact]
    public void Resolve_PlainStringValue_ReturnsUnchanged()
    {
        var r = new Resolver();
        var input = new Value(ValueType.String, "hello", false, null);
        var output = r.Resolve(input, "any.key", ValueType.String, new ContextSet());
        output.Should().BeSameAs(input);
    }

    // ----- ENV_VAR -----

    [Fact]
    public void Resolve_EnvVarSet_ReturnsCoercedValue()
    {
        var r = new Resolver(envLookup: name => name == "MY_VAR" ? "42" : null);
        var output = r.Resolve(Provided("MY_VAR"), "any.key", ValueType.Int, new ContextSet());
        output.Type.Should().Be(ValueType.Int);
        output.Payload.Should().Be(42L);
    }

    [Fact]
    public void Resolve_EnvVarMissing_ThrowsEnvVarNotSet()
    {
        var r = new Resolver(envLookup: _ => null);
        var act = () => r.Resolve(Provided("NOT_THERE"), "my.config", ValueType.String, new ContextSet());
        act.Should().Throw<QuonfigEnvVarNotSetException>()
            .WithMessage("*NOT_THERE*my.config*");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    public void Resolve_EnvVar_BoolCoercion(string envValue, bool expected)
    {
        var r = new Resolver(envLookup: _ => envValue);
        var output = r.Resolve(Provided("B"), "k", ValueType.Bool, new ContextSet());
        output.Payload.Should().Be(expected);
    }

    [Fact]
    public void Resolve_EnvVar_BadBoolean_ThrowsCoercion()
    {
        var r = new Resolver(envLookup: _ => "maybe");
        var act = () => r.Resolve(Provided("B"), "k", ValueType.Bool, new ContextSet());
        act.Should().Throw<QuonfigCoercionException>().WithMessage("*maybe*Bool*");
    }

    [Fact]
    public void Resolve_EnvVar_DoubleCoercion()
    {
        var r = new Resolver(envLookup: _ => "3.14");
        var output = r.Resolve(Provided("D"), "k", ValueType.Double, new ContextSet());
        output.Payload.Should().Be(3.14d);
    }

    private static readonly string[] ExpectedAbc = new[] { "a", "b", "c" };

    [Fact]
    public void Resolve_EnvVar_StringListCoercion()
    {
        var r = new Resolver(envLookup: _ => "a, b,c");
        var output = r.Resolve(Provided("SL"), "k", ValueType.StringList, new ContextSet());
        output.Payload.Should().BeEquivalentTo(ExpectedAbc);
    }

    [Fact]
    public void Resolve_EnvVar_EmptyStringList_IsEmpty()
    {
        var r = new Resolver(envLookup: _ => "");
        var output = r.Resolve(Provided("SL"), "k", ValueType.StringList, new ContextSet());
        ((IReadOnlyList<string>)output.Payload!).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EnvVar_DurationCoercion_Iso8601()
    {
        var r = new Resolver(envLookup: _ => "PT1H30M");
        var output = r.Resolve(Provided("DUR"), "k", ValueType.Duration, new ContextSet());
        output.Payload.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Resolve_EnvVar_JsonCoercion_Object()
    {
        var r = new Resolver(envLookup: _ => "{\"a\":1,\"b\":\"x\"}");
        var output = r.Resolve(Provided("J"), "k", ValueType.Json, new ContextSet());
        output.Payload.Should().BeAssignableTo<IDictionary<string, object?>>();
        var dict = (IDictionary<string, object?>)output.Payload!;
        dict["a"].Should().Be(1L);
        dict["b"].Should().Be("x");
    }

    [Fact]
    public void Resolve_EnvVar_JsonCoercion_Array()
    {
        var r = new Resolver(envLookup: _ => "[1, 2, 3]");
        var output = r.Resolve(Provided("J"), "k", ValueType.Json, new ContextSet());
        output.Payload.Should().BeAssignableTo<IReadOnlyList<object?>>();
    }

    [Fact]
    public void Resolve_EnvVar_LogLevelCoercion_PassThrough()
    {
        var r = new Resolver(envLookup: _ => "INFO");
        var output = r.Resolve(Provided("LL"), "k", ValueType.LogLevel, new ContextSet());
        output.Payload.Should().Be("INFO");
    }

    // ----- Weighted -----

    [Fact]
    public void Resolve_Weighted_DeterministicBucketing_MatchesIntegrationFixture()
    {
        // Mirrors get_weighted_values.yaml: trackingId a72c15f5 → value 1.
        var r = new Resolver();
        var wv = WeightedInt("user.tracking_id",
            (1000, 1L), (2000, 3L), (97000, 2L));
        var output = r.Resolve(wv, "feature-flag.weighted", ValueType.Int, UserCtx("tracking_id", "a72c15f5"));
        output.Type.Should().Be(ValueType.Int);
        output.Payload.Should().Be(1L);
    }

    [Fact]
    public void Resolve_Weighted_TrackingId_8f414100_GetsBucket1()
    {
        var r = new Resolver();
        var wv = WeightedInt("user.tracking_id",
            (1000, 1L), (2000, 3L), (97000, 2L));
        var output = r.Resolve(wv, "feature-flag.weighted", ValueType.Int, UserCtx("tracking_id", "8f414100"));
        output.Payload.Should().Be(3L);
    }

    [Fact]
    public void Resolve_Weighted_TrackingId_92a202f2_GetsBucket2()
    {
        var r = new Resolver();
        var wv = WeightedInt("user.tracking_id",
            (1000, 1L), (2000, 3L), (97000, 2L));
        var output = r.Resolve(wv, "feature-flag.weighted", ValueType.Int, UserCtx("tracking_id", "92a202f2"));
        output.Payload.Should().Be(2L);
    }

    [Fact]
    public void Resolve_Weighted_NoHashProperty_FallsBackToBucketZero()
    {
        var r = new Resolver();
        var wv = WeightedInt(null!, (1, 100L), (1, 200L));
        var output = r.Resolve(wv, "any.flag", ValueType.Int, new ContextSet());
        output.Payload.Should().Be(100L);
    }

    [Fact]
    public void Resolve_Weighted_MissingProperty_FallsBackToBucketZero()
    {
        var r = new Resolver();
        var wv = WeightedInt("user.id", (1, 100L), (1, 200L));
        var output = r.Resolve(wv, "any.flag", ValueType.Int, new ContextSet());
        output.Payload.Should().Be(100L);
    }

    // ----- Weighted-of-Provided: weighted variant whose chosen value is a PROVIDED ENV_VAR — recursive resolve. -----

    [Fact]
    public void Resolve_Weighted_PicksProvidedVariant_AndRecursesIntoEnvVar()
    {
        var r = new Resolver(envLookup: name => name == "SEC" ? "777" : null);
        // Variant 0 is an ENV_VAR provided value. Bucket 0 by single-entry weighting.
        var wv = new Value(
            ValueType.WeightedValues,
            new WeightedValuesPayload(null,
                new List<WeightedVariant> { new WeightedVariant(1, Provided("SEC")) }),
            false, null);

        var output = r.Resolve(wv, "any.key", ValueType.Int, new ContextSet());
        output.Type.Should().Be(ValueType.Int);
        output.Payload.Should().Be(777L);
    }

    // ----- AES-GCM decryption -----

    [Fact]
    public void Resolve_Confidential_DecryptsViaKeyLookup()
    {
        // Key resolver returns the hex key for "secrets.key"; resolver decrypts ciphertext.
        var r = new Resolver(
            keyResolver: (name, _) =>
                name == "secrets.key"
                    ? new Value(ValueType.String, FixtureKeyHex, false, null)
                    : null);

        var ciphertextVal = new Value(ValueType.String, FixtureCiphertext, true, "secrets.key");
        var output = r.Resolve(ciphertextVal, "a.secret.config", ValueType.String, new ContextSet());

        output.Type.Should().Be(ValueType.String);
        output.Payload.Should().Be(FixturePlaintext);
        output.Confidential.Should().BeTrue("decrypted plaintext must stay marked confidential");
    }

    [Fact]
    public void Resolve_Confidential_MissingKeyConfig_ThrowsDecryption()
    {
        var r = new Resolver(keyResolver: (_, _) => null);
        var ciphertextVal = new Value(ValueType.String, FixtureCiphertext, true, "missing.key");
        var act = () => r.Resolve(ciphertextVal, "a.secret.config", ValueType.String, new ContextSet());
        act.Should().Throw<QuonfigDecryptionException>().WithMessage("*missing.key*");
    }

    [Fact]
    public void Resolve_Confidential_KeyIsProvidedEnvVar_RecursivelyResolved()
    {
        // The encryption key is stored as a PROVIDED env-var value; resolver must recurse.
        var r = new Resolver(
            envLookup: n => n == "ENCRYPTION_KEY_HEX" ? FixtureKeyHex : null,
            keyResolver: (name, _) =>
                name == "secrets.key"
                    ? Provided("ENCRYPTION_KEY_HEX")
                    : null);

        var ciphertextVal = new Value(ValueType.String, FixtureCiphertext, true, "secrets.key");
        var output = r.Resolve(ciphertextVal, "a.secret.config", ValueType.String, new ContextSet());
        output.Payload.Should().Be(FixturePlaintext);
    }
}
