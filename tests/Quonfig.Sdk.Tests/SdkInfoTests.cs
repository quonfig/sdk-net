using FluentAssertions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// Bootstrap smoke tests. These exist to prove that:
///   * the Quonfig.Sdk assembly compiles for net8.0 AND netstandard2.0,
///   * the test project can reference and execute against it on both
///     net8.0 (Linux + Windows) and net48 (Windows, NS2.0 loader).
/// Subsequent beads replace these with real public-surface tests.
/// </summary>
public sealed class SdkInfoTests
{
    [Fact]
    public void Name_IsExpected()
    {
        SdkInfo.Name.Should().Be("Quonfig.Sdk");
    }

    [Fact]
    public void Version_MatchesDirectoryBuildProps()
    {
        SdkInfo.Version.Should().Be("0.0.1");
    }

    [Fact]
    public void Assembly_TargetsTheRunningTfm()
    {
        // The assembly under test must be loadable on whatever TFM is hosting this xUnit run.
        // On net8.0 → the net8.0 build is selected; on net48 → the netstandard2.0 build is selected.
        typeof(SdkInfo).Assembly.GetName().Name.Should().Be("Quonfig.Sdk");
    }
}
