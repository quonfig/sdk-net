using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace Quonfig.Sdk.Crypto;

/// <summary>
/// A qf1 reader failure (spec <c>project/plans/secrets-qf1-spec.md</c> section 9). <see cref="Class"/>
/// is the normative class name; <see cref="Message"/> is <c>qf1 &lt;class&gt;: &lt;detail&gt;</c>. The
/// detail names keysets and fingerprints only: never key material, env values, payload or plaintext.
/// </summary>
internal sealed class Qf1Error
{
    internal const string Malformed = "malformed";
    internal const string UnsupportedVersion = "unsupported_version";
    internal const string InvalidKeysetName = "invalid_keyset_name";
    internal const string KeysetMissing = "keyset_missing";
    internal const string KeysetInvalid = "keyset_invalid";
    internal const string KeyNotHeld = "key_not_held";
    internal const string InvalidEphemeralKey = "invalid_ephemeral_key";
    internal const string DecryptFailed = "decrypt_failed";
    internal const string InvalidKeyEntry = "invalid_key_entry";

    internal Qf1Error(string errorClass, string detail)
    {
        Class = errorClass;
        Detail = detail;
    }

    internal string Class { get; }

    internal string Detail { get; }

    internal string Message => "qf1 " + Class + ": " + Detail;
}

/// <summary>A parsed qf1 value (spec 3.5). Holds only public data.</summary>
internal sealed class Qf1Ciphertext
{
    internal Qf1Ciphertext(string keyset, string fingerprint, byte[] aad, byte[] ephemeral, byte[] sealedBytes)
    {
        Keyset = keyset;
        Fingerprint = fingerprint;
        Aad = aad;
        Ephemeral = ephemeral;
        Sealed = sealedBytes;
    }

    internal string Keyset { get; }

    internal string Fingerprint { get; }

    /// <summary><c>qf1:&lt;KEYSET&gt;:&lt;FINGERPRINT&gt;:</c> as ASCII bytes (spec 5.4).</summary>
    internal byte[] Aad { get; }

    /// <summary>E, the 32 ephemeral public key bytes as transmitted.</summary>
    internal byte[] Ephemeral { get; }

    /// <summary>C || T.</summary>
    internal byte[] Sealed { get; }
}

/// <summary>One private key held in a keyset env var, with the public key derived from it.</summary>
internal sealed class Qf1HeldKey
{
    internal Qf1HeldKey(X25519PrivateKeyParameters privateKey, byte[] publicKey)
    {
        PrivateKey = privateKey;
        PublicKey = publicKey;
    }

    internal X25519PrivateKeyParameters PrivateKey { get; }

    internal byte[] PublicKey { get; }
}

/// <summary>
/// The qf1 secret format, read side (spec <c>project/plans/secrets-qf1-spec.md</c>): X25519
/// (BouncyCastle on every target, since the BCL has none), HKDF-SHA256 written out from HMAC per
/// RFC 5869, AES-256-GCM with the header as AAD. Keys are looked up by (keyset name, fingerprint);
/// there is no trial decryption.
/// </summary>
internal static class Qf1
{
    internal const string PrivateKeyPrefix = "qf1-priv-";
    internal const int KeyLength = 32;
    internal const int MinPayloadLength = KeyLength + Aes256Gcm.TagBytes;
    internal const int OkmLength = 44;
    internal const string KeysetNamePattern = "^QUONFIG_KEYSET(_[A-Z0-9]+)*$";

    // ASCII "quonfig.com/qf1/X25519" (spec 5.1).
    internal static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("quonfig.com/qf1/X25519");

    // Full-string matches (spec 0): \A and \z, never ^/$ (.NET's $ matches before a final LF).
    // No IgnoreCase, so [A-Z0-9] and [0-9a-f] are ASCII only.
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex KeysetNameRegex = new(@"\AQUONFIG_KEYSET(_[A-Z0-9]+)*\z", Options, RegexTimeout);
    private static readonly Regex FingerprintRegex = new(@"\A[0-9a-f]{8}\z", Options, RegexTimeout);
    private static readonly Regex OtherVersionRegex = new(@"\Aqf[1-9][0-9]*:", Options, RegexTimeout);

