using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-177f: <see cref="IQuonfigDetailsReader.EvaluateDetails"/> is the untyped details read, matching sdk-go's
/// <c>Client.EvaluateDetails</c>. It returns the resolved value in its stored form (a duration
/// is its ISO 8601 text exactly as stored) with the same reason, variant and metadata as the
/// typed Get*Details getters. It runs no type check, no coercion and no parse validation, and
/// it never throws.
/// </summary>
public sealed class EvaluateDetailsTests : IDisposable
{
    private readonly string _root;

    public EvaluateDetailsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-evaluate-details-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        Write(dir, Static("dur.static", "duration", "{ \"type\": \"duration\", \"value\": \"PT90S\" }"));
        Write(dir, Static("dur.compound", "duration", "{ \"type\": \"duration\", \"value\": \"P1DT6H2M1.5S\" }"));
        Write(dir, Static("dur.malformed", "duration", "{ \"type\": \"duration\", \"value\": \"30s\" }"));
        Write(dir, Static("dur.env", "duration", Provided("QFG_TEST_DURATION")));
        Write(dir, Static("dur.env-bad", "duration", Provided("QFG_TEST_BAD_DURATION")));
        Write(dir, Static("dur.env-unset", "duration", Provided("QFG_TEST_UNSET")));
        Write(dir, Static("list.static", "string_list", "{ \"type\": \"string_list\", \"value\": [\"a\", \"b\"] }"));
        Write(dir, Static("int.static", "int", "{ \"type\": \"int\", \"value\": 42 }"));
        Write(dir, Static("str.static", "string", "{ \"type\": \"string\", \"value\": \"hello\" }"));
        Write(dir, "dur.targeted",
            "{ \"id\": \"2\", \"key\": \"dur.targeted\", \"type\": \"config\", \"valueType\": \"duration\", " +
            "\"default\": { \"rules\": [ " +
            "{ \"criteria\": [ { \"propertyName\": \"user.plan\", \"operator\": \"PROP_IS_ONE_OF\", " +
            "\"valueToMatch\": { \"type\": \"string_list\", \"value\": [\"pro\"] } } ], " +
            "\"value\": { \"type\": \"duration\", \"value\": \"PT5M\" } }, " +
            "{ \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": { \"type\": \"duration\", \"value\": \"PT1M\" } } ] } }");
        Write(dir, "dur.split",
            "{ \"id\": \"3\", \"key\": \"dur.split\", \"type\": \"feature_flag\", \"valueType\": \"duration\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], " +
            "\"value\": { \"type\": \"weighted_values\", \"value\": { \"hashByPropertyName\": \"user.key\", \"weightedValues\": [ " +
            "{ \"weight\": 50000, \"value\": { \"type\": \"duration\", \"value\": \"PT10S\" } }, " +
            "{ \"weight\": 50000, \"value\": { \"type\": \"duration\", \"value\": \"PT20S\" } } ] } } } ] } }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (string Key, string Json) Static(string key, string valueType, string valueJson) =>
        (key, "{ \"id\": \"1\", \"key\": \"" + key + "\", \"type\": \"config\", \"valueType\": \"" + valueType + "\", " +
              "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }");

    private static string Provided(string envVar) =>
        "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"" + envVar + "\" } }";

    private static void Write(string dir, (string Key, string Json) cfg) => Write(dir, cfg.Key, cfg.Json);

    private static void Write(string dir, string key, string json) =>
        File.WriteAllText(Path.Combine(dir, key + ".json"), json);

    private static string? Env(string name) => name switch
    {
        "QFG_TEST_DURATION" => "PT2H",
        "QFG_TEST_BAD_DURATION" => "two hours",
        _ => null,
    };

    private async Task<Quonfig> NewClientAsync(OnNoDefault policy = OnNoDefault.Throw)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = policy,
            EnvLookup = Env,
        });
        await client.InitAsync();
        return client;
    }

    private static ContextSet User(string key, string plan) =>
        new() { ["user"] = new ContextProperties { ["key"] = key, ["plan"] = plan } };

    [Theory]
    [InlineData("dur.static", "PT90S")]
    [InlineData("dur.compound", "P1DT6H2M1.5S")]
    public async Task StaticDuration_ReturnsStoredIsoTextUnchanged(string key, string stored)
    {
        await using var client = await NewClientAsync();
        var d = client.EvaluateDetails(key);
        d.Value.Should().BeOfType<string>().Which.Should().Be(stored);
        d.Reason.Should().Be(Reason.Static);
        d.Variant.Should().Be("static");
        d.ErrorCode.Should().BeNull();
        d.ErrorMessage.Should().BeNull();

        // Same reason / variant / metadata as the typed getter.
        var typed = client.GetDurationDetails(key);
        d.Reason.Should().Be(typed.Reason);
        d.Variant.Should().Be(typed.Variant);
        d.Metadata.Should().BeEquivalentTo(typed.Metadata);
        d.Metadata["configType"].Should().Be("Duration");
    }

    [Fact]
    public async Task Targeting_ReportsTargetingMatchAndVariant()
    {
        await using var client = await NewClientAsync();
        var pro = client.EvaluateDetails("dur.targeted", User("u1", "pro"));
        pro.Value.Should().Be("PT5M");
        pro.Reason.Should().Be(Reason.TargetingMatch);
        pro.Variant.Should().Be("targeting:0");
        pro.Metadata["ruleIndex"].Should().Be(0);

        var free = client.EvaluateDetails("dur.targeted", User("u1", "free"));
        free.Value.Should().Be("PT1M");
        free.Reason.Should().Be(Reason.TargetingMatch);
        free.Variant.Should().Be("targeting:1");
    }

    [Fact]
    public async Task Bound_MergesBoundContextLikeTypedGetters()
    {
        await using var client = await NewClientAsync();
        var bound = client.WithContext(User("u1", "pro"));
        var reader = bound.Should().BeAssignableTo<IQuonfigDetailsReader>().Subject;
        var d = reader.EvaluateDetails("dur.targeted");
        d.Value.Should().Be("PT5M");
        d.Variant.Should().Be(bound.GetDurationDetails("dur.targeted").Variant);

        // Per-call contexts layer on the bound context; the per-call named context wins.
        var free = reader.EvaluateDetails("dur.targeted", User("u1", "free"));
        free.Value.Should().Be("PT1M");
        free.Variant.Should().Be("targeting:1");
    }

    [Fact]
    public async Task IQuonfig_ExposesReaderByTypeCheck()
    {
        await using var client = await NewClientAsync();
        IQuonfig q = client;
        (q is IQuonfigDetailsReader r ? r.EvaluateDetails("dur.static").Value : null).Should().Be("PT90S");
    }

    [Fact]
    public void ExistingInterfaces_GainNoMembers()
    {
        // Purely additive for hand-written IQuonfig / IBoundQuonfig implementers (qfg-177f).
        typeof(IQuonfig).GetMethod(nameof(IQuonfigDetailsReader.EvaluateDetails)).Should().BeNull();
        typeof(IBoundQuonfig).GetMethod(nameof(IQuonfigDetailsReader.EvaluateDetails)).Should().BeNull();
        typeof(IQuonfig).GetInterfaces().Should().NotContain(typeof(IQuonfigDetailsReader));
        typeof(IBoundQuonfig).GetInterfaces().Should().NotContain(typeof(IQuonfigDetailsReader));
    }

    [Fact]
    public async Task WeightedSplit_ReportsSplitVariantMatchingTypedGetter()
    {
        await using var client = await NewClientAsync();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 40; i++)
        {
            var ctx = User("user-" + i, "free");
            var d = client.EvaluateDetails("dur.split", ctx);
            var typed = client.GetDurationDetails("dur.split", ctx);
            d.Reason.Should().Be(Reason.Split);
            d.Variant.Should().Be(typed.Variant);
            d.Metadata.Should().BeEquivalentTo(typed.Metadata);
            var text = d.Value.Should().BeOfType<string>().Subject;
            text.Should().Be(d.Variant == "split:0" ? "PT10S" : "PT20S");
            seen.Add(text);
        }
        seen.Should().BeEquivalentTo("PT10S", "PT20S");
    }

    [Fact]
    public async Task EnvVarProvidedDuration_ReturnsEnvText()
    {
        await using var client = await NewClientAsync();
        var d = client.EvaluateDetails("dur.env");
        d.Value.Should().Be("PT2H");
        d.Reason.Should().Be(Reason.Static);
        d.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task EnvVarUnresolvable_ReportsSameErrorAsTypedGetter_NeverThrows()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        foreach (var key in new[] { "dur.env-bad", "dur.env-unset" })
        {
            var d = client.EvaluateDetails(key);
            var typed = client.GetDurationDetails(key);
            d.Value.Should().BeNull();
            d.Reason.Should().Be(Reason.Error);
            d.ErrorCode.Should().Be(typed.ErrorCode);
        }
    }

    [Fact]
    public async Task MissingKey_ReturnsNullWithFlagNotFound_NeverThrows()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        var d = client.EvaluateDetails("does.not.exist");
        d.Value.Should().BeNull();
        d.Reason.Should().Be(Reason.Error);
        d.ErrorCode.Should().Be(ErrorCode.FlagNotFound);
        d.ErrorCode.Should().Be(client.GetDurationDetails("does.not.exist").ErrorCode);
    }

    [Fact]
    public async Task MalformedStoredDuration_ReturnedRawWithSuccessReason()
    {
        await using var client = await NewClientAsync();
        var d = client.EvaluateDetails("dur.malformed");
        d.Value.Should().Be("30s");
        d.Reason.Should().Be(Reason.Static);
        d.ErrorCode.Should().BeNull();
        // The typed getter still rejects it.
        client.GetDurationDetails("dur.malformed").Reason.Should().Be(Reason.Error);
    }

    [Fact]
    public async Task OtherTypes_ReturnTypedGetterForm_WithoutTypeCheck()
    {
        await using var client = await NewClientAsync();
        client.EvaluateDetails("list.static").Value.Should().BeEquivalentTo(client.GetStringList("list.static"));
        client.EvaluateDetails("int.static").Value.Should().Be(42L);
        client.EvaluateDetails("str.static").Value.Should().Be("hello");
    }

    [Fact]
    public async Task ValueType_IsStoredWireType_NullWhenNotFound()
    {
        await using var client = await NewClientAsync();
        client.EvaluateDetails("dur.static").ValueType.Should().Be("duration");
        client.EvaluateDetails("dur.split", User("u1", "free")).ValueType.Should().Be("duration");
        client.EvaluateDetails("dur.malformed").ValueType.Should().Be("duration");
        client.EvaluateDetails("dur.env").ValueType.Should().Be("duration");
        client.EvaluateDetails("list.static").ValueType.Should().Be("string_list");
        client.EvaluateDetails("int.static").ValueType.Should().Be("int");
        ((IQuonfigDetailsReader)client.WithContext(User("u1", "pro"))).EvaluateDetails("dur.targeted").ValueType.Should().Be("duration");
        // Found but unresolvable: the stored type is still known.
        client.EvaluateDetails("dur.env-bad").ValueType.Should().Be("duration");
        client.EvaluateDetails("does.not.exist").ValueType.Should().BeNull();
    }

    [Fact]
    public async Task StringList_IsCallerOwnedCopy()
    {
        await using var client = await NewClientAsync();
        var first = client.EvaluateDetails("list.static").Value;
        first.Should().NotBeSameAs(client.EvaluateDetails("list.static").Value);
    }
}
