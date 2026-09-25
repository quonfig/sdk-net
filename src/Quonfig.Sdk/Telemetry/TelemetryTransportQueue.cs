using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Quonfig.Sdk.Telemetry;

/// <summary>How a telemetry response status is handled (P3).</summary>
internal enum TelemetryStatusClass
{
    Ok,
    Retryable,
    Auth,
    Rejected,
}

/// <summary>
/// Telemetry transport policy (qfg-y8je.10; P1-P10 in
/// project/plans/2026-09-24-sdk-telemetry-transport-policy.md, contract tests T1-T8 in
/// integration-test-data/chaos/telemetry-transport-contract.md). Mirrors sdk-node's
/// <c>TelemetryTransportQueue</c>.
///
/// <para>Owns the retained queue of serialized batches, the send gate (30s floor after a failure plus
/// <c>Retry-After</c>), the drain loop, disable-on-auth and the P7 logging episodes. It knows nothing
/// about collectors or payload shape: it stores and resends opaque bytes. The reporter runs at most
/// one drain at a time; the lock guards the state against the property readers and <c>close()</c>.</para>
/// </summary>
internal sealed class TelemetryTransportQueue
{
    private readonly ITelemetryTransport _transport;
    private readonly ILogger _logger;
    private readonly ITelemetryClock _clock;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetainedBatches;
    private readonly long _maxRetainedBytes;
    private readonly long _maxRetainedAgeMs;
    private readonly Action _onDisabled;

    private readonly object _gate = new();
    private readonly List<RetainedBatch> _queue = new();
    private CancellationTokenSource? _inFlight;
    private long _lastFailureAt = long.MinValue / 2;
    private long _retryAfterUntil;
    private bool _disabled;

    // Outage episode (P7).
    private int _failuresSinceSuccess;
    private long? _firstFailureAt;
    private string _lastResult = string.Empty;
    private long? _lastDropWarnAt;
    private int _dropsSinceWarn;
    private int _dropsThisOutage;

    // Rejected-batch (other 4xx) cadence.
    private long? _lastRejectErrorAt;
    private int _rejectsSinceError;

    public TelemetryTransportQueue(
        ITelemetryTransport transport,
        ILogger logger,
        ITelemetryClock clock,
        TimeSpan timeout,
        int maxRetainedBatches,
        long maxRetainedBytes,
        TimeSpan maxRetainedAge,
        Action onDisabled)
    {
        _transport = transport;
        _logger = logger;
        _clock = clock;
        _timeout = timeout;
        _maxRetainedBatches = maxRetainedBatches;
        _maxRetainedBytes = maxRetainedBytes;
        _maxRetainedAgeMs = (long)maxRetainedAge.TotalMilliseconds;
        _onDisabled = onDisabled;
    }

    /// <summary>A POST is in flight.</summary>
    public bool Busy
    {
        get { lock (_gate) return _inFlight is not null; }
    }

    public bool Disabled
    {
        get { lock (_gate) return _disabled; }
    }

    /// <summary>A retryable failure happened and no POST has succeeded since.</summary>
    public bool Failing
    {
        get { lock (_gate) return _failuresSinceSuccess > 0; }
    }

    /// <summary>Every queued batch, including a not-yet-sent oversize one.</summary>
    public int RetainedCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public long RetainedBytes
    {
        get { lock (_gate) return RetainedBytesLocked(); }
    }

    /// <summary>2xx ok; 401/403/404 auth; 408/429/5xx retryable; every other status rejected (P3).</summary>
    public static TelemetryStatusClass ClassifyStatus(int status)
    {
        if (status >= 200 && status < 300) return TelemetryStatusClass.Ok;
        if (status == 401 || status == 403 || status == 404) return TelemetryStatusClass.Auth;
        if (status == 408 || status == 429 || (status >= 500 && status < 600)) return TelemetryStatusClass.Retryable;
        return TelemetryStatusClass.Rejected;
    }

