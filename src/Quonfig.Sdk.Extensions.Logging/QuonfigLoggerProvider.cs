using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Quonfig.Sdk.Extensions.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> decorator that wraps a set of underlying providers and
/// gates every emit through <see cref="IQuonfig.GetLogLevel"/>. Installed by
/// <see cref="QuonfigLoggingBuilderExtensions.AddQuonfigFilter"/>; the extension snapshots
/// any <see cref="ILoggerProvider"/>s already registered on the <c>ILoggingBuilder</c>,
/// removes them, and registers this single composite provider in their place. Subsequent
/// <c>Add*</c> calls register providers that sit alongside the wrapped chain and are NOT
/// gated by Quonfig — call <c>AddQuonfigFilter</c> last.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA1063:Implement IDisposable correctly",
    Justification = "ILoggerProvider's Dispose contract is fire-and-forget; no finalizer needed.")]
public sealed class QuonfigLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly IQuonfig _quonfig;
    private readonly ILoggerProvider[] _wrapped;
    private readonly ConcurrentDictionary<string, QuonfigLogger> _loggers = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>
    /// Constructs a Quonfig logger provider. <paramref name="wrapped"/> may be empty —
    /// the provider then acts as a sink-less filter (useful only in tests).
    /// </summary>
    public QuonfigLoggerProvider(IQuonfig quonfig, ILoggerProvider[] wrapped)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(quonfig);
        ArgumentNullException.ThrowIfNull(wrapped);
#else
        if (quonfig is null) throw new ArgumentNullException(nameof(quonfig));
        if (wrapped is null) throw new ArgumentNullException(nameof(wrapped));
#endif
        _quonfig = quonfig;
        _wrapped = wrapped;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(categoryName);
#else
        if (categoryName is null) throw new ArgumentNullException(nameof(categoryName));
#endif
        return _loggers.GetOrAdd(categoryName, name =>
        {
            var inner = new ILogger[_wrapped.Length];
            for (int i = 0; i < _wrapped.Length; i++)
            {
                inner[i] = _wrapped[i].CreateLogger(name);
            }
            return new QuonfigLogger(name, _quonfig, inner);
        });
    }

    /// <inheritdoc/>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        // Forward to any wrapped providers that support external scope. This is the standard
        // contract: the logger factory pushes a shared scope provider down to providers so
        // structured scopes propagate across the pipeline.
        for (int i = 0; i < _wrapped.Length; i++)
        {
            if (_wrapped[i] is ISupportExternalScope ses)
            {
                ses.SetScopeProvider(scopeProvider);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (int i = 0; i < _wrapped.Length; i++)
        {
            try { _wrapped[i].Dispose(); }
#pragma warning disable CA1031
            catch { /* don't let one provider's dispose take down the others */ }
#pragma warning restore CA1031
        }
    }
}
