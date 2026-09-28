using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Quonfig.Sdk.Eval;
using Quonfig.Sdk.Exceptions;
using Quonfig.Sdk.Wire;
using Xunit;

namespace Quonfig.Sdk.Tests.Eval;

/// <summary>
/// qfg-9dxb.7: a segment that references itself (directly or via a chain) through IN_SEG /
/// NOT_IN_SEG, or a decryptWith chain that loops, used to recurse until a
/// <see cref="StackOverflowException"/> — uncatchable in .NET, so it killed the host process.
/// Semantics match sdk-go (qfg-9dxb.4): a segment cycle is treated as a MISSING segment
/// (IN_SEG false, NOT_IN_SEG true); a decryptWith cycle is a decryption failure.
/// </summary>
public sealed class CycleGuardTests
{
    private static ConfigStore StoreOf(params string[] rows)
    {
        var store = new ConfigStore();
        var elements = new List<JsonElement>();
        foreach (var json in rows)
        {
            elements.Add(JsonDocument.Parse(json).RootElement.Clone());
        }
        store.Update(new ConfigEnvelope(elements, null));
        return store;
    }

    private static string Segment(string key, string op, string refKey) => $$"""
        {
          "key": "{{key}}",
          "type": "segment",
          "valueType": "bool",
          "default": {
            "rules": [
              {
                "criteria": [
                  { "operator": "{{op}}", "valueToMatch": { "type": "string", "value": "{{refKey}}" } }
                ],
                "value": { "type": "bool", "value": true }
              },
              { "criteria": [], "value": { "type": "bool", "value": false } }
            ]
          }
        }
        """;

    private static string Flag(string key, string op, string segKey) => $$"""
        {
          "key": "{{key}}",
          "type": "feature_flag",
          "valueType": "string",
          "default": {
            "rules": [
              {
                "criteria": [
                  { "operator": "{{op}}", "valueToMatch": { "type": "string", "value": "{{segKey}}" } }
                ],
                "value": { "type": "string", "value": "matched" }
              },
              { "criteria": [], "value": { "type": "string", "value": "fallthrough" } }
            ]
          }
        }
        """;

    private static string Encrypted(string key, string decryptWith) => $$"""
        {
          "key": "{{key}}",
          "type": "config",
          "valueType": "string",
          "default": {
            "rules": [
              {
                "criteria": [],
                "value": { "type": "string", "value": "deadbeef--cafe--f00d", "confidential": true, "decryptWith": "{{decryptWith}}" }
              }
            ]
          }
        }
        """;

    private static object? Eval(ConfigStore store, string key) =>
        new Evaluator(store).Evaluate(store.Get(key)!, new ContextSet(), "").Value!.Payload;

    [Fact]
    public void SelfReferencingSegment_InSeg_IsTreatedAsMissing()
    {
        // seg.a = IN_SEG seg.a -> the self-reference is "missing" (false), so seg.a falls to false.
        var store = StoreOf(Segment("seg.a", "IN_SEG", "seg.a"), Flag("flag", "IN_SEG", "seg.a"));

        Eval(store, "seg.a").Should().Be(false);
        Eval(store, "flag").Should().Be("fallthrough");
    }

    [Fact]
    public void SelfReferencingSegment_NotInSeg_IsTreatedAsMissing()
    {
        // seg.a = NOT_IN_SEG seg.a -> the self-reference is "missing", so NOT_IN_SEG is true and
        // seg.a evaluates true.
        var store = StoreOf(Segment("seg.a", "NOT_IN_SEG", "seg.a"), Flag("flag", "IN_SEG", "seg.a"));

        Eval(store, "seg.a").Should().Be(true);
        Eval(store, "flag").Should().Be("matched");
    }

    [Fact]
    public void TwoHopSegmentCycle_IsTreatedAsMissing()
    {
        // flag -> seg.a -> seg.b -> seg.a (cycle). seg.b's reference back to seg.a is missing, so
        // seg.b is false, seg.a is false, flag falls through; NOT_IN_SEG on the same chain is true.
        var store = StoreOf(
            Segment("seg.a", "IN_SEG", "seg.b"),
            Segment("seg.b", "IN_SEG", "seg.a"),
            Flag("flag", "IN_SEG", "seg.a"),
            Flag("flag.not", "NOT_IN_SEG", "seg.a"));

        Eval(store, "flag").Should().Be("fallthrough");
        Eval(store, "flag.not").Should().Be("matched");
    }

    [Fact]
    public void Diamond_IsNotACycle_AndStillResolves()
    {
        // flag.d requires seg.a AND seg.b; both reference seg.c (true). Not a cycle.
        const string segC = """
            { "key": "seg.c", "type": "segment", "valueType": "bool",
              "default": { "rules": [ { "criteria": [], "value": { "type": "bool", "value": true } } ] } }
            """;
        const string flagD = """
            { "key": "flag.d", "type": "feature_flag", "valueType": "string",
              "default": { "rules": [
                { "criteria": [
                    { "operator": "IN_SEG", "valueToMatch": { "type": "string", "value": "seg.a" } },
                    { "operator": "IN_SEG", "valueToMatch": { "type": "string", "value": "seg.b" } } ],
                  "value": { "type": "string", "value": "matched" } },
                { "criteria": [], "value": { "type": "string", "value": "fallthrough" } } ] } }
            """;
        var store = StoreOf(
            Segment("seg.a", "IN_SEG", "seg.c"),
            Segment("seg.b", "IN_SEG", "seg.c"),
            segC,
            flagD);

        Eval(store, "flag.d").Should().Be("matched");
    }

    [Fact]
    public void DecryptWith_SelfCycle_ThrowsDecryptionException()
    {
        // secret -> key.a, and key.a is itself encrypted with key.a.
        var store = StoreOf(Encrypted("secret", "key.a"), Encrypted("key.a", "key.a"));

        Action act = () => Eval(store, "secret");

        act.Should().Throw<QuonfigDecryptionException>();
    }

    [Fact]
    public void DecryptWith_TwoHopCycle_ThrowsDecryptionException()
    {
        // secret -> key.a -> key.b -> key.a.
        var store = StoreOf(
            Encrypted("secret", "key.a"),
            Encrypted("key.a", "key.b"),
            Encrypted("key.b", "key.a"));

        Action act = () => Eval(store, "secret");

        act.Should().Throw<QuonfigDecryptionException>();
    }
}
