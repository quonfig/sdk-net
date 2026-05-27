using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk;
using Quonfig.Sdk.Supervisor;
using Xunit;

namespace Quonfig.Sdk.Tests.Supervisor;

/// <summary>
/// Tier-1 supervisor contract tests — port of <c>SupervisorTest.java</c>
/// (qfg-47c2.18). Shared spec: <c>integration-test-data/chaos/supervisor-test-contract.md</c>.
/// Sub-millisecond backoff bounds keep the suite under a second of wall-clock;
/// the real 500ms→30s exponential schedule is verified directly via
/// <c>BackoffFor</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1031",
    Justification = "Tests assert on counters/state; broad catch is fine.")]
public sealed class SupervisorTests
{
    // Test 1 — Supervisor restarts a worker that throws within 1000ms.
    [Fact]
    public async Task RestartsThrownWorker()
    {
        int calls = 0;
        var restarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var spec = new Quonfig.Sdk.Supervisor.WorkerSpec(
            "1",
            async ctx =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    throw new InvalidOperationException("boom");
                }
                restarted.TrySetResult(true);
                await ctx.AwaitStopAsync().ConfigureAwait(false);
            });

        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { spec });

        s.Start();
        try
        {
            var done = await Task.WhenAny(restarted.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            done.Should().BeSameAs(restarted.Task,
                $"supervisor did not restart thrown worker within 1s; calls={calls}");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 2 — Exponential backoff (500ms → 1s → 2s → 4s → ... → 30s cap).
    [Fact]
    public async Task ExponentialBackoffFormula()
    {
        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(500),
            maxDelay: TimeSpan.FromSeconds(30));
        var want = new[]
        {
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30), // 32s -> cap
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
        };
        for (int i = 0; i < want.Length; i++)
        {
            s.BackoffFor(i).Should().Be(want[i], $"BackoffFor({i})");
        }
    }

    // Test 3 — Clean shutdown within 5s on stop().
    [Fact]
    public async Task StopJoinsWithinDeadline()
    {
        var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = new Quonfig.Sdk.Supervisor.WorkerSpec(
            "1",
            async ctx =>
            {
                running.TrySetResult(true);
                await ctx.AwaitStopAsync().ConfigureAwait(false);
            });

        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { spec });

        s.Start();
        (await Task.WhenAny(running.Task, Task.Delay(500))).Should().BeSameAs(running.Task,
            "worker never started");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.StopAsync();
        sw.Stop();
        sw.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));

        // Idempotency.
        Func<Task> second = async () => await s.StopAsync();
        await second.Should().NotThrowAsync();

        // After close, ConnectionState is Disconnected.
        s.ConnectionState.Should().Be(ConnectionState.Disconnected);
    }

    // Test 4 — worker_restart_total{layer="1"} increments per restart.
    [Fact]
    public async Task WorkerRestartTotalIncrements()
    {
        int calls = 0;
        const int wantRestarts = 3;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var spec = new Quonfig.Sdk.Supervisor.WorkerSpec(
            "1",
            async ctx =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n <= wantRestarts)
                {
                    throw new InvalidOperationException("boom");
                }
                done.TrySetResult(true);
                await ctx.AwaitStopAsync().ConfigureAwait(false);
            });

        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { spec });

        s.Start();
        try
        {
            (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(2))))
                .Should().BeSameAs(done.Task, $"worker never reached steady state; calls={calls}");
            s.WorkerRestartTotal("1").Should().Be(wantRestarts);
            s.WorkerRestartTotal("2").Should().Be(0, "untouched layer should be zero");
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 5 — Panic-in-callback recovery. A throw at the worker boundary must
    // not crash the supervisor loop.
    [Fact]
    public async Task RecoversFromOnEnvelopeStyleThrow()
    {
        int phase = 0;
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var spec = new Quonfig.Sdk.Supervisor.WorkerSpec(
            "1",
            async ctx =>
            {
                if (Interlocked.CompareExchange(ref phase, 1, 0) == 0)
                {
                    throw new InvalidOperationException("user OnEnvelope handler exploded");
                }
                resumed.TrySetResult(true);
                await ctx.AwaitStopAsync().ConfigureAwait(false);
            });

        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { spec });

        s.Start();
        try
        {
            (await Task.WhenAny(resumed.Task, Task.Delay(TimeSpan.FromSeconds(1))))
                .Should().BeSameAs(resumed.Task,
                    $"supervisor did not recover from OnEnvelope-style throw; phase={phase}");
            s.WorkerRestartTotal("1").Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await s.StopAsync();
        }
    }

    // Test 6 — ConnectionState transitions through documented values;
    // LastSuccessfulRefresh advances when the worker records an install.
    [Fact]
    public async Task ConnectionStateAndLastRefresh()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = new Quonfig.Sdk.Supervisor.WorkerSpec(
            "1",
            async ctx =>
            {
                await gate.Task.ConfigureAwait(false);
                await ctx.AwaitStopAsync().ConfigureAwait(false);
            });

        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { spec });

        // Before start: Initializing, null refresh.
        s.ConnectionState.Should().Be(ConnectionState.Initializing);
        s.LastSuccessfulRefresh.Should().BeNull("pre-start lastSuccessfulRefresh should be null");

        s.Start();
        try
        {
            s.SetConnectionState(ConnectionState.Connected);
            s.ConnectionState.Should().Be(ConnectionState.Connected);

            var before = DateTime.UtcNow;
            s.RecordSuccessfulRefresh();
            var got = s.LastSuccessfulRefresh;
            got.Should().NotBeNull();
            got!.Value.Should().BeOnOrAfter(before.AddMilliseconds(-1));
            got.Value.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));

            s.SetConnectionState(ConnectionState.Disconnected);
            s.ConnectionState.Should().Be(ConnectionState.Disconnected);

            s.SetConnectionState(ConnectionState.FallingBack);
            s.ConnectionState.Should().Be(ConnectionState.FallingBack);
        }
        finally
        {
            gate.TrySetResult(true);
            await s.StopAsync();
        }
    }

    // Sanity — a clean exit (worker returns after stop) is not counted as a restart.
    [Fact]
    public async Task CleanShutdownDoesNotCountAsRestart()
    {
        var spec = new Quonfig.Sdk.Supervisor.WorkerSpec(
            "1",
            async ctx => await ctx.AwaitStopAsync().ConfigureAwait(false));

        await using var s = new Quonfig.Sdk.Supervisor.Supervisor(
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            workers: new[] { spec });

        s.Start();
        await Task.Delay(20);
        await s.StopAsync();
        s.WorkerRestartTotal("1").Should().Be(0);
    }
}
