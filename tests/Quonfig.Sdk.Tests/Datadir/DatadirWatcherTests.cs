using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Datadir;
using Xunit;

namespace Quonfig.Sdk.Tests.Datadir;

/// <summary>
/// Unit tests for <see cref="DatadirWatcher"/> (qfg-zp7i.15). Mirrors sdk-java
/// <c>DatadirWatcherTest</c>: validates debounced coalescing, graceful failure on missing path,
/// and idempotent disposal.
/// </summary>
public sealed class DatadirWatcherTests : IDisposable
{
    private readonly string _root;

    public DatadirWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-watcher-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "configs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Start_Returns_False_When_Datadir_Does_Not_Exist()
    {
        string missing = Path.Combine(Path.GetTempPath(), "quonfig-watcher-does-not-exist-" + Path.GetRandomFileName());
        Exception? captured = null;
        await using var watcher = new DatadirWatcher(
            missing,
            TimeSpan.FromMilliseconds(50),
            onChange: () => { },
            onError: ex => captured = ex);

        bool started = watcher.Start();

        started.Should().BeFalse("missing datadir must NOT throw at construction");
        captured.Should().NotBeNull("the onError callback must surface the cause");
    }

    [Fact]
    public async Task Start_Then_Write_Fires_OnChange_Within_Debounce_Window()
    {
        using var fired = new SemaphoreSlim(0, 1);
        await using var watcher = new DatadirWatcher(
            _root,
            TimeSpan.FromMilliseconds(100),
            onChange: () => fired.Release(),
            onError: _ => { });

        watcher.Start().Should().BeTrue("the datadir is present");

        // Write a new config file inside the workspace.
        await File.WriteAllTextAsync(Path.Combine(_root, "configs", "new.config.json"), "{\"key\":\"x\"}");

        bool got = await fired.WaitAsync(TimeSpan.FromMilliseconds(100 + 1000));
        got.Should().BeTrue("OnChange should fire within debounce + tolerance after the file is written");
    }

    [Fact]
    public async Task Bursts_Coalesce_Into_A_Single_OnChange()
    {
        int count = 0;
        using var firedAtLeastOnce = new SemaphoreSlim(0, 10);
        await using var watcher = new DatadirWatcher(
            _root,
            TimeSpan.FromMilliseconds(200),
            onChange: () =>
            {
                Interlocked.Increment(ref count);
                try { firedAtLeastOnce.Release(); }
                catch (SemaphoreFullException) { /* tolerate >1 release */ }
            },
            onError: _ => { });

        watcher.Start().Should().BeTrue();

        // Burst of writes — must coalesce.
        for (int i = 0; i < 10; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "configs", $"f{i}.config.json"), "{\"key\":\"x\"}");
        }

        // Wait for the debounce to elapse + tolerance.
        bool fired = await firedAtLeastOnce.WaitAsync(TimeSpan.FromMilliseconds(200 + 1000));
        fired.Should().BeTrue();
        // Give any in-flight extra callbacks a chance to land.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref count).Should().Be(1, "all 10 writes within the 200ms debounce window must coalesce");
    }

    [Fact]
    public async Task DisposeAsync_Cancels_Pending_Debounce()
    {
        int count = 0;
        var watcher = new DatadirWatcher(
            _root,
            TimeSpan.FromMilliseconds(500),
            onChange: () => Interlocked.Increment(ref count),
            onError: _ => { });

        watcher.Start().Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(_root, "configs", "a.config.json"), "{\"key\":\"x\"}");

        // Dispose before the debounce window elapses.
        await watcher.DisposeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        Volatile.Read(ref count).Should().Be(0, "pending debounce must be cancelled on dispose");
    }

    [Fact]
    public async Task DisposeAsync_Is_Idempotent()
    {
        var watcher = new DatadirWatcher(_root, TimeSpan.FromMilliseconds(50), () => { }, _ => { });
        watcher.Start().Should().BeTrue();
        await watcher.DisposeAsync();
        // Second dispose must not throw.
        await watcher.DisposeAsync();
    }

    /// <summary>
    /// Regression test for qfg-zp7i.21. The sync <see cref="DatadirWatcher.Dispose"/> path must
    /// NOT wait for an in-flight <c>OnChange</c> callback to finish — doing so re-introduces the
    /// sync-over-async deadlock if the caller holds a lock that the callback is also blocked on.
    /// </summary>
    [Fact]
    public async Task Dispose_Does_Not_Wait_For_InFlight_OnChange_Callback()
    {
        using var callbackStarted = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        await using var watcher = new DatadirWatcher(
            _root,
            TimeSpan.FromMilliseconds(50),
            onChange: () =>
            {
                callbackStarted.Set();
                releaseCallback.Wait();
            },
            onError: _ => { });

        watcher.Start().Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(_root, "configs", "blocking.config.json"), "{\"k\":1}");

        callbackStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the debounced callback must start before we test Dispose");

        // Call sync Dispose from a worker thread. With the buggy sync-over-async implementation
        // Dispose() blocks until timer.DisposeAsync()'s await completes, which itself waits for
        // the in-flight callback to finish — and that callback is blocked on releaseCallback.
        var disposeTask = Task.Run(() => watcher.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));

        // Always release so the test doesn't leak a stuck thread.
        releaseCallback.Set();

        completed.Should().BeSameAs(disposeTask, "Dispose() must return without waiting for the in-flight callback");
        await disposeTask; // surface any exception
    }
}
