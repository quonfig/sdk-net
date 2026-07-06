# Changelog

## Unreleased

- **`QUONFIG_DOMAIN` support + SSE stream follows `ApiUrls` (qfg-41nh.27).** The SDK now derives `ApiUrls` and `TelemetryUrl` from the `QUONFIG_DOMAIN` env var (default `quonfig.com`): `primary.<domain>` / `secondary.<domain>` for config fetch and `telemetry.<domain>` for telemetry, matching the other backend SDKs and the CLI. Resolution precedence per field is explicit option > `QUONFIG_DOMAIN` > built-in production default. The SSE `StreamUrls` now **follow** the resolved `ApiUrls` when not set explicitly (each stream host is the api host with `stream.` prepended), so a customer overriding only `ApiUrls` (e.g. to staging) streams from the matching host instead of silently from `stream.primary.quonfig.com`. Explicit `StreamUrls` / `TelemetryUrl` still win. A single explicit `ApiUrls` entry disables automatic failover and logs a warning at construction. All additive and backward-compatible (the built-in defaults are unchanged); no new dependencies.

- **Failover telemetry emission (qfg-41nh.18).** The SDK now records failover-behavior counters and folds them into the periodic telemetry envelope as an additive `failover` event, alongside evaluation summaries and context shapes. The counters are: `hedgeFired` (config-fetch cycles whose parallel hedge fired the secondary leg), `guardRejected` (installs the reject-older ordering guard dropped, on both the HTTP config-fetch and SSE push paths), and `resolvedFromPrimary` / `resolvedFromSecondary` (which upstream leg served each successful HTTP install; SSE installs are not counted). `resolvedFromLkg` is reserved and always `0`. The event carries no user data, is emitted only when at least one counter is non-zero in the flush window (a healthy client emits nothing), and honors the existing telemetry opt-out (`CollectEvaluationSummaries = false` **and** `ContextUploadMode = None` records nothing). The wire field names are camelCase, matching the cross-SDK contract (mirrors sdk-go). No new dependencies.

## 1.1.1 - 2026-07-03

Backward-compatible patch (qfg-41nh.8, part of the coordinated 1.1.1 hardening train).

- **SSE stream pinned to the primary (the f05 invariant).** The reconnect loop previously walked the whole `StreamUrls` list, so an outage on the primary stream endpoint silently repointed the long-lived SSE stream at the secondary — binding real-time config freshness to the mirror, which is sized for poll traffic only. The stream is now pinned to `StreamUrls[0]` and a dead primary is retried forever with the existing exponential backoff; SSE never fails over. During a stream outage the Layer 2 fallback poller (whose hedged HTTP fetch keeps BOTH `ApiUrls` legs) covers freshness. The failover chaos suite's f05 probe, previously hardcoded off, is now wired to the client's real stream-leg bookkeeping, so a regression that reintroduces stream-leg walking fails CI.
- **Honest freshness stamping.** `LastSuccessfulRefresh` no longer advances when an SSE-pushed envelope is REJECTED by the reject-older guard — a stale replay loop previously reported an effectively frozen client as fresh. Conversely, an HTTP poll the server ANSWERS with nothing newer to install (a 304, or a same/older-generation payload the guard discards) now DOES advance the stamp: a successful poll is a successful refresh (sdk-go semantics).
- **Operator logging.** With any wired `ILogger` the client now emits: one startup line announcing the chosen polling mode (`quonfig: polling configuration mode=...`, Information), a Warning when the Layer 2 fallback poller ENGAGES (SSE down past threshold — the outage signal to alert on), and an Information line when it DISENGAGES (SSE recovered). The default logger remains the no-op `NullLogger`.

## 1.1.0 - 2026-07-01

Backward-compatible minor. Hardens the HTTP config-fetch path for fast, regression-free failover.

- **Parallel-failover hedge on the HTTP config-fetch (qfg-7h5d.1.14).** The init/refresh config fetch is now a parallel hedge: the SDK fires the **primary** URL first and, only if it answers within the hedge delay (~2s), the primary wins and the **secondary is never contacted** (cold standby, zero extra load on a healthy system). If the primary is slow past the hedge delay or errors fast, the SDK *also* fires the secondary **in parallel** (without cancelling the primary). Each arriving leg is installed through the existing reject-older guard, so watermark-max falls out: the higher `Meta.generation` wins, a late older payload never regresses an established client, and a late newer payload heals forward. SSE is untouched by the hedge. (Correction: this entry originally claimed SSE "never fails over" — at 1.1.0 the stream still walked the `StreamUrls` list on reconnect; the primary pin shipped in the next patch, see Unreleased.)
  - Two new additive options: `QuonfigOptions.ConfigFetchHedgeDelay` (default ~2s — how long to wait for the primary before hedging the secondary) and `QuonfigOptions.ConfigFetchHedgeAbort` (default ~6s — the per-leg hard-abort deadline). The existing `ConfigFetchTimeout` is unchanged and still governs the sequential `FetchAsync` path. A warning is logged at construction if `InitTimeout <= ConfigFetchHedgeAbort`.
  - **Backward-compatible behavioral notes.** `resolvedFrom` may now return `"primary"` in a fast-both topology where 1.0.0 (sequential) returned `"secondary"` only on a primary failure; an extra post-ready config-update callback may fire when a late-but-newer leg heals forward; and ETags are now tracked **per leg** (each URL has its own slot) so a 304 from one leg can no longer mask the other and there is no cross-leg ETag data race.

- **Failover + canonical-ordering reliability (qfg-7h5d.1.11).** Two additive, backward-compatible hardening changes proven red→green against the shared failover + ordering chaos corpus:
  - **Per-URL config-fetch timeout.** Each failover leg now gets its own deadline so a hung or slow primary aborts fast (~3s default) and the secondary is reached within the overall `InitTimeout`, instead of the primary starving the whole budget. Tunable via the new `QuonfigOptions.ConfigFetchTimeout` option (default 3s); applies to the initial fetch, the fallback poller, and in-band refresh.
  - **Reject-older install guard.** The SDK now reads the monotonic `Meta.generation` watermark and installs a network envelope only if it advances the held generation, with one carve-out for unversioned snapshots (below). A fresh client always accepts its first snapshot; an established client never regresses to an older versioned payload (e.g. on a failover to a stale secondary), and a same-generation snapshot is a no-op. Applies to every network install path (initial fetch, fallback poller, SSE snapshot/update, refresh). Datadir/datafile installs are a local source of truth and bypass the guard.
    - **Install-guard carve-out for unversioned snapshots.** A delivery payload whose `generation` is absent or `<= 0` (e.g. from a server that predates the generation watermark) is installed by an established client rather than rejected as older. Defensive back-compat guard — with servers that emit true generations it never triggers.

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
