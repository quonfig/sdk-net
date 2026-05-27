using System;
using System.Text;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// 32-bit Murmur3 (Austin Appleby) port — the canonical seed-0 variant used by sdk-java's
/// <c>Hashing.murmur3_32_fixed()</c> and sdk-go's <c>spaolacci/murmur3</c>. The output is the load-
/// bearing primitive for weighted-value bucketing across every Quonfig SDK; the same
/// <c>(configKey, trackingId)</c> pair must land in the same bucket on every runtime, so the bit
/// pattern emitted here is a hard contract — see <c>tests/Eval/Murmur3Tests.cs</c> for the vector
/// fixtures pulled from <c>integration-test-data</c>.
/// </summary>
public static class Murmur3
{
    private const uint C1 = 0xcc9e2d51u;
    private const uint C2 = 0x1b873593u;

    /// <summary>
    /// Hashes <paramref name="data"/> with Murmur3-32 using the given <paramref name="seed"/>.
    /// Callers wanting cross-SDK weighted-value bucketing must pass <c>seed = 0</c>.
    /// </summary>
    public static uint Hash32(byte[] data, uint seed = 0u)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(data);
#else
        if (data is null) throw new ArgumentNullException(nameof(data));
#endif

        uint h1 = seed;
        int len = data.Length;
        int nblocks = len >> 2;

        // Body — process 4-byte blocks little-endian.
        for (int i = 0; i < nblocks; i++)
        {
            int blockStart = i << 2;
            uint k1 = (uint)data[blockStart]
                     | ((uint)data[blockStart + 1] << 8)
                     | ((uint)data[blockStart + 2] << 16)
                     | ((uint)data[blockStart + 3] << 24);

            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;

            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = (h1 * 5) + 0xe6546b64u;
        }

        // Tail — 1, 2, or 3 leftover bytes.
        int tailStart = nblocks << 2;
        uint kt = 0;
        int rem = len & 3;
        if (rem == 3)
        {
            kt ^= (uint)data[tailStart + 2] << 16;
            kt ^= (uint)data[tailStart + 1] << 8;
            kt ^= (uint)data[tailStart];
            kt *= C1;
            kt = RotateLeft(kt, 15);
            kt *= C2;
            h1 ^= kt;
        }
        else if (rem == 2)
        {
            kt ^= (uint)data[tailStart + 1] << 8;
            kt ^= (uint)data[tailStart];
            kt *= C1;
            kt = RotateLeft(kt, 15);
            kt *= C2;
            h1 ^= kt;
        }
        else if (rem == 1)
        {
            kt ^= (uint)data[tailStart];
            kt *= C1;
            kt = RotateLeft(kt, 15);
            kt *= C2;
            h1 ^= kt;
        }

        // Finalization mix — fmix32.
        h1 ^= (uint)len;
        h1 ^= h1 >> 16;
        h1 *= 0x85ebca6bu;
        h1 ^= h1 >> 13;
        h1 *= 0xc2b2ae35u;
        h1 ^= h1 >> 16;
        return h1;
    }

    /// <summary>
    /// UTF-8-encodes <paramref name="value"/> and returns a deterministic fraction in <c>[0, 1]</c>
    /// computed as <c>Hash32(utf8) / uint.MaxValue</c> — the same arithmetic sdk-go and sdk-java
    /// use for weighted-value bucketing.
    /// </summary>
    public static double HashZeroToOne(string value)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value);
#else
        if (value is null) throw new ArgumentNullException(nameof(value));
#endif
        uint h = Hash32(Encoding.UTF8.GetBytes(value), 0u);
        return (double)h / (double)uint.MaxValue;
    }

    private static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));
}
