namespace Quonfig.Sdk;

/// <summary>
/// Error classifications returned in <see cref="EvaluationDetails{T}.ErrorCode"/> when
/// <see cref="Reason.Error"/> is the outcome. Names mirror OpenFeature's standard codes;
/// Quonfig-specific causes (env var missing, decryption failure) map onto <see cref="General"/>
/// with a descriptive <c>ErrorMessage</c>.
/// </summary>
public enum ErrorCode
{
    /// <summary>No config exists for the requested key.</summary>
    FlagNotFound,

    /// <summary>
    /// The config exists but its value cannot be read as the typed getter's type: another declared
    /// <c>valueType</c>, or a malformed stored or ENV_VAR value (for example an int <c>"twelve"</c>).
    /// </summary>
    TypeMismatch,

    /// <summary>Internal evaluation failure not covered by another code (env var missing, decryption, …).</summary>
    General,
}
