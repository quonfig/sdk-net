using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Exceptions;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

// CA1031: the sweep tests catch any exception to report every failing fixture value at once.
// CA1846/CA1865/CA2249: the span, char and Contains(string, StringComparison) overloads are not available on net48.
#pragma warning disable CA1031, CA1846, CA1865, CA2249

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-2agi.12: every string in the shared duration grammar fixture
/// (<c>integration-test-data/tests/duration/grammar.yaml</c>) driven through the PUBLIC
/// <see cref="Quonfig.GetDuration"/> / <see cref="Quonfig.GetDurationDetails"/> getters, from
/// both sources (a stored value and an ENV_VAR-provided value).
/// <list type="bullet">
/// <item><description>valid: exactly the fixture's integer millisecond count.</description></item>
/// <item><description>invalid: never a parsed value. With a default the default is returned
/// (Reason=Error) and a warning is logged once per key; with no default under
/// <see cref="OnNoDefault.Throw"/> a <see cref="QuonfigCoercionException"/> is raised; no
/// exception at load time.</description></item>
/// </list>
/// </summary>
public sealed class DurationGrammarTests : IDisposable
{
    private const string EnvPrefix = "QFG_DURATION_GRAMMAR_";
    private static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(7000);

    private static readonly Lazy<(List<(string Value, long Millis)> Valid, List<string> Invalid)> Fixture =
        new(LoadFixture);

