using System;
using System.Text;
using FluentAssertions;
using Quonfig.Sdk.Crypto;
using Quonfig.Sdk.Exceptions;
using Xunit;

namespace Quonfig.Sdk.Tests.Crypto;

/// <summary>
/// Cross-SDK byte-compatibility tests for <see cref="AesGcmCompat"/>. The fixtures here are the
/// exact same <c>(key, ciphertext, plaintext)</c> triple used by sdk-java's <c>ResolverTest</c>
/// and sdk-go's integration suite — encryption done by sdk-java's
/// <c>com.quonfig.sdk.eval.Encryption</c>; if our decryption produces a different plaintext (or
/// fails), the .NET SDK can't read confidential values written by any other Quonfig SDK.
/// </summary>
public sealed class AesGcmCompatTests
{
    // From sdk-java/core/src/test/java/com/quonfig/sdk/eval/ResolverTest.java
    // and integration-test-data/data/integration-tests/configs/a.secret.config.json.
    private const string FixtureKeyHex =
        "c87ba22d8662282abe8a0e4651327b579cb64a454ab0f4c170b45b15f049a221";

    private const string FixtureCiphertext =
        "875247386844c18c58a97c--b307b97a8288ac9da3ce0cf2--7ab0c32e044869e355586ed653a435de";

    private const string FixturePlaintext = "hello.world";

    [Fact]
    public void Decrypt_CrossSdkFixture_ProducesExpectedPlaintext()
    {
        string actual = AesGcmCompat.Decrypt(FixtureKeyHex, FixtureCiphertext);
        actual.Should().Be(FixturePlaintext);
    }

    private static string FlipLastNibble(string hex)
    {
        char last = hex[hex.Length - 1];
        char swap = last == '0' ? '1' : '0';
#if NET8_0_OR_GREATER
        return string.Concat(hex.AsSpan(0, hex.Length - 1), new ReadOnlySpan<char>(in swap));
#else
        return hex.Substring(0, hex.Length - 1) + swap;
#endif
    }

    [Fact]
    public void Decrypt_TamperedAuthTag_ThrowsDecryptionException()
    {
        // Flip the last hex nibble of the auth tag — GCM must reject this with a tag-mismatch.
        var act = () => AesGcmCompat.Decrypt(FixtureKeyHex, FlipLastNibble(FixtureCiphertext));
        act.Should().Throw<QuonfigDecryptionException>();
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsDecryptionException()
    {
        // Flip the last nibble of the key — different key, same ciphertext → auth tag mismatch.
        var act = () => AesGcmCompat.Decrypt(FlipLastNibble(FixtureKeyHex), FixtureCiphertext);
        act.Should().Throw<QuonfigDecryptionException>();
    }

    [Fact]
    public void Decrypt_MalformedCiphertext_NotThreePartsThrows()
    {
        // Missing the second "--" separator — wire format violation.
        var act = () => AesGcmCompat.Decrypt(FixtureKeyHex, "deadbeef--cafebabe");
        act.Should().Throw<QuonfigDecryptionException>();
    }

    [Fact]
    public void Decrypt_MalformedCiphertext_InvalidHexThrows()
    {
        var act = () => AesGcmCompat.Decrypt(FixtureKeyHex, "zz--zz--zz");
        act.Should().Throw<QuonfigDecryptionException>();
    }

    [Fact]
    public void Decrypt_MixedCaseHex_StillDecrypts()
    {
        // sdk-go normalizes hex case before decode; we must too.
        string upper = FixtureCiphertext.ToUpperInvariant();
        string upperKey = FixtureKeyHex.ToUpperInvariant();
        AesGcmCompat.Decrypt(upperKey, upper).Should().Be(FixturePlaintext);
    }
}
