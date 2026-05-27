namespace Quonfig.Sdk;

/// <summary>
/// Coarse classification of why an evaluation produced its value. Mirrors the OpenFeature
/// <c>Reason</c> enum so a future <c>openfeature-net</c> provider is a thin adapter.
///
/// Order matches the wire-protocol integer codes (see
/// <c>project/plans/openfeature-resolution-details.md</c> §5):
/// <c>Unknown</c>=0, <c>Static</c>=1, <c>TargetingMatch</c>=2, <c>Split</c>=3,
/// <c>Default</c>=4, <c>Error</c>=5.
/// </summary>
public enum Reason
{
    /// <summary>Defensive: any unmapped reason falls into this bucket.</summary>
    Unknown = 0,

    /// <summary>First rule with no criteria — no targeting needed.</summary>
    Static = 1,

    /// <summary>A rule's criteria all matched the supplied context.</summary>
    TargetingMatch = 2,

    /// <summary>Targeting matched and the matched value was a weighted bucket; one entry was selected.</summary>
    Split = 3,

    /// <summary>No rule matched; the caller's default value was returned.</summary>
    Default = 4,

    /// <summary>Evaluation could not complete (missing key, type mismatch, decryption failure, …).</summary>
    Error = 5,
}
