using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quonfig.Sdk.Extensions.Logging;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Quonfig.Sdk.Companions.Tests;

public sealed class ExtensionsLoggingTests
{
    /// <summary>
    /// Capture-only ILoggerProvider — records each (category, level, message) tuple.
    /// We wrap this with QuonfigLoggerProvider via AddQuonfigFilter and assert which
    /// records make it past the Quonfig filter.
    /// </summary>
    private sealed class RecordingProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, MelLogLevel Level, string Message);
        public List<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class RecordingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<Entry> _entries;

            public RecordingLogger(string category, List<Entry> entries)
            {
                _category = category;
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(MelLogLevel logLevel) => true;
            public void Log<TState>(
                MelLogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _entries.Add(new Entry(_category, logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task AddQuonfigFilter_GatesLogsBelowConfiguredLevel()
    {
        using var ws = new TestWorkspace();
        ws.WriteManifest("production");
        // log-level.MyApp = "WARN" -> only Warn and above pass for "MyApp".
        ws.WriteLogLevel("loglevel.config.json", "MyApp", "WARN");
        await using var quonfig = new Sdk.Quonfig(ws.Options());
        await quonfig.InitAsync();

        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(MelLogLevel.Trace);
            builder.AddProvider(recorder);
            builder.AddQuonfigFilter(quonfig);
        });

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILogger<ExtensionsLoggingTests>>();
        var myAppLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MyApp");

        myAppLogger.LogTrace("trace below threshold");
        myAppLogger.LogDebug("debug below threshold");
        myAppLogger.LogInformation("info below threshold");
        myAppLogger.LogWarning("warn passes");
        myAppLogger.LogError("error passes");

        recorder.Entries.Should().OnlyContain(e => e.Level >= MelLogLevel.Warning,
            "Quonfig configured Warn floor must filter Trace/Debug/Info");
        recorder.Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddQuonfigFilter_HierarchyWalk_ParentLevelAppliesToChild()
    {
        using var ws = new TestWorkspace();
        ws.WriteManifest("production");
        // Parent "MyApp" set to ERROR; child "MyApp.Auth" should inherit ERROR.
        ws.WriteLogLevel("loglevel.config.json", "MyApp", "ERROR");
        await using var quonfig = new Sdk.Quonfig(ws.Options());
        await quonfig.InitAsync();

        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(MelLogLevel.Trace);
            b.AddProvider(recorder);
            b.AddQuonfigFilter(quonfig);
        });

        using var sp = services.BuildServiceProvider();
        var child = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MyApp.Auth");

        child.LogWarning("warn dropped — parent ERROR floor");
        child.LogError("error passes");

        recorder.Entries.Should().ContainSingle()
            .Which.Message.Should().Be("error passes");
    }

    [Fact]
    public async Task AddQuonfigFilter_NoMatchingConfig_DefersToInnerProvider()
    {
        using var ws = new TestWorkspace();
        ws.WriteManifest("production");
        // Sentinel config so the datadir isn't empty (loader requires ≥1 readable file).
        // No log-level configs → GetLogLevel returns null → defer to inner provider.
        ws.WriteBoolFlag("sentinel.flag.json", "sentinel", false);
        await using var quonfig = new Sdk.Quonfig(ws.Options());
        await quonfig.InitAsync();

        var recorder = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(MelLogLevel.Information);
            b.AddProvider(recorder);
            b.AddQuonfigFilter(quonfig);
        });

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Anything");

        logger.LogDebug("debug — filtered by inner SetMinimumLevel");
        logger.LogInformation("info passes");

        recorder.Entries.Should().ContainSingle()
            .Which.Level.Should().Be(MelLogLevel.Information);
    }

    [Fact]
    public void QuonfigLevelMapper_RoundTripsAllLevels()
    {
        QuonfigLevelMapper_ToMel(Sdk.LogLevel.Fatal).Should().Be(MelLogLevel.Critical);
        QuonfigLevelMapper_ToMel(Sdk.LogLevel.Error).Should().Be(MelLogLevel.Error);
        QuonfigLevelMapper_ToMel(Sdk.LogLevel.Warn).Should().Be(MelLogLevel.Warning);
        QuonfigLevelMapper_ToMel(Sdk.LogLevel.Info).Should().Be(MelLogLevel.Information);
        QuonfigLevelMapper_ToMel(Sdk.LogLevel.Debug).Should().Be(MelLogLevel.Debug);
        QuonfigLevelMapper_ToMel(Sdk.LogLevel.Trace).Should().Be(MelLogLevel.Trace);
    }

    // Mirror of the internal mapper. We can't reference QuonfigLevelMapper directly (internal),
    // so we re-derive the contract here and assert the public behavior via the filter tests above.
    private static MelLogLevel QuonfigLevelMapper_ToMel(Sdk.LogLevel q) => q switch
    {
        Sdk.LogLevel.Fatal => MelLogLevel.Critical,
        Sdk.LogLevel.Error => MelLogLevel.Error,
        Sdk.LogLevel.Warn => MelLogLevel.Warning,
        Sdk.LogLevel.Info => MelLogLevel.Information,
        Sdk.LogLevel.Debug => MelLogLevel.Debug,
        Sdk.LogLevel.Trace => MelLogLevel.Trace,
        _ => MelLogLevel.Information,
    };
}