    /// <summary>
    /// A <c>Retry-After</c> header as a wait in ms: delta-seconds, or an HTTP-date relative to
    /// <paramref name="nowMs"/> (past dates are 0). Unparseable is <c>null</c>. Clamped to 600s.
    /// </summary>
    public static long? ParseRetryAfterMs(string? header, long nowMs)
    {
        if (header is null) return null;
        string v = header.Trim();
        if (v.Length == 0) return null;
        long ms;
        if (IsAllDigits(v))
        {
            ms = long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                && seconds <= TelemetryDefaults.RetryAfterCapMs / 1000
                ? seconds * 1000
                : TelemetryDefaults.RetryAfterCapMs;
        }
        else if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            ms = Math.Max(0, at.ToUnixTimeMilliseconds() - nowMs);
        }
        else
        {
            return null;
        }
        return Math.Min(ms, TelemetryDefaults.RetryAfterCapMs);
    }

    /// <summary>Discards batches older than the max age (strictly greater). Tick step 2.</summary>
    public void Expire()
    {
        lock (_gate)
        {
            long now = _clock.NowMs();
            // Batches are appended in time order, so only the head can be expired.
            while (_queue.Count > 0 && now - _queue[0].CreatedAt > _maxRetainedAgeMs)
            {
                _queue.RemoveAt(0);
                RecordDropLocked(FormattableString.Invariant($"batch older than {Math.Round(_maxRetainedAgeMs / 60_000.0)} min"));
            }
        }
    }

    /// <summary>The 30s floor after a failure and any Retry-After have both elapsed. Tick step 3.</summary>
    public bool SendAllowed()
    {
        lock (_gate)
        {
            long now = _clock.NowMs();
            return now >= _lastFailureAt + TelemetryDefaults.ResendFloorMs && now >= _retryAfterUntil;
        }
    }

    /// <summary>Appends a serialized window and enforces the caps (drop oldest). Tick step 4.</summary>
    public void Append(TelemetryBatch batch)
    {
        lock (_gate)
        {
            bool oversize = batch.Body.Length > _maxRetainedBytes;
            _queue.Add(new RetainedBatch(batch, _clock.NowMs(), oversize));

            int count = 0;
            long bytes = 0;
            foreach (var b in _queue)
            {
                if (b.Oversize) continue;
                count++;
                bytes += b.Bytes;
            }
            while (count > _maxRetainedBatches || bytes > _maxRetainedBytes)
            {
                int i = _queue.FindIndex(b => !b.Oversize);
                if (i < 0) break;
                var evicted = _queue[i];
                _queue.RemoveAt(i);
                count--;
                bytes -= evicted.Bytes;
                RecordDropLocked("retained queue full");
            }
        }
    }

    /// <summary>POSTs queued batches oldest-first, one at a time; stops at the first failure. Tick step 5.</summary>
    public async Task DrainAsync()
    {
        for (; ; )
        {
            RetainedBatch batch;
            CancellationTokenSource abort;
            lock (_gate)
            {
                if (_queue.Count == 0 || _disabled) break;
                batch = _queue[0];
                abort = new CancellationTokenSource();
                _inFlight = abort;
            }

            SendOutcome outcome;
            try
            {
                outcome = await SendWithDeadlineAsync(batch.Batch, _timeout, abort.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_inFlight, abort)) _inFlight = null;
                }
                abort.Dispose();
            }

            if (outcome.Aborted) return;
            if (outcome.Response is null)
            {
                OnRetryableFailure(batch, outcome.Error!, retryAfter: null);
                break;
            }

            int status = outcome.Response.Status;
            var cls = ClassifyStatus(status);
            if (cls == TelemetryStatusClass.Ok)
            {
                lock (_gate)
                {
                    _queue.Remove(batch);
                    OnSuccessLocked();
                }
                continue;
            }
            if (cls == TelemetryStatusClass.Retryable)
            {
                OnRetryableFailure(batch, status.ToString(CultureInfo.InvariantCulture), outcome.Response.RetryAfter);
                break;
            }
            if (cls == TelemetryStatusClass.Auth)
            {
                Disable(status);
                return;
            }
            // Rejected: drop this batch, report, carry on with the next one.
            lock (_gate)
            {
                _queue.Remove(batch);
                OnRejectedLocked(status, batch.Bytes, outcome.Response.BodySnippet);
            }
        }

        // Oversize batches are never carried across ticks.
        lock (_gate)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (!_queue[i].Oversize) continue;
                _queue.RemoveAt(i);
                RecordDropLocked("batch larger than the byte cap");
            }
        }
    }

    /// <summary>Aborts the in-flight POST, if any (close()). The aborted batch is kept but never resent.</summary>
    public void AbortInFlight()
    {
        lock (_gate)
        {
            try { _inFlight?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// close(): one POST of the live window bounded by <paramref name="deadline"/>. Never retains,
    /// never touches the outage episode, never throws, and returns by the deadline even if the
    /// transport ignores cancellation.
    /// </summary>
    public async Task SendFinalAsync(TelemetryBatch batch, TimeSpan deadline)
    {
        var abort = new CancellationTokenSource();
        lock (_gate) _inFlight = abort;
        SendOutcome outcome;
        try
        {
            outcome = await SendWithDeadlineAsync(batch, deadline, abort.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, abort)) _inFlight = null;
            }
            abort.Dispose();
        }

        string? result = outcome.Response is null
            ? outcome.Error ?? "aborted"
            : ClassifyStatus(outcome.Response.Status) == TelemetryStatusClass.Ok
                ? null
                : outcome.Response.Status.ToString(CultureInfo.InvariantCulture);
        if (result is not null)
        {
            _logger.LogDebug(
                "Telemetry final flush at shutdown failed ({Result}); {Bytes} bytes dropped, {RetainedCount} retained batch(es) abandoned",
                result, batch.Body.Length, RetainedCount);
        }
    }

    /// <summary>
    /// One POST with a deadline driven by the telemetry clock. The deadline (or an abort) cancels the
    /// request and also ends the wait, so a transport that ignores cancellation cannot hold the tick.
    /// </summary>
    private async Task<SendOutcome> SendWithDeadlineAsync(TelemetryBatch batch, TimeSpan deadline, CancellationToken abortToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool timedOut = false;
        using var timer = _clock.Schedule(deadline, () =>
        {
            Volatile.Write(ref timedOut, true);
            stopped.TrySetResult(true);
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        using var abortReg = abortToken.Register(() => stopped.TrySetResult(true));

        Task<TelemetryHttpResult> send;
        try
        {
            send = _transport.SendAsync(batch, cts.Token);
        }
#pragma warning disable CA1031 // any transport failure is a failed POST, never an exception out of the loop
        catch (Exception e)
#pragma warning restore CA1031
        {
            send = Task.FromException<TelemetryHttpResult>(e);
        }

        var winner = await Task.WhenAny(send, stopped.Task).ConfigureAwait(false);
        if (winner != send)
        {
            _ = send.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return abortToken.IsCancellationRequested && !Volatile.Read(ref timedOut)
                ? SendOutcome.AbortedOutcome
                : SendOutcome.Failed("timeout");
        }

        try
        {
            return SendOutcome.Answered(await send.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (abortToken.IsCancellationRequested && !Volatile.Read(ref timedOut))
        {
            return SendOutcome.AbortedOutcome;
        }
        catch (OperationCanceledException)
        {
            // Our deadline, or HttpClient.Timeout firing on its own clock: either way a timeout.
            return SendOutcome.Failed("timeout");
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            return SendOutcome.Failed("network error: " + Describe(e));
        }
    }

    private void OnSuccessLocked()
    {
        if (_failuresSinceSuccess == 0) return;
        long now = _clock.NowMs();
        long seconds = (long)Math.Round((now - (_firstFailureAt ?? now)) / 1000.0, MidpointRounding.AwayFromZero);
        _logger.LogInformation(
            "Telemetry recovered: POST succeeded after {Failures} failed attempt(s) over {Seconds}s; {Dropped} batch(es) were dropped.",
            _failuresSinceSuccess, seconds, _dropsThisOutage);
        _failuresSinceSuccess = 0;
        _firstFailureAt = null;
        _dropsThisOutage = 0;
        _lastDropWarnAt = null;
        _dropsSinceWarn = 0;
    }

    private void OnRetryableFailure(RetainedBatch batch, string result, string? retryAfter)
    {
        lock (_gate)
        {
            long now = _clock.NowMs();
            _failuresSinceSuccess++;
            _firstFailureAt ??= now;
            _lastFailureAt = now;
            _lastResult = result;
            long? wait = ParseRetryAfterMs(retryAfter, now);
            if (wait is not null) _retryAfterUntil = now + wait.Value;

            long nextMs = Math.Max(_lastFailureAt + TelemetryDefaults.ResendFloorMs, _retryAfterUntil) - now;
            _logger.LogDebug(
                "Telemetry POST failed ({Result}); {RetainedCount} batch(es) / {RetainedBytes} bytes retained, next send in >= {WaitSeconds}s",
                result, _queue.Count, RetainedBytesLocked(), (long)Math.Ceiling(nextMs / 1000.0));

            if (batch.Oversize && _queue.Remove(batch))
            {
                RecordDropLocked("batch larger than the byte cap");
            }
        }
    }

    private void Disable(int status)
    {
        lock (_gate)
        {
            string hint = status == 404 ? "wrong TelemetryUrl" : "the SDK key was rejected";
            _logger.LogError(
                "Telemetry disabled for this process: {Url} answered {Status} ({Hint}). Flag evaluation is unaffected.",
                _transport.Url, status, hint);
            _queue.Clear();
            _disabled = true;
        }
        _onDisabled();
    }

    private void OnRejectedLocked(int status, int bytes, string bodySnippet)
    {
        long now = _clock.NowMs();
        if (_lastRejectErrorAt is null || now - _lastRejectErrorAt.Value >= TelemetryDefaults.DropWarnIntervalMs)
        {
            int n = _rejectsSinceError;
            string more = n > 0 ? FormattableString.Invariant($", {n} more since the last report") : string.Empty;
            _logger.LogError(
                "Telemetry batch rejected with {Status} and dropped ({Bytes} bytes{More}): {Body}. This is likely an SDK bug; please report it.",
                status, bytes, more, bodySnippet);
            _lastRejectErrorAt = now;
            _rejectsSinceError = 0;
        }
        else
        {
            _rejectsSinceError++;
            _logger.LogDebug("Telemetry batch rejected with {Status} and dropped ({Bytes} bytes)", status, bytes);
        }
    }

    private void RecordDropLocked(string reason)
    {
        long now = _clock.NowMs();
        _dropsSinceWarn++;
        _dropsThisOutage++;
        string lastResult = _lastResult.Length > 0 ? _lastResult : "none";
        if (_lastDropWarnAt is null)
        {
            _logger.LogWarning(
                "Telemetry is dropping data: {Reason} (last POST result: {LastResult}). {Dropped} batch(es) dropped so far; retained queue {RetainedCount}/{MaxBatches} batches, {RetainedBytes} bytes. Flag evaluation is unaffected; further drops log at debug with a summary every 10 min.",
                reason, lastResult, _dropsThisOutage, _queue.Count, _maxRetainedBatches, RetainedBytesLocked());
            _lastDropWarnAt = now;
            _dropsSinceWarn = 0;
        }
        else if (now - _lastDropWarnAt.Value >= TelemetryDefaults.DropWarnIntervalMs)
        {
            long minutes = (long)Math.Round((now - _lastDropWarnAt.Value) / 60_000.0, MidpointRounding.AwayFromZero);
            _logger.LogWarning(
                "Telemetry still dropping data: {Dropped} batch(es) dropped in the last {Minutes} min (last POST result: {LastResult}); retained queue {RetainedCount} batches, {RetainedBytes} bytes.",
                _dropsSinceWarn, minutes, lastResult, _queue.Count, RetainedBytesLocked());
            _lastDropWarnAt = now;
            _dropsSinceWarn = 0;
        }
        else
        {
            _logger.LogDebug("Telemetry dropped a batch: {Reason}; {Count} since the last warning", reason, _dropsSinceWarn);
        }
    }

    private long RetainedBytesLocked()
    {
        long n = 0;
        foreach (var b in _queue) n += b.Bytes;
        return n;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>The innermost exception message (e.g. "Connection refused") for the lastResult text.</summary>
    private static string Describe(Exception e)
    {
        var inner = e;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return e is HttpRequestException && !ReferenceEquals(inner, e) ? inner.Message : e.Message;
    }

    private sealed class RetainedBatch
    {
        public RetainedBatch(TelemetryBatch batch, long createdAt, bool oversize)
        {
            Batch = batch;
            CreatedAt = createdAt;
            Oversize = oversize;
        }

        public TelemetryBatch Batch { get; }
        public int Bytes => Batch.Body.Length;
        public long CreatedAt { get; }
        public bool Oversize { get; }
    }

    private sealed class SendOutcome
    {
        public static readonly SendOutcome AbortedOutcome = new(null, null, aborted: true);

        private SendOutcome(TelemetryHttpResult? response, string? error, bool aborted)
        {
            Response = response;
            Error = error;
            Aborted = aborted;
        }

        public TelemetryHttpResult? Response { get; }
        public string? Error { get; }
        public bool Aborted { get; }

        public static SendOutcome Answered(TelemetryHttpResult r) => new(r, null, aborted: false);

        public static SendOutcome Failed(string error) => new(null, error, aborted: false);
    }
}
