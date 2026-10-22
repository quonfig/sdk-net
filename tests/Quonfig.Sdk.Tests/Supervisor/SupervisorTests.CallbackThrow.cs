using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Quonfig.Sdk.Tests.Supervisor;

using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

// Test 5 — Callback-throw isolation (integration-test-data v2026.10.22.2,
// chaos/supervisor-test-contract.md; Jeff 2026-10-22, qfg-6vno.25 / qfg-6vno.21). A throw from a
// user callback (OnConfigChange = on_config_update, OnConnectionStateChange =
// on_connection_state_change) is caught, counted in callback_errors_total{callback}, logged at
// ERROR with the exception and its stack, and is NOT a worker restart. The SDK keeps installing
// envelopes, remaining listeners still run, and connection state is untouched.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2000",
    Justification = "Responses and streams are owned and disposed by HttpClient / the SDK.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1835",
    Justification = "Byte-array Stream overloads keep the fake stream portable across target frameworks.")]
public sealed partial class SupervisorTests
{
    private const string CallbackKey = "callback-throw.value";

    private static string CallbackEnvelope(int generation, string value) =>
        "{\"configs\":[{ \"id\": \"1\", \"key\": \"" + CallbackKey + "\", \"type\": \"config\", \"valueType\": \"string\", " +
        "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], " +
        "\"value\": { \"type\": \"string\", \"value\": \"" + value + "\" } } ] } }]," +
        "\"meta\":{\"version\":\"v" + generation + "\",\"environment\":\"production\",\"generation\":" + generation + "}}";

