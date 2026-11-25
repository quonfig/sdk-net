using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Quonfig.Sdk.Tests.Crypto;

/// <summary>
/// Loads <c>integration-test-data/tests/secrets/qf1-vectors.json</c> (TEST KEYS ONLY) from the
/// sibling checkout CI uses. Private key entries are read from that file at run time, so none is
/// written into this repo.
/// </summary>
internal static class Qf1Vectors
{
    private static readonly Lazy<JsonDocument> Doc = new(Load);

    internal static JsonElement Root => Doc.Value.RootElement;

    internal static IEnumerable<JsonElement> Section(string name)
    {
        foreach (var e in Root.GetProperty(name).EnumerateArray()) yield return e;
    }

    internal static JsonElement Case(string section, string name)
    {
        foreach (var e in Section(section))
        {
            if (e.GetProperty("name").GetString() == name) return e;
        }
        throw new InvalidOperationException($"no {section} vector named {name}");
    }

    internal static IEnumerable<object[]> Names(string section, Func<JsonElement, bool>? filter = null)
    {
        foreach (var e in Section(section))
        {
            if (filter is null || filter(e)) yield return new object[] { e.GetProperty("name").GetString()! };
        }
    }

    internal static Dictionary<string, string> Env(JsonElement env)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in env.EnumerateObject()) d[p.Name] = p.Value.GetString()!;
        return d;
    }

    internal static byte[] Hex(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)((Nibble(hex[i * 2]) << 4) | Nibble(hex[(i * 2) + 1]));
        return b;
    }

    private static int Nibble(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    internal static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var x in bytes) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static JsonDocument Load()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "integration-test-data", "tests", "secrets", "qf1-vectors.json");
            if (File.Exists(candidate)) return JsonDocument.Parse(File.ReadAllBytes(candidate));
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new FileNotFoundException(
            "qf1-vectors.json not found: check out integration-test-data next to sdk-net (CI does)");
    }
}
