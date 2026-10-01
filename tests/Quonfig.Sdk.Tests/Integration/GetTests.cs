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
}
