using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when an env-var-sourced (or otherwise raw-string) value cannot be coerced into the
/// config's declared <see cref="Eval.ValueType"/>. Maps to the <c>unable_to_coerce_env_var</c> YAML
/// error key in the cross-SDK integration suite.
/// </summary>
public sealed class QuonfigCoercionException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigCoercionException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigCoercionException(string message, Exception? innerException) : base(message, innerException) { }
}
