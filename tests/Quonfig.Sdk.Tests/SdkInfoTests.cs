using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// SDK identity. <see cref="SdkInfo.Version"/> feeds the <c>X-Quonfig-SDK-Version: dotnet/{ver}</c>
/// header on every request, so it must be the real package version (qfg-pkig: it was a hand-synced
/// literal stuck at 0.0.1 while the package shipped 1.0.0 -> 1.2.2).
/// </summary>
public sealed class SdkInfoTests
{
    /// <summary>
    /// The assembly's informational version (fed by Directory.Build.props &lt;Version&gt;) without any
    /// <c>+buildmetadata</c> suffix. The header tests build their expected value from this.
    /// </summary>
    internal static string AssemblyVersion()
    {
        string info = typeof(SdkInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        return info.Split('+')[0];
    }

    [Fact]
    public void Name_IsExpected()
    {
        SdkInfo.Name.Should().Be("Quonfig.Sdk");
    }

    [Fact]
    public void Version_IsTheAssemblyVersion()
    {
        SdkInfo.Version.Should().Be(AssemblyVersion());
        SdkInfo.Version.Should().NotContain("+");
    }

    [Fact]
    public void Version_MatchesDirectoryBuildProps()
    {
        SdkInfo.Version.Should().Be(PropsVersion());
    }

    [Fact]
    public void Assembly_TargetsTheRunningTfm()
    {
        // The assembly under test must be loadable on whatever TFM is hosting this xUnit run.
        // On net8.0 → the net8.0 build is selected; on net48 → the netstandard2.0 build is selected.
        typeof(SdkInfo).Assembly.GetName().Name.Should().Be("Quonfig.Sdk");
    }

    /// <summary>
    /// Reads &lt;Version&gt; from the repo's Directory.Build.props, walking up from
    /// <see cref="AppContext.BaseDirectory"/> (not [CallerFilePath], which deterministic CI builds rewrite).
    /// </summary>
    private static string PropsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            string props = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(props))
            {
                var m = Regex.Match(File.ReadAllText(props), "<Version>([^<]+)</Version>");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        throw new DirectoryNotFoundException("Directory.Build.props not found above " + AppContext.BaseDirectory);
    }
}
