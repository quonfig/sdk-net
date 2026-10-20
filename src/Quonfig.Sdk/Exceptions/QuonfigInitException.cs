using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>Why a <see cref="QuonfigInitException"/> was raised.</summary>
public enum QuonfigInitFailureReason
{
    /// <summary>The delivery legs answered with errors (refused, 5xx, bad body) until the init timeout.</summary>
    Unavailable = 0,

    /// <summary>Every delivery leg rejected the SDK key with 401 or 403. Raised at once, without waiting out the timeout.</summary>
    Unauthorized = 1,

    /// <summary>No delivery leg answered before the init timeout. Raised as <see cref="QuonfigInitTimeoutException"/>.</summary>
    Timeout = 2,
}

/// <summary>
/// Raised by <see cref="Quonfig.InitAsync"/> under <see cref="OnInitFailure.Fail"/> (the default)
/// when the client has no config at the end of initialization. <see cref="Reason"/> says why;
/// <see cref="Exception.InnerException"/> carries the last delivery error. The init-timeout case is
/// the <see cref="QuonfigInitTimeoutException"/> subclass. A client that raised this keeps retrying
/// in the background, so a caller that catches it and keeps the client still gets a client that heals.
/// </summary>
public class QuonfigInitException : QuonfigException
{
    /// <summary>Initializes a new instance with reason <see cref="QuonfigInitFailureReason.Unavailable"/>.</summary>
    public QuonfigInitException(string message) : this(message, QuonfigInitFailureReason.Unavailable, null) { }

    /// <summary>Initializes a new instance with reason <see cref="QuonfigInitFailureReason.Unavailable"/> and an inner cause.</summary>
    public QuonfigInitException(string message, Exception? innerException)
        : this(message, QuonfigInitFailureReason.Unavailable, innerException) { }

    /// <summary>Initializes a new instance with the given reason and inner cause.</summary>
    public QuonfigInitException(string message, QuonfigInitFailureReason reason, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Why initialization failed.</summary>
    public QuonfigInitFailureReason Reason { get; }
}
