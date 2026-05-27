using System.Linq;
using Xunit;

namespace Quonfig.Sdk.Chaos.Tests;

/// <summary>
/// Unit tests for the YAML scenario parser. Pin the snake_case → PascalCase shovelling so the
/// runner can rely on the model shape without a live chaos boot. Mirrors sdk-java's expectations
/// in <c>ChaosYamlLoaderTest</c>.
/// </summary>
public class ChaosYamlLoaderTests
{
    [Fact]
    public void ParsesScenarioMetadataAndSetupBlock()
    {
        const string yaml = @"
function: sse_resilience
tests:
  - name: ""01 baseline""
    description: |
      healthy baseline.
    setup:
      sdk: any
      sse_endpoint: chaos
      http_endpoint: chaos
      wall_clock_seconds: 30
      user_callback: throw
    chaos: []
    expectations:
      - within_ms: 5000
        assert: ""client.connectionState() == 'connected'""
";
        var s = ChaosYamlLoader.LoadString(yaml);
        Assert.Equal("sse_resilience", s.Function);
        Assert.Single(s.Tests);
        var run = s.Tests[0];
        Assert.Equal("01 baseline", run.Name);
        Assert.Equal("any", run.Setup.Sdk);
        Assert.Equal("chaos", run.Setup.SseEndpoint);
        Assert.Equal(30, run.Setup.WallClockSeconds);
        Assert.Equal("throw", run.Setup.UserCallback);
        Assert.Single(run.Expectations);
        Assert.Equal(5000, run.Expectations[0].WithinMs);
        Assert.Equal("client.connectionState() == 'connected'", run.Expectations[0].AssertExpr);
    }

    [Fact]
    public void ParsesInjectAliasesAndClearEvents()
    {
        const string yaml = @"
function: sse_resilience
tests:
  - name: ""silent stall""
    setup:
      wall_clock_seconds: 200
    chaos:
      - at_ms: 5000
        inject:
          name: stall
          sse_silent_stall_after_ms: 0
      - at_ms: 125000
        clear: stall
    expectations: []
";
        var s = ChaosYamlLoader.LoadString(yaml);
        var ev = s.Tests[0].Chaos;
        Assert.Equal(2, ev.Count);
        Assert.NotNull(ev[0].Inject);
        Assert.Equal("stall", ev[0].Inject!.Name);
        Assert.Equal(0, ev[0].Inject!.SseSilentStallAfterMs);
        Assert.Equal(125000, ev[1].AtMs);
        Assert.Equal("stall", ev[1].Clear);
    }

    [Fact]
    public void ParsesRawToxicEscapeHatch()
    {
        const string yaml = @"
function: sse_resilience
tests:
  - name: ""bandwidth""
    setup:
      wall_clock_seconds: 60
    chaos:
      - at_ms: 5000
        inject:
          proxy: sse
          toxic:
            type: bandwidth
            attributes:
              rate: 1
    expectations: []
";
        var s = ChaosYamlLoader.LoadString(yaml);
        var inj = s.Tests[0].Chaos[0].Inject!;
        Assert.Equal("sse", inj.Proxy);
        Assert.NotNull(inj.Toxic);
        Assert.Equal("bandwidth", inj.Toxic!["type"]);
        var attrs = (System.Collections.Generic.Dictionary<string, object?>)inj.Toxic["attributes"]!;
        Assert.Equal(1, attrs["rate"]);
    }

    [Fact]
    public void LoadsEveryRealScenarioWithExpectations()
    {
        // Sanity: every scenarios/*.yaml shipped with integration-test-data must parse cleanly
        // and yield at least one expectation. Catches grammar drift that the schema validator
        // alone wouldn't catch (e.g. a new convenience alias landing in YAML before the loader
        // knows about it would silently round-trip as Inject=null with all aliases null).
        var dir = ChaosPaths.ScenariosDir();
        if (!System.IO.Directory.Exists(dir))
        {
            // CI builds without integration-test-data sibling can't run this; skip cleanly.
            return;
        }
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.yaml").OrderBy(x => x))
        {
            var s = ChaosYamlLoader.Load(path);
            Assert.True(s.Tests.Count >= 1, $"{path}: no tests parsed");
            foreach (var run in s.Tests)
            {
                Assert.True(run.Expectations.Count >= 1, $"{path}: no expectations parsed for {run.Name}");
                foreach (var e in run.Expectations)
                {
                    Assert.True(e.WithinMs > 0, $"{path}: expectation has within_ms<=0 for {run.Name}");
                    Assert.False(string.IsNullOrWhiteSpace(e.AssertExpr), $"{path}: empty assert for {run.Name}");
                }
            }
        }
    }
}
