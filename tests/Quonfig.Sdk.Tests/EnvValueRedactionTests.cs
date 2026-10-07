using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-goi1.2.15 item 5: an ENV_VAR value that cannot be converted to the config's type must not
/// appear in the exception message or in <c>ErrorMessage</c> (customers and the OpenFeature provider
/// log both). The message names the environment variable, the target type and the config key, as
/// sdk-go's Wave 1 fix does.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA2007",
    Justification = "Test code; ConfigureAwait(false) not required.")]
public sealed class EnvValueRedactionTests : IDisposable
{
    private const string Key = "env.port";
    private const string EnvVar = "QFG_TEST_PORT";
    private const string Secret = "postgres://admin:hunter2@db";
    private readonly string _root;

    public EnvValueRedactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-redact-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Key + ".json"),
            "{ \"id\": \"1\", \"key\": \"" + Key + "\", \"type\": \"config\", \"valueType\": \"int\", " +
            "\"default\": { \"rules\": [ { \"criteria\": [ { \"operator\": \"ALWAYS_TRUE\" } ], \"value\": " +
            "{ \"type\": \"provided\", \"value\": { \"source\": \"ENV_VAR\", \"lookup\": \"" + EnvVar + "\" } } } ] } }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<Quonfig> NewClientAsync()
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = "production",
            OnNoDefault = OnNoDefault.Throw,
            EnvLookup = name => name == EnvVar ? Secret : null,
        });
        await client.InitAsync();
        return client;
    }

    [Fact]
    public async Task UnconvertibleEnvValue_IsNotInTheExceptionMessage()
    {
        await using var client = await NewClientAsync();

        var ex = Assert.Throws<QuonfigCoercionException>(() => client.GetInt(Key));
        ex.Message.Should().NotContain("hunter2");
        ex.Message.Should().Contain(EnvVar).And.Contain(Key);
    }

    [Fact]
    public async Task UnconvertibleEnvValue_IsNotInTheDetailsErrorMessage()
    {
        await using var client = await NewClientAsync();

        var details = client.GetIntDetails(Key);
        details.Reason.Should().Be(Reason.Error);
        details.ErrorMessage.Should().NotContain("hunter2");
        details.ErrorMessage.Should().Contain(EnvVar).And.Contain(Key);
    }
}
