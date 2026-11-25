using System;
using System.Text;
using Quonfig.Sdk.Exceptions;

namespace Quonfig.Sdk.Crypto;

/// <summary>
/// AES-GCM decryption shim — uses <c>System.Security.Cryptography.AesGcm</c> on net8.0+ and
/// BouncyCastle on netstandard2.0 (where the BCL <c>AesGcm</c> type doesn't exist).
///
/// <para>Wire format mirrors sdk-go and sdk-java: <c>DATA--IV--AUTH_TAG</c>, each segment a
/// hex string, with a 128-bit auth tag and AES key bytes of length 16/24/32. Hex is decoded
/// case-insensitively to match sdk-go's normalization.</para>
///
/// <para>Throws <see cref="QuonfigDecryptionException"/> on any failure (wire format, hex parse,
/// AES init, or auth-tag mismatch) so callers don't need to discriminate between provider-specific
/// crypto exceptions. The <c>net8.0</c> and <c>netstandard2.0</c> branches must agree on the wire
/// format byte-for-byte — see <c>tests/Crypto/AesGcmCompatTests.cs</c> for the cross-SDK fixture
/// originally encrypted by sdk-java.</para>
///
/// <para>Deprecated: the SDK no longer reads the <c>decryptWith</c> format (a value carrying it
/// fails to resolve); secrets are qf1 values in the workspace's <c>secrets/</c> directory. This
/// helper is kept, unused by the SDK, so code calling it keeps compiling.</para>
/// </summary>
[Obsolete("The decryptWith secret format is no longer read by the SDK; secrets are qf1 values in secrets/. AesGcmCompat will be removed in a future major version.")]
public static class AesGcmCompat
{
    private const int GcmTagBits = 128;
    private const int GcmTagBytes = GcmTagBits / 8;
    private const int ValidParts = 3;
    private static readonly string[] PartSeparator = new[] { "--" };

    /// <summary>
    /// Decrypts a <c>DATA--IV--AUTH_TAG</c> ciphertext using the given hex-encoded AES key. The
    /// plaintext is returned as a UTF-8 string.
    /// </summary>
    /// <exception cref="QuonfigDecryptionException">Wire format, hex parse, or auth-tag failure.</exception>
    public static string Decrypt(string secretKeyHex, string ciphertext)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(secretKeyHex);
        ArgumentNullException.ThrowIfNull(ciphertext);
#else
        if (secretKeyHex is null) throw new ArgumentNullException(nameof(secretKeyHex));
        if (ciphertext is null) throw new ArgumentNullException(nameof(ciphertext));
#endif

        string[] parts = ciphertext.Split(PartSeparator, StringSplitOptions.None);
        if (parts.Length != ValidParts)
        {
            throw new QuonfigDecryptionException(
                "invalid encrypted value format: expected DATA--IV--AUTH_TAG");
        }

        byte[] key;
        byte[] iv;
        byte[] data;
        byte[] tag;
        try
        {
            key = HexDecode(secretKeyHex);
            data = HexDecode(parts[0]);
            iv = HexDecode(parts[1]);
            tag = HexDecode(parts[2]);
        }
        catch (FormatException ex)
        {
            throw new QuonfigDecryptionException("invalid hex in ciphertext or key", ex);
        }

        if (tag.Length != GcmTagBytes)
        {
            throw new QuonfigDecryptionException(
                $"auth tag must be {GcmTagBytes} bytes ({GcmTagBits} bits) — got {tag.Length}");
        }

        try
        {
            byte[] plaintext = DecryptCore(key, iv, data, tag);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (QuonfigDecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Auth-tag failures surface here from both BCL (CryptographicException) and
            // BouncyCastle (InvalidCipherTextException). Unify to QuonfigDecryptionException so
            // callers don't need to know which crypto backend they're running on.
            throw new QuonfigDecryptionException("AES-GCM decryption failed: " + ex.Message, ex);
        }
    }

    private static byte[] DecryptCore(byte[] key, byte[] iv, byte[] data, byte[] tag)
    {
        if (!Aes256Gcm.TryOpen(key, iv, Array.Empty<byte>(), data, tag, out var plaintext))
        {
            throw new QuonfigDecryptionException("AES-GCM authentication failed");
        }
        return plaintext;
    }

    private static byte[] HexDecode(string hex)
    {
        if (hex.Length == 0) return Array.Empty<byte>();
        if ((hex.Length & 1) != 0)
        {
            throw new FormatException("hex string has odd length");
        }
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            int hi = HexNibble(hex[i * 2]);
            int lo = HexNibble(hex[(i * 2) + 1]);
            result[i] = (byte)((hi << 4) | lo);
        }
        return result;
    }

    private static int HexNibble(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        throw new FormatException("invalid hex character: " + c);
    }
}
