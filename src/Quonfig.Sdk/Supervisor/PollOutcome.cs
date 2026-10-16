namespace Quonfig.Sdk.Supervisor;

/// <summary>
/// What one fallback-poller fetch cycle came back with (qfg-goi1.10.28). Drives the auth-failure
/// rule: a cycle with no 200/304 on any leg and an auth-class status (401/403/404/410) on the
/// primary leg enters auth-failed (R2); while auth-failed, the first probe that
/// <see cref="Answered"/> exits (R4).
/// </summary>
internal readonly struct PollOutcome
{
    private PollOutcome(bool answered, int authStatus, string? host)
    {
        Answered = answered;
        AuthStatus = authStatus;
        Host = host;
    }

    /// <summary>A cycle in which at least one leg answered 200 (valid envelope) or 304.</summary>
    internal static PollOutcome Ok { get; } = new(true, 0, null);

    /// <summary>A cycle in which every leg failed, not with an auth-class primary.</summary>
    internal static PollOutcome Failed { get; } = new(false, 0, null);

    /// <summary>A cycle that qualifies for auth-failed (R2).</summary>
    internal static PollOutcome AuthRejected(int status, string host) => new(false, status, host);

    /// <summary>True when some leg returned 200 with a valid envelope, or 304.</summary>
    internal bool Answered { get; }

    /// <summary>The primary leg's auth-class status when the cycle qualifies for auth-failed, else 0.</summary>
    internal int AuthStatus { get; }

    /// <summary>The primary leg's host (never the key) when <see cref="IsAuthRejected"/>.</summary>
    internal string? Host { get; }

    internal bool IsAuthRejected => AuthStatus != 0;
}
