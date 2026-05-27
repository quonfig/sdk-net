namespace Quonfig.Sdk;

/// <summary>
/// Customer-visible transport health surface for the Quonfig client. Values match the
/// cross-SDK spec in
/// <c>project/plans/sdk-hardening-and-verification.md "Customer-visible health primitives"</c>.
///
/// Do not wire this enum (or <c>Quonfig.LastSuccessfulRefresh</c>) directly into a Kubernetes
/// liveness probe — these signals are diagnostic, not pass/fail. A liveness probe based on SDK
/// freshness will amplify transient network blips into restart cascades.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// Pre-init state: the client hasn't yet installed an envelope. For HTTP+SSE mode this is
    /// the state during the initial fetch before any worker has reported success. For
    /// datadir/datafile modes the constructor installs synchronously, so this state is never
    /// observed after the constructor returns.
    /// </summary>
    Initializing,

    /// <summary>An SSE stream (Layer 1) is live, OR a static-mode client has loaded its envelope.</summary>
    Connected,

    /// <summary>
    /// The Layer 1 worker is between connection attempts — after a drop, before the next reconnect succeeds.
    /// </summary>
    Disconnected,

    /// <summary>Layer 1 is unable to maintain a connection and the Layer 2 fallback poller is active.</summary>
    FallingBack,
}
