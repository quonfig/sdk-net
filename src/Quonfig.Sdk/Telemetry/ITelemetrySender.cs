using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Sends a single telemetry envelope to api-telemetry. Implementations must throw on transport
/// failure or non-2xx HTTP status so <see cref="TelemetryReporter"/> can apply its exponential
/// backoff policy. Returning normally signals success.
/// </summary>
public interface ITelemetrySender
{
    /// <summary>
    /// Posts the supplied envelope. Implementations are responsible for serialization (typically
    /// System.Text.Json) and HTTP transport.
    /// </summary>
    Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken);
}
