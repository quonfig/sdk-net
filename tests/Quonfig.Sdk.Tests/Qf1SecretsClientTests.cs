using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Tests.Crypto;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qf1 secrets through the public client (qfg-8seuf.13; spec sections 8, 9, 13.3, 13.5, 13.7):
/// datadir loads <c>secrets/</c> last and the directory makes a document a secret; the wire maps
/// <c>type: "secret"</c>; a missing keyset fails only that get, never startup; parallel first gets;
/// no failing path leaks key material, env values, payload or plaintext; typed getters never echo a
/// secret's plaintext; telemetry reports type <c>secret</c> with the marker over the stored string.
/// Keysets come from <c>qf1-vectors.json</c> (test keys) through <see cref="QuonfigOptions.EnvLookup"/>.
/// </summary>
public sealed class Qf1SecretsClientTests : IDisposable
{
    private const string SecretKey = "db.password";
    private const string PlainKey = "plain.greeting";

    private static readonly JsonElement Bare = Qf1Vectors.Case("positive", "bare-default-keyset");
    private static readonly string BareCiphertext = Bare.GetProperty("ciphertext").GetString()!;
    private static readonly string BarePlaintext = Bare.GetProperty("plaintext").GetString()!;
    private static readonly Dictionary<string, string> PositiveEnv =
        Qf1Vectors.Env(Qf1Vectors.Root.GetProperty("positiveEnv"));

    private readonly string _root;

    public Qf1SecretsClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-qf1-client-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        Write("configs", PlainKey, Doc(PlainKey, "config", StringValue("hi", confidential: false)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Write(string dir, string key, string json)
    {
        var path = Path.Combine(_root, dir);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, key + ".json"), json);
    }

    private static string StringValue(string value, bool confidential) =>
        "{ \"type\": \"string\", \"value\": " + JsonSerializer.Serialize(value) +
        (confidential ? ", \"confidential\": true" : "") + " }";

    private static string Doc(string key, string type, string valueJson) =>
        "{ \"id\": \"id-" + key + "\", \"key\": \"" + key + "\", \"type\": \"" + type + "\", \"valueType\": \"string\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " + valueJson + " } ] } }";

