# sdk-net

.NET SDK for [Quonfig](https://quonfig.com) — feature flags and configuration as files in git.

> **Status:** Bootstrap scaffold (v0.0.1). The public client surface, evaluation engine, transport,
> SSE watchdog, supervisor, telemetry, ASP.NET Core integration, and ILogger / Serilog filters all
> land in subsequent beads under epic **qfg-zp7i**. See `project/plans/sdk-net.md` for the full plan.

## Artifacts (planned)

This repo will publish four NuGet packages in lock-step from a single tag.

| Package                              | Purpose                                                                                                |
|--------------------------------------|--------------------------------------------------------------------------------------------------------|
| `Quonfig.Sdk`                        | Core SDK — config evaluation, HTTP+SSE transport, datadir loader, telemetry.                           |
| `Quonfig.Sdk.AspNetCore`             | DI + `IHostedService` + per-request `ContextSet` via `HttpContext`.                                     |
| `Quonfig.Sdk.Extensions.Logging`     | `ILoggerProvider` filter — dynamic log levels via the BCL `Microsoft.Extensions.Logging` pipeline.      |
| `Quonfig.Sdk.Serilog`                | Serilog `LoggingLevelSwitch` provider for dynamic log levels.                                            |

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
