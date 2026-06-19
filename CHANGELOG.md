# Changelog

## 1.1.0 - 2026-06-19

Backward-compatible minor. Hardens the HTTP config-fetch path for fast, regression-free failover.

- **Parallel-failover hedge on the HTTP config-fetch (qfg-7h5d.1.14).** The init/refresh config fetch is now a parallel hedge: the SDK fires the **primary** URL first and, only if it answers within the hedge delay (~2s), the primary wins and the **secondary is never contacted** (cold standby, zero extra load on a healthy system). If the primary is slow past the hedge delay or errors fast, the SDK *also* fires the secondary **in parallel** (without cancelling the primary). Each arriving leg is installed through the existing reject-older guard, so watermark-max falls out: the higher `Meta.generation` wins, a late older payload never regresses an established client, and a late newer payload heals forward. SSE is untouched and never fails over.
  - Two new additive options: `QuonfigOptions.ConfigFetchHedgeDelay` (default ~2s — how long to wait for the primary before hedging the secondary) and `QuonfigOptions.ConfigFetchHedgeAbort` (default ~6s — the per-leg hard-abort deadline). The existing `ConfigFetchTimeout` is unchanged and still governs the sequential `FetchAsync` path. A warning is logged at construction if `InitTimeout <= ConfigFetchHedgeAbort`.
  - **Backward-compatible behavioral notes.** `resolvedFrom` may now return `"primary"` in a fast-both topology where 1.0.0 (sequential) returned `"secondary"` only on a primary failure; an extra post-ready config-update callback may fire when a late-but-newer leg heals forward; and ETags are now tracked **per leg** (each URL has its own slot) so a 304 from one leg can no longer mask the other and there is no cross-leg ETag data race.

- **Failover + canonical-ordering reliability (qfg-7h5d.1.11).** Two additive, backward-compatible hardening changes proven red→green against the shared failover + ordering chaos corpus:
  - **Per-URL config-fetch timeout.** Each failover leg now gets its own deadline so a hung or slow primary aborts fast (~3s default) and the secondary is reached within the overall `InitTimeout`, instead of the primary starving the whole budget. Tunable via the new `QuonfigOptions.ConfigFetchTimeout` option (default 3s); applies to the initial fetch, the fallback poller, and in-band refresh.
  - **Reject-older install guard.** The SDK now reads the monotonic `Meta.generation` watermark and installs a network envelope only if it strictly advances the held generation. A fresh client always accepts its first snapshot; an established client never regresses to an older payload (e.g. on a failover to a stale secondary), and a same-generation snapshot is a no-op. Applies to every network install path (initial fetch, fallback poller, SSE snapshot/update, refresh). Datadir/datafile installs are a local source of truth and bypass the guard.

## 1.0.0 - 2026-06-06

- **Stable 1.0.0 release.** The Quonfig .NET SDK (`Quonfig.Sdk` and the
  `Quonfig.Sdk.AspNetCore` / `Quonfig.Sdk.Extensions.Logging` / `Quonfig.Sdk.Serilog`
  companions) is now declared stable. No API or behavior changes from 0.0.3 — this is
  a coordinated 1.0.0 version stamp across the entire Quonfig SDK family.

## 0.0.3 - 2026-06-02

- **Token-file dev-context loader, default-on (qfg-bw7g.7).** New `DevContext` loader mirrors sdk-node: it reads the per-domain qfg-login tokens file (`~/.quonfig/tokens.json`, or `tokens-<domain>.json` for non-prod domains derived from `ApiUrls`), parses `userEmail`, and returns a `{ "quonfig-user": { email } }` context. No-ops when the file is missing or has no `userEmail`; logs one warning on parse error. Adds a nullable `EnableQuonfigUserContext` option (`null` = unset). Resolution: explicit option wins, else `QUONFIG_DEV_CONTEXT` env (`true`/`false`), else default `true`. Injection merges **under** the customer `GlobalContext`, so customer keys win on collision. Default-on is inert in production (no tokens file). Config home is overridable via `QUONFIG_CONFIG_HOME`. No new NuGet dependency (System.Text.Json only). Set `EnableQuonfigUserContext = false` or `QUONFIG_DEV_CONTEXT=false` to opt out.

## 0.0.2 - 2026-05-29

Per-environment override fixes for delivery (SDK-key) mode. `0.0.1` shipped without these, so per-environment config overrides were not honored when connecting via an SDK key.

