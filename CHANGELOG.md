# Changelog

## Unreleased

- **`ContextUploadMode` now defaults to `PeriodicExample` (was `ShapesOnly`) — more context data is sent to Quonfig by default (qfg-y8je.10).** With `ShapesOnly` the SDK uploaded only context field names and types. With `PeriodicExample` it additionally uploads **example contexts**: for each distinct context `key` (or `trackingId`), at most once per hour, the full evaluation context of that evaluation — every named context (`user`, `team`, ...) including the `GlobalContext`, with **all property values** (for example `user.email`, `user.name`, `team.plan`). Contexts with neither a `key` nor a `trackingId` are never sampled. This matches every other Quonfig SDK (decided 2026-09-25) and powers the dashboard's example-context drill-down. **To keep the old behavior set `ContextUploadMode = ContextUploadMode.ShapesOnly`** (field names and types only, no values), or `ContextUploadMode.None` to send no context data at all; setting `CollectEvaluationSummaries = false` as well turns telemetry off entirely.
- **Telemetry transport policy (qfg-y8je.10).** Telemetry POSTs now have a 15s overall deadline (was 30s, `TelemetryTimeout`) and, on net8.0 when the SDK builds its own HTTP handler, a 5s connect/TLS deadline (`TelemetryConnectTimeout`; an injected `HttpMessageHandler` is never modified). A failed batch (timeout, network error, 408, 429, 5xx) is kept byte-for-byte and resent unchanged, never merged with newer data, so the server dedups a resend of a batch that did land; before, it was dropped. Resends happen no sooner than 30s after a failure and honor `Retry-After` up to 10 min. The retained queue is capped at 5 batches / 2MB / 5 min (oldest dropped; a batch larger than the byte cap is never kept). At most one POST is in flight; the flush interval is a fixed 60s (the adaptive backoff is gone). 401/403/404 disable telemetry for the process with one error; any other 4xx drops that batch with one error. **Logging** goes through `QuonfigOptions.Logger`: a failed POST logs at `Debug`, one `Warning` when data is actually dropped (then a summary at most every 10 min), one `Information` on recovery. Before this, a failure logged a `Warning` every time.
- **A timed-out telemetry POST no longer stops telemetry for the life of the process (qfg-y8je.1).** The sender's `HttpClient.Timeout` (30s) surfaced as a `TaskCanceledException`, which the reporter treated as a shutdown: the flush loop exited and no further telemetry was ever sent, so a single slow api-telemetry response (e.g. a backend stall) silently ended all telemetry. A per-request timeout is now an ordinary failed POST (the batch is kept and resent, see above) and the loop keeps ticking. Contract test T1 pins it.
- **`CloseAsync()` / `DisposeAsync()`** sends the live window once with a 5s deadline and no longer waits on kept batches; it never blocks shutdown on a slow telemetry endpoint. The built-in telemetry `HttpClient` is now disposed on close.
- **Memory caps:** evaluation-summary keys, context-shape fields (before: only context names were capped, so fields grew with traffic) and example contexts are capped at 10,000 per window, and the example-context rate-limit map at 100,000. Keys already recorded keep counting at the cap.
- **New options** on `QuonfigOptions`: `TelemetryTimeout` (15s), `TelemetryConnectTimeout` (5s), `TelemetryMaxRetainedBatches` (5), `TelemetryMaxRetainedBytes` (2,097,152), `TelemetryMaxRetainedAge` (5 min), `TelemetryMaxEvaluationSummaries`, `TelemetryMaxContextShapeFields`, `TelemetryMaxExampleContexts` (10,000 each). Non-positive values fall back to the default. `TelemetryInitialDelay` and `TelemetryMaxInterval` are kept for source compatibility but no longer used: the first POST happens one `TelemetryFlushInterval` after start.
- **Public telemetry types:** `HttpTelemetrySender.DefaultTimeout` is now 15s. `TelemetryReporter.FlushAsync` sends the live window now and no longer throws on a failed POST; `CurrentInterval` is the fixed flush interval; `FlushAndApplyBackoffAsync` is deprecated (runs `FlushAsync`, returns `false` while POSTs are failing); the constructor's `initialDelay` / `maxInterval` are ignored. A custom `ITelemetrySender` that throws is treated as a retryable failure. No wire change, no removed public API, no new dependencies.

## 1.2.2 - 2026-09-14

