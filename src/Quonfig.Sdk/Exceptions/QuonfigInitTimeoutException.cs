using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when a <c>Quonfig</c> client cannot complete initialization within
/// <c>QuonfigOptions.InitTimeout</c>. Used for the <c>OnInitFailure=Throw</c> construction
/// policy when the configured API endpoint is unreachable (the cross-SDK YAML's
/// <c>initialization_timeout</c> error key).
/// </summary>
public sealed class QuonfigInitTimeoutException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigInitTimeoutException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigInitTimeoutException(string message, Exception? innerException) : base(message, innerException) { }
}
