using System;
using System.Collections.Generic;
using Quonfig.Sdk.Telemetry;

namespace Quonfig.Sdk.Tests.Telemetry.Helpers;

/// <summary>
/// Manual clock for the telemetry transport contract (the contract's <c>advance(ms)</c>). Injected
/// through the internal <see cref="QuonfigOptions.TelemetryClock"/> seam, so only the SDK's telemetry
/// timers move (tick timer, per-POST deadline, shutdown deadline, floor / Retry-After / age checks);
/// real sockets and the thread pool are untouched.
/// </summary>
internal sealed class ManualTelemetryClock : ITelemetryClock
{
    private readonly object _gate = new();
    private readonly List<Entry> _timers = new();
    private long _now;
    private long _seq;

    public ManualTelemetryClock(long startMs = 1_790_294_400_000) // 2026-09-25T00:00:00Z
    {
        _now = startMs;
    }

    public long NowMs()
    {
        lock (_gate) return _now;
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        lock (_gate)
        {
            var e = new Entry(this, _now + Math.Max(0, (long)delay.TotalMilliseconds), ++_seq, callback);
            _timers.Add(e);
            return e;
        }
    }

    /// <summary>Timers scheduled and not yet fired or cancelled.</summary>
    public int Pending
    {
        get { lock (_gate) return _timers.Count; }
    }

    /// <summary>
    /// Move time forward, firing due timers in time order (ties in scheduling order). Callbacks run
    /// inline on the calling thread, so a tick's synchronous prefix (serialize, append, start the
    /// POST, arm its deadline) has happened by the time this returns.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate) target = _now + (long)by.TotalMilliseconds;
        for (; ; )
        {
            Entry? next = null;
            lock (_gate)
            {
                foreach (var t in _timers)
                {
                    if (t.At > target) continue;
                    if (next is null || t.At < next.At || (t.At == next.At && t.Seq < next.Seq)) next = t;
                }
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _timers.Remove(next);
                _now = next.At;
            }
            next.Callback();
        }
    }

    private void Cancel(Entry e)
    {
        lock (_gate) _timers.Remove(e);
    }

    private sealed class Entry : IDisposable
    {
        private readonly ManualTelemetryClock _owner;

        public Entry(ManualTelemetryClock owner, long at, long seq, Action callback)
        {
            _owner = owner;
            At = at;
            Seq = seq;
            Callback = callback;
        }

        public long At { get; }
        public long Seq { get; }
        public Action Callback { get; }

        public void Dispose() => _owner.Cancel(this);
    }
}
