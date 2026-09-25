using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace Quonfig.Sdk.Tests.Telemetry.Helpers;

/// <summary>One scripted answer to a telemetry POST.</summary>
internal sealed class StubStep
{
    private StubStep(int status, string? retryAfter, bool hang, string? body)
    {
        Status = status;
        RetryAfter = retryAfter;
        Hang = hang;
        Body = body;
    }

    public int Status { get; }
    public string? RetryAfter { get; }
    public bool Hang { get; }
    public string? Body { get; }

    public static StubStep Answer(int status, string? retryAfter = null, string? body = null) =>
        new(status, retryAfter, hang: false, body);

    /// <summary>Accept the request and never answer until <see cref="TelemetryStub.Release"/>.</summary>
    public static StubStep HangUntilReleased() => new(0, null, hang: true, null);
}

/// <summary>
/// Scriptable in-process telemetry endpoint (WireMock.Net) for the transport contract. Every POST
/// to <c>/api/v1/telemetry/</c> is recorded (raw body bytes, including requests the client later
/// aborts) and answered by the next scripted <see cref="StubStep"/>, or the default step.
/// </summary>
internal sealed class TelemetryStub : IDisposable
{
    private readonly object _gate = new();
    private readonly WireMockServer _server;
    private readonly Queue<StubStep> _script = new();
    private readonly List<byte[]> _bodies = new();
    private readonly Dictionary<int, TaskCompletionSource<StubStep>> _held = new();
    private StubStep _default = StubStep.Answer(200);

    public TelemetryStub()
    {
        _server = WireMockServer.Start();
        _server
            .Given(Request.Create().WithPath("/api/v1/telemetry/").UsingPost())
            .RespondWith(Response.Create().WithCallback(HandleAsync));
    }

    public string Url => _server.Urls[0];

    public int PostCount
    {
        get { lock (_gate) return _bodies.Count; }
    }

    public void Script(params StubStep[] steps)
    {
        lock (_gate)
        {
            foreach (var s in steps) _script.Enqueue(s);
        }
    }

    public void SetDefault(StubStep step)
    {
        lock (_gate) _default = step;
    }

    public byte[] Body(int i)
    {
        lock (_gate) return _bodies[i];
    }

    public string Text(int i) => Encoding.UTF8.GetString(Body(i));

    public string Sha(int i)
    {
#if NET8_0_OR_GREATER
        return Convert.ToBase64String(SHA256.HashData(Body(i)));
#else
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Body(i)));
#endif
    }

    /// <summary>Answer the held (hanging) POST <paramref name="i"/> with <paramref name="step"/>.</summary>
    public void Release(int i, StubStep step)
    {
        TaskCompletionSource<StubStep>? tcs;
        lock (_gate) _held.TryGetValue(i, out tcs);
        tcs?.TrySetResult(step);
    }

    /// <summary>Wait (real time, bounded) until the stub has received at least <paramref name="n"/> POSTs.</summary>
    public async Task WaitForPostsAsync(int n)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (PostCount < n)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"telemetry stub saw {PostCount} POSTs, expected {n}");
            }
            await Task.Delay(5);
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<StubStep>> held;
        lock (_gate) held = _held.Values.ToList();
        foreach (var h in held) h.TrySetResult(StubStep.Answer(200));
        _server.Stop();
        _server.Dispose();
    }

    private async Task<ResponseMessage> HandleAsync(IRequestMessage req)
    {
        StubStep step;
        TaskCompletionSource<StubStep>? hold = null;
        lock (_gate)
        {
            int index = _bodies.Count;
            _bodies.Add(req.BodyAsBytes ?? Array.Empty<byte>());
            step = _script.Count > 0 ? _script.Dequeue() : _default;
            if (step.Hang)
            {
                hold = new TaskCompletionSource<StubStep>(TaskCreationOptions.RunContinuationsAsynchronously);
                _held[index] = hold;
            }
        }
        if (hold is not null)
        {
            step = await hold.Task.ConfigureAwait(false);
        }

        var headers = new Dictionary<string, WireMockList<string>>();
        if (step.RetryAfter is not null)
        {
            headers["Retry-After"] = new WireMockList<string>(step.RetryAfter);
        }
        return new ResponseMessage
        {
            StatusCode = step.Status,
            Headers = headers,
            BodyData = step.Body is null
                ? null
                : new BodyData { BodyAsString = step.Body, DetectedBodyType = BodyType.String, Encoding = Encoding.UTF8 },
        };
    }
}
