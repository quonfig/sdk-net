using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading;
using Quonfig.Sdk.Wire;

namespace Quonfig.Sdk.Eval;

/// <summary>
/// Thread-safe in-memory cache of <see cref="ConfigResponse"/> entries keyed by config name.
/// Backed by an <see cref="ImmutableDictionary{TKey,TValue}"/> held behind <see cref="Volatile.Read{T}"/>
/// so reads are lock-free and never observe a torn or half-applied update — the supervisor swaps the
/// whole map in one publish, and any reader either sees the old map or the new map, never a mix.
///
/// <para>Mirrors <c>sdk-java InMemoryConfigStore</c> and <c>sdk-go in-memory store</c> in role: it is
/// the read path the evaluator hits for every getter and for every <c>IN_SEGMENT</c> recursion. Hot,
/// so reads must be allocation-free and contention-free. Writes happen only on a fresh envelope from
/// either SSE/HTTP or the datadir loader — a few times a second at most.</para>
/// </summary>
public sealed class ConfigStore
{
    private ImmutableDictionary<string, ConfigResponse> _byKey = ImmutableDictionary<string, ConfigResponse>.Empty;

    /// <summary>Returns the config row for <paramref name="key"/>, or <c>null</c> if missing.</summary>
    public ConfigResponse? Get(string key)
    {
        var snapshot = Volatile.Read(ref _byKey);
        return snapshot.TryGetValue(key, out var row) ? row : null;
    }

    /// <summary>Snapshot of all keys currently in the store. Stable for the caller, even if a writer publishes mid-iteration.</summary>
    public IReadOnlyList<string> Keys()
    {
        var snapshot = Volatile.Read(ref _byKey);
        var keys = new string[snapshot.Count];
        int i = 0;
        foreach (var k in snapshot.Keys) keys[i++] = k;
        return keys;
    }

    /// <summary>
    /// Atomically replaces the entire store with the configs from <paramref name="envelope"/>. Rows
    /// without a usable <c>"key"</c> field are skipped — defense-in-depth so a stray non-envelope
    /// JSON file (e.g. a schema doc) never produces a ghost row (qfg-b5yi).
    /// </summary>
    public void Update(ConfigEnvelope envelope)
    {
        // ArgumentNullException.ThrowIfNull is net6.0+ only; we still target netstandard2.0.
#pragma warning disable CA1510
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
#pragma warning restore CA1510
        var builder = ImmutableDictionary.CreateBuilder<string, ConfigResponse>();
        foreach (var element in envelope.Configs)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!element.TryGetProperty("key", out var keyProp)) continue;
            if (keyProp.ValueKind != JsonValueKind.String) continue;
            var key = keyProp.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            // Last-write-wins on duplicate keys, matching sdk-java's LinkedHashMap put().
            builder[key!] = new ConfigResponse(key!, element);
        }
        Volatile.Write(ref _byKey, builder.ToImmutable());
    }
}
