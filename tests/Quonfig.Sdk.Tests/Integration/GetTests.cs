// AUTO-GENERATED from integration-test-data/tests/eval/get.yaml. DO NOT EDIT.
// Regenerate with:
//   cd integration-test-data/generators && npm run generate -- --target=dotnet
// Source: integration-test-data/generators/src/targets/dotnet.ts

using System;
using System.Threading.Tasks;
using Xunit;

namespace Quonfig.Sdk.Tests.Integration;

public sealed class GetTests
{

    [Fact(DisplayName = "get returns a found value for key")]
    public async Task GetReturnsAFoundValueForKey()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("my-test-key");
        Assert.Equal("my-test-value", actual);
    }

    [Fact(DisplayName = "get returns nil if value not found")]
    public async Task GetReturnsNilIfValueNotFound()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("my-missing-key");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "get returns a default for a missing value if a default is given")]
    public async Task GetReturnsADefaultForAMissingValueIfADefaultIsGiven()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("my-missing-key", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "get ignores a provided default if the key is found")]
    public async Task GetIgnoresAProvidedDefaultIfTheKeyIsFound()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("my-test-key", defaultValue: "DEFAULT");
        Assert.Equal("my-test-value", actual);
    }

    [Fact(DisplayName = "get can return a double")]
    public async Task GetCanReturnADouble()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("my-double-key");
        TestSetup.AssertDoubleEquals(9.95d, actual);
    }

    [Fact(DisplayName = "get can return a string list")]
    public async Task GetCanReturnAStringList()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("my-string-list-key");
        Assert.Equal(new[] { "a", "b", "c" }, actual);
    }

    [Fact(DisplayName = "can return a value provided by an environment variable")]
    public async Task CanReturnAValueProvidedByAnEnvironmentVariable()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("prefab.secrets.encryption.key");
        Assert.Equal("c87ba22d8662282abe8a0e4651327b579cb64a454ab0f4c170b45b15f049a221", actual);
    }

    [Fact(DisplayName = "can return a value provided by an environment variable after type coercion")]
    public async Task CanReturnAValueProvidedByAnEnvironmentVariableAfterTypeCoercion()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("provided.a.number");
        Assert.Equal(1234L, actual);
    }

    [Fact(DisplayName = "can decrypt and return a secret value (with decryption key in in env var)")]
    public async Task CanDecryptAndReturnASecretValueWithDecryptionKeyInInEnvVar()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("a.secret.config");
        Assert.Equal("hello.world", actual);
    }

    [Fact(DisplayName = "decrypts a secret whose key config has an environment rule")]
    public async Task DecryptsASecretWhoseKeyConfigHasAnEnvironmentRule()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("env.scoped.secret");
        Assert.Equal("hello.world", actual);
    }

    [Fact(DisplayName = "duration 200 ms")]
    public async Task Duration200Ms()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(200L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT0.2S"));
        var details = client.GetDurationDetails("test.duration.PT0.2S");
        Assert.Equal(TimeSpan.FromTicks(200L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration 90S")]
    public async Task Duration90s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(90000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT90S"));
        var details = client.GetDurationDetails("test.duration.PT90S");
        Assert.Equal(TimeSpan.FromTicks(90000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration 30M")]
    public async Task Duration30m()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(1800000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT30M"));
        var details = client.GetDurationDetails("test.duration.PT30M");
        Assert.Equal(TimeSpan.FromTicks(1800000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration test.duration.P1DT6H2M1.5S")]
    public async Task DurationTestDurationP1dt6h2m15s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(108121500L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.P1DT6H2M1.5S"));
        var details = client.GetDurationDetails("test.duration.P1DT6H2M1.5S");
        Assert.Equal(TimeSpan.FromTicks(108121500L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration zero PT0S")]
    public async Task DurationZeroPt0s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(0L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT0S"));
        var details = client.GetDurationDetails("test.duration.PT0S");
        Assert.Equal(TimeSpan.FromTicks(0L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration zero P0D")]
    public async Task DurationZeroP0d()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(0L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.P0D"));
        var details = client.GetDurationDetails("test.duration.P0D");
        Assert.Equal(TimeSpan.FromTicks(0L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration days only P2D")]
    public async Task DurationDaysOnlyP2d()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(172800000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.P2D"));
        var details = client.GetDurationDetails("test.duration.P2D");
        Assert.Equal(TimeSpan.FromTicks(172800000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration hours only PT1H")]
    public async Task DurationHoursOnlyPt1h()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(3600000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT1H"));
        var details = client.GetDurationDetails("test.duration.PT1H");
        Assert.Equal(TimeSpan.FromTicks(3600000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration minutes only PT1M")]
    public async Task DurationMinutesOnlyPt1m()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(60000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT1M"));
        var details = client.GetDurationDetails("test.duration.PT1M");
        Assert.Equal(TimeSpan.FromTicks(60000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration seconds only PT1S")]
    public async Task DurationSecondsOnlyPt1s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(1000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT1S"));
        var details = client.GetDurationDetails("test.duration.PT1S");
        Assert.Equal(TimeSpan.FromTicks(1000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration leading zero PT05S")]
    public async Task DurationLeadingZeroPt05s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(5000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT05S"));
        var details = client.GetDurationDetails("test.duration.PT05S");
        Assert.Equal(TimeSpan.FromTicks(5000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration hours and minutes PT1H30M")]
    public async Task DurationHoursAndMinutesPt1h30m()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(5400000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT1H30M"));
        var details = client.GetDurationDetails("test.duration.PT1H30M");
        Assert.Equal(TimeSpan.FromTicks(5400000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration days and hours P1DT2H")]
    public async Task DurationDaysAndHoursP1dt2h()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(93600000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.P1DT2H"));
        var details = client.GetDurationDetails("test.duration.P1DT2H");
        Assert.Equal(TimeSpan.FromTicks(93600000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration one millisecond PT0.001S")]
    public async Task DurationOneMillisecondPt0001s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(1L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT0.001S"));
        var details = client.GetDurationDetails("test.duration.PT0.001S");
        Assert.Equal(TimeSpan.FromTicks(1L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration magnitude ceiling P36500D")]
    public async Task DurationMagnitudeCeilingP36500d()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(3153600000000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.P36500D"));
        var details = client.GetDurationDetails("test.duration.P36500D");
        Assert.Equal(TimeSpan.FromTicks(3153600000000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration rounding PT2.01S")]
    public async Task DurationRoundingPt201s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(2010L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT2.01S"));
        var details = client.GetDurationDetails("test.duration.PT2.01S");
        Assert.Equal(TimeSpan.FromTicks(2010L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration rounding PT1.005S")]
    public async Task DurationRoundingPt1005s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(1005L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT1.005S"));
        var details = client.GetDurationDetails("test.duration.PT1.005S");
        Assert.Equal(TimeSpan.FromTicks(1005L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration rounding half up PT0.0005S")]
    public async Task DurationRoundingHalfUpPt00005s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(1L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT0.0005S"));
        var details = client.GetDurationDetails("test.duration.PT0.0005S");
        Assert.Equal(TimeSpan.FromTicks(1L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "duration rounding down PT0.0004S")]
    public async Task DurationRoundingDownPt00004s()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(0L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.PT0.0004S"));
        var details = client.GetDurationDetails("test.duration.PT0.0004S");
        Assert.Equal(TimeSpan.FromTicks(0L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "json test")]
    public async Task JsonTest()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("test.json");
        Assert.Equal(TestSetup.Map("a", 1L, "b", "c"), actual);
    }

    [Fact(DisplayName = "get returns a native json object (not a stringified payload)")]
    public async Task GetReturnsANativeJsonObjectNotAStringifiedPayload()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("test.json");
        Assert.Equal(TestSetup.Map("a", 1L, "b", "c"), actual);
    }

    [Fact(DisplayName = "list on left side test (1)")]
    public async Task ListOnLeftSideTest1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("left.hand.list.test", new ContextSet { ["user"] = new ContextProperties { ["name"] = "james", ["aka"] = new ContextValueStringList(new[] { "happy", "sleepy" }) } });
        Assert.Equal("correct", actual);
    }

    [Fact(DisplayName = "list on left side test (2)")]
    public async Task ListOnLeftSideTest2()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("left.hand.list.test", new ContextSet { ["user"] = new ContextProperties { ["name"] = "james", ["aka"] = new ContextValueStringList(new[] { "a", "b" }) } });
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "list on left side test opposite (1)")]
    public async Task ListOnLeftSideTestOpposite1()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("left.hand.test.opposite", new ContextSet { ["user"] = new ContextProperties { ["name"] = "james", ["aka"] = new ContextValueStringList(new[] { "happy", "sleepy" }) } });
        Assert.Equal("default", actual);
    }

    [Fact(DisplayName = "list on left side test (3)")]
    public async Task ListOnLeftSideTest3()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("left.hand.test.opposite", new ContextSet { ["user"] = new ContextProperties { ["name"] = "james", ["aka"] = new ContextValueStringList(new[] { "a", "b" }) } });
        Assert.Equal("correct", actual);
    }

    [Fact(DisplayName = "env-var-provided duration PT1.5S via get")]
    public async Task EnvVarProvidedDurationPt15sViaGet()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_PT1_5S", "PT1.5S");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(1500L * TimeSpan.TicksPerMillisecond), client.GetDuration("provided.duration.PT1.5S"));
        var details = client.GetDurationDetails("provided.duration.PT1.5S");
        Assert.Equal(TimeSpan.FromTicks(1500L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.NotEqual(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "stored malformed duration 30s returns the default")]
    public async Task StoredMalformedDuration30sReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.malformed.30s", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("test.duration.malformed.30s", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "stored malformed duration 30s with no default returns nil")]
    public async Task StoredMalformedDuration30sWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("test.duration.malformed.30s");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "stored malformed duration PT0.5H returns the default")]
    public async Task StoredMalformedDurationPt05hReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.malformed.PT0.5H", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("test.duration.malformed.PT0.5H", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "stored malformed duration PT0.5H with no default returns nil")]
    public async Task StoredMalformedDurationPt05hWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("test.duration.malformed.PT0.5H");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "stored malformed duration P1DT returns the default")]
    public async Task StoredMalformedDurationP1dtReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.malformed.P1DT", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("test.duration.malformed.P1DT", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "stored malformed duration P1DT with no default returns nil")]
    public async Task StoredMalformedDurationP1dtWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("test.duration.malformed.P1DT");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "stored malformed duration garbage returns the default")]
    public async Task StoredMalformedDurationGarbageReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.malformed.garbage", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("test.duration.malformed.garbage", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "stored malformed duration garbage with no default returns nil")]
    public async Task StoredMalformedDurationGarbageWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("test.duration.malformed.garbage");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "stored malformed duration empty returns the default")]
    public async Task StoredMalformedDurationEmptyReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("test.duration.malformed.empty", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("test.duration.malformed.empty", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "stored malformed duration empty with no default returns nil")]
    public async Task StoredMalformedDurationEmptyWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("test.duration.malformed.empty");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "env-var-provided malformed duration 30s returns the default")]
    public async Task EnvVarProvidedMalformedDuration30sReturnsTheDefault()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_30S", "30s");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("provided.duration.malformed.30s", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("provided.duration.malformed.30s", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "env-var-provided malformed duration 30s with no default returns nil")]
    public async Task EnvVarProvidedMalformedDuration30sWithNoDefaultReturnsNil()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_30S", "30s");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("provided.duration.malformed.30s");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "env-var-provided malformed duration PT0.5H returns the default")]
    public async Task EnvVarProvidedMalformedDurationPt05hReturnsTheDefault()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_PT0_5H", "PT0.5H");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("provided.duration.malformed.PT0.5H", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("provided.duration.malformed.PT0.5H", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "env-var-provided malformed duration PT0.5H with no default returns nil")]
    public async Task EnvVarProvidedMalformedDurationPt05hWithNoDefaultReturnsNil()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_PT0_5H", "PT0.5H");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("provided.duration.malformed.PT0.5H");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "env-var-provided malformed duration P1DT returns the default")]
    public async Task EnvVarProvidedMalformedDurationP1dtReturnsTheDefault()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_P1DT", "P1DT");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("provided.duration.malformed.P1DT", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("provided.duration.malformed.P1DT", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "env-var-provided malformed duration P1DT with no default returns nil")]
    public async Task EnvVarProvidedMalformedDurationP1dtWithNoDefaultReturnsNil()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_P1DT", "P1DT");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("provided.duration.malformed.P1DT");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "env-var-provided malformed duration garbage returns the default")]
    public async Task EnvVarProvidedMalformedDurationGarbageReturnsTheDefault()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_GARBAGE", "garbage");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("provided.duration.malformed.garbage", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("provided.duration.malformed.garbage", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "env-var-provided malformed duration garbage with no default returns nil")]
    public async Task EnvVarProvidedMalformedDurationGarbageWithNoDefaultReturnsNil()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_DURATION_GARBAGE", "garbage");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("provided.duration.malformed.garbage");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on stored int returns the default")]
    public async Task StringGetterOnStoredIntReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("getter.contract.int", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on stored int with no default returns nil")]
    public async Task StringGetterOnStoredIntWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("getter.contract.int");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on stored double returns the default")]
    public async Task StringGetterOnStoredDoubleReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("getter.contract.double", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on stored double with no default returns nil")]
    public async Task StringGetterOnStoredDoubleWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("getter.contract.double");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on stored bool returns the default")]
    public async Task StringGetterOnStoredBoolReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("getter.contract.bool", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on stored bool with no default returns nil")]
    public async Task StringGetterOnStoredBoolWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("getter.contract.bool");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on stored string_list returns the default")]
    public async Task StringGetterOnStoredStringListReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("getter.contract.string_list", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on stored string_list with no default returns nil")]
    public async Task StringGetterOnStoredStringListWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("getter.contract.string_list");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on stored json returns the default")]
    public async Task StringGetterOnStoredJsonReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("getter.contract.json", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on stored json with no default returns nil")]
    public async Task StringGetterOnStoredJsonWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("getter.contract.json");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on stored duration returns the default")]
    public async Task StringGetterOnStoredDurationReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("getter.contract.duration", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on stored duration with no default returns nil")]
    public async Task StringGetterOnStoredDurationWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("getter.contract.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored string returns the default")]
    public async Task IntGetterOnStoredStringReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.string", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored string with no default returns nil")]
    public async Task IntGetterOnStoredStringWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.string");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored double 3.9 returns the default")]
    public async Task IntGetterOnStoredDouble39ReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.double", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored double 3.9 with no default returns nil")]
    public async Task IntGetterOnStoredDouble39WithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.double");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored bool returns the default")]
    public async Task IntGetterOnStoredBoolReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.bool", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored bool with no default returns nil")]
    public async Task IntGetterOnStoredBoolWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.bool");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored string_list returns the default")]
    public async Task IntGetterOnStoredStringListReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.string_list", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored string_list with no default returns nil")]
    public async Task IntGetterOnStoredStringListWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.string_list");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored json returns the default")]
    public async Task IntGetterOnStoredJsonReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.json", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored json with no default returns nil")]
    public async Task IntGetterOnStoredJsonWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.json");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored duration returns the default")]
    public async Task IntGetterOnStoredDurationReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.duration", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored duration with no default returns nil")]
    public async Task IntGetterOnStoredDurationWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored string returns the default")]
    public async Task DoubleGetterOnStoredStringReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.string", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored string with no default returns nil")]
    public async Task DoubleGetterOnStoredStringWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.string");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored bool returns the default")]
    public async Task DoubleGetterOnStoredBoolReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.bool", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored bool with no default returns nil")]
    public async Task DoubleGetterOnStoredBoolWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.bool");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored string_list returns the default")]
    public async Task DoubleGetterOnStoredStringListReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.string_list", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored string_list with no default returns nil")]
    public async Task DoubleGetterOnStoredStringListWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.string_list");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored json returns the default")]
    public async Task DoubleGetterOnStoredJsonReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.json", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored json with no default returns nil")]
    public async Task DoubleGetterOnStoredJsonWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.json");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored duration returns the default")]
    public async Task DoubleGetterOnStoredDurationReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.duration", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored duration with no default returns nil")]
    public async Task DoubleGetterOnStoredDurationWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string returns the default")]
    public async Task BooleanGetterOnStoredStringReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.string", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string with no default returns nil")]
    public async Task BooleanGetterOnStoredStringWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.string");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored int returns the default")]
    public async Task BooleanGetterOnStoredIntReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.int", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored int with no default returns nil")]
    public async Task BooleanGetterOnStoredIntWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.int");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored double returns the default")]
    public async Task BooleanGetterOnStoredDoubleReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.double", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored double with no default returns nil")]
    public async Task BooleanGetterOnStoredDoubleWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.double");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string_list returns the default")]
    public async Task BooleanGetterOnStoredStringListReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.string_list", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string_list with no default returns nil")]
    public async Task BooleanGetterOnStoredStringListWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.string_list");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored json returns the default")]
    public async Task BooleanGetterOnStoredJsonReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.json", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored json with no default returns nil")]
    public async Task BooleanGetterOnStoredJsonWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.json");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored duration returns the default")]
    public async Task BooleanGetterOnStoredDurationReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.duration", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored duration with no default returns nil")]
    public async Task BooleanGetterOnStoredDurationWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored string returns the default")]
    public async Task StringListGetterOnStoredStringReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("getter.contract.string", defaultValue: new[] { "DEFAULT" });
        Assert.Equal(new[] { "DEFAULT" }, actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored string with no default returns nil")]
    public async Task StringListGetterOnStoredStringWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetStringList("getter.contract.string");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored int returns the default")]
    public async Task StringListGetterOnStoredIntReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("getter.contract.int", defaultValue: new[] { "DEFAULT" });
        Assert.Equal(new[] { "DEFAULT" }, actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored int with no default returns nil")]
    public async Task StringListGetterOnStoredIntWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetStringList("getter.contract.int");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored double returns the default")]
    public async Task StringListGetterOnStoredDoubleReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("getter.contract.double", defaultValue: new[] { "DEFAULT" });
        Assert.Equal(new[] { "DEFAULT" }, actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored double with no default returns nil")]
    public async Task StringListGetterOnStoredDoubleWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetStringList("getter.contract.double");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored bool returns the default")]
    public async Task StringListGetterOnStoredBoolReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("getter.contract.bool", defaultValue: new[] { "DEFAULT" });
        Assert.Equal(new[] { "DEFAULT" }, actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored bool with no default returns nil")]
    public async Task StringListGetterOnStoredBoolWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetStringList("getter.contract.bool");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored json returns the default")]
    public async Task StringListGetterOnStoredJsonReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("getter.contract.json", defaultValue: new[] { "DEFAULT" });
        Assert.Equal(new[] { "DEFAULT" }, actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored json with no default returns nil")]
    public async Task StringListGetterOnStoredJsonWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetStringList("getter.contract.json");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored duration returns the default")]
    public async Task StringListGetterOnStoredDurationReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetStringList("getter.contract.duration", defaultValue: new[] { "DEFAULT" });
        Assert.Equal(new[] { "DEFAULT" }, actual);
    }

    [Fact(DisplayName = "STRING_LIST getter on stored duration with no default returns nil")]
    public async Task StringListGetterOnStoredDurationWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetStringList("getter.contract.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "JSON getter on stored string returns the default")]
    public async Task JsonGetterOnStoredStringReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("getter.contract.string", defaultValue: TestSetup.Map("default", true));
        Assert.Equal(TestSetup.Map("default", true), actual);
    }

    [Fact(DisplayName = "JSON getter on stored string with no default returns nil")]
    public async Task JsonGetterOnStoredStringWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetJson("getter.contract.string");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "JSON getter on stored int returns the default")]
    public async Task JsonGetterOnStoredIntReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("getter.contract.int", defaultValue: TestSetup.Map("default", true));
        Assert.Equal(TestSetup.Map("default", true), actual);
    }

    [Fact(DisplayName = "JSON getter on stored int with no default returns nil")]
    public async Task JsonGetterOnStoredIntWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetJson("getter.contract.int");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "JSON getter on stored double returns the default")]
    public async Task JsonGetterOnStoredDoubleReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("getter.contract.double", defaultValue: TestSetup.Map("default", true));
        Assert.Equal(TestSetup.Map("default", true), actual);
    }

    [Fact(DisplayName = "JSON getter on stored double with no default returns nil")]
    public async Task JsonGetterOnStoredDoubleWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetJson("getter.contract.double");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "JSON getter on stored bool returns the default")]
    public async Task JsonGetterOnStoredBoolReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("getter.contract.bool", defaultValue: TestSetup.Map("default", true));
        Assert.Equal(TestSetup.Map("default", true), actual);
    }

    [Fact(DisplayName = "JSON getter on stored bool with no default returns nil")]
    public async Task JsonGetterOnStoredBoolWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetJson("getter.contract.bool");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "JSON getter on stored string_list returns the default")]
    public async Task JsonGetterOnStoredStringListReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("getter.contract.string_list", defaultValue: TestSetup.Map("default", true));
        Assert.Equal(TestSetup.Map("default", true), actual);
    }

    [Fact(DisplayName = "JSON getter on stored string_list with no default returns nil")]
    public async Task JsonGetterOnStoredStringListWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetJson("getter.contract.string_list");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "JSON getter on stored duration returns the default")]
    public async Task JsonGetterOnStoredDurationReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetJson("getter.contract.duration", defaultValue: TestSetup.Map("default", true));
        Assert.Equal(TestSetup.Map("default", true), actual);
    }

    [Fact(DisplayName = "JSON getter on stored duration with no default returns nil")]
    public async Task JsonGetterOnStoredDurationWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetJson("getter.contract.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored string returns the default")]
    public async Task DurationGetterOnStoredStringReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.string", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.string", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored string with no default returns nil")]
    public async Task DurationGetterOnStoredStringWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.string");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored int returns the default")]
    public async Task DurationGetterOnStoredIntReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.int", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.int", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored int with no default returns nil")]
    public async Task DurationGetterOnStoredIntWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.int");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored double returns the default")]
    public async Task DurationGetterOnStoredDoubleReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.double", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.double", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored double with no default returns nil")]
    public async Task DurationGetterOnStoredDoubleWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.double");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored bool returns the default")]
    public async Task DurationGetterOnStoredBoolReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.bool", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.bool", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored bool with no default returns nil")]
    public async Task DurationGetterOnStoredBoolWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.bool");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored string_list returns the default")]
    public async Task DurationGetterOnStoredStringListReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.string_list", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.string_list", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored string_list with no default returns nil")]
    public async Task DurationGetterOnStoredStringListWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.string_list");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored json returns the default")]
    public async Task DurationGetterOnStoredJsonReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.json", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.json", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored json with no default returns nil")]
    public async Task DurationGetterOnStoredJsonWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.json");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string \"false\" returns the default")]
    public async Task BooleanGetterOnStoredStringFalseReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.string.false", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string \"false\" with no default returns nil")]
    public async Task BooleanGetterOnStoredStringFalseWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.string.false");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string \"false\" returns a true default")]
    public async Task BooleanGetterOnStoredStringFalseReturnsATrueDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.string.false", defaultValue: true);
        Assert.True(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string \"true\" returns the default")]
    public async Task BooleanGetterOnStoredStringTrueReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.string.true", defaultValue: false);
        Assert.False(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on stored string \"true\" with no default returns nil")]
    public async Task BooleanGetterOnStoredStringTrueWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.string.true");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored string \"3\" returns the default")]
    public async Task IntGetterOnStoredString3ReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.string.number", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored string \"3\" with no default returns nil")]
    public async Task IntGetterOnStoredString3WithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.string.number");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored string \"3\" returns the default")]
    public async Task DoubleGetterOnStoredString3ReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.string.number", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored string \"3\" with no default returns nil")]
    public async Task DoubleGetterOnStoredString3WithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.string.number");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DURATION getter on stored string \"PT1S\" returns the default")]
    public async Task DurationGetterOnStoredStringPt1sReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), client.GetDuration("getter.contract.string.duration", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond)));
        var details = client.GetDurationDetails("getter.contract.string.duration", defaultValue: TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond));
        Assert.Equal(TimeSpan.FromTicks(7000L * TimeSpan.TicksPerMillisecond), details.Value);
        Assert.Equal(Reason.Error, details.Reason);
    }

    [Fact(DisplayName = "DURATION getter on stored string \"PT1S\" with no default returns nil")]
    public async Task DurationGetterOnStoredStringPt1sWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDuration("getter.contract.string.duration");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE get on stored int 30 returns 30.0")]
    public async Task DoubleGetOnStoredInt30Returns300()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.int");
        TestSetup.AssertDoubleEquals(30d, actual);
    }

    [Fact(DisplayName = "INT get on stored whole double 3.0 returns 3")]
    public async Task IntGetOnStoredWholeDouble30Returns3()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.double.whole");
        Assert.Equal(3L, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored int 30 ignores the default")]
    public async Task DoubleGetterOnStoredInt30IgnoresTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.int", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(30d, actual);
    }

    [Fact(DisplayName = "INT getter on stored whole double 3.0 ignores the default")]
    public async Task IntGetterOnStoredWholeDouble30IgnoresTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.double.whole", defaultValue: 7L);
        Assert.Equal(3L, actual);
    }

    [Fact(DisplayName = "INT getter on stored double 1e300 returns the default")]
    public async Task IntGetterOnStoredDouble1e300ReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.double.huge", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored double 1e300 with no default returns nil")]
    public async Task IntGetterOnStoredDouble1e300WithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.double.huge");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored double 2^53 (outside the safe int range) returns the default")]
    public async Task IntGetterOnStoredDouble253OutsideTheSafeIntRangeReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.double.unsafe", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored double 2^53 (outside the safe int range) with no default returns nil")]
    public async Task IntGetterOnStoredDouble253OutsideTheSafeIntRangeWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.double.unsafe");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on stored malformed int \"twelve\" returns the default")]
    public async Task IntGetterOnStoredMalformedIntTwelveReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.int.malformed", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on stored malformed int \"twelve\" with no default returns nil")]
    public async Task IntGetterOnStoredMalformedIntTwelveWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.int.malformed");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored malformed double \"abc\" returns the default")]
    public async Task DoubleGetterOnStoredMalformedDoubleAbcReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.double.malformed", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on stored malformed double \"abc\" with no default returns nil")]
    public async Task DoubleGetterOnStoredMalformedDoubleAbcWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.double.malformed");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "INT getter on env-var-provided malformed int \"twelve\" returns the default")]
    public async Task IntGetterOnEnvVarProvidedMalformedIntTwelveReturnsTheDefault()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_GETTER_INT", "twelve");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetLong("getter.contract.provided.int.malformed", defaultValue: 7L);
        Assert.Equal(7L, actual);
    }

    [Fact(DisplayName = "INT getter on env-var-provided malformed int \"twelve\" with no default returns nil")]
    public async Task IntGetterOnEnvVarProvidedMalformedIntTwelveWithNoDefaultReturnsNil()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_GETTER_INT", "twelve");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetLong("getter.contract.provided.int.malformed");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "DOUBLE getter on env-var-provided malformed double \"abc\" returns the default")]
    public async Task DoubleGetterOnEnvVarProvidedMalformedDoubleAbcReturnsTheDefault()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_GETTER_DOUBLE", "abc");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetDouble("getter.contract.provided.double.malformed", defaultValue: 7.5d);
        TestSetup.AssertDoubleEquals(7.5d, actual);
    }

    [Fact(DisplayName = "DOUBLE getter on env-var-provided malformed double \"abc\" with no default returns nil")]
    public async Task DoubleGetterOnEnvVarProvidedMalformedDoubleAbcWithNoDefaultReturnsNil()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_GETTER_DOUBLE", "abc");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetDouble("getter.contract.provided.double.malformed");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on a missing env var returns the default")]
    public async Task StringGetterOnAMissingEnvVarReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("provided.by.missing.env.var", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on a missing env var with no default returns nil")]
    public async Task StringGetterOnAMissingEnvVarWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("provided.by.missing.env.var");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "STRING getter on a decryption failure returns the default")]
    public async Task StringGetterOnADecryptionFailureReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetString("a.broken.secret.config", defaultValue: "DEFAULT");
        Assert.Equal("DEFAULT", actual);
    }

    [Fact(DisplayName = "STRING getter on a decryption failure with no default returns nil")]
    public async Task StringGetterOnADecryptionFailureWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetString("a.broken.secret.config");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on a missing bool env var returns the default")]
    public async Task BooleanGetterOnAMissingBoolEnvVarReturnsTheDefault()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.provided.bool.missing", defaultValue: true);
        Assert.True(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on a missing bool env var with no default returns nil")]
    public async Task BooleanGetterOnAMissingBoolEnvVarWithNoDefaultReturnsNil()
    {
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
            OnNoDefault = OnNoDefault.Ignore,
        });
        var actual = client.GetBool("getter.contract.provided.bool.missing");
        Assert.Null(actual);
    }

    [Fact(DisplayName = "BOOLEAN getter on env-var-provided bool \"false\" returns false")]
    public async Task BooleanGetterOnEnvVarProvidedBoolFalseReturnsFalse()
    {
        using var env = TestSetup.Env("QUONFIG_ITD_GETTER_BOOL", "false");
        await using var client = TestSetup.NewClient(new QuonfigOptions
        {
            Datadir = TestSetup.DATADIR,
            Environment = TestSetup.ENV_ID,
        });
        var actual = client.GetBool("getter.contract.provided.bool");
        Assert.False(actual);
    }
}
