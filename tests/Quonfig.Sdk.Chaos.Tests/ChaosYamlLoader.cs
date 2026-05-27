using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YamlDotNet.RepresentationModel;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// YamlDotNet-based parser for chaos scenario files. Walks the raw
/// <see cref="YamlMappingNode"/> tree rather than using YamlDotNet's typed deserializer because
/// the YAML keys are snake_case while the model is PascalCase, and the inject block is a
/// discriminated union of convenience aliases vs raw toxiproxy escape hatch. Mirrors
/// <c>sdk-java/.../ChaosYamlLoader.java</c>.
/// </summary>
internal static class ChaosYamlLoader
{
    public static ChaosScenario Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0)
        {
            throw new InvalidDataException($"chaos YAML {path} contained no documents");
        }
        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"chaos YAML {path}: root is not a mapping");
        }
        return Parse(root);
    }

    public static ChaosScenario LoadString(string yamlText)
    {
        if (yamlText is null) throw new ArgumentNullException(nameof(yamlText));
        using var reader = new StringReader(yamlText);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0)
        {
            throw new InvalidDataException("chaos YAML contained no documents");
        }
        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("chaos YAML root is not a mapping");
        }
        return Parse(root);
    }

    private static ChaosScenario Parse(YamlMappingNode root)
    {
        var scenario = new ChaosScenario
        {
            Function = AsString(Lookup(root, "function")),
        };
        if (Lookup(root, "tests") is YamlSequenceNode tests)
        {
            foreach (var t in tests)
            {
                if (t is YamlMappingNode run) scenario.Tests.Add(ParseRun(run));
            }
        }
        return scenario;
    }

    private static ChaosScenario.Run ParseRun(YamlMappingNode m)
    {
        var run = new ChaosScenario.Run
        {
            Name = AsString(Lookup(m, "name")),
            Description = AsString(Lookup(m, "description")),
            Setup = ParseSetup(Lookup(m, "setup") as YamlMappingNode),
            Chaos = ParseEvents(Lookup(m, "chaos")),
            Expectations = ParseExpectations(Lookup(m, "expectations")),
        };
        return run;
    }

    private static ChaosScenario.Setup ParseSetup(YamlMappingNode? m)
    {
        var s = new ChaosScenario.Setup();
        if (m is null) return s;
        s.Sdk = AsString(Lookup(m, "sdk"));
        s.SseEndpoint = AsString(Lookup(m, "sse_endpoint"));
        s.HttpEndpoint = AsString(Lookup(m, "http_endpoint"));
        s.WallClockSeconds = AsInt(Lookup(m, "wall_clock_seconds"), 30);
        s.UserCallback = AsString(Lookup(m, "user_callback"));
        return s;
    }

    private static List<ChaosScenario.Event> ParseEvents(YamlNode? raw)
    {
        var list = new List<ChaosScenario.Event>();
        if (raw is not YamlSequenceNode seq) return list;
        foreach (var el in seq)
        {
            if (el is not YamlMappingNode em) continue;
            var ev = new ChaosScenario.Event
            {
                AtMs = AsInt(Lookup(em, "at_ms"), 0),
            };
            if (Lookup(em, "inject") is YamlMappingNode injNode)
            {
                ev.Inject = ParseInject(injNode);
            }
            if (Lookup(em, "clear") is YamlScalarNode clearScalar)
            {
                ev.Clear = clearScalar.Value;
            }
            if (Lookup(em, "process") is YamlMappingNode procNode)
            {
                ev.Process = ParseProcess(procNode);
            }
            list.Add(ev);
        }
        return list;
    }

    private static ChaosScenario.Inject ParseInject(YamlMappingNode m)
    {
        var inj = new ChaosScenario.Inject
        {
            Name = AsString(Lookup(m, "name")),
            SseSilentStallAfterMs = AsIntOrNull(Lookup(m, "sse_silent_stall_after_ms")),
            SseLatencyMs = AsIntOrNull(Lookup(m, "sse_latency_ms")),
            SseBandwidthKbps = AsIntOrNull(Lookup(m, "sse_bandwidth_kbps")),
            SseDownMs = AsIntOrNull(Lookup(m, "sse_down_ms")),
            BothDownMs = AsIntOrNull(Lookup(m, "both_down_ms")),
            SseHalfOpenAfterBytes = AsIntOrNull(Lookup(m, "sse_half_open_after_bytes")),
            SseHttpStatus = AsIntOrNull(Lookup(m, "sse_http_status")),
            Proxy = AsString(Lookup(m, "proxy")),
        };
        if (Lookup(m, "toxic") is YamlMappingNode toxic)
        {
            inj.Toxic = ToDictionary(toxic);
        }
        return inj;
    }

    private static ChaosScenario.Process ParseProcess(YamlMappingNode m)
    {
        return new ChaosScenario.Process
        {
            Action = AsString(Lookup(m, "action")),
            Count = AsInt(Lookup(m, "count"), 0),
            IntervalMs = AsInt(Lookup(m, "interval_ms"), 0),
        };
    }

    private static List<ChaosScenario.Expectation> ParseExpectations(YamlNode? raw)
    {
        var list = new List<ChaosScenario.Expectation>();
        if (raw is not YamlSequenceNode seq) return list;
        foreach (var el in seq)
        {
            if (el is not YamlMappingNode em) continue;
            list.Add(new ChaosScenario.Expectation
            {
                WithinMs = AsInt(Lookup(em, "within_ms"), 0),
                MustHoldForMs = AsInt(Lookup(em, "must_hold_for_ms"), 0),
                AssertExpr = AsString(Lookup(em, "assert")) ?? string.Empty,
            });
        }
        return list;
    }

    private static Dictionary<string, object?> ToDictionary(YamlMappingNode m)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in m.Children)
        {
            if (kv.Key is not YamlScalarNode keyScalar) continue;
            d[keyScalar.Value ?? string.Empty] = ToObject(kv.Value);
        }
        return d;
    }

    private static object? ToObject(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode s:
                if (int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return i;
                if (double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                if (bool.TryParse(s.Value, out var b))
                    return b;
                return s.Value;
            case YamlSequenceNode seq:
                var list = new List<object?>();
                foreach (var el in seq) list.Add(ToObject(el));
                return list;
            case YamlMappingNode m:
                return ToDictionary(m);
            default:
                return null;
        }
    }

    private static YamlNode? Lookup(YamlMappingNode m, string key)
    {
        var k = new YamlScalarNode(key);
        if (m.Children.TryGetValue(k, out var v)) return v;
        return null;
    }

    private static string? AsString(YamlNode? n) => n is YamlScalarNode s ? s.Value : null;

    private static int AsInt(YamlNode? n, int def)
    {
        if (n is YamlScalarNode s && int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return def;
    }

    private static int? AsIntOrNull(YamlNode? n)
    {
        if (n is YamlScalarNode s && int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return null;
    }
}
