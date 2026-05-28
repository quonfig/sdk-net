using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Regression tests for the init-timeout to <see cref="QuonfigInitTimeoutException"/> contract
/// (cross-SDK <c>initialization_timeout</c> error key, surfaced via
/// <c>get_or_raise.yaml</c> integration case <c>get_or_raise can raise an error if the client
/// does not initialize in time</c>).
///
/// <para>The bug fixed here: <c>RunHttpInitAsync</c> arms two redundant timers — its own
/// <see cref="CancellationTokenSource"/> (<c>initCts</c>) and <c>HttpClient.Timeout</c>
/// (per-request). When <c>HttpClient.Timeout</c> wins the race — which happens whenever both
/// are set to the same small value, e.g. the integration YAML's 10ms —
/// <c>HttpTransport.FetchAsync</c> wraps the per-request <see cref="OperationCanceledException"/>
/// in a bare <see cref="QuonfigException"/> ("HTTP timeout contacting …"), the loop exhausts
/// every URL, and the catch in <c>RunHttpInitAsync</c> used to emit a plain
/// <see cref="QuonfigException"/> from that path — breaking the YAML's
/// <c>QuonfigInitTimeoutException</c> assertion.</para>
///
/// <para>The fix: scope <c>initCts</c> outside the try block and, in the catch-all clause,
/// check <c>initCts.IsCancellationRequested</c>. If yes, translate to
/// <see cref="QuonfigInitTimeoutException"/> regardless of which inner exception type leaked
/// through.</para>
/// </summary>
public sealed class QuonfigInitTimeoutTests
{
    private const string SdkKey = "init-timeout-tests";

    /// <summary>
    /// Handler that, after a short delay, throws <see cref="TaskCanceledException"/> tagged
    /// with a token OTHER than the caller's — mimicking the CI failure mode where
    /// <c>HttpClient.Timeout</c> fires from the inside. The resulting OCE's
    /// <see cref="OperationCanceledException.CancellationToken"/> is the HttpClient's internal
    /// timeout-CTS, NOT the caller-supplied <c>initCts.Token</c>, so
    /// <c>HttpTransport.FetchAsync</c> lands in the <c>catch (OperationCanceledException ex)</c>
    /// arm at line 175 (not the rethrow at 171), wrapping the OCE in a bare
    /// <see cref="QuonfigException"/>.
    /// </summary>
    private sealed class HttpClientTimeoutStyleHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public HttpClientTimeoutStyleHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, CancellationToken.None).ConfigureAwait(false);

            // TaskCanceledException with an inner TimeoutException mirrors the exact shape
            // HttpClient surfaces when Timeout fires from inside SendAsync. The OCE's
            // CancellationToken defaults to none — equivalent to "a foreign token, NOT the
            // caller's initCts.Token" — which keeps HttpTransport from re-throwing via the
            // `when (cancellationToken.IsCancellationRequested)` clause.
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout (simulated).",
                new TimeoutException("A task was canceled."));
        }
    }

    [Fact]
    public async Task InitAsync_HttpClientTimeoutWinsRace_CachedTcsExceptionIsTimeoutType()
    {
        // Reproduces the CI failure exactly: HttpClient.Timeout fires from inside SendAsync,
        // surfacing an OCE whose cancellation token is NOT the caller's initCts.Token. The
        // OCE bubbles up to HttpTransport, gets wrapped as a bare QuonfigException ("HTTP
        // timeout contacting …"), and the catch in RunHttpInitAsync USED to cache a plain
        // QuonfigException on _initTcs. After the fix, the catch sees
        // initCts.IsCancellationRequested == true and translates to
        // QuonfigInitTimeoutException, which is what every subsequent InitAsync await
        // re-throws.
        //
        // Handler delay (20ms) < InitTimeout (50ms): the handler returns its OCE BEFORE
        // initCts fires, so HttpTransport.FetchAsync lands in the wrap path (catch
        // OperationCanceledException at line 175, NOT the re-throw at 171) and emits a bare
        // QuonfigException("HTTP timeout contacting …"). RunHttpInitAsync must still translate
        // that to QuonfigInitTimeoutException — recognized by walking the inner-exception
        // chain for an OCE, which is the exact CI failure signature.
        using var handler = new HttpClientTimeoutStyleHandler(TimeSpan.FromMilliseconds(20));
        await using var client = new Quonfig(new QuonfigOptions
        {
            SdkKey = SdkKey,
            ApiUrls = new[] { "http://127.0.0.1:9/" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(50),
            OnInitFailure = OnInitFailure.Throw,
            HttpMessageHandler = handler,
        });

        // First await: surface the timeout (the exact source — InitAsync's own Task.Delay
        // race or the cached _initTcs exception — depends on scheduling and is not asserted
        // here; we just want to drive the path).
        try { await client.InitAsync(); }
        catch (QuonfigInitTimeoutException) { /* expected */ }
        catch (QuonfigException) { /* will be tightened below */ }

        // Wait for the background RunHttpInitAsync to settle _initTcs.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Second await: hits the IsCompleted fast-path and re-throws the cached exception.
        // MUST be QuonfigInitTimeoutException to honor the cross-SDK initialization_timeout
        // contract.
        Func<Task> act = () => client.InitAsync();
        await act.Should().ThrowAsync<QuonfigInitTimeoutException>();
    }
}
