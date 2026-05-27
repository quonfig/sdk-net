namespace Quonfig.Sdk;

/// <summary>
/// Policy controlling what a typed getter does when the requested key is missing AND the caller
/// did not supply a <c>defaultValue</c>. Matches sdk-java's <c>OnNoDefault</c> behavior.
/// </summary>
public enum OnNoDefault
{
    /// <summary>Throw <see cref="Exceptions.QuonfigKeyNotFoundException"/> (default — fail loud).</summary>
    Throw,

    /// <summary>Log a warning and return <c>default(T)</c> / <c>null</c>.</summary>
    Warn,

    /// <summary>Silently return <c>default(T)</c> / <c>null</c> — no log.</summary>
    Ignore,
}
