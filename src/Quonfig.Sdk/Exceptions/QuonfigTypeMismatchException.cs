using System;

namespace Quonfig.Sdk.Exceptions;

/// <summary>
/// Raised by a typed getter with no default (under <see cref="OnNoDefault.Throw"/>) when the
/// stored value's type does not match the getter, for example <c>GetBool</c> on a stored string
/// <c>"false"</c>, or <c>GetLong</c> on a stored <c>3.9</c>. Strings are never coerced; the only
/// conversions are the safe numeric ones (a whole double within +/-(2^53-1) reads as a long, and
/// a long reads as a double). Maps to the <c>type_mismatch</c> YAML error key in the cross-SDK
/// integration suite (qfg-zvyy).
/// </summary>
public sealed class QuonfigTypeMismatchException : QuonfigException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public QuonfigTypeMismatchException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner cause.</summary>
    public QuonfigTypeMismatchException(string message, Exception? innerException) : base(message, innerException) { }
}
