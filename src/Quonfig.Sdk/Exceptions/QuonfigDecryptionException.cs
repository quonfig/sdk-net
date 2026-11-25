using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when a secret cannot be read: a qf1 value (spec <c>project/plans/secrets-qf1-spec.md</c>)
/// that fails to parse or decrypt, whose <c>QUONFIG_KEYSET*</c> env var is missing or invalid, or
/// whose key this runtime does not hold (message <c>config "&lt;key&gt;": qf1 &lt;class&gt;: ...</c>),
/// or a value that still carries the old <c>decryptWith</c> format, which is no longer read. Fails
/// only that read. Maps to the <c>unable_to_decrypt</c> YAML error key.
/// </summary>
public sealed class QuonfigDecryptionException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigDecryptionException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigDecryptionException(string message, Exception? innerException) : base(message, innerException) { }
}
