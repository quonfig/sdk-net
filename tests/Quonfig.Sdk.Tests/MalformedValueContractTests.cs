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
/// qfg-2agi.17: a value that cannot be resolved (ENV_VAR not set, ENV_VAR not coercible to the
/// config's type, decryption failure) follows the epic's malformed-value contract through the
/// PUBLIC getters:
/// <list type="bullet">
/// <item><description><see cref="OnNoDefault.Throw"/> with no defaultValue: the specific
/// <see cref="QuonfigException"/> subtype is raised (previously the getter returned null
/// silently).</description></item>
/// <item><description>A supplied defaultValue, or <see cref="OnNoDefault.Warn"/> /
/// <see cref="OnNoDefault.Ignore"/>: the default (or null) is returned.</description></item>
/// <item><description>Every mode logs a warning, once per config key per client.</description></item>
/// </list>
/// </summary>
public sealed class MalformedValueContractTests : IDisposable
{
    private const string MissingEnvKey = "env.missing";
    private const string BadIntEnvKey = "env.bad-int";
    private const string BadSecretKey = "secret.bad";
    private const string KeyConfigKey = "secret.key";
    private const string GoodEnvKey = "env.good";

    private readonly string _root;

    public MalformedValueContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-malformed-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MissingEnvKey + ".json"), Provided(MissingEnvKey, "string", "QFG_TEST_UNSET_VAR"));
        File.WriteAllText(Path.Combine(dir, BadIntEnvKey + ".json"), Provided(BadIntEnvKey, "int", "QFG_TEST_BAD_INT"));
        File.WriteAllText(Path.Combine(dir, GoodEnvKey + ".json"), Provided(GoodEnvKey, "int", "QFG_TEST_GOOD_INT"));
        File.WriteAllText(Path.Combine(dir, KeyConfigKey + ".json"),
            Config(KeyConfigKey, "string", "{ \"type\": \"string\", \"value\": \"" + new string('a', 64) + "\" }"));
        File.WriteAllText(Path.Combine(dir, BadSecretKey + ".json"),
            Config(BadSecretKey, "string",
                "{ \"type\": \"string\", \"value\": \"00--00--00\", \"confidential\": true, \"decryptWith\": \"" + KeyConfigKey + "\" }"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Config(string key, string valueType, string valueJson) =>
        "{ \"id\": \"1\", \"key\": \"" + key + "\", \"type\": \"config\", \"valueType\": \"" + valueType + "\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }";

    private static string Provided(string key, string valueType, string envVar) =>
        Config(key, valueType, "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"" + envVar + "\" } }");

    private static string? Env(string name) => name switch
    {
        "QFG_TEST_BAD_INT" => "not-a-number",
        "QFG_TEST_GOOD_INT" => "42",
        _ => null,
    };

    private async Task<Quonfig> NewClientAsync(OnNoDefault policy, ILogger? logger = null)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = policy,
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

    // ----- OnNoDefault.Throw, no defaultValue: raise the specific subtype -----

    [Fact]
    public async Task Throw_MissingEnvVar_RaisesEnvVarNotSet()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Throw, logger);
        var act = () => client.GetString(MissingEnvKey);
        act.Should().Throw<QuonfigEnvVarNotSetException>();
        Warnings(logger, MissingEnvKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task Throw_UncoercibleEnvVar_RaisesCoercion()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Throw, logger);
        var act = () => client.GetInt(BadIntEnvKey);
        act.Should().Throw<QuonfigCoercionException>();
        Warnings(logger, BadIntEnvKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task Throw_DecryptionFailure_RaisesDecryption()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Throw, logger);
        var act = () => client.GetString(BadSecretKey);
        act.Should().Throw<QuonfigDecryptionException>();
        Warnings(logger, BadSecretKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task Throw_DetailsGetter_NeverRaises_ReportsError()
    {
        // Get*Details never throw (qfg-2agi.12 release decision); see DetailsNeverThrowTests.
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        var d = client.GetIntDetails(BadIntEnvKey);
        d.Value.Should().BeNull();
        d.Reason.Should().Be(Reason.Error);
        d.ErrorCode.Should().Be(ErrorCode.General);
    }

    [Fact]
    public async Task Throw_GoodEnvVar_StillResolves()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        client.GetInt(GoodEnvKey).Should().Be(42);
    }

    // ----- a supplied defaultValue wins under every policy, with a warning -----

    [Theory]
    [InlineData(OnNoDefault.Throw)]
    [InlineData(OnNoDefault.Warn)]
    [InlineData(OnNoDefault.Ignore)]
    public async Task WithDefault_ReturnsDefault_AndWarns(OnNoDefault policy)
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(policy, logger);
        client.GetString(MissingEnvKey, defaultValue: "fallback").Should().Be("fallback");
        client.GetInt(BadIntEnvKey, defaultValue: 7).Should().Be(7);
        client.GetString(BadSecretKey, defaultValue: "fallback").Should().Be("fallback");

        var d = client.GetIntDetails(BadIntEnvKey, defaultValue: 7);
        d.Value.Should().Be(7);
        d.Reason.Should().Be(Reason.Error);

        Warnings(logger, MissingEnvKey).Should().HaveCount(1);
        Warnings(logger, BadIntEnvKey).Should().HaveCount(1);
        Warnings(logger, BadSecretKey).Should().HaveCount(1);
    }

    // ----- return-default policies, no defaultValue: null, with a warning -----

    [Theory]
    [InlineData(OnNoDefault.Warn)]
    [InlineData(OnNoDefault.Ignore)]
    public async Task ReturnDefaultPolicies_NoDefault_ReturnNull_AndWarn(OnNoDefault policy)
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(policy, logger);
        client.GetString(MissingEnvKey).Should().BeNull();
        client.GetInt(BadIntEnvKey).Should().BeNull();
        client.GetString(BadSecretKey).Should().BeNull();

        Warnings(logger, MissingEnvKey).Should().HaveCount(1);
        Warnings(logger, BadIntEnvKey).Should().HaveCount(1);
        Warnings(logger, BadSecretKey).Should().HaveCount(1);
    }

    [Fact]
    public async Task Warning_IsLoggedOncePerKeyPerClient()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Warn, logger);
        for (var i = 0; i < 5; i++)
        {
            client.GetInt(BadIntEnvKey);
            client.GetInt(BadIntEnvKey, defaultValue: 1);
        }
        Warnings(logger, BadIntEnvKey).Should().HaveCount(1);
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