    private readonly string _root;
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public DurationGrammarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-duration-grammar-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);

        var (valid, invalid) = Fixture.Value;
        for (var i = 0; i < valid.Count; i++) Write(dir, "valid", i, valid[i].Value);
        for (var i = 0; i < invalid.Count; i++) Write(dir, "invalid", i, invalid[i]);
    }

    private void Write(string dir, string kind, int i, string value)
    {
        var stored = StoredKey(kind, i);
        File.WriteAllText(Path.Combine(dir, stored + ".json"),
            Config(stored, "{ \"type\": \"duration\", \"value\": " + JsonSerializer.Serialize(value) + " }"));
        var provided = ProvidedKey(kind, i);
        var envVar = EnvPrefix + kind.ToUpperInvariant() + "_" + i.ToString(CultureInfo.InvariantCulture);
        _env[envVar] = value;
        File.WriteAllText(Path.Combine(dir, provided + ".json"),
            Config(provided, "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"" + envVar + "\" } }"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string StoredKey(string kind, int i) => "duration." + kind + "." + i.ToString(CultureInfo.InvariantCulture);
    private static string ProvidedKey(string kind, int i) => "provided.duration." + kind + "." + i.ToString(CultureInfo.InvariantCulture);

    private static string Config(string key, string valueJson) =>
        "{ \"id\": \"1\", \"key\": \"" + key + "\", \"type\": \"config\", \"valueType\": \"duration\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }";

    private async Task<Quonfig> NewClientAsync(OnNoDefault policy, ILogger? logger = null)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = policy,
            EnvLookup = name => _env.TryGetValue(name, out var v) ? v : null,
            Logger = logger,
            CollectEvaluationSummaries = false,
        });
        await client.InitAsync();
        return client;
    }

    private static string[] Keys(string kind, int i) => new[] { StoredKey(kind, i), ProvidedKey(kind, i) };

    private static string Show(string s) => JsonSerializer.Serialize(s);

    [Fact]
    public void Fixture_IsLoaded()
    {
        Fixture.Value.Valid.Should().NotBeEmpty();
        Fixture.Value.Invalid.Should().Contain("PT5S\n").And.Contain("PT٥S").And.Contain("");
    }

    [Fact]
    public async Task Valid_ReturnsExactMillis_StoredAndEnvVar()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        var valid = Fixture.Value.Valid;
        var failures = new List<string>();
        for (var i = 0; i < valid.Count; i++)
        {
            foreach (var key in Keys("valid", i))
            {
                var want = valid[i].Millis * TimeSpan.TicksPerMillisecond;
                TimeSpan? got;
                try { got = client.GetDuration(key, defaultValue: Fallback); }
                catch (Exception e) { failures.Add($"{key} {Show(valid[i].Value)}: threw {e.GetType().Name}"); continue; }
                var details = client.GetDurationDetails(key);
                if (got?.Ticks != want || details.Value?.Ticks != want || details.Reason == Reason.Error)
                {
                    failures.Add($"{key} {Show(valid[i].Value)}: got {got?.Ticks / TimeSpan.TicksPerMillisecond} ms " +
                                 $"(details {details.Reason}), want {valid[i].Millis} ms");
                }
            }
        }
        failures.Should().BeEmpty("every fixture value must pass:\n" + string.Join("\n", failures));
    }

    [Theory]
    [InlineData(OnNoDefault.Throw)]
    [InlineData(OnNoDefault.Warn)]
    [InlineData(OnNoDefault.Ignore)]
    public async Task Invalid_WithDefault_ReturnsDefault_ReasonError_WarnsOncePerKey(OnNoDefault policy)
    {
        var logger = new RecordingLogger();
        await using var client = await NewClientAsync(policy, logger);
        var invalid = Fixture.Value.Invalid;
        var failures = new List<string>();
        for (var i = 0; i < invalid.Count; i++)
        {
            foreach (var key in Keys("invalid", i))
            {
                TimeSpan? got;
                try
                {
                    got = client.GetDuration(key, defaultValue: Fallback);
                    client.GetDuration(key, defaultValue: Fallback);
                }
                catch (Exception e) { failures.Add($"{key} {Show(invalid[i])}: threw {e.GetType().Name}"); continue; }
                var details = client.GetDurationDetails(key, defaultValue: Fallback);
                if (got != Fallback || details.Value != Fallback || details.Reason != Reason.Error)
                {
                    failures.Add($"{key} {Show(invalid[i])}: got {got?.Ticks / TimeSpan.TicksPerMillisecond} ms " +
                                 $"(details {details.Reason}), want the 7000 ms default with Reason=Error");
                }
                var warnings = logger.Messages(MelLogLevel.Warning)
                    .Count(m => m.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0);
                if (warnings != 1) failures.Add($"{key} {Show(invalid[i])}: {warnings} warnings, want 1");
            }
        }
        failures.Should().BeEmpty("every fixture value must pass:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Invalid_NoDefault_Throw_RaisesCoercion()
    {
        await using var client = await NewClientAsync(OnNoDefault.Throw);
        var invalid = Fixture.Value.Invalid;
        var failures = new List<string>();
        for (var i = 0; i < invalid.Count; i++)
        {
            foreach (var key in Keys("invalid", i))
            {
                try
                {
                    var got = client.GetDuration(key);
                    failures.Add($"{key} {Show(invalid[i])}: returned {got?.Ticks / TimeSpan.TicksPerMillisecond} ms, want QuonfigCoercionException");
                }
                catch (QuonfigCoercionException) { }
                catch (Exception e) { failures.Add($"{key} {Show(invalid[i])}: threw {e.GetType().Name}, want QuonfigCoercionException"); }
            }
        }
        failures.Should().BeEmpty("every fixture value must pass:\n" + string.Join("\n", failures));
    }

    [Theory]
    [InlineData(OnNoDefault.Warn)]
    [InlineData(OnNoDefault.Ignore)]
    public async Task Invalid_NoDefault_ReturnDefaultPolicies_ReturnNull(OnNoDefault policy)
    {
        await using var client = await NewClientAsync(policy);
        var invalid = Fixture.Value.Invalid;
        var failures = new List<string>();
        for (var i = 0; i < invalid.Count; i++)
        {
            foreach (var key in Keys("invalid", i))
            {
                var got = client.GetDuration(key);
                if (got is not null) failures.Add($"{key} {Show(invalid[i])}: returned {got.Value.Ticks / TimeSpan.TicksPerMillisecond} ms, want null");
            }
        }
        failures.Should().BeEmpty("every fixture value must pass:\n" + string.Join("\n", failures));
    }

    // ---------------------------------------------------------------------------
    // Minimal reader for grammar.yaml's fixed shape (no YAML dependency):
    //   valid:   - { value: "<double-quoted>", millis: <int> }
    //   invalid: - "<double-quoted>"
    // ---------------------------------------------------------------------------

    private static (List<(string, long)>, List<string>) LoadFixture()
    {
        var valid = new List<(string, long)>();
        var invalid = new List<string>();
        string? section = null;
        foreach (var rawLine in File.ReadAllLines(LocateFixture()))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line == "valid:" || line == "invalid:") { section = line.TrimEnd(':'); continue; }
            if (!line.StartsWith("- ", StringComparison.Ordinal) || section is null)
                throw new InvalidDataException("unexpected grammar.yaml line: " + rawLine);
            var pos = line.IndexOf("\"", StringComparison.Ordinal);
            var value = ReadQuoted(line, ref pos);
            if (section == "invalid") { invalid.Add(value); continue; }
            var m = line.IndexOf("millis:", pos, StringComparison.Ordinal);
            var digits = new string(line.Substring(m + "millis:".Length).Trim().TakeWhile(char.IsDigit).ToArray());
            valid.Add((value, long.Parse(digits, CultureInfo.InvariantCulture)));
        }
        return (valid, invalid);
    }

    private static string ReadQuoted(string line, ref int pos)
    {
        if (pos < 0) throw new InvalidDataException("expected a double-quoted string: " + line);
        var sb = new StringBuilder();
        for (pos++; pos < line.Length; pos++)
        {
            var c = line[pos];
            if (c == '"') { pos++; return sb.ToString(); }
            if (c != '\\') { sb.Append(c); continue; }
            var e = line[++pos];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u':
                    sb.Append((char)int.Parse(line.Substring(pos + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    pos += 4;
                    break;
                default: throw new InvalidDataException("unsupported escape \\" + e + ": " + line);
            }
        }
        throw new InvalidDataException("unterminated string: " + line);
    }

    private static string LocateFixture()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "integration-test-data", "tests", "duration", "grammar.yaml");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("integration-test-data/tests/duration/grammar.yaml not found above " + AppContext.BaseDirectory);
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
