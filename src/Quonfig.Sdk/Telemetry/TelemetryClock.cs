using System;
using System.Threading;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Time source for the telemetry transport: the tick timer, the per-POST deadline, the shutdown
/// deadline, the 30s resend floor, <c>Retry-After</c> and retained-batch age all read it. Production
/// uses <see cref="SystemTelemetryClock"/>; the transport contract tests inject a manual clock through
/// <see cref="QuonfigOptions.TelemetryClock"/>. (<c>System.TimeProvider</c> is net8-only; the
/// netstandard2.0 build would need a new package for it, so this is a small internal seam instead.)
/// </summary>
internal interface ITelemetryClock
{
    /// <summary>Current time in Unix milliseconds.</summary>
    long NowMs();

    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delay"/>. Disposing the handle cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>Wall clock backed by one-shot <see cref="Timer"/>s (background, never keep the process alive).</summary>
internal sealed class SystemTelemetryClock : ITelemetryClock
{
    public static readonly SystemTelemetryClock Instance = new();

    private SystemTelemetryClock() { }

    public long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public IDisposable Schedule(TimeSpan delay, Action callback) => new OneShot(delay, callback);

    private sealed class OneShot : IDisposable
    {
        private readonly Timer _timer;

        public OneShot(TimeSpan delay, Action callback)
        {
            _timer = new Timer(_ => callback(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => _timer.Dispose();
    }
}
