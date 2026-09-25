using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Quonfig.Sdk.Telemetry;

/// <summary>
/// Sends a single telemetry envelope to api-telemetry. Implementations must throw on transport
/// failure or non-2xx HTTP status; returning normally signals success. When a custom sender is
/// injected, <see cref="TelemetryReporter"/> treats any exception as a retryable failure: the batch is
/// kept and handed to the sender again, unchanged, on a later tick (see the transport policy in the
/// README).
/// </summary>
public interface ITelemetrySender
{
    /// <summary>
    /// Posts the supplied envelope. Implementations are responsible for serialization (typically
    /// System.Text.Json) and HTTP transport.
    /// </summary>
    Task SendAsync(IDictionary<string, object?> payload, CancellationToken cancellationToken);
}
