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
    /// The config exists but its value is of another type than the typed getter's (for example
    /// <c>GetStringDetails</c> on an int config, or <c>GetLongDetails</c> on <c>3.9</c>).
    /// </summary>
    TypeMismatch,

    /// <summary>Internal evaluation failure not covered by another code (env var missing, decryption, …).</summary>
    General,

    /// <summary>
    /// The value has the requested type but does not parse as it: an ENV_VAR value that cannot be
    /// converted to the config's type (an int variable <c>"abc"</c>), or a malformed stored int,
    /// double or duration (<c>"twelve"</c>, <c>"30s"</c>). OpenFeature's <c>PARSE_ERROR</c>.
    /// Added in 2.2.0, after <see cref="General"/> so the existing members keep their values;
    /// earlier versions report these as <see cref="TypeMismatch"/>.
    /// </summary>
    ParseError,
}
