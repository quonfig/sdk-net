using System;
using System.Collections.Generic;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>Settable <see cref="IChaosClientView"/> for evaluator unit tests.</summary>
internal sealed class FakeClientView : IChaosClientView
{
    public Sdk.ConnectionState State { get; set; } = Sdk.ConnectionState.Initializing;
    public bool FallbackActive { get; set; }
    public DateTimeOffset? LastRefresh { get; set; }
    public long Attempts { get; set; }
    public long Restarts1 { get; set; }
    public long Restarts2 { get; set; }
    public bool Ready { get; set; }
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Sdk.ConnectionState ConnectionState => State;
    public bool FallbackPollerActive => FallbackActive;
    public DateTimeOffset? LastSuccessfulRefresh => LastRefresh;
    public long SseConnectAttemptsTotal => Attempts;
    public long WorkerRestartTotal(string layer) => layer == "1" ? Restarts1 : layer == "2" ? Restarts2 : 0;
    public bool IsReady => Ready;
    public string? GetString(string key, string defaultValue) => Values.TryGetValue(key, out var v) ? v : defaultValue;

    public static (ChaosProbe Probe, FakeClientView Fake) Probe()
    {
        var fake = new FakeClientView();
        var probe = new ChaosProbe();
        probe.SetClient(fake);
        return (probe, fake);
    }
}
