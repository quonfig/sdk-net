using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when a <c>Quonfig</c> client has no config by <c>QuonfigOptions.InitTimeout</c> because no
/// delivery leg answered in time (the cross-SDK YAML's <c>initialization_timeout</c> error key).
/// A <see cref="QuonfigInitException"/> with <see cref="QuonfigInitFailureReason.Timeout"/>, so a
/// <c>catch (QuonfigInitException)</c> handles every init failure.
/// </summary>
public sealed class QuonfigInitTimeoutException : QuonfigInitException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigInitTimeoutException(string message) : base(message, QuonfigInitFailureReason.Timeout, null) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigInitTimeoutException(string message, Exception? innerException)
        : base(message, QuonfigInitFailureReason.Timeout, innerException) { }
}
