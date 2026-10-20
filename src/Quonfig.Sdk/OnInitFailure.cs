using System;

namespace Quonfig.Sdk;

/// <summary>
/// What <see cref="Quonfig.InitAsync"/> does when no config has arrived by
/// <see cref="QuonfigOptions.InitTimeout"/>. Before the timeout the client retries through blips
/// (jittered backoff 0.5s, 1s, 2s, ... capped at 30s) in every mode. Datadir / datafile mode loads
/// synchronously and is not subject to this policy.
/// <para>In every mode a client object that exists keeps retrying in the background and heals:
/// there are no permanently dead clients.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1027:Mark enums with FlagsAttribute",
    Justification = "Not a flags enum: Throw and ReturnDefaults are [Obsolete] aliases that share the values of Fail and Continue.")]
public enum OnInitFailure
{
    /// <summary>
    /// Default. <see cref="Quonfig.InitAsync"/> throws a <see cref="Exceptions.QuonfigInitException"/>
    /// at the timeout (<see cref="Exceptions.QuonfigInitTimeoutException"/> when the delivery legs
    /// hung, <see cref="Exceptions.QuonfigInitFailureReason.Unavailable"/> when they answered with
    /// errors), or at once when every leg rejects the SDK key with 401/403. A caller that catches
    /// the exception and keeps the client still gets a client that heals.
    /// </summary>
    Fail = 0,

    /// <summary>
    /// <see cref="Quonfig.InitAsync"/> returns at the timeout without throwing; getters return their
    /// supplied defaults (or honor <see cref="OnNoDefault"/>) until the background load completes.
    /// A 401/403 from every leg continues at once.
    /// </summary>
    Continue = 1,

    /// <summary>
    /// <see cref="Quonfig.InitAsync"/> keeps blocking past the timeout, with no upper bound, until the
    /// first config arrives (or the caller's <see cref="System.Threading.CancellationToken"/> fires).
    /// A 401/403 from every leg is logged at Error and retried, since the key may be provisioned
    /// late. Wire a readiness probe to <see cref="Quonfig.IsReady"/> so the orchestrator holds traffic.
    /// </summary>
    Wait = 2,

    /// <summary>Deprecated alias of <see cref="Fail"/> (same value).</summary>
    [Obsolete("Use OnInitFailure.Fail. Throw is the same value and will be removed in the next major.")]
    Throw = Fail,

    /// <summary>Deprecated alias of <see cref="Continue"/> (same value).</summary>
    [Obsolete("Use OnInitFailure.Continue. ReturnDefaults is the same value and will be removed in the next major.")]
    ReturnDefaults = Continue,
}
