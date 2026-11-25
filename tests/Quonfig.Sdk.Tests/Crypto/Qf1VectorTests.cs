using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Org.BouncyCastle.Crypto.Parameters;
using Quonfig.Sdk.Crypto;
using Xunit;

namespace Quonfig.Sdk.Tests.Crypto;

/// <summary>
/// The qf1 reader against every reader-relevant vector of
/// <c>integration-test-data/tests/secrets/qf1-vectors.json</c> (spec section 14): RFC checks, every
/// positive case with its intermediates, decryptOnly, every negative case with its exact class
/// (mustReject ones included), and the private-entry and env-value key entry cases. Runs on every
/// test target framework (net8.0 and net48).
/// </summary>
public sealed class Qf1VectorTests
{
    public static IEnumerable<object[]> Positive => Qf1Vectors.Names("positive");

    public static IEnumerable<object[]> DecryptOnly => Qf1Vectors.Names("decryptOnly");

    public static IEnumerable<object[]> Negative => Qf1Vectors.Names("negative");

    public static IEnumerable<object[]> PrivateEntries =>
        Qf1Vectors.Names("keyEntryCases", e => e.GetProperty("kind").GetString() == "privateKeyEntry");

    public static IEnumerable<object[]> EnvValues =>
        Qf1Vectors.Names("keyEntryCases", e => e.GetProperty("kind").GetString() == "envValue");

    // An env lookup over exactly the case's env, recording every name read.
    private sealed class RecordingEnv
    {
        private readonly Dictionary<string, string> _env;

        public RecordingEnv(Dictionary<string, string> env) => _env = env;

        public List<string> Reads { get; } = new();

        public string? Lookup(string name)
        {
            lock (Reads) Reads.Add(name);
            return _env.TryGetValue(name, out var v) ? v : null;
        }
    }

    [Fact]
    public void Constants_MatchTheVectorsFile()
    {
        var c = Qf1Vectors.Root.GetProperty("constants");
        Qf1Vectors.ToHex(Qf1.HkdfInfo).Should().Be(c.GetProperty("hkdfInfoHex").GetString());
        Qf1.OkmLength.Should().Be(c.GetProperty("okmLength").GetInt32());
        Qf1.PrivateKeyPrefix.Should().Be(c.GetProperty("privateKeyPrefix").GetString());
        Qf1.KeysetNamePattern.Should().Be(c.GetProperty("keysetNamePattern").GetString());
    }

    [Fact]
    public void Rfc7748_X25519()
    {
        var v = Qf1Vectors.Root.GetProperty("rfcChecks").GetProperty("rfc7748_6_1");
        var alice = new X25519PrivateKeyParameters(Qf1Vectors.Hex(v.GetProperty("alicePrivateHex").GetString()!));
        var bob = new X25519PrivateKeyParameters(Qf1Vectors.Hex(v.GetProperty("bobPrivateHex").GetString()!));
        Qf1Vectors.ToHex(alice.GeneratePublicKey().GetEncoded()).Should().Be(v.GetProperty("alicePublicHex").GetString());
        Qf1Vectors.ToHex(bob.GeneratePublicKey().GetEncoded()).Should().Be(v.GetProperty("bobPublicHex").GetString());
        var shared = Qf1.SharedSecret(alice, Qf1Vectors.Hex(v.GetProperty("bobPublicHex").GetString()!));
        Qf1Vectors.ToHex(shared!).Should().Be(v.GetProperty("sharedSecretHex").GetString());
        var shared2 = Qf1.SharedSecret(bob, Qf1Vectors.Hex(v.GetProperty("alicePublicHex").GetString()!));
        Qf1Vectors.ToHex(shared2!).Should().Be(v.GetProperty("sharedSecretHex").GetString());
    }