    // Only these four are stripped around an env entry (spec 6.2). Never String.Trim() with no
    // arguments: it also strips VT, FF, NBSP, U+2028 and the rest of Unicode whitespace.
    private static readonly char[] EnvStripChars = { ' ', '\t', '\r', '\n' };

    // Strict UTF-8: throws on any invalid sequence (overlong forms, encoded surrogates) instead of
    // substituting U+FFFD. GetString never strips a leading BOM, so U+FEFF survives (spec 3.3).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    internal static bool IsValidKeysetName(string name) => KeysetNameRegex.IsMatch(name);

    /// <summary>Spec 3.5, in order; the first failing step decides the class. Reads no environment.</summary>
    internal static Qf1Ciphertext? Parse(string value, out Qf1Error? error)
    {
        error = null;
        if (!value.StartsWith("qf1:", StringComparison.Ordinal))
        {
            error = OtherVersionRegex.IsMatch(value)
                ? new Qf1Error(Qf1Error.UnsupportedVersion, "value uses an unsupported qf version; this SDK reads qf1 only")
                : new Qf1Error(Qf1Error.Malformed, "value is not a qf1 string");
            return null;
        }

        // string.Split(char) keeps empty fields, trailing ones included.
        string[] fields = value.Split(':');
        if (fields.Length != 4)
        {
            error = new Qf1Error(Qf1Error.Malformed,
                string.Format(CultureInfo.InvariantCulture, "value has {0} colon-separated fields, want 4", fields.Length));
            return null;
        }

        string keyset = fields[1];
        string fp = fields[2];
        if (!IsValidKeysetName(keyset))
        {
            error = new Qf1Error(Qf1Error.InvalidKeysetName,
                "keyset name " + QuoteName(keyset) + " does not match " + KeysetNamePattern);
            return null;
        }
        if (!FingerprintRegex.IsMatch(fp))
        {
            error = new Qf1Error(Qf1Error.Malformed, "fingerprint field is not 8 lowercase hex characters");
            return null;
        }
        if (!TryDecodeBase64Url(fields[3], out var payload))
        {
            error = new Qf1Error(Qf1Error.Malformed, "payload is not canonical unpadded base64url");
            return null;
        }
        if (payload.Length < MinPayloadLength)
        {
            error = new Qf1Error(Qf1Error.Malformed,
                string.Format(CultureInfo.InvariantCulture, "payload is {0} bytes, want at least {1}", payload.Length, MinPayloadLength));
            return null;
        }
        if ((payload[KeyLength - 1] & 0x80) != 0)
        {
            error = new Qf1Error(Qf1Error.Malformed, "ephemeral key has its top bit set");
            return null;
        }

        var ephemeral = new byte[KeyLength];
        Buffer.BlockCopy(payload, 0, ephemeral, 0, KeyLength);
        var sealedBytes = new byte[payload.Length - KeyLength];
        Buffer.BlockCopy(payload, KeyLength, sealedBytes, 0, sealedBytes.Length);
        byte[] aad = Encoding.ASCII.GetBytes("qf1:" + keyset + ":" + fp + ":");
        return new Qf1Ciphertext(keyset, fp, aad, ephemeral, sealedBytes);
    }

    /// <summary>
    /// <c>qf1-priv-</c> + 43 canonical base64url characters -> the raw, unclamped 32-byte X25519
    /// scalar (spec 6.1). Null when the entry is invalid (<c>invalid_key_entry</c>).
    /// </summary>
    internal static byte[]? ParsePrivateKeyEntry(string entry)
    {
        if (!entry.StartsWith(PrivateKeyPrefix, StringComparison.Ordinal)) return null;
        string body = entry.Substring(PrivateKeyPrefix.Length);
        if (body.Length != 43) return null;
        if (!TryDecodeBase64Url(body, out var raw) || raw.Length != KeyLength) return null;
        return raw;
    }

