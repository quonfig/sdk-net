using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when a config's <c>PROVIDED</c> (env-var-sourced) value cannot be resolved because
/// the named environment variable is not set. Maps to the <c>missing_env_var</c> YAML error key.
/// </summary>
public sealed class QuonfigEnvVarNotSetException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigEnvVarNotSetException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigEnvVarNotSetException(string message, Exception? innerException) : base(message, innerException) { }
}