    [Fact]
    public void Rfc5869_A1_Hkdf()
    {
        var v = Qf1Vectors.Root.GetProperty("rfcChecks").GetProperty("rfc5869_A_1");
        var ikm = Qf1Vectors.Hex(v.GetProperty("ikmHex").GetString()!);
        var salt = Qf1Vectors.Hex(v.GetProperty("saltHex").GetString()!);
        var info = Qf1Vectors.Hex(v.GetProperty("infoHex").GetString()!);
        int length = v.GetProperty("length").GetInt32();
        var prk = Qf1.HkdfExtract(salt, ikm);
        Qf1Vectors.ToHex(prk).Should().Be(v.GetProperty("prkHex").GetString());
        var okm = Qf1.HkdfExpand(prk, info, length);
        Qf1Vectors.ToHex(okm).Should().Be(v.GetProperty("okmHex").GetString());
#if NET8_0_OR_GREATER
        // Cross-check the written-out HKDF against the BCL's (net8.0 only; the SDK never calls it).
        var bcl = System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256, ikm, length, salt, info);
        okm.Should().Equal(bcl);
#endif
    }

    [Theory]
    [MemberData(nameof(Positive))]
    public void Positive_ReproducesEveryIntermediateAndDecrypts(string name)
    {
        var v = Qf1Vectors.Case("positive", name);
        string S(string field) => v.GetProperty(field).GetString()!;

        var priv = Qf1.ParsePrivateKeyEntry(S("recipientPrivateKeyEntry"));
        priv.Should().NotBeNull();
        var recipient = new X25519PrivateKeyParameters(priv!);
        var pub = recipient.GeneratePublicKey().GetEncoded();
        Qf1Vectors.ToHex(pub).Should().Be(S("recipientPublicKeyHex"));
        Qf1.Fingerprint(pub).Should().Be(S("fingerprint"));

        var ephemeralPriv = new X25519PrivateKeyParameters(Qf1Vectors.Hex(S("ephemeralPrivateKeyHex")));
        var e = ephemeralPriv.GeneratePublicKey().GetEncoded();
        Qf1Vectors.ToHex(e).Should().Be(S("ephemeralPublicKeyHex"));

        // Writer side and reader side agree on S.
        Qf1Vectors.ToHex(Qf1.SharedSecret(ephemeralPriv, pub)!).Should().Be(S("sharedSecretHex"));
        var shared = Qf1.SharedSecret(recipient, e)!;
        Qf1Vectors.ToHex(shared).Should().Be(S("sharedSecretHex"));

        var salt = e.Concat(pub).ToArray();
        Qf1Vectors.ToHex(salt).Should().Be(S("hkdfSaltHex"));
        Qf1Vectors.ToHex(Qf1.HkdfInfo).Should().Be(S("hkdfInfoHex"));
        var prk = Qf1.HkdfExtract(salt, shared);
        Qf1Vectors.ToHex(prk).Should().Be(S("prkHex"));
        var okm = Qf1.DeriveOkm(shared, e, pub);
        Qf1Vectors.ToHex(okm).Should().Be(S("okmHex"));
        Qf1Vectors.ToHex(okm.Take(32).ToArray()).Should().Be(S("aesKeyHex"));
        Qf1Vectors.ToHex(okm.Skip(32).ToArray()).Should().Be(S("nonceHex"));

        var ct = Qf1.Parse(S("ciphertext"), out var parseError);
        parseError.Should().BeNull();
        ct!.Keyset.Should().Be(S("keyset"));
        ct.Fingerprint.Should().Be(S("fingerprint"));
        Encoding.ASCII.GetString(ct.Aad).Should().Be(S("aadAscii"));
        Qf1Vectors.ToHex(ct.Aad).Should().Be(S("aadHex"));
        Qf1Vectors.ToHex(ct.Ephemeral).Should().Be(S("ephemeralPublicKeyHex"));
        Qf1Vectors.ToHex(ct.Sealed).Should().Be(S("ciphertextAndTagHex"));
        Qf1Vectors.ToHex(ct.Ephemeral.Concat(ct.Sealed).ToArray()).Should().Be(S("blobHex"));
        Qf1.EncodeBase64Url(Qf1Vectors.Hex(S("blobHex"))).Should().Be(S("ciphertext").Split(':')[3]);

        var env = new RecordingEnv(Qf1Vectors.Env(Qf1Vectors.Root.GetProperty("positiveEnv")));
        var plaintext = new Qf1Reader(env.Lookup).Decrypt(S("ciphertext"), out var error);
        error.Should().BeNull();
        plaintext.Should().Be(S("plaintext"));
        Qf1Vectors.ToHex(Encoding.UTF8.GetBytes(plaintext!)).Should().Be(S("plaintextHex"));
        // Only the env var the value names is read.
        var keysetName = S("keyset");
        env.Reads.Should().OnlyContain(n => n == keysetName);
    }

    [Theory]
    [MemberData(nameof(DecryptOnly))]
    public void DecryptOnly_Decrypts(string name)
    {
        var v = Qf1Vectors.Case("decryptOnly", name);
        var env = new RecordingEnv(Qf1Vectors.Env(v.GetProperty("env")));
        var plaintext = new Qf1Reader(env.Lookup).Decrypt(v.GetProperty("ciphertext").GetString()!, out var error);
        error.Should().BeNull();
        plaintext.Should().Be(v.GetProperty("plaintext").GetString());
        Qf1Vectors.ToHex(Encoding.UTF8.GetBytes(plaintext!)).Should().Be(v.GetProperty("plaintextHex").GetString());
    }

    [Theory]
    [MemberData(nameof(Negative))]
    public void Negative_FailsWithItsClass(string name)
    {
        var v = Qf1Vectors.Case("negative", name);
        var expected = v.GetProperty("expectedError").GetString()!;
        var envValues = Qf1Vectors.Env(v.GetProperty("env"));
        var env = new RecordingEnv(envValues);
        var ciphertext = v.GetProperty("ciphertext").GetString()!;

        var plaintext = new Qf1Reader(env.Lookup).Decrypt(ciphertext, out var error);

        plaintext.Should().BeNull();
        error.Should().NotBeNull();
        error!.Class.Should().Be(expected, v.GetProperty("note").GetString());
        error.Message.Should().StartWith("qf1 " + expected + ": ");

        // Parse failures read no env at all; otherwise only the keyset named by the value.
        if (expected is Qf1Error.Malformed or Qf1Error.UnsupportedVersion or Qf1Error.InvalidKeysetName)
        {
            env.Reads.Should().BeEmpty();
        }
        else
        {
            var named = ciphertext.Split(':')[1];
            env.Reads.Should().OnlyContain(n => n == named);
        }

        // Error text: no env value or private entry, no payload (spec 9.2).
        foreach (var value in envValues.Values)
        {
            foreach (var piece in value.Split(','))
            {
                var p = piece.Trim(' ', '\t', '\r', '\n');
                if (p.Length >= 8) error.Message.Should().NotContain(p);
            }
        }
        error.Message.Should().NotContain("qf1-priv-");
        var parts = ciphertext.Split(':');
        if (parts.Length == 4 && parts[3].Length >= 16) error.Message.Should().NotContain(parts[3]);
        if (expected == Qf1Error.KeyNotHeld || expected == Qf1Error.KeysetMissing || expected == Qf1Error.KeysetInvalid)
        {
            error.Message.Should().Contain(parts[1]);
        }
        if (expected == Qf1Error.KeyNotHeld)
        {
            error.Message.Should().Contain(parts[2]);
        }
    }

    [Fact]
    public void Negative_EveryMustRejectCaseIsCovered()
    {
        // mustReject cases are authentic except for the one rule they test: a build missing the
        // rule decrypts them. They run in Negative_FailsWithItsClass; this pins that they exist.
        Qf1Vectors.Section("negative").Count(e => e.GetProperty("mustReject").GetBoolean()).Should().BeGreaterThan(20);
    }

    [Theory]
    [MemberData(nameof(PrivateEntries))]
    public void PrivateKeyEntry_Cases(string name)
    {
        var v = Qf1Vectors.Case("keyEntryCases", name);
        var raw = Qf1.ParsePrivateKeyEntry(v.GetProperty("input").GetString()!);
        if (v.TryGetProperty("expectedHex", out var hex))
        {
            raw.Should().NotBeNull();
            Qf1Vectors.ToHex(raw!).Should().Be(hex.GetString());
        }
        else
        {
            v.GetProperty("expectedError").GetString().Should().Be(Qf1Error.InvalidKeyEntry);
            raw.Should().BeNull();
        }
    }

    [Theory]
    [MemberData(nameof(EnvValues))]
    public void EnvValue_Cases(string name)
    {
        var v = Qf1Vectors.Case("keyEntryCases", name);
        var keyset = v.GetProperty("keyset").GetString()!;
        var keys = Qf1.ParseKeyset(keyset, v.GetProperty("input").GetString(), out var error);
        if (v.TryGetProperty("expectedFingerprints", out var fps))
        {
            error.Should().BeNull();
            keys!.Keys.Should().BeEquivalentTo(fps.EnumerateArray().Select(f => f.GetString()));
        }
        else
        {
            keys.Should().BeNull();
            error!.Class.Should().Be(v.GetProperty("expectedError").GetString());
            error.Message.Should().Contain(keyset);
        }
    }

    [Fact]
    public void KeysetMissing_WhenUnset()
    {
        Qf1.ParseKeyset("QUONFIG_KEYSET_X", null, out var error).Should().BeNull();
        error!.Class.Should().Be(Qf1Error.KeysetMissing);
    }

    [Fact]
    public void KeysetInvalid_NamesThe1BasedPositionAmongNonEmptyPieces()
    {
        // ",, A ,\n, B" -> pieces A and B; the bad entry B is entry 2, not piece 5.
        var good = Qf1Vectors.Case("keyEntryCases", "private-valid").GetProperty("input").GetString()!;
        Qf1.ParseKeyset("QUONFIG_KEYSET_X", ",, " + good + " ,\n, not-a-key", out var error).Should().BeNull();
        error!.Message.Should().Be("qf1 keyset_invalid: QUONFIG_KEYSET_X entry 2 is not a qf1 private key");
    }

    [Theory]
    [InlineData("QUONFIG_KEYSET", true)]
    [InlineData("QUONFIG_KEYSET_PROD", true)]
    [InlineData("QUONFIG_KEYSET_CI_2", true)]
    [InlineData("QUONFIG_KEYSETX", false)]
    [InlineData("QUONFIG_KEYSET_", false)]
    [InlineData("QUONFIG_KEYSET__A", false)]
    [InlineData("QUONFIG_KEYSET_prod", false)]
    [InlineData("quonfig_keyset", false)]
    [InlineData("HOME", false)]
    [InlineData("", false)]
    [InlineData("QUONFIG_KEYSET_TEST\n", false)]
    [InlineData("HOME\nQUONFIG_KEYSET", false)]
    [InlineData("QUONFIG_KEYSET_١", false)]
    public void KeysetName_FullStringMatch(string name, bool valid)
    {
        Qf1.IsValidKeysetName(name).Should().Be(valid);
    }

    [Fact]
    public void Base64Url_IsStrict()
    {
        Qf1.TryDecodeBase64Url("", out _).Should().BeFalse();
        Qf1.TryDecodeBase64Url("AAAAA", out _).Should().BeFalse(); // L mod 4 == 1
        Qf1.TryDecodeBase64Url("AA==", out _).Should().BeFalse();
        Qf1.TryDecodeBase64Url("A+/A", out _).Should().BeFalse();
        Qf1.TryDecodeBase64Url("AB", out _).Should().BeFalse(); // non-zero trailing bits
        Qf1.TryDecodeBase64Url("AQ", out var one).Should().BeTrue();
        one.Should().Equal(new byte[] { 1 });
        Qf1.TryDecodeBase64Url("-_8", out var two).Should().BeTrue();
        two.Should().Equal(new byte[] { 0xfb, 0xff });
    }
}
