using System.Collections.Generic;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests;

public sealed class EvaluationDetailsTests
{
    [Fact]
    public void Constructs_WithAllFields()
    {
        var d = new EvaluationDetails<string>(
            value: "pro",
            reason: Reason.TargetingMatch,
            variant: "targeting:0",
            variantIndex: null,
            errorCode: null,
            errorMessage: null,
            metadata: new Dictionary<string, object?> { ["configId"] = "abc", ["ruleIndex"] = 0 });

        d.Value.Should().Be("pro");
        d.Reason.Should().Be(Reason.TargetingMatch);
        d.Variant.Should().Be("targeting:0");
        d.VariantIndex.Should().BeNull();
        d.ErrorCode.Should().BeNull();
        d.ErrorMessage.Should().BeNull();
        d.Metadata.Should().ContainKey("configId");
        d.Metadata.Should().ContainKey("ruleIndex");
    }

    [Fact]
    public void Metadata_DefaultsToEmpty_WhenNull()
    {
        var d = new EvaluationDetails<bool>(true, Reason.Static, "static", null, null, null, null);
        d.Metadata.Should().NotBeNull();
        d.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void Reason_EnumHasExpectedSet()
    {
        // Mirrors sdk-java Reason — required for EvaluationDetails cross-SDK alignment.
        System.Enum.GetNames(typeof(Reason))
            .Should().BeEquivalentTo("Unknown", "Static", "TargetingMatch", "Split", "Default", "Error");
    }

    [Fact]
    public void ErrorCode_EnumHasExpectedSet()
    {
        System.Enum.GetNames(typeof(ErrorCode))
            .Should().BeEquivalentTo("FlagNotFound", "TypeMismatch", "General");
    }

    [Fact]
    public void LogLevel_FromString_IsCaseInsensitive()
    {
        LogLevels.FromString("warn").Should().Be(LogLevel.Warn);
        LogLevels.FromString("WARN").Should().Be(LogLevel.Warn);
        LogLevels.FromString("Warn").Should().Be(LogLevel.Warn);
        LogLevels.FromString("nope").Should().BeNull();
        LogLevels.FromString(null).Should().BeNull();
    }

    [Fact]
    public void ConnectionState_EnumHasExpectedSet()
    {
        System.Enum.GetNames(typeof(ConnectionState))
            .Should().BeEquivalentTo("Initializing", "Connected", "Disconnected", "FallingBack");
    }

    [Fact]
    public void ExceptionHierarchy_HasQuonfigExceptionBase()
    {
        new QuonfigKeyNotFoundException("k").Should().BeAssignableTo<QuonfigException>();
        new QuonfigInitTimeoutException("t").Should().BeAssignableTo<QuonfigException>();
        new QuonfigEnvVarNotSetException("v").Should().BeAssignableTo<QuonfigException>();
        new QuonfigDecryptionException("d").Should().BeAssignableTo<QuonfigException>();
        new QuonfigException("base").Should().BeAssignableTo<System.Exception>();
    }
}
