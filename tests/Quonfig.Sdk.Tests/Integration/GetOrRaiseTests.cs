// AUTO-GENERATED from integration-test-data/tests/eval/get_or_raise.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Quonfig.Sdk.Exceptions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class GetOrRaiseTests
{

    [Fact(DisplayName = "get_or_raise can raise an error if value not found")]
    public async Task GetOrRaiseCanRaiseAnErrorIfValueNotFound()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigKeyNotFoundException>(() => client.GetString("my-missing-key"));
    }

    [Fact(DisplayName = "get_or_raise returns a default value instead of raising")]
    public async Task GetOrRaiseReturnsADefaultValueInsteadOfRaising()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("my-missing-key", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "init fails by default when every delivery leg refuses the connection")]
    public async Task InitFailsByDefaultWhenEveryDeliveryLegRefusesTheConnection()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init with on_init_failure fail raises when every delivery leg refuses the connection")]
    public async Task InitWithOnInitFailureFailRaisesWhenEveryDeliveryLegRefusesTheConnection()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init with on_init_failure fail raises the init timeout when every delivery leg hangs")]
    public async Task InitWithOnInitFailureFailRaisesTheInitTimeoutWhenEveryDeliveryLegHangs()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://192.0.2.1:8001", "http://192.0.2.1:8002" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(200),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.IsAssignableFrom<QuonfigInitTimeoutException>(raised);
        Assert.Equal(QuonfigInitFailureReason.Timeout, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init with on_init_failure fail raises at once when every delivery leg answers 401")]
    public async Task InitWithOnInitFailureFailRaisesAtOnceWhenEveryDeliveryLegAnswers401()
    {
        using var leg0 = WireMockServer.Start();
        leg0.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(401));
        using var leg1 = WireMockServer.Start();
        leg1.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(401));
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { leg0.Urls[0], leg1.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(8000),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unauthorized, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unauthorized, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 4000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "get_or_raise returns the default after init continues past refused delivery legs")]
    public async Task GetOrRaiseReturnsTheDefaultAfterInitContinuesPastRefusedDeliveryLegs()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.Continue,
        });
        await client.InitAsync();
        var actual = client.GetString("any-key", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "get_or_raise raises missing_default after init continues past refused delivery legs")]
    public async Task GetOrRaiseRaisesMissingDefaultAfterInitContinuesPastRefusedDeliveryLegs()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.Continue,
        });
        await client.InitAsync();
        Assert.Throws<QuonfigKeyNotFoundException>(() => client.GetString("any-key"));
    }

    [Fact(DisplayName = "deprecated on_init_failure raise behaves as fail when every delivery leg refuses the connection")]
    public async Task DeprecatedOnInitFailureRaiseBehavesAsFailWhenEveryDeliveryLegRefusesTheConnection()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.Throw,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "deprecated on_init_failure return behaves as continue when every delivery leg refuses the connection")]
    public async Task DeprecatedOnInitFailureReturnBehavesAsContinueWhenEveryDeliveryLegRefusesTheConnection()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.ReturnDefaults,
        });
        await client.InitAsync();
        Assert.Throws<QuonfigKeyNotFoundException>(() => client.GetString("any-key"));
    }

    [Fact(DisplayName = "init with on_init_failure fail raises at once when every delivery leg answers 404")]
    public async Task InitWithOnInitFailureFailRaisesAtOnceWhenEveryDeliveryLegAnswers404()
    {
        using var leg0 = WireMockServer.Start();
        leg0.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(404));
        using var leg1 = WireMockServer.Start();
        leg1.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(404));
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { leg0.Urls[0], leg1.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(8000),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unauthorized, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unauthorized, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 4000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init with on_init_failure fail raises at once when every delivery leg answers 410")]
    public async Task InitWithOnInitFailureFailRaisesAtOnceWhenEveryDeliveryLegAnswers410()
    {
        using var leg0 = WireMockServer.Start();
        leg0.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(410));
        using var leg1 = WireMockServer.Start();
        leg1.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(410));
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { leg0.Urls[0], leg1.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(8000),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unauthorized, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unauthorized, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 4000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init with on_init_failure fail raises unavailable when only the primary answers 401 and the secondary 503")]
    public async Task InitWithOnInitFailureFailRaisesUnavailableWhenOnlyThePrimaryAnswers401AndTheSecondary503()
    {
        using var leg0 = WireMockServer.Start();
        leg0.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(401));
        using var leg1 = WireMockServer.Start();
        leg1.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(503));
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { leg0.Urls[0], leg1.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(1500),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 6000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init with init_timeout_ms 0 and on_init_failure fail raises after one attempt when every delivery leg refuses the connection")]
    public async Task InitWithInitTimeoutMs0AndOnInitFailureFailRaisesAfterOneAttemptWhenEveryDeliveryLegRefusesTheConnection()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(0),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "get_or_raise returns the default after init with init_timeout_ms 0 continues past refused delivery legs")]
    public async Task GetOrRaiseReturnsTheDefaultAfterInitWithInitTimeoutMs0ContinuesPastRefusedDeliveryLegs()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://127.0.0.1:1", "http://127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(0),
            OnInitFailure = OnInitFailure.Continue,
        });
        await client.InitAsync();
        var actual = client.GetString("any-key", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "init with on_init_failure fail raises the init timeout when every delivery leg hangs past its per-leg abort")]
    public async Task InitWithOnInitFailureFailRaisesTheInitTimeoutWhenEveryDeliveryLegHangsPastItsPerLegAbort()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://192.0.2.1:8001", "http://192.0.2.1:8002" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(10000),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.IsAssignableFrom<QuonfigInitTimeoutException>(raised);
        Assert.Equal(QuonfigInitFailureReason.Timeout, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 14000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact(DisplayName = "init error never carries the userinfo of a delivery URL")]
    public async Task InitErrorNeverCarriesTheUserinfoOfADeliveryUrl()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { "http://qfguser:qfgsecret@127.0.0.1:1", "http://qfguser:qfgsecret@127.0.0.1:2" },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
        for (Exception? e = raised; e != null; e = e.InnerException)
        {
            Assert.DoesNotContain("qfgsecret", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("qfguser", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "init error never carries an upstream response body")]
    public async Task InitErrorNeverCarriesAnUpstreamResponseBody()
    {
        using var leg0 = WireMockServer.Start();
        leg0.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(503).WithBody("upstream exploded: qfg-upstream-body-marker"));
        using var leg1 = WireMockServer.Start();
        leg1.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(503).WithBody("upstream exploded: qfg-upstream-body-marker"));
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            SdkKey = "itd-init-failure",
            ApiUrls = new[] { leg0.Urls[0], leg1.Urls[0] },
            StreamUrls = Array.Empty<string>(),
            FallbackPollEnabled = false,
            InitTimeout = TimeSpan.FromMilliseconds(500),
            OnInitFailure = OnInitFailure.Fail,
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raised = await Assert.ThrowsAnyAsync<QuonfigInitException>(() => client.InitAsync());
        stopwatch.Stop();
        Assert.False(raised is QuonfigInitTimeoutException, "reason unavailable, not the init timeout");
        Assert.Equal(QuonfigInitFailureReason.Unavailable, raised.Reason);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"InitAsync raised after {stopwatch.ElapsedMilliseconds}ms");
        for (Exception? e = raised; e != null; e = e.InnerException)
        {
            Assert.DoesNotContain("qfg-upstream-body-marker", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "raises an error if a config is provided by a missing environment variable")]
    public async Task RaisesAnErrorIfAConfigIsProvidedByAMissingEnvironmentVariable()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigEnvVarNotSetException>(() => client.GetString("provided.by.missing.env.var"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided config cannot be coerced to configured type")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedConfigCannotBeCoercedToConfiguredType()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetLong("provided.not.a.number"));
    }

    [Fact(DisplayName = "coercion error for an int env var names the variable and never its value")]
    public async Task CoercionErrorForAnIntEnvVarNamesTheVariableAndNeverItsValue()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_SECRET_ENV", "postgres://admin:hunter2@db");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var raised = Assert.Throws<QuonfigCoercionException>(() => client.GetLong("provided.secret.not.an.int"));
        Assert.Contains("environment variable \"QUONFIG_ITD_SECRET_ENV\" cannot be converted to int for config \"provided.secret.not.an.int\"", raised.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", raised.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres://", raised.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "coercion error for a double env var names the variable and never its value")]
    public async Task CoercionErrorForADoubleEnvVarNamesTheVariableAndNeverItsValue()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_SECRET_ENV", "postgres://admin:hunter2@db");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var raised = Assert.Throws<QuonfigCoercionException>(() => client.GetDouble("provided.secret.not.a.double"));
        Assert.Contains("environment variable \"QUONFIG_ITD_SECRET_ENV\" cannot be converted to double for config \"provided.secret.not.a.double\"", raised.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", raised.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres://", raised.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "raises an error for decryption failure")]
    public async Task RaisesAnErrorForDecryptionFailure()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigDecryptionException>(() => client.GetString("a.broken.secret.config"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration 30s cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDuration30sCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_30S", "30s");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.30s"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration PT0.5H cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDurationPt05hCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_PT0_5H", "PT0.5H");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.PT0.5H"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration P1DT cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDurationP1dtCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_P1DT", "P1DT");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.P1DT"));
    }

    [Fact(DisplayName = "raises an error if an env-var-provided duration garbage cannot be coerced")]
    public async Task RaisesAnErrorIfAnEnvVarProvidedDurationGarbageCannotBeCoerced()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_GARBAGE", "garbage");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("provided.duration.malformed.garbage"));
    }

    [Fact(DisplayName = "raises an error if a stored duration 30s cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDuration30sCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.30s"));
    }

    [Fact(DisplayName = "raises an error if a stored duration PT0.5H cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationPt05hCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.PT0.5H"));
    }

    [Fact(DisplayName = "raises an error if a stored duration P1DT cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationP1dtCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.P1DT"));
    }

    [Fact(DisplayName = "raises an error if a stored duration garbage cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationGarbageCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.garbage"));
    }

    [Fact(DisplayName = "raises an error if a stored duration empty cannot be coerced")]
    public async Task RaisesAnErrorIfAStoredDurationEmptyCannotBeCoerced()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Throws<QuonfigCoercionException>(() => client.GetDuration("test.duration.malformed.empty"));
    }
}