    [Fact]
    public async Task Test5_OnConfigUpdateThrow_IsCountedAndLoggedAtError_NotARestart()
    {
        var log = new ExceptionCaptureLogger();
        int calls = 0;
        var thirdCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new Quonfig(CallbackOptions(log, onConfigChange: () =>
        {
            int n = Interlocked.Increment(ref calls);
            if (n == 1) throw new InvalidOperationException("user onConfigUpdate exploded");
            if (n >= 3) thirdCall.TrySetResult(true);
        }));

        await client.InitAsync();
        (await Task.WhenAny(thirdCall.Task, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(thirdCall.Task,
            "after the throw the SDK still installs both SSE envelopes and runs the callback again (calls={0})", Volatile.Read(ref calls));
        await WaitUntilAsync(() => client.ConnectionState == ConnectionState.Connected, TimeSpan.FromSeconds(2));

        client.CallbackErrorsTotal("on_config_update").Should().Be(1);
        client.CallbackErrorsTotal("on_connection_state_change").Should().Be(0);
        client.WorkerRestartTotal("1").Should().Be(0, "a callback throw is not a worker restart");
        client.WorkerRestartTotal("2").Should().Be(0, "a callback throw is not a worker restart");
        client.GetString(CallbackKey, null, "default").Should().Be("v3", "the next envelopes are still installed");
        client.ConnectionState.Should().Be(ConnectionState.Connected, "a callback throw does not change connection state");
        client.LastSuccessfulRefresh.Should().NotBeNull();

        var errors = log.Lines.Where(l => l.Level == MelLogLevel.Error).ToList();
        errors.Should().ContainSingle("one ERROR per caught throw");
        errors[0].Message.Should().MatchRegex("(?i)callback|onConfigUpdate");
        errors[0].Exception.Should().NotBeNull("the ERROR carries the exception");
        errors[0].Exception!.Message.Should().Be("user onConfigUpdate exploded");
        errors[0].Exception!.StackTrace.Should().NotBeNullOrEmpty("the ERROR carries the stack");
    }

    [Fact]
    public async Task Test5_OnConnectionStateChangeThrow_IsCountedAndLoggedAtError_RemainingListenersRun()
    {
        var log = new ExceptionCaptureLogger();
        // Hold the stream until both listeners are subscribed: the client starts init (and then
        // the stream) from its constructor, so the Connected edge could otherwise fire first.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new Quonfig(CallbackOptions(log, onConfigChange: null, release.Task));
        int throwingCalls = 0;
        var seenByNext = new List<ConnectionState>();
        client.OnConnectionStateChange += _ =>
        {
            if (Interlocked.Increment(ref throwingCalls) == 1) throw new InvalidOperationException("user onConnectionStateChange exploded");
        };
        client.OnConnectionStateChange += st =>
        {
            lock (seenByNext) seenByNext.Add(st);
        };
        release.SetResult(true);

        await client.InitAsync();
        await WaitUntilAsync(
            () => client.ConnectionState == ConnectionState.Connected && client.GetString(CallbackKey, null, "default") == "v3",
            TimeSpan.FromSeconds(5));

        client.CallbackErrorsTotal("on_connection_state_change").Should().Be(1);
        client.CallbackErrorsTotal("on_config_update").Should().Be(0);
        client.WorkerRestartTotal("1").Should().Be(0, "a callback throw is not a worker restart");
        client.WorkerRestartTotal("2").Should().Be(0, "a callback throw is not a worker restart");
        client.ConnectionState.Should().Be(ConnectionState.Connected, "a callback throw does not change connection state");
        client.GetString(CallbackKey, null, "default").Should().Be("v3", "envelopes are still installed");
        lock (seenByNext) seenByNext.Should().Contain(ConnectionState.Connected, "the listener after the throwing one still runs");

        var errors = log.Lines.Where(l => l.Level == MelLogLevel.Error).ToList();
        errors.Should().ContainSingle("one ERROR per caught throw");
        errors[0].Message.Should().MatchRegex("(?i)callback");
        errors[0].Exception!.Message.Should().Be("user onConnectionStateChange exploded");
        errors[0].Exception!.StackTrace.Should().NotBeNullOrEmpty();
    }

    private static QuonfigOptions CallbackOptions(ILogger log, Action? onConfigChange, Task? streamRelease = null) => new()
    {
        SdkKey = "test-sdk-key",
        ApiUrls = new[] { "http://callback-throw.test" },
        StreamUrls = new[] { "http://callback-throw.test" },
        InitTimeout = TimeSpan.FromSeconds(5),
        CollectEvaluationSummaries = false,
        ContextUploadMode = ContextUploadMode.None,
        HttpMessageHandler = new CallbackRigHandler(streamRelease ?? Task.CompletedTask),
        Logger = log,
        OnConfigChange = onConfigChange,
    };

    private static async Task WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cond())
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// <c>/api/v2/configs</c> serves gen 1 ("v1"); the SSE stream delivers gen 2 and gen 3
    /// back-to-back (once <c>release</c> completes) and then stays open, so no worker run ends
    /// during the test.
    /// </summary>
    private sealed class CallbackRigHandler : HttpMessageHandler
    {
        private readonly Task _release;

        public CallbackRigHandler(Task release) { _release = release; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage resp;
            if (path.EndsWith("/api/v2/sse/config", StringComparison.Ordinal))
            {
                await _release;
                var events = "data: " + CallbackEnvelope(2, "v2") + "\n\n" + "data: " + CallbackEnvelope(3, "v3") + "\n\n";
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HoldOpenStream(Encoding.UTF8.GetBytes(events))) };
                resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CallbackEnvelope(1, "v1"), Encoding.UTF8, "application/json") };
            }
            return resp;
        }
    }

    /// <summary>Returns its bytes, then blocks reads until disposed or cancelled (a held stream).</summary>
    private sealed class HoldOpenStream : Stream
    {
        private readonly byte[] _data;
        private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pos;

        public HoldOpenStream(byte[] data) { _data = data; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_pos < _data.Length)
            {
                int n = Math.Min(count, _data.Length - _pos);
                Buffer.BlockCopy(_data, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            using (cancellationToken.Register(() => _closed.TrySetResult(true)))
            {
                await _closed.Task;
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(HoldOpenStream));
        }
#if NET8_0_OR_GREATER

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var tmp = new byte[buffer.Length];
            int n = await ReadAsync(tmp, 0, tmp.Length, cancellationToken);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }
#endif

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }

    /// <summary>Records <c>(level, message, exception)</c> for every log call.</summary>
    private sealed class ExceptionCaptureLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(MelLogLevel Level, string Message, Exception? Exception)> _lines = new();

        public IReadOnlyList<(MelLogLevel Level, string Message, Exception? Exception)> Lines
        {
            get { lock (_gate) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => true;

        public void Log<TState>(MelLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _lines.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
