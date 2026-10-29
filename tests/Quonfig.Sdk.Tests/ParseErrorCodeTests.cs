using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-wg1n: the details API reports a value that has the requested type but does not parse as it
/// (a malformed ENV_VAR value, a malformed stored int, double or duration) as
/// <see cref="ErrorCode.ParseError"/>, not <see cref="ErrorCode.TypeMismatch"/>. A value of another
/// type is still <see cref="ErrorCode.TypeMismatch"/>. The reason, value, variant and the plain
/// getters' exceptions are unchanged.
/// </summary>
public sealed class ParseErrorCodeTests : IDisposable
{
    private readonly string _root;

    public ParseErrorCodeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-parse-error-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        Write(dir, "env.int", "int", Provided("QFG_BAD_INT"));
        Write(dir, "env.double", "double", Provided("QFG_BAD_DOUBLE"));
        Write(dir, "env.bool", "bool", Provided("QFG_BAD_BOOL"));
        Write(dir, "env.duration", "duration", Provided("QFG_BAD_DURATION"));
        Write(dir, "env.json", "json", Provided("QFG_BAD_JSON"));
        Write(dir, "env.unset", "int", Provided("QFG_UNSET"));
        Write(dir, "stored.int", "int", "{ \"type\": \"int\", \"value\": \"twelve\" }");
        Write(dir, "stored.double", "double", "{ \"type\": \"double\", \"value\": \"one point five\" }");
        Write(dir, "stored.duration", "duration", "{ \"type\": \"duration\", \"value\": \"30s\" }");
        Write(dir, "good.int", "int", "{ \"type\": \"int\", \"value\": 42 }");
        Write(dir, "good.fraction", "double", "{ \"type\": \"double\", \"value\": 3.9 }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Write(string dir, string key, string valueType, string valueJson) =>
        File.WriteAllText(
            Path.Combine(dir, key + ".json"),
            "{ \"id\": \"1\", \"key\": \"" + key + "\", \"type\": \"config\", \"valueType\": \"" + valueType + "\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }");

    private static string Provided(string envVar) =>
        "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"" + envVar + "\" } }";

    private static string? Env(string name) => name switch
    {
        "QFG_BAD_INT" => "abc",
        "QFG_BAD_DOUBLE" => "abc",
        "QFG_BAD_BOOL" => "maybe",
        "QFG_BAD_DURATION" => "30s",
        "QFG_BAD_JSON" => "{not json",
        _ => null,
    };

    private async Task<Quonfig> NewClientAsync()
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = OnNoDefault.Throw,
            EnvLookup = Env,
        });
        await client.InitAsync();
        return client;
    }

    private static void ShouldBe<T>(EvaluationDetails<T> d, ErrorCode code, T expectedValue)
    {
        d.Reason.Should().Be(Reason.Error);
        d.ErrorCode.Should().Be(code);
        d.ErrorMessage.Should().NotBeNullOrEmpty();
        d.Value.Should().Be(expectedValue);
    }

    // ----- malformed ENV_VAR value, every typed details getter -----

    [Fact]
    public async Task MalformedEnvVar_TypedDetails_ReportParseError()
    {
        await using var client = await NewClientAsync();
        ShouldBe(client.GetIntDetails("env.int"), ErrorCode.ParseError, null);
        ShouldBe(client.GetLongDetails("env.int"), ErrorCode.ParseError, null);
        ShouldBe(client.GetLongDetails("env.int", defaultValue: 7), ErrorCode.ParseError, 7L);
        ShouldBe(client.GetDoubleDetails("env.double"), ErrorCode.ParseError, null);
        ShouldBe(client.GetBoolDetails("env.bool"), ErrorCode.ParseError, null);
        ShouldBe(client.GetDurationDetails("env.duration"), ErrorCode.ParseError, null);
        client.GetJsonDetails("env.json").ErrorCode.Should().Be(ErrorCode.ParseError);
    }

    [Fact]
    public async Task MalformedEnvVar_EvaluateDetails_ReportsParseError()
    {
        await using var client = await NewClientAsync();
        foreach (var key in new[] { "env.int", "env.double", "env.bool", "env.duration", "env.json" })
        {
            var d = client.EvaluateDetails(key);
            d.Value.Should().BeNull(key);
            d.Reason.Should().Be(Reason.Error, key);
            d.ErrorCode.Should().Be(ErrorCode.ParseError, key);
        }
    }

    [Fact]
    public async Task MalformedEnvVar_BoundView_ReportsParseError()
    {
        await using var client = await NewClientAsync();
        var bound = client.WithContext(new ContextSet());
        bound.GetLongDetails("env.int").ErrorCode.Should().Be(ErrorCode.ParseError);
        ((IQuonfigDetailsReader)bound).EvaluateDetails("env.int").ErrorCode.Should().Be(ErrorCode.ParseError);
    }

    // ----- malformed stored int / double / duration -----

    [Fact]
    public async Task MalformedStoredValue_TypedDetails_ReportParseError()
    {
        await using var client = await NewClientAsync();
        ShouldBe(client.GetIntDetails("stored.int"), ErrorCode.ParseError, null);
        ShouldBe(client.GetLongDetails("stored.int"), ErrorCode.ParseError, null);
        ShouldBe(client.GetDoubleDetails("stored.int", defaultValue: 1.5), ErrorCode.ParseError, 1.5);
        ShouldBe(client.GetDoubleDetails("stored.double"), ErrorCode.ParseError, null);
        ShouldBe(client.GetLongDetails("stored.double"), ErrorCode.ParseError, null);
        ShouldBe(client.GetDurationDetails("stored.duration"), ErrorCode.ParseError, null);
    }

    [Fact]
    public async Task MalformedStoredValue_EvaluateDetails_StillReturnedAsStored()
    {
        // The untyped read runs no parse validation (qfg-177f), so a malformed stored value is a
        // success, not a ParseError.
        await using var client = await NewClientAsync();
        var d = client.EvaluateDetails("stored.duration");
        d.Value.Should().Be("30s");
        d.ErrorCode.Should().BeNull();
    }

    // ----- a real type mismatch stays TypeMismatch; other errors stay General -----

    [Fact]
    public async Task OtherType_StaysTypeMismatch()
    {
        await using var client = await NewClientAsync();
        client.GetStringDetails("good.int").ErrorCode.Should().Be(ErrorCode.TypeMismatch);
        client.GetBoolDetails("good.int").ErrorCode.Should().Be(ErrorCode.TypeMismatch);
        client.GetLongDetails("good.fraction").ErrorCode.Should().Be(ErrorCode.TypeMismatch);
        client.GetStringDetails("stored.duration").ErrorCode.Should().Be(ErrorCode.TypeMismatch);
    }

    [Fact]
    public async Task MalformedEnvVar_IsParseError_WhicheverGetterReadsIt()
    {
        // An ENV_VAR value is converted to the config's declared type before any getter sees it,
        // so a value that does not convert is a parse error even on a getter of another type.
        await using var client = await NewClientAsync();
        client.GetStringDetails("env.int").ErrorCode.Should().Be(ErrorCode.ParseError);
    }

    [Fact]
    public async Task UnsetEnvVar_StaysGeneral()
    {
        await using var client = await NewClientAsync();
        client.GetLongDetails("env.unset").ErrorCode.Should().Be(ErrorCode.General);
        client.EvaluateDetails("env.unset").ErrorCode.Should().Be(ErrorCode.General);
    }

    // ----- plain getters and IsFeatureEnabled are unchanged -----

    [Fact]
    public async Task PlainGetters_StillThrowCoercion()
    {
        await using var client = await NewClientAsync();
        ((Action)(() => client.GetLong("env.int"))).Should().Throw<QuonfigCoercionException>();
        ((Action)(() => client.GetLong("stored.int"))).Should().Throw<QuonfigCoercionException>();
        ((Action)(() => client.GetDuration("stored.duration"))).Should().Throw<QuonfigCoercionException>();
        client.GetLong("env.int", defaultValue: 9).Should().Be(9);
        client.IsFeatureEnabled("env.bool").Should().BeFalse();
    }

    [Fact]
    public void ParseError_IsAppendedAfterGeneral()
    {
        // openfeature-net 2.3.0 matches the member by name, or by this ordinal on an older SDK.
        ((int)ErrorCode.FlagNotFound).Should().Be(0);
        ((int)ErrorCode.TypeMismatch).Should().Be(1);
        ((int)ErrorCode.General).Should().Be(2);
        ((int)ErrorCode.ParseError).Should().Be(3);
        ErrorCode.ParseError.ToString().Should().Be("ParseError");
    }
}
