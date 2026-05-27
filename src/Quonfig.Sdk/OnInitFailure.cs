namespace Quonfig.Sdk;

/// <summary>
/// Policy controlling what happens when the first HTTP+SSE initialization does not complete inside
/// <see cref="QuonfigOptions.InitTimeout"/>. Datadir / datafile mode loads synchronously and is
/// not subject to this policy.
/// </summary>
public enum OnInitFailure
{
    /// <summary>
    /// <see cref="Quonfig.InitAsync"/> throws <see cref="Exceptions.QuonfigInitTimeoutException"/>
    /// when the first envelope does not arrive inside the timeout (default — fail loud at start-up).
    /// </summary>
    Throw,

    /// <summary>
    /// <see cref="Quonfig.InitAsync"/> returns once the timeout elapses; getters return their
    /// supplied defaults (or honor <see cref="OnNoDefault"/>) until the background load completes.
    /// </summary>
    ReturnDefaults,
}
