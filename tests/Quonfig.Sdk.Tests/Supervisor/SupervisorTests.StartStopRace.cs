using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Supervisor;
using Xunit;

namespace Quonfig.Sdk.Tests.Supervisor;

public sealed partial class SupervisorTests
{
    // qfg-vyq7: StopAsync racing Start must never see a half-added worker slot. Start added to
    // _workerTasks without the lock StopAsync snapshots under, so a concurrent List<T>.Add could
    // expose a null entry and Task.WhenAll threw "The tasks argument included a null value"
    // (seen on windows / net48). Many workers make List<T> grow while StopAsync copies it.
    [Fact]
    public async Task StopAsync_RacingStart_NeverThrows()
    {
        for (var round = 0; round < 200; round++)
        {
            var workers = Enumerable.Range(0, 64)
                .Select(i => new WorkerSpec("w" + i, ctx => ctx.AwaitStopAsync()))
                .ToList();
            var sup = new global::Quonfig.Sdk.Supervisor.Supervisor(
                workers,
                initialDelay: TimeSpan.FromMilliseconds(1),
                maxDelay: TimeSpan.FromMilliseconds(1),
                stopTimeout: TimeSpan.FromSeconds(5));
            using var go = new ManualResetEventSlim(false);
            var starter = Task.Run(() =>
            {
                go.Wait();
                sup.Start();
            });
            var stopper = Task.Run(async () =>
            {
                go.Wait();
                await sup.StopAsync();
            });
            go.Set();
            var act = async () => await Task.WhenAll(starter, stopper);
            await act.Should().NotThrowAsync("round {0}", round);
            await sup.DisposeAsync();
        }
    }
}
