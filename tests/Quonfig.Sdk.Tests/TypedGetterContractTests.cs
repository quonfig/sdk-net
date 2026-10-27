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
/// The typed-getter contract (qfg-zvyy) through the PUBLIC getters. The cross-SDK matrix lives in
/// integration-test-data (getter.contract.* fixtures, generated into Integration/); these cover
/// what the corpus cannot express: the int/double range edges, values JSON cannot store (NaN,
/// infinity), the once-per-key warning, and IsFeatureEnabled never throwing.
/// </summary>
public sealed class TypedGetterContractTests : IDisposable
{
    private readonly string _root;

    public TypedGetterContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-typed-getter-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        void Write(string key, string valueType, string valueJson) =>
            File.WriteAllText(Path.Combine(dir, key + ".json"), Config(key, valueType, valueJson));

        Write("d.whole", "double", "{ \"type\": \"double\", \"value\": 3.0 }");
        Write("d.negwhole", "double", "{ \"type\": \"double\", \"value\": -42.0 }");
        Write("d.maxsafe", "double", "{ \"type\": \"double\", \"value\": 9007199254740991 }");
        Write("d.minsafe", "double", "{ \"type\": \"double\", \"value\": -9007199254740991 }");
        Write("d.abovesafe", "double", "{ \"type\": \"double\", \"value\": 9007199254740992 }");
        Write("d.belowsafe", "double", "{ \"type\": \"double\", \"value\": -9007199254740992 }");
        Write("d.fraction", "double", "{ \"type\": \"double\", \"value\": 3.9 }");
        Write("d.huge", "double", "{ \"type\": \"double\", \"value\": 1e300 }");
        Write("d.int32overflow", "double", "{ \"type\": \"double\", \"value\": 3000000000.0 }");
        Write("i.thirty", "int", "{ \"type\": \"int\", \"value\": 30 }");
        Write("i.malformed", "int", "{ \"type\": \"int\", \"value\": \"twelve\" }");
        Write("s.false", "string", "{ \"type\": \"string\", \"value\": \"false\" }");
        Write("k.a", "string", "{ \"type\": \"string\", \"value\": \"fast\" }");
        Write("k.b", "string", "{ \"type\": \"string\", \"value\": \"slow\" }");
        Write("env.missing.bool", "bool",
            "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"QFG_TEST_TYPED_UNSET\" } }");
        Write("secret.key", "string", "{ \"type\": \"string\", \"value\": \"" + new string('a', 64) + "\" }");
        Write("secret.bad", "string",
            "{ \"type\": \"string\", \"value\": \"00--00--00\", \"confidential\": true, \"decryptWith\": \"secret.key\" }");
        // A flag whose only rule targets a user that never matches: no rule matches, no fallback.
        File.WriteAllText(Path.Combine(dir, "flag.nomatch.json"),
            "{ \"id\": \"2\", \"key\": \"flag.nomatch\", \"type\": \"feature_flag\", \"valueType\": \"bool\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"propertyName\": \"user.key\", \"operator\": \"PROP_IS_ONE_OF\", " +
            "\"valueToMatch\": { \"type\": \"string_list\", \"value\": [\"nobody\"] } } ], \"value\": { \"type\": \"bool\", \"value\": true } } ] } }");
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

    private async Task<Quonfig> NewClientAsync(OnNoDefault policy = OnNoDefault.Ignore, ILogger? logger = null)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = policy,
            EnvLookup = _ => null,
            Logger = logger,
            EnableQuonfigUserContext = false,
            DatadirAutoReload = false,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });
        await client.InitAsync();
        return client;
    }

    // CA2249: Contains(string, StringComparison) is not available on net48.
#pragma warning disable CA2249
    private static List<string> Warnings(RecordingLogger logger, string key) =>
        logger.Messages(MelLogLevel.Warning).Where(m => m.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0).ToList();
