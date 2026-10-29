# sdk-net

.NET SDK for [Quonfig](https://quonfig.com) — feature flags and configuration as files in git.

[![NuGet](https://img.shields.io/nuget/v/Quonfig.Sdk.svg)](https://www.nuget.org/packages/Quonfig.Sdk)

## Install

```bash
dotnet add package Quonfig.Sdk
```

Companion packages (install only the ones you need):

```bash
dotnet add package Quonfig.Sdk.AspNetCore
dotnet add package Quonfig.Sdk.Extensions.Logging
dotnet add package Quonfig.Sdk.Serilog
```

Or via `<PackageReference>`:

```xml
<PackageReference Include="Quonfig.Sdk" Version="2.1.0" />
```

## Packages

This repo publishes four NuGet packages in lock-step from a single tag.

| Package                              | Purpose                                                                                                |
|--------------------------------------|--------------------------------------------------------------------------------------------------------|
| `Quonfig.Sdk`                        | Core SDK — config evaluation, HTTP+SSE transport, datadir loader, telemetry.                           |
| `Quonfig.Sdk.AspNetCore`             | DI + `IHostedService` + per-request `ContextSet` via `HttpContext`.                                     |
| `Quonfig.Sdk.Extensions.Logging`     | `ILoggerProvider` filter — dynamic log levels via the BCL `Microsoft.Extensions.Logging` pipeline.      |
| `Quonfig.Sdk.Serilog`                | Serilog `LoggingLevelSwitch` provider for dynamic log levels.                                            |

## Typed getters

`GetString`, `GetInt`, `GetLong`, `GetBool`, `GetDouble`, `GetStringList`, `GetJson` and
`GetDuration` return the value only when it has the getter's type. This is the typed-getter
contract every Quonfig SDK follows.

- **No string coercion.** A stored string `"false"` is not a bool, and `"3"` is not an int.
- **Safe number conversions only.** `GetDouble` reads an int config (`30` -> `30.0`).
  `GetLong` / `GetInt` read a double config only when it is whole and within +/-(2^53-1)
  (`3.0` -> `3`). `3.9` or `1e300` is a type mismatch. Reads never truncate.
- **A failed read returns your default.** A wrong-type value, a malformed stored value, an unset
  ENV_VAR or a decryption failure returns `defaultValue` (or `null` when you pass none) and logs
  one warning per key per client. The warning names the key, never the value.
- **Hard failure on request.** With no `defaultValue` and `OnNoDefault = OnNoDefault.Throw` (the
  default), a failed read throws: `QuonfigTypeMismatchException`, `QuonfigCoercionException`,
  `QuonfigEnvVarNotSetException` or `QuonfigDecryptionException`. Use this form for values your
  app cannot run without, such as secrets: a wrong decryption key then fails loudly instead of
  serving defaults.
- **`IsFeatureEnabled` never throws.** It is true only for a resolved bool `true`. A missing
  key, a flag with no matching rule, a non-bool value or a resolution error returns `false` and
  logs once per key.
- **`Get*Details` never throw.** A wrong-type or malformed value reports `Reason = Error` and
  `ErrorCode = TypeMismatch`, with your default as the value.

### Untyped details: `EvaluateDetails`

`EvaluateDetails(key, contexts)` on the `IQuonfigDetailsReader` interface returns the resolved
value as it is stored, with the same reason, variant and metadata as the `Get*Details` getters.
It runs no type check and no parse, so it suits integrations that pass values through, such as an
OpenFeature provider. A duration is its ISO 8601 text exactly as stored:

```csharp
// Quonfig and the bound view from WithContext implement IQuonfigDetailsReader.
// Code that holds an IQuonfig reaches it with a type check:
if (client is IQuonfigDetailsReader reader)
{
    var d = reader.EvaluateDetails("http.timeout");
    // d.Value == "PT90S" (a string), d.Reason == Reason.Static, d.ValueType == "duration"
}
```

An int is a `long`, a double a `double`, a string list an `IReadOnlyList<string>`, and JSON the
same tree `GetJson` returns. A stored value a typed getter rejects (for example the duration
`"30s"`) comes back as stored with a success reason. `EvaluateDetails` never throws: a missing key
returns a `null` value with `Reason = Error` and `ErrorCode = FlagNotFound`, and an unset or
malformed ENV_VAR or a decryption failure returns `null` with the error code a typed getter reports.

## Startup failure (`InitTimeout` / `OnInitFailure`)

`await client.InitAsync()` waits for the first config from Quonfig delivery. Until
`InitTimeout` (10 s by default, measured from construction) the client retries with
jittered backoff; then `OnInitFailure` decides:

| `OnInitFailure`   | At the timeout                                                                                       |
|-------------------|------------------------------------------------------------------------------------------------------|
| `Fail` (default)  | Throws `QuonfigInitTimeoutException` if the last attempt hung, else `QuonfigInitException` (`Reason = Unavailable`). |
| `Continue`        | Returns; getters serve code defaults until the background retry heals.                               |
| `Wait`            | Keeps blocking (no upper bound) until config arrives or your `CancellationToken` fires.              |

- If every configured delivery URL answers 401, 403, 404 or 410, `Fail` throws at once with
  `Reason = Unauthorized` and `Continue` continues at once; `Wait` logs an error and keeps retrying.
- `InitTimeout` of zero or below (including `Timeout.InfiniteTimeSpan`) means "try once": exactly one
  fetch attempt, then the policy above applies immediately. It never means "no deadline".
- A client you keep after a caught failure keeps retrying in the background and heals. Watch
  `client.IsReady` / `WaitForReadyAsync()` for readiness.
- Init log lines and init exceptions name the failing hosts (`host:port`) and HTTP statuses, never the
  SDK key, URL credentials (`user:pass@`) or response bodies.

## Failover & `QUONFIG_DOMAIN`

By default the SDK derives every hostname from `QUONFIG_DOMAIN` (default `quonfig.com`):

| Role                     | URL                                    |
|--------------------------|----------------------------------------|
| Config fetch (primary)   | `https://primary.quonfig.com`          |
| SSE stream (primary)     | `https://stream.primary.quonfig.com`   |
| Config fetch (secondary) | `https://secondary.quonfig.com`        |
| SSE stream (secondary)   | `https://stream.secondary.quonfig.com` |
| Telemetry                | `https://telemetry.quonfig.com`        |

Set `QUONFIG_DOMAIN` to move all of them together (e.g. `QUONFIG_DOMAIN=quonfig-staging.com`
points api, SSE, and telemetry at staging). **Automatic failover and hedging between the primary
and the secondary are on by default** — the secondary runs on separate infrastructure, and the SDK
fails over to it if the primary is unreachable and hedges to it if the primary is slow.

`QuonfigOptions.ApiUrls` replaces the derived list wholesale and wins over `QUONFIG_DOMAIN`. The SSE
stream URLs follow it automatically (each stream host is the api host with `stream.` prepended), and
`QuonfigOptions.TelemetryUrl` still follows `QUONFIG_DOMAIN` unless you set it too. To keep automatic
failover with custom URLs, **pass both a primary and a secondary URL**:

```csharp
var client = new Quonfig(new QuonfigOptions
{
    SdkKey = "your-sdk-key",
    ApiUrls = new[]
    {
        "https://primary.your-proxy.example",
        "https://secondary.your-proxy.example",
    },
    // StreamUrls left unset — derived as stream.primary.your-proxy.example / stream.secondary...
});
```

A single URL disables failover, and the SDK logs a warning at construction
(`quonfig: explicit ApiUrls disables automatic failover to the secondary; pass both primary and
secondary URLs to keep it`). Set `StreamUrls` or `TelemetryUrl` explicitly to override the derived
values. See
[Reliability](https://docs.quonfig.com/docs/explanations/architecture/resiliency) for the full model.

## Telemetry

The SDK sends usage telemetry to `TelemetryUrl` so the Quonfig dashboard can show which flags and
configs are evaluated and with what contexts. Telemetry never affects flag evaluation: every failure
below is contained in the background reporter.

**What is sent.** Evaluation summaries (per flag/config: counts per rule and value), context shapes
(context field names and types), example contexts (for each context `key` or `trackingId`, at most
one full context per hour, **with its values**) and failover counters. Opt out with
`CollectEvaluationSummaries = false` and `ContextUploadMode = ContextUploadMode.ShapesOnly` (no
example contexts, no values) or `ContextUploadMode.None` (no context data). With both off, no
reporter runs. `ContextUploadMode` defaults to `PeriodicExample` since 1.3.0 (was `ShapesOnly`).

**Developer context (on by default).** On a machine where someone ran `qfg login`, the SDK reads the
signed-in email from `~/.quonfig/tokens.json` and adds it to every evaluation as
`quonfig-user.email`, so it is sent with context shapes and example contexts. Without that file
(for example on a server) nothing is added. To turn it off, set `EnableQuonfigUserContext = false`
or `QUONFIG_DEV_CONTEXT=false`.

**How it is sent.**

- One POST every `TelemetryFlushInterval` (60s), with at most one POST in flight. A tick that fires
  while a POST is still out is skipped and its data rolls into the next window.
- Each POST has an overall deadline of `TelemetryTimeout` (15s). On net8.0 the TCP connect and TLS
  handshake have their own `TelemetryConnectTimeout` (5s) when the SDK builds its own HTTP handler;
  on netstandard2.0, or with an injected `HttpMessageHandler`, the overall deadline covers connect.
- When a POST fails (timeout, network error, 408, 429 or 5xx), the serialized batch is kept
  byte-for-byte and resent unchanged, never merged with newer data, so the server can recognize a
  resend of a batch that did land. Up to `TelemetryMaxRetainedBatches` (5) batches /
  `TelemetryMaxRetainedBytes` (2MB) are kept for up to `TelemetryMaxRetainedAge` (5 min); beyond
  that the oldest is dropped, and a single batch larger than the byte cap is sent once and never
  kept. Resends happen no sooner than 30s after a failure and after any `Retry-After` (honored up
  to 10 minutes), oldest first, then the current window.
- A 401, 403 or 404 means the SDK key or `TelemetryUrl` is wrong: the SDK logs one error and
  disables telemetry for the rest of the process. Any other 4xx drops that one batch with an error
  (the server rejected the payload) and telemetry continues.

**Logging** (through `QuonfigOptions.Logger`; the default is a no-op logger). A failed POST logs at
`Debug` only. The first batch actually dropped logs one `Warning` with the last POST result and
queue depth; further drops log at `Debug` with a summary `Warning` at most every 10 minutes; the
first success after failures logs one `Information` line.

**Shutdown.** `CloseAsync()` / `DisposeAsync()` sends the current window once with a 5s deadline,
does not resend kept batches, and never blocks shutdown on a slow telemetry endpoint.

**Memory.** Everything is bounded: at most `TelemetryMaxEvaluationSummaries` (10,000)
evaluation-summary counters, `TelemetryMaxContextShapeFields` (10,000) context-shape fields and
`TelemetryMaxExampleContexts` (10,000) example contexts per window (counters already seen keep
counting at the cap), a 100,000-entry example-context rate-limit map, and the 2MB retained queue.

## Target frameworks

`net8.0` and `netstandard2.0`. Both are gated by CI on `ubuntu-latest` and `windows-latest`:

| matrix.tfm        | ubuntu-latest                       | windows-latest                              |
|-------------------|-------------------------------------|---------------------------------------------|
| `net8.0`          | `dotnet test -f net8.0`             | `dotnet test -f net8.0`                     |
| `netstandard2.0`  | `dotnet test -f net8.0` (NS2 lib)   | `dotnet test -f net48` (NS2 lib on .NETFx)  |

`netstandard2.0` is a contract, not a runtime, so the test project targets `net8.0;net48`; the
appropriate host is chosen per cell in `.github/workflows/test.yaml`.

## Layout

```
sdk-net/
  Quonfig.sln
  Directory.Build.props        # version, nullable, langversion, analyzers
  Directory.Packages.props     # central package management
  global.json                  # .NET SDK pin
  .editorconfig                # dotnet format gates this in CI

  src/
    Quonfig.Sdk/               # core SDK (net8.0;netstandard2.0)

  tests/
    Quonfig.Sdk.Tests/         # xUnit (net8.0;net48)

  .github/workflows/
    test.yaml                  # PR gate: matrix build + test + format check
```

## Local development

Prerequisites: .NET SDK 8.0.x (see `global.json` for the exact pin).

```bash
# Restore + build all TFMs
dotnet restore
dotnet build

# Test (defaults to net8.0 host on macOS/Linux)
dotnet test

# Format check (CI gate per .claude/rules/formatters.md)
dotnet format --verify-no-changes
```

`netstandard2.0` runtime verification requires Windows + .NET Framework 4.8; rely on the CI matrix
for that cell.

## License

Apache License 2.0
