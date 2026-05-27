using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised when a <c>decryptWith</c> confidential value cannot be decrypted: the key config is
/// missing/unmatched, the key value is empty, or AES-GCM decryption itself fails. Maps to the
/// <c>unable_to_decrypt</c> YAML error key.
/// </summary>
public sealed class QuonfigDecryptionException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigDecryptionException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigDecryptionException(string message, Exception? innerException) : base(message, innerException) { }
}
