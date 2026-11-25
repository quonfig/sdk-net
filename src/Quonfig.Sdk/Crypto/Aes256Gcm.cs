using System;

#if NET8_0_OR_GREATER
using System.Security.Cryptography;
#else
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
#endif

namespace Quonfig.Sdk.Crypto;

/// <summary>
/// AES-GCM open with a 128-bit tag: <c>System.Security.Cryptography.AesGcm</c> on net8.0, and
/// BouncyCastle on netstandard2.0, where the BCL type does not exist. Both check the tag in
/// constant time and release no plaintext before it verifies.
/// </summary>
internal static class Aes256Gcm
{
    internal const int TagBytes = 16;

    /// <summary>
    /// Decrypts <paramref name="ciphertext"/> and checks <paramref name="tag"/> over it and
    /// <paramref name="aad"/>. Returns false on an authentication failure, with no plaintext.
    /// </summary>
    internal static bool TryOpen(byte[] key, byte[] nonce, byte[] aad, byte[] ciphertext, byte[] tag, out byte[] plaintext)
    {
#if NET8_0_OR_GREATER
        var output = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, output, aad);
        }
        catch (CryptographicException)
        {
            // AuthenticationTagMismatchException derives from CryptographicException.
            Array.Clear(output, 0, output.Length);
            plaintext = Array.Empty<byte>();
            return false;
        }
        plaintext = output;
        return true;
#else
        // BouncyCastle takes the tag appended to the ciphertext.
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), TagBytes * 8, nonce, aad));
        var input = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, input, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, input, ciphertext.Length, tag.Length);
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int len;
        try
        {
            len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            len += cipher.DoFinal(output, len);
        }
        catch (InvalidCipherTextException)
        {
            Array.Clear(output, 0, output.Length);
            plaintext = Array.Empty<byte>();
            return false;
        }
        if (len == output.Length)
        {
            plaintext = output;
            return true;
        }
        plaintext = new byte[len];
        Buffer.BlockCopy(output, 0, plaintext, 0, len);
        Array.Clear(output, 0, output.Length);
        return true;
#endif
    }
}
