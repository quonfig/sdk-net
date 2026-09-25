using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Telemetry;
using Quonfig.Sdk.Tests.Telemetry.Helpers;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests.Telemetry;

using LogLevel = Microsoft.Extensions.Logging.LogLevel;

/// <summary>
/// Telemetry transport contract T1-T8 (qfg-y8je.10): sdk-net implementation of
/// integration-test-data/chaos/telemetry-transport-contract.md, mirroring the sdk-node reference
/// (test/telemetry-transport.test.ts).
///
/// <para>Fixture: a real <see cref="Quonfig"/> client (datafile mode + an SDK key, so the real
/// reporter, retained queue and <see cref="HttpTelemetrySender"/> run) pointed at a scriptable
/// WireMock.Net stub; a <see cref="ManualTelemetryClock"/> injected through the internal
/// <see cref="QuonfigOptions.TelemetryClock"/> seam drives the tick timer, the per-POST deadline and
/// every telemetry time comparison; a capturing <see cref="ILogger"/> records every level. Only the
/// endpoint, the clock and the logger are mocked.</para>
/// </summary>
public sealed class TelemetryTransportContractTests : IAsyncDisposable
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private readonly TelemetryStub _stub = new();
    private readonly CaptureLogger _logger = new();
    private readonly ManualTelemetryClock _clock = new();
    private Quonfig? _client;

    public async ValueTask DisposeAsync()
    {
        _stub.Dispose();
        if (_client is not null) await _client.DisposeAsync();
    }

    private static ConfigEnvelope Envelope()
    {
        var configs = Enumerable.Range(0, 20).Select(i =>
        {
            string key = FormattableString.Invariant($"cfg-{i:00}");
            string json = "{\"id\":\"id-" + key + "\",\"key\":\"" + key + "\",\"type\":\"config\",\"valueType\":\"string\","
                + "\"default\":{\"rules\":[{\"criteria\":[],\"value\":{\"type\":\"string\",\"value\":\"v-" + key + "\"}}]}}";
            return JsonDocument.Parse(json).RootElement.Clone();
        }).ToList();
        return new ConfigEnvelope(configs, new Meta("v1", "production", "ws-test"));
    }

    private async Task<(Quonfig Q, TelemetryReporter R)> ClientAsync(Action<QuonfigOptions>? configure = null)
    {
        var opts = new QuonfigOptions
        {
            SdkKey = "test-sdk-key",
            DatafileEnvelope = Envelope(),
            TelemetryUrl = _stub.Url,
            EnableQuonfigUserContext = false,
            // Bodies identify their evaluation set by example-context key.
            ContextUploadMode = ContextUploadMode.PeriodicExample,
            Logger = _logger,
            TelemetryClock = _clock,
        };
        configure?.Invoke(opts);
        _client = new Quonfig(opts);
        await _client.InitAsync();
        _logger.Clear();
        return (_client, _client.TelemetryReporter!);
    }

    /// <summary>
    /// Evaluation set <paramref name="tag"/>: three evaluations over configs <c>cfgBase..cfgBase+2</c>,
    /// each with a distinct context key <c>{tag}-i</c>, so a body identifies its set by example-context key.
    /// </summary>
    private static void Record(Quonfig q, string tag, int cfgBase = 0)
    {
        for (int i = 0; i < 3; i++)
        {
            string key = FormattableString.Invariant($"cfg-{(cfgBase + i) % 20:00}");
            q.GetString(key, Ctx(FormattableString.Invariant($"{tag}-{i}")));
        }
    }

    private static ContextSet Ctx(string userKey) =>
        new() { ["user"] = new ContextProperties { ["key"] = userKey } };

    private bool Has(int i, string tag) => ContainsOrdinal(_stub.Text(i), "\"" + tag + "-0\"");

    private bool HasConfig(int i, string key) => ContainsOrdinal(_stub.Text(i), "\"key\":\"" + key + "\"");

    private static bool ContainsOrdinal(string haystack, string needle)
    {
#if NET8_0_OR_GREATER
        return haystack.Contains(needle, StringComparison.Ordinal);
#else
        return haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
#endif
    }

    /// <summary>Advance the clock, then let any POSTs the tick started reach the stub and settle.</summary>
    private async Task AdvanceAsync(TelemetryReporter r, TimeSpan by, int? expectPosts = null)
    {
        _clock.Advance(by);
        if (expectPosts is not null) await _stub.WaitForPostsAsync(expectPosts.Value);
        await r.WhenIdleAsync();
    }

    // ---------------------------------------------------------------- T1

    [Fact]
    public async Task T1_TimeoutAbortsAndRetains()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        _stub.Script(StubStep.HangUntilReleased(), StubStep.Answer(200));

        _clock.Advance(Min); // tick 1: POST 0 hangs
        await _stub.WaitForPostsAsync(1);
        await AdvanceAsync(r, TimeSpan.FromSeconds(15)); // the request is aborted
        _stub.PostCount.Should().Be(1);
        r.RetainedCount.Should().Be(1);
        _logger.LogCount(LogLevel.Warning).Should().Be(0);
        _logger.LogCount(LogLevel.Error).Should().Be(0);
        _logger.LogCount(LogLevel.Debug, @"Telemetry POST failed \(timeout\)").Should().Be(1);

        await AdvanceAsync(r, TimeSpan.FromSeconds(45), 2); // tick 2, 45s after the failure
        _stub.PostCount.Should().Be(2);
        _stub.Sha(1).Should().Be(_stub.Sha(0));
        r.RetainedCount.Should().Be(0);
        _logger.LogCount(LogLevel.Information, "recover").Should().Be(1);
        _logger.LogCount(LogLevel.Warning).Should().Be(0);
    }

    [Fact]
    public async Task T1_Defaults_Timeout15s_Connect5s_Interval60s()
    {
        var (_, r) = await ClientAsync();
        r.Settings.Timeout.Should().Be(TimeSpan.FromMilliseconds(15_000));
        r.Settings.ConnectTimeout.Should().Be(TimeSpan.FromMilliseconds(5_000));
        r.Settings.FlushInterval.Should().Be(TimeSpan.FromMilliseconds(60_000));
        var opts = new QuonfigOptions();
        opts.TelemetryTimeout.Should().Be(TimeSpan.FromSeconds(15));
        opts.TelemetryConnectTimeout.Should().Be(TimeSpan.FromSeconds(5));
        opts.TelemetryFlushInterval.Should().Be(TimeSpan.FromSeconds(60));
        HttpTelemetrySender.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Defaults_ContextUploadModeIsPeriodicExample()
    {
        // Decided 2026-09-25 (policy plan, "uniform defaults"): periodic_example in every SDK.
        new QuonfigOptions().ContextUploadMode.Should().Be(ContextUploadMode.PeriodicExample);
    }

    // ---------------------------------------------------------------- T2

    [Fact]
    public async Task T2_5xxRetainsVerbatimAndResends()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A", 0);
        _stub.Script(StubStep.Answer(503), StubStep.Answer(503), StubStep.Answer(200), StubStep.Answer(200));

        await AdvanceAsync(r, Min, 1);
        r.RetainedCount.Should().Be(1);

        Record(q, "B", 3);
        await AdvanceAsync(r, Min, 2);
        r.RetainedCount.Should().Be(2);

        await AdvanceAsync(r, Min, 4);
        _stub.PostCount.Should().Be(4);
        _stub.Sha(1).Should().Be(_stub.Sha(0));
        _stub.Sha(2).Should().Be(_stub.Sha(0));
        Has(3, "B").Should().BeTrue();
        HasConfig(3, "cfg-03").Should().BeTrue();
        Has(3, "A").Should().BeFalse();
        HasConfig(3, "cfg-00").Should().BeFalse();
        r.RetainedCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- T3

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task T3a_AuthStatusDisablesTelemetryForTheProcess(int status)
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        _stub.Script(StubStep.Answer(503), StubStep.Answer(status));
        await AdvanceAsync(r, Min, 1);
        r.RetainedCount.Should().Be(1);

        Record(q, "B", 3);
        await AdvanceAsync(r, Min, 2);
        _logger.LogCount(LogLevel.Error, status.ToString(System.Globalization.CultureInfo.InvariantCulture)).Should().Be(1);
        r.TelemetryEnabled.Should().BeFalse();
        r.RetainedCount.Should().Be(0);
        r.TimerActive.Should().BeFalse();
        _logger.LogCount(LogLevel.Warning).Should().Be(0);

        for (int k = 0; k < 3; k++)
        {
            Record(q, "C" + k, 6);
            await AdvanceAsync(r, Min);
        }
        await r.FlushAsync(default);
        _stub.PostCount.Should().Be(2);
        _logger.LogCount(LogLevel.Error).Should().Be(1);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(422)]
    public async Task T3b_OtherClientErrorDropsTheBatchAndKeepsTicking(int status)
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        _stub.Script(StubStep.Answer(status, body: "bad payload"), StubStep.Answer(200));
        await AdvanceAsync(r, Min, 1);
        r.RetainedCount.Should().Be(0);
        _logger.LogCount(LogLevel.Error).Should().Be(1);
        _logger.LogCount(LogLevel.Error, "bad payload").Should().Be(1);
        _logger.LogCount(LogLevel.Warning).Should().Be(0);
        r.TelemetryEnabled.Should().BeTrue();

        Record(q, "B", 3);
        await AdvanceAsync(r, Min, 2);
        _stub.PostCount.Should().Be(2);
        _stub.Sha(1).Should().NotBe(_stub.Sha(0));
    }

    [Fact]
    public async Task T3_408IsRetryable_AntiVacuity()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        _stub.Script(StubStep.Answer(408));
        await AdvanceAsync(r, Min, 1);
        r.RetainedCount.Should().Be(1);
        r.TelemetryEnabled.Should().BeTrue();
        _logger.LogCount(LogLevel.Error).Should().Be(0);
    }

    // ---------------------------------------------------------------- T4

    [Fact]
    public async Task T4a_ThirtySecondFloorAfterAFailure()
    {
        var (q, r) = await ClientAsync(o => o.TelemetryFlushInterval = TimeSpan.FromSeconds(8));
        Record(q, "A");
        _stub.Script(StubStep.Answer(503), StubStep.Answer(200));
        var step = TimeSpan.FromSeconds(8);
        await AdvanceAsync(r, step, 1); // F = 8s
        for (int k = 0; k < 3; k++)
        {
            await AdvanceAsync(r, step); // F+8, F+16, F+24
            _stub.PostCount.Should().Be(1);
        }
        await AdvanceAsync(r, step, 2); // F+32: first tick at or after F+30
        _stub.PostCount.Should().Be(2);
        _stub.Sha(1).Should().Be(_stub.Sha(0));
    }

    [Fact]
    public async Task T4b_RetryAfterDeltaSecondsHonored()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        _stub.Script(StubStep.Answer(429, retryAfter: "120"), StubStep.Answer(200));
        await AdvanceAsync(r, Min, 1); // F = 60s
        await AdvanceAsync(r, Min); // F+60
        await AdvanceAsync(r, TimeSpan.FromSeconds(59)); // F+119
        _stub.PostCount.Should().Be(1);
        await AdvanceAsync(r, TimeSpan.FromSeconds(1), 2); // F+120, the timer tick at 180s
        _stub.PostCount.Should().Be(2);
        _stub.Sha(1).Should().Be(_stub.Sha(0));
    }

    [Fact]
    public async Task T4c_RetryAfterClampedTo600s_AgedBatchDiscardedWithOneWarn()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A", 0);
        _stub.Script(StubStep.Answer(503, retryAfter: "3600"), StubStep.Answer(200));
        await AdvanceAsync(r, Min, 1); // F = 60s
        Record(q, "B", 3);
        for (int k = 0; k < 9; k++) await AdvanceAsync(r, Min); // up to F+540
        await AdvanceAsync(r, TimeSpan.FromSeconds(59)); // F+599
        _stub.PostCount.Should().Be(1);
        await AdvanceAsync(r, TimeSpan.FromSeconds(1), 2); // F+600 (tick 11 at 660s)
        _stub.PostCount.Should().Be(2);
        Has(1, "B").Should().BeTrue();
        Has(1, "A").Should().BeFalse();
        _logger.LogCount(LogLevel.Warning).Should().Be(1);
        _logger.LogCount(LogLevel.Warning, "older than 5 min").Should().Be(1);
    }

    [Fact]
    public async Task T4d_RetryAfterHttpDateHonored()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        string at = DateTimeOffset.FromUnixTimeMilliseconds(_clock.NowMs() + (3 * 60_000))
            .ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        _stub.Script(StubStep.Answer(503, retryAfter: at), StubStep.Answer(200));
        await AdvanceAsync(r, Min, 1); // F = 60s, Retry-After = 180s wall clock -> 120s after F
        await AdvanceAsync(r, Min); // F+60
        _stub.PostCount.Should().Be(1);
        await AdvanceAsync(r, Min, 2); // F+120
        _stub.Sha(1).Should().Be(_stub.Sha(0));
    }

    // ---------------------------------------------------------------- T5

    [Fact]
    public async Task T5_QueueCaps_FiveBatches_OldestEvicted_ResentOldestFirst()
    {
        var (q, r) = await ClientAsync();
        _stub.SetDefault(StubStep.Answer(503));
        var firstPost = new Dictionary<string, string>();
        for (int k = 1; k <= 8; k++)
        {
            Record(q, "E" + k, k);
            await AdvanceAsync(r, Min, _stub.PostCount + 1);
            int i = _stub.PostCount - 1;
            for (int s = 1; s <= k; s++)
            {
                if (Has(i, "E" + s) && !firstPost.ContainsKey("E" + s)) firstPost["E" + s] = _stub.Sha(i);
            }
            r.RetainedCount.Should().BeLessThanOrEqualTo(5);
            r.RetainedBytes.Should().BeLessThanOrEqualTo(r.Settings.MaxRetainedBytes);
        }
        r.RetainedCount.Should().Be(5);

        _stub.SetDefault(StubStep.Answer(200));
        int start = _stub.PostCount;
        await AdvanceAsync(r, Min, start + 5);
        _stub.PostCount.Should().Be(start + 5);
        var order = new[] { "E4", "E5", "E6", "E7", "E8" };
        for (int j = 0; j < order.Length; j++)
        {
            Has(start + j, order[j]).Should().BeTrue();
            if (firstPost.TryGetValue(order[j], out var sha)) _stub.Sha(start + j).Should().Be(sha);
        }
        for (int i = start; i < start + 5; i++) Has(i, "E1").Should().BeFalse();
        r.RetainedCount.Should().Be(0);
    }

    [Fact]
    public async Task T5_MaxAgeDiscardsBatchesOlderThanFiveMinutes()
    {
        var (q, r) = await ClientAsync();
        _stub.SetDefault(StubStep.Answer(503));
        for (int k = 1; k <= 3; k++)
        {
            Record(q, "E" + k, k);
            await AdvanceAsync(r, Min, _stub.PostCount + 1);
        }
        for (int k = 0; k < 6; k++) await AdvanceAsync(r, Min);
        r.RetainedCount.Should().Be(0);

        _stub.SetDefault(StubStep.Answer(200));
        int before = _stub.PostCount;
        await AdvanceAsync(r, Min);
        for (int i = before; i < _stub.PostCount; i++)
        {
            foreach (var tag in new[] { "E1", "E2", "E3" }) Has(i, tag).Should().BeFalse();
        }
    }

    [Fact]
    public async Task T5_OversizeBatchIsDroppedNotRetained()
    {
        var (q, r) = await ClientAsync(o => o.TelemetryMaxRetainedBytes = 4096);
        for (int k = 0; k < 20; k++) Record(q, "X" + k, k);
        _stub.Script(StubStep.Answer(503));
        await AdvanceAsync(r, Min, 1);
        _stub.Body(0).Length.Should().BeGreaterThan(4096);
        r.RetainedCount.Should().Be(0);
        r.RetainedBytes.Should().Be(0);
        _logger.LogCount(LogLevel.Warning).Should().Be(1);
        _logger.LogCount(LogLevel.Warning, "byte cap").Should().Be(1);
    }

    [Fact]
    public async Task T5_ShippedQueueDefaults_5_2097152_300000()
    {
        var (_, r) = await ClientAsync();
        r.Settings.MaxRetainedBatches.Should().Be(5);
        r.Settings.MaxRetainedBytes.Should().Be(2_097_152);
        r.Settings.MaxRetainedAge.Should().Be(TimeSpan.FromMilliseconds(300_000));
        var opts = new QuonfigOptions();
        opts.TelemetryMaxRetainedBatches.Should().Be(5);
        opts.TelemetryMaxRetainedBytes.Should().Be(2_097_152);
        opts.TelemetryMaxRetainedAge.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task T5_AggregatorDefaults_10000Each()
    {
        var (q, _) = await ClientAsync();
        q.EvaluationSummaries!.MaxDataSize.Should().Be(10_000);
        q.ContextShapes!.MaxDataSize.Should().Be(10_000);
        q.ExampleContexts!.MaxDataSize.Should().Be(10_000);
        var opts = new QuonfigOptions();
        opts.TelemetryMaxEvaluationSummaries.Should().Be(10_000);
        opts.TelemetryMaxContextShapeFields.Should().Be(10_000);
        opts.TelemetryMaxExampleContexts.Should().Be(10_000);
    }

    [Fact]
    public async Task T5_AggregatorCaps_EvaluationSummaries_ExistingKeyKeepsCounting()
    {
        var (q, r) = await ClientAsync(o => o.TelemetryMaxEvaluationSummaries = 3);
        for (int i = 0; i < 6; i++) q.GetString(FormattableString.Invariant($"cfg-0{i}"), Ctx("u"));
        q.GetString("cfg-00", Ctx("u"));
        await AdvanceAsync(r, Min, 1);
        var summaries = Event(0, "summaries").GetProperty("summaries").EnumerateArray().ToList();
        summaries.Select(s => s.GetProperty("key").GetString()).OrderBy(k => k, StringComparer.Ordinal)
            .Should().Equal("cfg-00", "cfg-01", "cfg-02");
        var c0 = summaries.Single(s => s.GetProperty("key").GetString() == "cfg-00");
        c0.GetProperty("counters")[0].GetProperty("count").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task T5_AggregatorCaps_ContextShapeFields()
    {
        var (q, r) = await ClientAsync(o => o.TelemetryMaxContextShapeFields = 3);
        q.GetString("cfg-00", new ContextSet
        {
            ["user"] = new ContextProperties { ["key"] = "u", ["a"] = 1, ["b"] = 2 },
            ["team"] = new ContextProperties { ["c"] = 3, ["d"] = 4 },
        });
        await AdvanceAsync(r, Min, 1);
        var shapes = Event(0, "contextShapes").GetProperty("shapes").EnumerateArray().ToList();
        shapes.SelectMany(s => s.GetProperty("fieldTypes").EnumerateObject()).Should().HaveCount(3);
    }

    [Fact]
    public async Task T5_AggregatorCaps_ExampleContexts()
    {
        var (q, r) = await ClientAsync(o => o.TelemetryMaxExampleContexts = 3);
        for (int i = 0; i < 6; i++) q.GetString("cfg-00", Ctx("u" + i));
        await AdvanceAsync(r, Min, 1);
        Event(0, "exampleContexts").GetProperty("examples").GetArrayLength().Should().Be(3);
    }

    private JsonElement Event(int post, string name)
    {
        using var doc = JsonDocument.Parse(_stub.Body(post));
        foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            if (e.TryGetProperty(name, out var v)) return v.Clone();
        }
        throw new InvalidOperationException("no " + name + " event in POST " + post);
    }

    // ---------------------------------------------------------------- T6

    [Fact]
    public async Task T6a_Blip_NoWarn_OneRecoveryInfo()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A");
        _stub.Script(StubStep.Answer(503), StubStep.Answer(200));
        await AdvanceAsync(r, Min, 1);
        await AdvanceAsync(r, Min, 2);
        _logger.LogCount(LogLevel.Warning).Should().Be(0);
        _logger.LogCount(LogLevel.Information, "recover").Should().Be(1);
        _logger.LogCount(LogLevel.Debug).Should().BeGreaterThanOrEqualTo(1);
        _logger.LogCount(LogLevel.Error).Should().Be(0);
    }

    [Fact]
    public async Task T6b_Sustained503_OneWarnAtFirstDrop_InfoOnRecovery()
    {
        var (q, r) = await ClientAsync();
        _stub.SetDefault(StubStep.Answer(503));
        for (int k = 1; k <= 5; k++)
        {
            Record(q, "E" + k, k);
            await AdvanceAsync(r, Min, _stub.PostCount + 1);
        }
        _logger.LogCount(LogLevel.Warning).Should().Be(0);
        Record(q, "E6", 6);
        await AdvanceAsync(r, Min, _stub.PostCount + 1); // tick 6 evicts E1
        _logger.LogCount(LogLevel.Warning).Should().Be(1);
        string warn = _logger.First(LogLevel.Warning);
        warn.Should().Contain("last POST result: 503");
        warn.Should().Contain("retained queue 5/5 batches");
        warn.Should().Contain("1 batch(es) dropped so far");
        for (int k = 7; k <= 9; k++)
        {
            Record(q, "E" + k, k);
            await AdvanceAsync(r, Min, _stub.PostCount + 1);
        }
        _logger.LogCount(LogLevel.Warning).Should().Be(1);
        _stub.SetDefault(StubStep.Answer(200));
        await AdvanceAsync(r, Min, _stub.PostCount + 1);
        _logger.LogCount(LogLevel.Information, "recover").Should().Be(1);
        _logger.LogCount(LogLevel.Error).Should().Be(0);
    }

    [Fact]
    public async Task T6c_WarnSummaryAtMostOncePerTenMinutes()
    {
        var (q, r) = await ClientAsync();
        _stub.SetDefault(StubStep.Answer(503));
        // Tick 6 (360s) is the first drop; the next WARN is due at >= 960s (tick 16).
        for (int k = 1; k <= 15; k++)
        {
            Record(q, "E" + k, k);
            await AdvanceAsync(r, Min, _stub.PostCount + 1);
        }
        _logger.LogCount(LogLevel.Warning).Should().Be(1);
        Record(q, "E16", 16);
        await AdvanceAsync(r, Min, _stub.PostCount + 1);
        _logger.LogCount(LogLevel.Warning).Should().Be(2);
        _logger.LogCount(LogLevel.Warning, @"still dropping data: \d+ batch\(es\) dropped in the last 10 min").Should().Be(1);
        for (int k = 17; k <= 25; k++)
        {
            Record(q, "E" + k, k);
            await AdvanceAsync(r, Min, _stub.PostCount + 1);
        }
        _logger.LogCount(LogLevel.Warning).Should().Be(2);
        _logger.LogCount(LogLevel.Error).Should().Be(0);
    }

    // ---------------------------------------------------------------- T7

    [Fact]
    public async Task T7_OnePostInFlight_SkippedWindowsAggregate()
    {
        var (q, r) = await ClientAsync();
        Record(q, "A", 0);
        _stub.Script(StubStep.HangUntilReleased());
        var first = r.TickAsync();
        await _stub.WaitForPostsAsync(1);
        r.InFlight.Should().BeTrue();

        Record(q, "B", 3);
        await r.TickAsync();
        Record(q, "C", 6);
        await r.TickAsync();
        _stub.PostCount.Should().Be(1);

        _stub.Release(0, StubStep.Answer(200));
        await first;
        await r.WhenIdleAsync();
        await r.TickAsync();
        await _stub.WaitForPostsAsync(2);
        await r.WhenIdleAsync();
        _stub.PostCount.Should().Be(2);
        Has(1, "B").Should().BeTrue();
        Has(1, "C").Should().BeTrue();
        Has(1, "A").Should().BeFalse();
    }

    // ---------------------------------------------------------------- T8

    [Fact]
    public async Task T8_Close_FiveSecondFinalFlush_RetainedQueueNotDrained_NothingLeftRunning()
    {
        var (q, r) = await ClientAsync();
        _stub.Script(StubStep.Answer(503), StubStep.Answer(503));
        Record(q, "A", 0);
        await AdvanceAsync(r, Min, 1);
        Record(q, "B", 3);
        await AdvanceAsync(r, Min, 2);
        r.RetainedCount.Should().Be(2);
        Record(q, "C", 6);
        _stub.SetDefault(StubStep.HangUntilReleased());

        var closing = q.CloseAsync();
        await _stub.WaitForPostsAsync(3);
        closing.IsCompleted.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(5));
        (await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(closing);
        await closing;

        _stub.PostCount.Should().Be(3);
        Has(2, "C").Should().BeTrue();
        Has(2, "A").Should().BeFalse();
        Has(2, "B").Should().BeFalse();
        _stub.Sha(2).Should().NotBe(_stub.Sha(0));
        _stub.Sha(2).Should().NotBe(_stub.Sha(1));

        _clock.Advance(TimeSpan.FromMinutes(10));
        _stub.PostCount.Should().Be(3);
        r.InFlight.Should().BeFalse();
        r.TimerActive.Should().BeFalse();
        r.IsClosed.Should().BeTrue();
        _clock.Pending.Should().Be(0);
        await q.CloseAsync(); // second close is a no-op and does not throw
    }
}
