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

    /// <summary>The config exists but its declared <c>valueType</c> does not match the typed getter called.</summary>
    TypeMismatch,

    /// <summary>Internal evaluation failure not covered by another code (env var missing, decryption, …).</summary>
    General,
}