#pragma warning restore CA2249

    // ----- int <-> double: safe conversions only -----

    [Theory]
    [InlineData("d.whole", 3L)]
    [InlineData("d.negwhole", -42L)]
    [InlineData("d.maxsafe", 9007199254740991L)]
    [InlineData("d.minsafe", -9007199254740991L)]
    public async Task GetLong_OnWholeDoubleInSafeRange_ReturnsIt(string key, long expected)
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        client.GetLong(key).Should().Be(expected);
        client.GetLong(key, defaultValue: 7).Should().Be(expected);
        client.GetLongDetails(key).ErrorCode.Should().BeNull();
    }

    [Theory]
    [InlineData("d.fraction")]
    [InlineData("d.huge")]
    [InlineData("d.abovesafe")]
    [InlineData("d.belowsafe")]
    public async Task GetLong_OnFractionalOrUnsafeDouble_IsTypeMismatch(string key)
    {
        await using var throwing = await NewClientAsync(OnNoDefault.Throw);
        throwing.GetLong(key, defaultValue: 7).Should().Be(7);
        var act = () => throwing.GetLong(key);
        act.Should().Throw<QuonfigTypeMismatchException>();

        var details = throwing.GetLongDetails(key, defaultValue: 7);
        details.Value.Should().Be(7);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorCode.Should().Be(ErrorCode.TypeMismatch);

        await using var ignoring = await NewClientAsync(OnNoDefault.Ignore);
        ignoring.GetLong(key).Should().BeNull();
    }

    [Fact]
    public async Task GetInt_OnWholeDouble_ReturnsIt_AndNeverTruncates()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        client.GetInt("d.whole").Should().Be(3);
        client.GetInt("d.fraction", defaultValue: 7).Should().Be(7);
        var act = () => client.GetInt("d.fraction");
        act.Should().Throw<QuonfigTypeMismatchException>();
        // Whole and safe, but outside Int32: GetInt cannot hold it. It never wraps or truncates.
        client.GetInt("d.int32overflow", defaultValue: 7).Should().Be(7);
        client.GetLong("d.int32overflow").Should().Be(3000000000L);
    }

    [Fact]
    public async Task GetDouble_OnInt_ReturnsIt()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        client.GetDouble("i.thirty").Should().Be(30.0);
        client.GetDouble("i.thirty", defaultValue: 1.5).Should().Be(30.0);
        var details = client.GetDoubleDetails("i.thirty");
        details.Value.Should().Be(30.0);
        details.ErrorCode.Should().BeNull();
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    [InlineData(3.9, false)]
    [InlineData(9007199254740992d, false)]
    [InlineData(-9007199254740992d, false)]
    [InlineData(1e300, false)]
    [InlineData(3.0, true)]
    [InlineData(-0.0, true)]
    [InlineData(9007199254740991d, true)]
    [InlineData(-9007199254740991d, true)]
    public void IsSafeWhole_OnlyAcceptsWholeFiniteValuesInSafeRange(double d, bool expected)
    {
        // NaN and infinity cannot be stored in a JSON config; this pins the rule for them anyway.
        Quonfig.IsSafeWhole(d).Should().Be(expected);
    }

    // ----- no string coercion -----

    [Fact]
    public async Task GetBool_OnStringFalse_IsTypeMismatch_NotFalse()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        client.GetBool("s.false", defaultValue: true).Should().BeTrue();
        var act = () => client.GetBool("s.false");
        act.Should().Throw<QuonfigTypeMismatchException>();
        client.GetBoolDetails("s.false", defaultValue: true).ErrorCode.Should().Be(ErrorCode.TypeMismatch);
    }

    // ----- malformed stored int -----

    [Fact]
    public async Task MalformedStoredInt_LoadsAndReadsAsUnableToCoerce()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Throw, logger);
        client.GetLong("i.malformed", defaultValue: 7).Should().Be(7);
        client.GetDouble("i.malformed", defaultValue: 1.5).Should().Be(1.5);
        var act = () => client.GetLong("i.malformed");
        act.Should().Throw<QuonfigCoercionException>();
        Warnings(logger, "i.malformed").Should().ContainSingle();
        logger.Messages(MelLogLevel.Warning).Should().NotContain(m => m.Contains("twelve"));
    }

    // ----- once-per-key logging -----

    [Fact]
    public async Task FailedRead_LogsOncePerKey_AndNeverTheValue()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Ignore, logger);
        for (int i = 0; i < 3; i++)
        {
            client.GetBool("k.a", defaultValue: false);
            client.GetLong("k.a", defaultValue: 1);
            client.GetBoolDetails("k.a");
            client.IsFeatureEnabled("k.a").Should().BeFalse();
        }
        client.GetBool("k.b");

        Warnings(logger, "k.a").Should().ContainSingle();
        Warnings(logger, "k.b").Should().ContainSingle();
        logger.Messages(MelLogLevel.Warning).Should().NotContain(m => m.Contains("fast") || m.Contains("slow"));
    }

    [Fact]
    public async Task MissingKeyWithDefault_DoesNotLog()
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(OnNoDefault.Ignore, logger);
        client.GetString("no.such.key", defaultValue: "d").Should().Be("d");
        Warnings(logger, "no.such.key").Should().BeEmpty();
    }

    // ----- IsFeatureEnabled never throws -----

    [Theory]
    [InlineData("no.such.flag")]       // missing key
    [InlineData("flag.nomatch")]       // present flag, no matching rule
    [InlineData("s.false")]            // non-bool value
    [InlineData("i.thirty")]           // non-bool value
    [InlineData("env.missing.bool")]   // unset ENV_VAR
    [InlineData("secret.bad")]         // decryption failure
    [InlineData("i.malformed")]        // malformed value
    public async Task IsFeatureEnabled_NeverThrows_ReturnsFalse_AndLogsOncePerKey(string key)
    {
        var logger = new RecordingLogger();
        // OnNoDefault.Throw must not reach IsFeatureEnabled.
        await using var client = await NewClientAsync(OnNoDefault.Throw, logger);
        for (int i = 0; i < 3; i++)
        {
            var act = () => client.IsFeatureEnabled(key);
            act.Should().NotThrow().Which.Should().BeFalse();
        }
        Warnings(logger, key).Should().ContainSingle();
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