    /// <summary>
    /// Parses the value of the keyset env var <paramref name="name"/> (spec 6.2). A null
    /// <paramref name="value"/> means unset. Returns fingerprint -> key, or null with
    /// <paramref name="error"/> set (<c>keyset_missing</c> or <c>keyset_invalid</c>).
    /// </summary>
    internal static IReadOnlyDictionary<string, Qf1HeldKey>? ParseKeyset(string name, string? value, out Qf1Error? error)
    {
        error = null;
        if (value is null)
        {
            error = new Qf1Error(Qf1Error.KeysetMissing, "this runtime has no " + name);
            return null;
        }

        var entries = new List<string>();
        foreach (var piece in value.Split(','))
        {
            string stripped = piece.Trim(EnvStripChars);
            if (stripped.Length > 0) entries.Add(stripped);
        }
        if (entries.Count == 0)
        {
            error = new Qf1Error(Qf1Error.KeysetMissing, name + " is set but holds no keys");
            return null;
        }

        var keys = new Dictionary<string, Qf1HeldKey>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            byte[]? raw = ParsePrivateKeyEntry(entries[i]);
            if (raw is null)
            {
                // Position only (1-based, over the non-empty pieces), never content (spec 9.2).
                error = new Qf1Error(Qf1Error.KeysetInvalid,
                    string.Format(CultureInfo.InvariantCulture, "{0} entry {1} is not a qf1 private key", name, i + 1));
                return null;
            }
            var priv = new X25519PrivateKeyParameters(raw);
            Array.Clear(raw, 0, raw.Length);
            byte[] pub = priv.GeneratePublicKey().GetEncoded();
            string fp = Fingerprint(pub);
            if (keys.TryGetValue(fp, out var existing))
            {
                // The same public key (identical or clamp-equivalent entries) collapses; two
                // different keys with one fingerprint make the whole keyset invalid (spec 4).
                if (BytesEqual(existing.PublicKey, pub)) continue;
                error = new Qf1Error(Qf1Error.KeysetInvalid,
                    name + " holds two different keys with fingerprint " + fp);
                return null;
            }
            keys[fp] = new Qf1HeldKey(priv, pub);
        }
        return keys;
    }

    /// <summary>lowercase-hex(SHA-256(pub)[0:4]) (spec 4).</summary>
    internal static string Fingerprint(byte[] publicKey)
    {
#if NET8_0_OR_GREATER
        byte[] sum = SHA256.HashData(publicKey);
#else
        byte[] sum;
        using (var sha = SHA256.Create())
        {
            sum = sha.ComputeHash(publicKey);
        }
#endif
        var sb = new StringBuilder(8);
        for (int i = 0; i < 4; i++) sb.Append(sum[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// X25519(priv, u) (RFC 7748, spec 5.2). Null when the result is all zero: BouncyCastle refuses
    /// a small-order input with <c>InvalidOperationException: X25519 agreement failed</c>, which IS
    /// the all-zero failure; the output is also checked for libraries that return zeros. Any other
    /// library error propagates as the bug it is.
    /// </summary>
    internal static byte[]? SharedSecret(X25519PrivateKeyParameters privateKey, byte[] u)
    {
        var agreement = new X25519Agreement();
        agreement.Init(privateKey);
        var shared = new byte[agreement.AgreementSize];
        try
        {
            agreement.CalculateAgreement(new X25519PublicKeyParameters(u), shared, 0);
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "X25519 agreement failed", StringComparison.Ordinal))
        {
            return null;
        }
        return IsAllZero(shared) ? null : shared;
    }

    /// <summary>RFC 5869 Extract: HMAC-SHA256(key = salt, msg = ikm).</summary>
    internal static byte[] HkdfExtract(byte[] salt, byte[] ikm) => HmacSha256(salt, ikm);

    /// <summary>RFC 5869 Expand: T(i) = HMAC(PRK, T(i-1) || info || i), OKM = first L bytes.</summary>
    internal static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        var okm = new byte[length];
        var t = Array.Empty<byte>();
        int written = 0;
        for (int i = 1; written < length; i++)
        {
            var msg = new byte[t.Length + info.Length + 1];
            Buffer.BlockCopy(t, 0, msg, 0, t.Length);
            Buffer.BlockCopy(info, 0, msg, t.Length, info.Length);
            msg[msg.Length - 1] = (byte)i;
            t = HmacSha256(prk, msg);
            int n = Math.Min(t.Length, length - written);
            Buffer.BlockCopy(t, 0, okm, written, n);
            written += n;
        }
        return okm;
    }

    /// <summary>salt = E || pub_r, PRK = Extract(salt, S), OKM = Expand(PRK, INFO, 44) (spec 5.3).</summary>
    internal static byte[] DeriveOkm(byte[] shared, byte[] ephemeral, byte[] recipientPublic)
    {
        var salt = new byte[ephemeral.Length + recipientPublic.Length];
        Buffer.BlockCopy(ephemeral, 0, salt, 0, ephemeral.Length);
        Buffer.BlockCopy(recipientPublic, 0, salt, ephemeral.Length, recipientPublic.Length);
        byte[] prk = HkdfExtract(salt, shared);
        byte[] okm = HkdfExpand(prk, HkdfInfo, OkmLength);
        Array.Clear(prk, 0, prk.Length);
        return okm;
    }

    /// <summary>
    /// Spec 8 steps 3 to 7 for a parsed value and its keyset: look up the fingerprint, X25519,
    /// HKDF, AES-256-GCM, strict UTF-8.
    /// </summary>
    internal static string? Open(Qf1Ciphertext ct, IReadOnlyDictionary<string, Qf1HeldKey> keyset, out Qf1Error? error)
    {
        error = null;
        if (!keyset.TryGetValue(ct.Fingerprint, out var held))
        {
            var held2 = new List<string>(keyset.Keys);
            held2.Sort(StringComparer.Ordinal);
            error = new Qf1Error(Qf1Error.KeyNotHeld,
                "value is encrypted to " + ct.Keyset + " fingerprint " + ct.Fingerprint +
                "; this runtime holds " + ct.Keyset + " fingerprints [" + string.Join(" ", held2) + "]");
            return null;
        }

        byte[]? shared = SharedSecret(held.PrivateKey, ct.Ephemeral);
        if (shared is null)
        {
            error = new Qf1Error(Qf1Error.InvalidEphemeralKey,
                "X25519 shared secret for " + ct.Keyset + " fingerprint " + ct.Fingerprint + " is all zero");
            return null;
        }

        byte[] okm = DeriveOkm(shared, ct.Ephemeral, held.PublicKey);
        Array.Clear(shared, 0, shared.Length);
        var key = new byte[32];
        var nonce = new byte[12];
        Buffer.BlockCopy(okm, 0, key, 0, 32);
        Buffer.BlockCopy(okm, 32, nonce, 0, 12);
        Array.Clear(okm, 0, okm.Length);

        int cLen = ct.Sealed.Length - Aes256Gcm.TagBytes;
        var c = new byte[cLen];
        var tag = new byte[Aes256Gcm.TagBytes];
        Buffer.BlockCopy(ct.Sealed, 0, c, 0, cLen);
        Buffer.BlockCopy(ct.Sealed, cLen, tag, 0, Aes256Gcm.TagBytes);

        bool ok = Aes256Gcm.TryOpen(key, nonce, ct.Aad, c, tag, out var plain);
        Array.Clear(key, 0, key.Length);
        if (!ok)
        {
            error = new Qf1Error(Qf1Error.DecryptFailed,
                "authentication failed for " + ct.Keyset + " fingerprint " + ct.Fingerprint);
            return null;
        }

        try
        {
            return StrictUtf8.GetString(plain);
        }
        catch (DecoderFallbackException)
        {
            // The exception text would quote the offending bytes: drop it.
            error = new Qf1Error(Qf1Error.DecryptFailed,
                "plaintext for " + ct.Keyset + " fingerprint " + ct.Fingerprint + " is not valid UTF-8");
            return null;
        }
        finally
        {
            Array.Clear(plain, 0, plain.Length);
        }
    }

    /// <summary>
    /// Strict RFC 4648 section 5 base64url without padding (spec 3.4): rejects the empty string,
    /// any character outside the alphabet, a length with L mod 4 == 1, and a non-canonical
    /// encoding (checked by re-encoding and comparing). Implemented here because netstandard2.0
    /// has no strict unpadded base64url decoder.
    /// </summary>
    internal static bool TryDecodeBase64Url(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        int len = text.Length;
        if (len == 0 || len % 4 == 1) return false;
        var output = new byte[len * 3 / 4];
        int acc = 0;
        int bits = 0;
        int o = 0;
        foreach (char ch in text)
        {
            int v = Base64UrlValue(ch);
            if (v < 0) return false;
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output[o++] = (byte)(acc >> bits);
                acc &= (1 << bits) - 1;
            }
        }
        if (!string.Equals(EncodeBase64Url(output), text, StringComparison.Ordinal)) return false;
        bytes = output;
        return true;
    }

    /// <summary>Unpadded base64url encoding.</summary>
    internal static string EncodeBase64Url(byte[] data)
    {
        var sb = new StringBuilder(((data.Length * 4) + 2) / 3);
        int i = 0;
        for (; i + 2 < data.Length; i += 3)
        {
            int n = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
            sb.Append(Base64UrlAlphabet[(n >> 18) & 63]).Append(Base64UrlAlphabet[(n >> 12) & 63])
              .Append(Base64UrlAlphabet[(n >> 6) & 63]).Append(Base64UrlAlphabet[n & 63]);
        }
        int rest = data.Length - i;
        if (rest == 1)
        {
            int n = data[i] << 16;
            sb.Append(Base64UrlAlphabet[(n >> 18) & 63]).Append(Base64UrlAlphabet[(n >> 12) & 63]);
        }
        else if (rest == 2)
        {
            int n = (data[i] << 16) | (data[i + 1] << 8);
            sb.Append(Base64UrlAlphabet[(n >> 18) & 63]).Append(Base64UrlAlphabet[(n >> 12) & 63])
              .Append(Base64UrlAlphabet[(n >> 6) & 63]);
        }
        return sb.ToString();
    }

    private static int Base64UrlValue(char c)
    {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '-') return 62;
        if (c == '_') return 63;
        return -1;
    }

    // At most 64 characters, control and non-ASCII characters escaped (spec 9.2).
    internal static string QuoteName(string name)
    {
        string s = name.Length > 64 ? name.Substring(0, 64) : name;
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static byte[] HmacSha256(byte[] key, byte[] message)
    {
#if NET8_0_OR_GREATER
        return HMACSHA256.HashData(key, message);
#else
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(message);
#endif
    }

    private static bool IsAllZero(byte[] b)
    {
        int acc = 0;
        foreach (var x in b) acc |= x;
        return acc == 0;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

/// <summary>
/// Decrypts qf1 values with keysets read lazily through the SDK's injected env lookup (never
/// <see cref="Environment.GetEnvironmentVariable(string)"/> directly). Only the env var named by a
/// value's keyset field is read, and only after that name passes spec section 2. A parsed keyset is
/// cached per env var name and reused only while the variable's VALUE is unchanged, so a missing
/// or broken keyset is never remembered past a fix. Safe for concurrent use.
/// </summary>
internal sealed class Qf1Reader
{
    private readonly Func<string, string?> _lookup;
    private readonly ConcurrentDictionary<string, CachedKeyset> _cache = new(StringComparer.Ordinal);

    internal Qf1Reader(Func<string, string?> lookup)
    {
        _lookup = lookup;
    }

    /// <summary>Spec 8. Returns the plaintext, or null with <paramref name="error"/> set.</summary>
    internal string? Decrypt(string value, out Qf1Error? error)
    {
        var ct = Qf1.Parse(value, out error);
        if (ct is null) return null;

        var keyset = Keyset(ct.Keyset, out error);
        if (keyset is null)
        {
            if (error!.Class == Qf1Error.KeysetMissing)
            {
                error = new Qf1Error(Qf1Error.KeysetMissing,
                    "value is encrypted to " + ct.Keyset + " fingerprint " + ct.Fingerprint + "; " + error.Detail);
            }
            return null;
        }
        return Qf1.Open(ct, keyset, out error);
    }

    private IReadOnlyDictionary<string, Qf1HeldKey>? Keyset(string name, out Qf1Error? error)
    {
        string? value = _lookup(name);
        if (value is null) return Qf1.ParseKeyset(name, null, out error);

        if (_cache.TryGetValue(name, out var cached) && string.Equals(cached.Value, value, StringComparison.Ordinal))
        {
            error = cached.Error;
            return cached.Keys;
        }
        var keys = Qf1.ParseKeyset(name, value, out error);
        _cache[name] = new CachedKeyset(value, keys, error);
        return keys;
    }

    private sealed class CachedKeyset
    {
        internal CachedKeyset(string value, IReadOnlyDictionary<string, Qf1HeldKey>? keys, Qf1Error? error)
        {
            Value = value;
            Keys = keys;
            Error = error;
        }

        internal string Value { get; }

        internal IReadOnlyDictionary<string, Qf1HeldKey>? Keys { get; }

        internal Qf1Error? Error { get; }
    }
}
