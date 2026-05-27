using System;
using System.IO;
using System.Linq;

namespace Quonfig.Sdk.Companions.Tests;

/// <summary>
/// Builds a datadir-mode workspace on disk so tests can drive a real
/// <see cref="Quonfig.Sdk.Quonfig"/> instance without standing up SSE / HTTP.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; }

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "quonfig-companions-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Root);
    }

    public void WriteManifest(params string[] environments)
    {
        var envs = string.Join(",", environments.Select(e => $"\"{e}\""));
        File.WriteAllText(Path.Combine(Root, "quonfig.json"), $"{{\"environments\":[{envs}]}}");
    }

    public void WriteLogLevel(string filename, string key, string level)
    {
        var body = $$"""
                     {
                       "key": "{{key}}",
                       "type": "config",
                       "valueType": "log_level",
                       "default": {
                         "rules": [
                           { "criteria": [], "value": { "type": "log_level", "value": "{{level}}" } }
                         ]
                       }
                     }
                     """;
        WriteConfigFile(filename, body);
    }

    public void WriteBoolFlag(string filename, string key, bool value)
    {
        var body = $$"""
                     {
                       "key": "{{key}}",
                       "type": "feature_flag",
                       "valueType": "bool",
                       "default": {
                         "rules": [
                           { "criteria": [], "value": { "type": "bool", "value": {{(value ? "true" : "false")}} } }
                         ]
                       }
                     }
                     """;
        WriteConfigFile(filename, body);
    }

    /// <summary>
    /// Writes a bool flag that returns <paramref name="onPlan"/> when
    /// <c>user.plan == "pro"</c>, otherwise <paramref name="offPlan"/>. Lets us prove a
    /// per-request <see cref="ContextSet"/> reached the evaluator.
    /// </summary>
    public void WritePlanGatedFlag(string filename, string key, bool onPlan, bool offPlan)
    {
        var body = $$"""
                     {
                       "key": "{{key}}",
                       "type": "feature_flag",
                       "valueType": "bool",
                       "default": {
                         "rules": [
                           {
                             "criteria": [
                               { "propertyName": "user.plan", "operator": "PROP_IS_ONE_OF",
                                 "valueToMatch": { "type": "string_list", "value": ["pro"] } }
                             ],
                             "value": { "type": "bool", "value": {{(onPlan ? "true" : "false")}} }
                           },
                           { "criteria": [], "value": { "type": "bool", "value": {{(offPlan ? "true" : "false")}} } }
                         ]
                       }
                     }
                     """;
        WriteConfigFile(filename, body);
    }

    private void WriteConfigFile(string filename, string contents)
    {
        var dir = Path.Combine(Root, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), contents);
    }

    public QuonfigOptions Options() => new()
    {
        Datadir = Root,
        Environment = "production",
    };

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