- **Only a strictly older payload counts as `guardRejected` (qfg-e206, qfg-rr5b).** The reject-older install guard drops any envelope that does not strictly advance the held generation, and every drop was reported as a `guardRejected` failover counter. Two ordinary server behaviors re-deliver an envelope the client already holds at the **same** generation — api-delivery's SSE `sendInitialConfig` re-sends the current envelope on every connect (SDK clients send no `Last-Event-Id`), and a config poll with an empty per-leg ETag slot (a fresh transport, a reconnect, the fallback poller's engage-time fetch) answers with a full 200 — so a perfectly healthy client accumulated one `guardRejected` per SSE reconnect and per cold-ETag poll. `guardRejected` feeds the `sdk_failover` alerting signal, where it is supposed to mean "a leg tried to move us backwards". It now counts **only** a strictly older payload (incoming generation **<** held). An equal-generation re-delivery is a silent no-op: it is still not installed, and it still advances (or, on the SSE push path, still does not advance) `LastSuccessfulRefresh` exactly where it did before — only the counter branch changed. The unversioned carve-out (incoming generation `<= 0` installs rather than being rejected) is untouched. **Operational note: `guardRejected` reads LOWER on 1.2.2 than on 1.2.1 for identical traffic** — the drop is the removal of same-generation re-delivery noise, not a behavior regression; a steady-state client should now report 0. No wire-format, ClickHouse, or dashboard-query change, no public API change, and no new dependencies.

## 1.2.1 - 2026-08-19

- **Fallback poller no longer loses an SSE state edge (qfg-vov2).** The Layer 2 fallback poller decided each tick's wait duration while holding its lock, then released the lock, ran that tick's side effects (engage/disengage callbacks and the fetch), and only then sampled `_sseConnected` a **second** time to establish the baseline it watches for a connection-state change. Any `SetSseConnected()` delivered in that gap was already folded into the baseline, so it never registered as an edge and the worker slept the **full** duration it had decided on: **one hour** on the connected branch, one poll interval on the engaged one. In the severe direction an SSE disconnect that landed in the gap left Layer 2 idle for up to an hour — precisely the outage Layer 2 exists to cover — with the client serving stale config and no fallback polling; in the symmetric direction a reconnect that landed in the gap kept the poller fetching over a healthy stream for a full interval before disengaging. The wait now takes the state the decision was made against as an explicit baseline (and checks it once before the first sleep), so a change in the gap is seen immediately. This matches sdk-go, whose `fallback_poller.go` latches state edges on a buffered channel and cannot lose them. Behavior-only fix; no API change, no new dependencies.

## 1.2.0 - 2026-07-08

- **`QUONFIG_BACKEND_SDK_KEY` / `QUONFIG_ENVIRONMENT` env-var fallbacks (qfg-2qcq.1).** `QuonfigOptions.SdkKey` now falls back to the `QUONFIG_BACKEND_SDK_KEY` env var and `QuonfigOptions.Environment` falls back to `QUONFIG_ENVIRONMENT` when the matching option is left unset, so a service that exports the canonical vars (as `qfg run` and the `fly.*.toml` configs do) can construct with a bare `new Quonfig.Sdk.Quonfig(new QuonfigOptions())`. Precedence is **explicit option > env var** in both cases, matching sdk-go, sdk-node, sdk-python, and sdk-java. In HTTP+SSE (delivery) mode an env-provided `QUONFIG_BACKEND_SDK_KEY` satisfies the SDK-key requirement; in datadir mode an env-provided `QUONFIG_ENVIRONMENT` satisfies the environment requirement. A `QUONFIG_ENVIRONMENT` pin still no-ops (with the existing WARN) in delivery mode, where the SDK key determines the active environment. The `QuonfigOptions.EnvLookup` override still governs the lookup for tests/DI. Additive and backward-compatible; no new dependencies.

- **Runtime telemetry is now actually emitted (qfg-gxm6).** The live client never constructed its `TelemetryReporter`, so sdk-net emitted **zero** runtime telemetry of any kind — evaluation summaries, context shapes/examples, and the qfg-41nh.18 failover counters were all dead despite the collectors and wire format being correct and unit-tested. The client now constructs the reporter (and feeds the evaluation-summary + context collectors from the real evaluation call sites) whenever telemetry is enabled and a sender resolves — delivery mode with an SDK key, or an injected sender. It starts a periodic flush loop and flushes once more on `CloseAsync`/dispose, mirroring sdk-java's reporter lifecycle. Confidential / encrypted values are redacted to the cross-SDK `*****<md5-prefix>` reportable marker before any summary reaches the wire (never plaintext). The existing opt-out is honored: `CollectEvaluationSummaries = false` **and** `ContextUploadMode = None` constructs no reporter and emits nothing. New additive options mirror sdk-java for DI/testing and tuning: `TelemetrySender`, `TelemetryInitialDelay` (8s), `TelemetryFlushInterval` (60s), `TelemetryMaxInterval` (600s). No new dependencies.

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