    private async Task<Quonfig> NewClientAsync(Func<string, string?> env, CaptureLogger? logger = null, OnNoDefault onNoDefault = OnNoDefault.Throw)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            EnvLookup = env,
            OnNoDefault = onNoDefault,
            Logger = logger,
            EnableQuonfigUserContext = false,
            DatadirAutoReload = false,
            CollectEvaluationSummaries = false,
            ContextUploadMode = ContextUploadMode.None,
        });
        await client.InitAsync();
        return client;
    }

    private static Func<string, string?> EnvOf(Dictionary<string, string> env) =>
        name => env.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public async Task NoKeysetEnv_ClientBoots_ServesPlainKeys_AndOnlyTheSecretGetFails()
    {
        Write("secrets", SecretKey, Doc(SecretKey, "secret", StringValue(BareCiphertext, confidential: true)));
        var logger = new CaptureLogger();
        await using var client = await NewClientAsync(_ => null, logger);

        client.GetString(PlainKey).Should().Be("hi");
        client.GetString(SecretKey, defaultValue: "fallback").Should().Be("fallback");
        var details = client.GetStringDetails(SecretKey);
        details.Value.Should().BeNull();
        details.Reason.Should().Be(Reason.Error);
        details.ErrorMessage.Should().Contain("qf1 keyset_missing").And.Contain("QUONFIG_KEYSET");
        var ex = ((Action)(() => client.GetString(SecretKey))).Should().Throw<QuonfigDecryptionException>().Which;
        ex.Message.Should().StartWith("config \"" + SecretKey + "\": qf1 keyset_missing: ");
        // Still serving after the failure.
        client.GetString(PlainKey).Should().Be("hi");
    }

    [Fact]
    public async Task Datadir_SecretsLoadedLast_ShadowConfigsTwin_AndTheDirectoryDecidesTheType()
    {
        // configs/ copy is the old format; secrets/ copy wins. The secrets/ file even claims
        // type "config": the directory, not the field, makes it a secret.
        Write("configs", SecretKey, Doc(SecretKey, "config",
            "{ \"type\": \"string\", \"value\": \"00--00--00\", \"confidential\": true, \"decryptWith\": \"k\" }"));
        Write("secrets", SecretKey, Doc(SecretKey, "config", StringValue(BareCiphertext, confidential: true)));
        await using var client = await NewClientAsync(EnvOf(PositiveEnv));

        client.GetString(SecretKey).Should().Be(BarePlaintext);
        var details = client.GetStringDetails(SecretKey);
        details.Value.Should().Be(BarePlaintext);
        details.Reason.Should().Be(Reason.Static);
    }

    [Fact]
    public async Task Datadir_AConfigsValueWithDecryptWith_FailsInsteadOfReturningCiphertext()
    {
        Write("configs", "legacy.secret", Doc("legacy.secret", "config",
            "{ \"type\": \"string\", \"value\": \"00--00--00\", \"confidential\": true, \"decryptWith\": \"k\" }"));
        await using var client = await NewClientAsync(EnvOf(PositiveEnv));
        client.GetString("legacy.secret", defaultValue: "d").Should().Be("d");
        ((Action)(() => client.GetString("legacy.secret"))).Should().Throw<QuonfigDecryptionException>()
            .WithMessage("*decryptWith*");
    }

    [Fact]
    public void Wire_TypeSecret_ParsesAsSecret()
    {
        using var doc = JsonDocument.Parse(Doc(SecretKey, "secret", StringValue(BareCiphertext, confidential: true)));
        ConfigRowParser.ParseRow(SecretKey, doc.RootElement).Type.Should().Be(ConfigType.Secret);
    }

    private sealed class CapturingSender : ITelemetrySender
    {
        private readonly object _gate = new();
        private readonly List<IDictionary<string, object?>> _envelopes = new();

        public IDictionary<string, object?>[] Envelopes
        {
            get { lock (_gate) { return _envelopes.ToArray(); } }
        }

        public Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken)
        {
            lock (_gate) { _envelopes.Add(payload); }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Wire_SecretEntry_Decrypts_AndTelemetryReportsSecretWithTheStoredCiphertextMarker()
    {
        // No "confidential": true on the stored value: a secret is redacted anyway.
        var entry = Doc(SecretKey, "secret", StringValue(BareCiphertext, confidential: false));
        var envelope = "{\"meta\":{\"version\":\"v1\",\"environment\":\"production\",\"generation\":5},\"configs\":[" + entry + "]}";
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/api/v2/configs").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("ETag", "\"v1\"")
                .WithBody(envelope));

        var sender = new CapturingSender();
        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = "qf1-wire-key",
            ApiUrls = new[] { server.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromSeconds(5),
            TelemetrySender = sender,
            TelemetryInitialDelay = TimeSpan.FromMinutes(5),
            TelemetryFlushInterval = TimeSpan.FromMinutes(5),
            OnNoDefault = OnNoDefault.Ignore,
            EnvLookup = EnvOf(PositiveEnv),
            EnableQuonfigUserContext = false,
        });
        await client.InitAsync();

        client.GetString(SecretKey).Should().Be(BarePlaintext);
        await client.CloseAsync();

        var wire = JsonSerializer.Serialize(sender.Envelopes);
        wire.Should().NotContain(BarePlaintext);
        var summary = sender.Envelopes
            .SelectMany(env => (IEnumerable<IDictionary<string, object?>>)env["events"]!)
            .Where(e => e.ContainsKey("summaries"))
            .Select(e => (IDictionary<string, object?>)e["summaries"]!)
            .SelectMany(s => (IEnumerable<Dictionary<string, object?>>)s["summaries"]!)
            .Where(s => (string?)s["key"] == SecretKey)
            .Should().ContainSingle().Subject;
        summary["type"].Should().Be("secret");
        var counter = ((IEnumerable<Dictionary<string, object?>>)summary["counters"]!).Should().ContainSingle().Subject;
        var selected = (IDictionary<string, object?>)counter["selectedValue"]!;
        selected["string"].Should().Be(Resolver.RedactedMarker(BareCiphertext));
        selected["string"].Should().NotBe(Resolver.RedactedMarker(BarePlaintext));
    }

    [Fact]
    public async Task ParallelFirstGets_AllDecrypt()
    {
        Write("secrets", SecretKey, Doc(SecretKey, "secret", StringValue(BareCiphertext, confidential: true)));
        for (int round = 0; round < 20; round++)
        {
            await using var client = await NewClientAsync(EnvOf(PositiveEnv));
            var failures = new ConcurrentBag<string>();
            using var gate = new ManualResetEventSlim(false);
            var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
            {
                gate.Wait();
                for (int i = 0; i < 10; i++)
                {
                    var d = client.GetStringDetails(SecretKey);
                    if (d.Value != BarePlaintext) failures.Add(d.ErrorMessage ?? "wrong value");
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            gate.Set();
            threads.ForEach(t => t.Join());
            failures.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ParallelFirstGets_WhileTheKeysetEnvChanges_NeverMixResults()
    {
        // The keyset cache is keyed on the env var's value: a get with the keyset unset fails,
        // a get with it set succeeds, whatever the other threads saw first.
        Write("secrets", SecretKey, Doc(SecretKey, "secret", StringValue(BareCiphertext, confidential: true)));
        var local = new ThreadLocal<bool>();
        await using var client = await NewClientAsync(name => local.Value && PositiveEnv.TryGetValue(name, out var v) ? v : null);
        var failures = new ConcurrentBag<string>();
        using var gate = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 16).Select(n => new Thread(() =>
        {
            local.Value = n % 2 == 0;
            gate.Wait();
            for (int i = 0; i < 20; i++)
            {
                var d = client.GetStringDetails(SecretKey);
                bool ok = local.Value ? d.Value == BarePlaintext : d.Value is null && d.ErrorMessage!.Split(' ').Contains("keyset_missing:");
                if (!ok) failures.Add(n + ": " + (d.ErrorMessage ?? d.Value));
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        gate.Set();
        threads.ForEach(t => t.Join());
        local.Dispose();
        failures.Should().BeEmpty();
    }

    public static IEnumerable<object[]> NegativeNames => Qf1Vectors.Names("negative");

    [Theory]
    [MemberData(nameof(NegativeNames))]
    public async Task EveryFailingVector_LeaksNothing_InMessages_Exceptions_OrLogs(string name)
    {
        var v = Qf1Vectors.Case("negative", name);
        var ciphertext = v.GetProperty("ciphertext").GetString()!;
        var env = Qf1Vectors.Env(v.GetProperty("env"));
        Write("secrets", SecretKey, Doc(SecretKey, "secret", StringValue(ciphertext, confidential: true)));
        await AssertNoLeak(ciphertext, env, plaintext: null);
    }

    public static IEnumerable<object[]> PositiveNames => Qf1Vectors.Names("positive", e => e.GetProperty("plaintext").GetString()!.Length >= 5);

    [Theory]
    [MemberData(nameof(PositiveNames))]
    public async Task KeyNotHeldAndKeysetMissing_OnARealSecret_LeakNoPlaintext(string name)
    {
        var v = Qf1Vectors.Case("positive", name);
        var ciphertext = v.GetProperty("ciphertext").GetString()!;
        var keyset = v.GetProperty("keyset").GetString()!;
        var plaintext = v.GetProperty("plaintext").GetString()!;
        Write("secrets", SecretKey, Doc(SecretKey, "secret", StringValue(ciphertext, confidential: true)));

        // key_not_held: the keyset holds another keyset's key.
        var otherKeyset = PositiveEnv.First(kv => kv.Key != keyset).Value;
        var wrong = new Dictionary<string, string>(PositiveEnv) { [keyset] = otherKeyset };
        await AssertNoLeak(ciphertext, wrong, plaintext);

        // keyset_missing.
        var missing = new Dictionary<string, string>(PositiveEnv);
        missing.Remove(keyset);
        await AssertNoLeak(ciphertext, missing, plaintext);
    }

    private async Task AssertNoLeak(string ciphertext, Dictionary<string, string> env, string? plaintext)
    {
        var forbidden = new List<string> { "qf1-priv-" };
        foreach (var value in env.Values)
        {
            foreach (var piece in value.Split(','))
            {
                var p = piece.Trim(' ', '\t', '\r', '\n');
                if (p.Length >= 8) forbidden.Add(p);
            }
        }
        var parts = ciphertext.Split(':');
        if (parts.Length == 4 && parts[3].Length >= 16) forbidden.Add(parts[3]);
        if (!string.IsNullOrEmpty(plaintext)) forbidden.Add(plaintext!);

        var logger = new CaptureLogger();
        var texts = new List<string>();
        await using (var client = await NewClientAsync(EnvOf(env), logger))
        {
            var details = client.GetStringDetails(SecretKey);
            details.Value.Should().BeNull();
            details.Reason.Should().Be(Reason.Error);
            texts.Add(details.ErrorMessage ?? "");
            client.GetString(SecretKey, defaultValue: "d").Should().Be("d");

            var ex = ((Action)(() => client.GetString(SecretKey))).Should().Throw<QuonfigDecryptionException>().Which;
            ex.InnerException.Should().BeNull();
            ex.Data.Count.Should().Be(0);
            ex.Message.Should().Contain("qf1 ");
            texts.Add(ex.Message);
            texts.Add(ex.ToString());
        }
        texts.AddRange(logger.Lines.Select(l => l.Message));
        texts.AddRange(logger.Exceptions.Select(e => e.ToString()));

        foreach (var text in texts)
        {
            foreach (var f in forbidden) text.Should().NotContain(f);
        }
    }

    [Fact]
    public async Task TypedGetters_OnADecryptedSecret_NeverEchoThePlaintext()
    {
        Write("secrets", SecretKey, Doc(SecretKey, "secret", StringValue(BareCiphertext, confidential: true)));
        var logger = new CaptureLogger();
        var texts = new List<string>();
        await using (var client = await NewClientAsync(EnvOf(PositiveEnv), logger))
        {
            void Capture(Action a)
            {
                try { a(); }
                catch (QuonfigException ex) { texts.Add(ex.ToString()); }
            }

            Capture(() => client.GetInt(SecretKey));
            Capture(() => client.GetLong(SecretKey));
            Capture(() => client.GetBool(SecretKey));
            Capture(() => client.GetDouble(SecretKey));
            Capture(() => client.GetStringList(SecretKey));
            Capture(() => client.GetJson(SecretKey));
            Capture(() => client.GetDuration(SecretKey));
            client.GetInt(SecretKey, defaultValue: 7).Should().Be(7);
            client.GetBool(SecretKey, defaultValue: true).Should().BeTrue();
            client.IsFeatureEnabled(SecretKey).Should().BeFalse();
            texts.Add(client.GetIntDetails(SecretKey).ErrorMessage ?? "");
            texts.Add(client.GetLongDetails(SecretKey).ErrorMessage ?? "");
            texts.Add(client.GetBoolDetails(SecretKey).ErrorMessage ?? "");
            texts.Add(client.GetDoubleDetails(SecretKey).ErrorMessage ?? "");
            texts.Add(client.GetStringListDetails(SecretKey).ErrorMessage ?? "");
            texts.Add(client.GetJsonDetails(SecretKey).ErrorMessage ?? "");
            texts.Add(client.GetDurationDetails(SecretKey).ErrorMessage ?? "");
            // The string getter returns it.
            client.GetString(SecretKey).Should().Be(BarePlaintext);
        }
        texts.Should().HaveCountGreaterThan(7);
        texts.AddRange(logger.Lines.Select(l => l.Message));
        foreach (var t in texts) t.Should().NotContain(BarePlaintext).And.NotContain(BareCiphertext.Split(':')[3]);
    }
}
