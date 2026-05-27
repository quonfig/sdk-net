using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Base class for all exceptions thrown by the Quonfig SDK. Catch this when you want to handle
/// any Quonfig failure uniformly; catch a subclass when you need to react to a specific cause
/// (missing key, init timeout, missing env var, decryption failure).
/// </summary>
public class QuonfigException : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigException(string message, Exception? innerException) : base(message, innerException) { }
}
