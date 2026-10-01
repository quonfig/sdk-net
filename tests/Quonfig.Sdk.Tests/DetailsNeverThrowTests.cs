using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Exceptions;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-2agi.12 (Jeff's release decision 2026-10-01): the <c>Get*Details</c> getters NEVER throw.
/// Under the default <see cref="OnNoDefault.Throw"/> with no defaultValue, a value that cannot be
/// resolved (ENV_VAR not set, ENV_VAR not convertible, decryption failure, a stored value the
/// getter cannot convert) or a missing key returns details with <see cref="Reason.Error"/>, an
/// error code and a null value; a resolve failure logs one warning per key. The plain typed
/// getters keep throwing the specific <see cref="QuonfigException"/> subtype (qfg-2agi.17).
/// </summary>
public sealed class DetailsNeverThrowTests : IDisposable
{
    private const string MissingEnvKey = "env.missing";
    private const string MissingEnvListKey = "env.missing-list";
    private const string MissingEnvJsonKey = "env.missing-json";
    private const string BadIntEnvKey = "env.bad-int";
    private const string BadDurationEnvKey = "env.bad-duration";
    private const string BadSecretKey = "secret.bad";
    private const string KeyConfigKey = "secret.key";
    private const string BigLongKey = "stored.big-long";
    private const string NotThereKey = "no.such.key";

    private readonly string _root;

    public DetailsNeverThrowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-details-nothrow-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        Write(dir, MissingEnvKey, Provided(MissingEnvKey, "string", "QFG_TEST_UNSET_VAR"));
        Write(dir, MissingEnvListKey, Provided(MissingEnvListKey, "string_list", "QFG_TEST_UNSET_VAR"));
        Write(dir, MissingEnvJsonKey, Provided(MissingEnvJsonKey, "json", "QFG_TEST_UNSET_VAR"));
        Write(dir, BadIntEnvKey, Provided(BadIntEnvKey, "int", "QFG_TEST_BAD_INT"));
        Write(dir, BadDurationEnvKey, Provided(BadDurationEnvKey, "duration", "QFG_TEST_BAD_DURATION"));
        Write(dir, KeyConfigKey, Config(KeyConfigKey, "string", "{ \"type\": \"string\", \"value\": \"" + new string('a', 64) + "\" }"));
        Write(dir, BadSecretKey, Config(BadSecretKey, "string",
            "{ \"type\": \"string\", \"value\": \"00--00--00\", \"confidential\": true, \"decryptWith\": \"" + KeyConfigKey + "\" }"));
        Write(dir, BigLongKey, Config(BigLongKey, "int", "{ \"type\": \"int\", \"value\": 3000000000 }"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Write(string dir, string key, string json) =>
        File.WriteAllText(Path.Combine(dir, key + ".json"), json);

    private static string Config(string key, string valueType, string valueJson) =>
        "{ \"id\": \"1\", \"key\": \"" + key + "\", \"type\": \"config\", \"valueType\": \"" + valueType + "\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }";

    private static string Provided(string key, string valueType, string envVar) =>
        Config(key, valueType, "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"" + envVar + "\" } }");

    private static string? Env(string name) => name switch
    {
        "QFG_TEST_BAD_INT" => "not-a-number",
        "QFG_TEST_BAD_DURATION" => "PT0.5H",
        _ => null,
    };

    private async Task<Quonfig> NewClientAsync(ILogger? logger = null)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = OnNoDefault.Throw,
            EnvLookup = Env,
            Logger = logger,
        });
        await client.InitAsync();
        return client;
    }

    // CA2249: Contains(string, StringComparison) is not available on net48.
#pragma warning disable CA2249
    private static List<string> Warnings(RecordingLogger logger, string key) =>
        logger.Messages(MelLogLevel.Warning).Where(m => m.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0).ToList();
#pragma warning restore CA2249

    private static void ShouldBeError<T>(EvaluationDetails<T> d, ErrorCode code)
    {
        d.Value.Should().BeNull();
        d.Reason.Should().Be(Reason.Error);
        d.ErrorCode.Should().Be(code);
        d.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UnsetEnvVar_Details_ReturnsError_AndWarnsOnce()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(logger);
        for (var i = 0; i < 3; i++)
        {
            ShouldBeError(client.GetStringDetails(MissingEnvKey), ErrorCode.General);
        }
        Warnings(logger, MissingEnvKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task UnsetEnvVar_StringListAndJsonDetails_ReturnError()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(logger);
        ShouldBeError(client.GetStringListDetails(MissingEnvListKey), ErrorCode.General);
        ShouldBeError(client.GetJsonDetails(MissingEnvJsonKey), ErrorCode.General);
        Warnings(logger, MissingEnvListKey).Should().HaveCount(1);
        Warnings(logger, MissingEnvJsonKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task UncoercibleEnvVar_Details_ReturnsError_AndWarnsOnce()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(logger);
        ShouldBeError(client.GetIntDetails(BadIntEnvKey), ErrorCode.General);
        ShouldBeError(client.GetIntDetails(BadIntEnvKey), ErrorCode.General);
        ShouldBeError(client.GetDurationDetails(BadDurationEnvKey), ErrorCode.General);
        Warnings(logger, BadIntEnvKey).Should().HaveCount(1);
        Warnings(logger, BadDurationEnvKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task DecryptionFailure_Details_ReturnsError_AndWarnsOnce()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(logger);
        ShouldBeError(client.GetStringDetails(BadSecretKey), ErrorCode.General);
        ShouldBeError(client.GetStringDetails(BadSecretKey), ErrorCode.General);
        Warnings(logger, BadSecretKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task StoredValueNotConvertible_IntDetails_ReturnsError_AndWarnsOnce()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(logger);
        ShouldBeError(client.GetIntDetails(BigLongKey), ErrorCode.TypeMismatch);
        ShouldBeError(client.GetIntDetails(BigLongKey), ErrorCode.TypeMismatch);
        Warnings(logger, BigLongKey).Should().HaveCount(1);
        // The same stored value is fine as a long.
        client.GetLong(BigLongKey).Should().Be(3000000000L);
    }

    [Fact]
    public async Task MissingKey_EveryDetailsGetter_ReturnsFlagNotFound()
    {
        await using var client = await NewClientAsync();
        ShouldBeError(client.GetStringDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetIntDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetLongDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetBoolDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetDoubleDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetStringListDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetJsonDetails(NotThereKey), ErrorCode.FlagNotFound);
        ShouldBeError(client.GetDurationDetails(NotThereKey), ErrorCode.FlagNotFound);
    }

    [Fact]
    public async Task BoundClient_Details_DoNotThrow()
    {
        await using var client = await NewClientAsync();
        var bound = client.WithContext(new ContextSet());
        ShouldBeError(bound.GetIntDetails(BadIntEnvKey), ErrorCode.General);
        ShouldBeError(bound.GetStringDetails(NotThereKey), ErrorCode.FlagNotFound);
    }

    // ----- the plain typed getters keep throwing the specific subtype -----

    [Fact]
    public async Task PlainGetters_StillThrowSpecificSubtype()
    {
        await using var client = await NewClientAsync();
        ((Action)(() => client.GetString(MissingEnvKey))).Should().Throw<QuonfigEnvVarNotSetException>();
        ((Action)(() => client.GetStringList(MissingEnvListKey))).Should().Throw<QuonfigEnvVarNotSetException>();
        ((Action)(() => client.GetInt(BadIntEnvKey))).Should().Throw<QuonfigCoercionException>();
        ((Action)(() => client.GetDuration(BadDurationEnvKey))).Should().Throw<QuonfigCoercionException>();
        ((Action)(() => client.GetString(BadSecretKey))).Should().Throw<QuonfigDecryptionException>();
        ((Action)(() => client.GetInt(BigLongKey))).Should().Throw<QuonfigCoercionException>();
        ((Action)(() => client.GetJson(NotThereKey))).Should().Throw<QuonfigKeyNotFoundException>();
        var bound = client.WithContext(new ContextSet());
        ((Action)(() => bound.GetInt(BadIntEnvKey))).Should().Throw<QuonfigCoercionException>();
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(MelLogLevel Level, string Message)> _entries = new();

        public List<string> Messages(MelLogLevel level)
        {
            lock (_gate) { return _entries.Where(e => e.Level == level).Select(e => e.Message).ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => true;

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) { _entries.Add((logLevel, formatter(state, exception))); }
        }
    }
}
