namespace Quonfig.Sdk;

/// <summary>
/// Compile-time SDK identity. Concrete <see cref="Quonfig"/> client and the rest of the public
/// surface arrive in subsequent beads under epic qfg-zp7i.
/// </summary>
public static class SdkInfo
{
    /// <summary>Marketing name of the package.</summary>
    public const string Name = "Quonfig.Sdk";

    /// <summary>
    /// Compile-time version string. Source of truth lives in <c>Directory.Build.props</c>;
    /// this constant is kept in sync manually until automated release wiring lands.
    /// </summary>
    public const string Version = "0.0.1";
}
