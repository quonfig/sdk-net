using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Quonfig.Sdk.Tests;

/// <summary>
/// qfg-goi1.2.14: the <c>decryptWith</c> key config must be evaluated in the client's effective
/// environment, like any other config (sdk-go <c>runtime_resolver.go</c> passes <c>envID</c>).
/// It used to be evaluated with environment <c>""</c>, so an environment-scoped key rule (the shape
/// Quonfig's own workspace uses: a default <c>QUONFIG_ENCRYPTION_KEY</c> rule plus a
/// <c>production</c> <c>QUONFIG_ENCRYPTION_KEY_PROD</c> rule) was ignored and the secret was
/// decrypted with the default key.
/// </summary>
public sealed class EnvScopedDecryptionKeyTests : IDisposable
{
    // Cross-SDK AES-GCM fixture (see Crypto/AesGcmCompatTests).
    private const string FixtureKeyHex =
        "c87ba22d8662282abe8a0e4651327b579cb64a454ab0f4c170b45b15f049a221";

    private const string FixtureCiphertext =
        "875247386844c18c58a97c--b307b97a8288ac9da3ce0cf2--7ab0c32e044869e355586ed653a435de";

    private const string FixturePlaintext = "hello.world";

    // A valid 32-byte key that did not encrypt the fixture.
    private static readonly string OtherKeyHex = new('a', 64);

    private const string KeyConfigKey = "secrets.encryption.key";
    private const string SecretKey = "db.password";
    private const string DefaultKeyVar = "QFG_TEST_ENCRYPTION_KEY";
    private const string ProdKeyVar = "QFG_TEST_ENCRYPTION_KEY_PROD";

    private readonly string _root;

    public EnvScopedDecryptionKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quonfig-env-key-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "quonfig.json"), "{\"environments\":[\"production\",\"staging\"]}");
        var dir = Path.Combine(_root, "configs");
        Directory.CreateDirectory(dir);

        // Key config: default rule reads the default key var; the production rule reads the prod
        // key var. Same shape as our-config's quonfig.secrets.encryption.key.
        File.WriteAllText(Path.Combine(dir, KeyConfigKey + ".json"), $$"""
            {
              "id": "1",
              "key": "{{KeyConfigKey}}",
              "type": "config",
              "valueType": "string",
              "default": {
                "rules": [ { "criteria": [ { "operator": "ALWAYS_TRUE" } ],
                  "value": { "type": "provided", "value": { "source": "ENV_VAR", "lookup": "{{DefaultKeyVar}}" } } } ]
              },
              "environments": [
                { "id": "production",
                  "rules": [ { "criteria": [ { "operator": "ALWAYS_TRUE" } ],
                    "value": { "type": "provided", "value": { "source": "ENV_VAR", "lookup": "{{ProdKeyVar}}" } } } ] }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(dir, SecretKey + ".json"), $$"""
            {
              "id": "2",
              "key": "{{SecretKey}}",
              "type": "config",
              "valueType": "string",
              "default": {
                "rules": [ { "criteria": [ { "operator": "ALWAYS_TRUE" } ],
                  "value": { "type": "string", "value": "{{FixtureCiphertext}}", "confidential": true, "decryptWith": "{{KeyConfigKey}}" } } ]
              }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<Quonfig> NewClientAsync(string environment, string defaultKey, string prodKey)
    {
        var client = new Quonfig(new QuonfigOptions
        {
            Datadir = _root,
            Environment = environment,
            OnNoDefault = OnNoDefault.Throw,
            EnvLookup = name => name switch
            {
                DefaultKeyVar => defaultKey,
                ProdKeyVar => prodKey,
                _ => null,
            },
        });
        await client.InitAsync();
        return client;
    }

    [Fact]
    public async Task SecretEncryptedWithEnvScopedKey_DecryptsInThatEnvironment()
    {
        // The secret was encrypted with the production key; the default key is different.
        await using var client = await NewClientAsync("production", defaultKey: OtherKeyHex, prodKey: FixtureKeyHex);

        // Reading the key config directly already resolves the production key...
        client.GetString(KeyConfigKey).Should().Be(FixtureKeyHex);
        // ...and the decrypt path must agree with it.
        client.GetString(SecretKey).Should().Be(FixturePlaintext);
        var details = client.GetStringDetails(SecretKey);
        details.Value.Should().Be(FixturePlaintext);
        details.Reason.Should().NotBe(Reason.Error);
    }

    [Fact]
    public async Task EnvironmentWithoutKeyOverride_UsesDefaultKeyRule()
    {
        await using var client = await NewClientAsync("staging", defaultKey: FixtureKeyHex, prodKey: OtherKeyHex);

        client.GetString(SecretKey).Should().Be(FixturePlaintext);
    }

    [Fact]
    public async Task SecretEncryptedWithDefaultKey_FailsInEnvironmentThatOverridesTheKey()
    {
        // The edge the fix changes (CHANGELOG): a secret encrypted with the DEFAULT key, read in
        // an environment whose key rule points elsewhere, is decrypted with that environment's key
        // and fails, as it already does in sdk-go/node/java/ruby.
        await using var client = await NewClientAsync("production", defaultKey: FixtureKeyHex, prodKey: OtherKeyHex);

        var details = client.GetStringDetails(SecretKey);
        details.Reason.Should().Be(Reason.Error);
        details.Value.Should().BeNull();
    }
}