- Parse the singular delivery `environment` block and scope evaluation to `meta.environment` (qfg-64m9)
- In delivery (SDK-key) mode `meta.environment` is authoritative: an explicit `Environment` pin is datadir-only and is ignored in delivery mode, with a WARN logged when one is set (qfg-pinh)

## 0.0.1 - 2026-05-27

First public release of the Quonfig .NET SDK. Greenfield port of the Quonfig client targeting `net8.0` and `netstandard2.0`, published to nuget.org as a four-package family from a single tag. Tracks the [qfg-zp7i epic](https://github.com/quonfig/sdk-net/issues).

All four packages ship in lock-step from this tag:

- `Quonfig.Sdk` — core SDK
- `Quonfig.Sdk.AspNetCore` — ASP.NET Core integration (DI, `IHostedService`, per-request `ContextSet` middleware)
- `Quonfig.Sdk.Extensions.Logging` — dynamic log-level filter for `Microsoft.Extensions.Logging`
- `Quonfig.Sdk.Serilog` — `LoggingLevelSwitch` provider for Serilog

### Core SDK (`Quonfig.Sdk`)

- Bootstrap .NET solution with `Directory.Build.props` / `Directory.Packages.props` central management, `global.json` SDK pin, `dotnet format` gate (qfg-zp7i.1)
- Wire types: `ConfigEnvelope`, `Meta`, `EvaluationDetails<T>`, `ContextSet`, `ContextValue` with implicit conversions (qfg-zp7i.4)
- Datadir loader: workspace JSON tree to in-memory `ConfigStore`, `schemas/` directory excluded (qfg-zp7i.5)
- HTTP transport with Basic auth, ETag handling, and primary to secondary failover (qfg-zp7i.6)
- `Murmur3` + `Resolver` (ENV_VAR, weighted, AES-GCM, type coercion) and `AesGcmCompat` netstandard2.0 polyfill via BouncyCastle (qfg-zp7i.7)
- Rule evaluator + 22 operators + semver matching (qfg-zp7i.8)
- SSE client with Layer 1 read watchdog (90s, `CancellationTokenSource.CancelAfter`) (qfg-zp7i.9)
- Supervisor + `FallbackPoller` (Layer 2, on-failure-only, 120s engage) (qfg-zp7i.10)
- Public `Quonfig` + `BoundQuonfig` + `IQuonfig` facade with `ConnectionState` and `LastSuccessfulRefresh` health primitives (qfg-zp7i.11)
- Telemetry collectors + reporter + HTTP sender (qfg-zp7i.12)

### Companion packages

- `Quonfig.Sdk.AspNetCore`: `AddQuonfig` DI extension, `QuonfigHostedService` lifecycle, `UseQuonfigContext` per-request middleware (qfg-zp7i.16)
- `Quonfig.Sdk.Extensions.Logging`: `AddQuonfigFilter` extension wiring `QuonfigLoggerProvider` into the `ILoggerFactory` pipeline (qfg-zp7i.16)
- `Quonfig.Sdk.Serilog`: `QuonfigLoggingLevelSwitchProvider` that drives a per-logger `LoggingLevelSwitch` from Quonfig config (qfg-zp7i.16)

### Tests, chaos, and CI

- Integration tests + `TestSetup` harness wired into the cross-SDK `integration-test-data` generator (qfg-zp7i.13)
- Chaos suite wired into `integration-test-data/chaos/scenarios/`, scenarios 01-11 green (qfg-zp7i.14)
- CI matrix on `ubuntu-latest` + `windows-latest`, target frameworks `net8.0` + `netstandard2.0` (host: `net8.0` everywhere except Windows + NS2.0 which uses `net48`)
- `dotnet format --verify-no-changes` gates every PR per `.claude/rules/formatters.md`

### Release pipeline

- Tag-triggered `.github/workflows/release.yaml` publishes all four packages lock-step from a single `v*` tag; verifies tag matches `Directory.Build.props` `<Version>` before packing; `dotnet nuget push *.nupkg` + `*.snupkg` with `--skip-duplicate` to nuget.org; auto-creates the GitHub Release from this CHANGELOG section (qfg-zp7i.19)
- Source Link + embedded untracked sources + symbol packages (`.snupkg`) enabled in `Directory.Build.props`
