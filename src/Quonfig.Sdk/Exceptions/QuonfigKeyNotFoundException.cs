using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when a config key has no resolved value and the caller did not supply a default.
///
/// Mirrors <c>QuonfigKeyNotFoundError</c> in sdk-python / sdk-java and the <c>missing_default</c>
/// YAML error key in the cross-SDK integration suite. Also raised when a <c>PROVIDED</c>
/// env-var value fails type coercion (e.g. coercing <c>"not_a_number"</c> to INT).
/// </summary>
public sealed class QuonfigKeyNotFoundException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigKeyNotFoundException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigKeyNotFoundException(string message, Exception? innerException) : base(message, innerException) { }
}
