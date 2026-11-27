# Building Tesserafin

This is the contributor guide for building, testing, and linting the Tesserafin
server from a fresh clone. It is a development document — for installing a
release, see the [installation guide](docs/install.md).

Everything below is exactly what Continuous Integration runs, so a change that
is green locally is green in CI for the same reasons.

## Prerequisites

- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet).**
  [`global.json`](global.json) pins the SDK band to `10.0.0` with
  `rollForward: latestMinor`, so any `10.0.x` SDK at or above `10.0.0` is
  accepted and anything older is refused. Check yours with `dotnet --version`.
- **`ffmpeg` and `libfontconfig1` on the host** for the media-encoding and
  playback tests, and for the SkiaSharp image tests. Without them those tests
  fail on an otherwise-sound tree — the hosted CI job installs exactly these
  two packages before running the suite, and so does [`Dockerfile.ci`](Dockerfile.ci).
- **Docker** (optional) if you want to run the hermetic local gate `./ci/run.sh`.
- An IDE is optional: Visual Studio 2022+, or VS Code with the workspace-
  recommended extensions.

Supported on all major operating systems except FreeBSD.

## Reproducible build

The build is reproducible because every input is pinned:

- **SDK** — `global.json` (see above).
- **NuGet packages** — [Central Package Management](Directory.Packages.props):
  every package version is declared once in `Directory.Packages.props`, so no
  project can float a version. [`nuget.config`](nuget.config) pins the package
  sources.
- **Analyzers and warnings** — [`Directory.Build.props`](Directory.Build.props)
  sets `TreatWarningsAsErrors` and, in `Debug`, `AnalysisMode=AllEnabledByDefault`
  plus the repository's own `Tesserafin.CodeAnalysis` analyzer, `stylecop.json`,
  and `BannedSymbols.txt`. A warning is a build failure.

From a clean checkout:

```bash
git clone https://github.com/tesserafin/tesserafin.git
cd tesserafin
dotnet restore Tesserafin.sln     # resolves every project from the pinned sources
dotnet build Tesserafin.sln       # warnings are errors; analyzers armed in Debug
```

> Purge `bin/` and `obj/` before trusting a build — stale output can skip
> analyzers. `./ci/run.sh` does this for you and refuses to build if the purge
> did not complete; see [`docs/local-ci.md`](docs/local-ci.md).

## Test

```bash
dotnet test --solution Tesserafin.sln --filter 'Category!=Smoke'
```

Tests run on Microsoft.Testing.Platform (`global.json` sets `test.runner`), so
`dotnet test` takes `--solution`/`--project`, `--report-trx` and `--coverlet`
instead of the VSTest `--logger`/`--collect` switches.

`Category=Smoke` is the optional, heavier ffmpeg/HLS stage; it is deliberately
excluded from the core gate and run on its own with `./ci/smoke.sh`. The core
suite includes the OpenAPI contract-drift check and the provider-authentication
structural audit, so a green run has already proven those.

## Lint / format

```bash
dotnet format --verify-no-changes --verbosity minimal
```

This is the exact command the `Format` CI check runs. It fails if any file is
not formatted or is missing required documentation. Run `dotnet format` without
`--verify-no-changes` to fix issues in place.

## The authoritative local gate

```bash
./ci/run.sh
```

`./ci/run.sh` builds the `tesserafin-ci` image from `Dockerfile.ci` and runs the
full `dotnet build` + `dotnet test` over `Tesserafin.sln` inside a container,
bind-mounting the current checkout so it tests exactly what is on disk. It purges
every `bin/` and `obj/` first and refuses to proceed if that purge fails. This
is the recommended pre-push check because it reproduces the hosted environment
including the native dependencies. See [`docs/local-ci.md`](docs/local-ci.md).

## What runs in CI

Every pull request against `master`, and every push to `master`, runs the
GitHub Actions workflows in [`.github/workflows/`](.github/workflows/). The
following are **required status checks** on `master` (branch protection is
enabled, with strict up-to-date branches, linear history, and admin
enforcement), so a pull request cannot merge until they are green:

| Check | Workflow | Proves |
| --- | --- | --- |
| `run-tests (ubuntu-latest)` | `ci-tests.yml` | build + every test project except the real-server suite |
| `integration-tests (ubuntu-latest)` | `ci-tests.yml` | `Tesserafin.Server.Integration.Tests` (real-server end-to-end suite) |
| `format-check` | `ci-format.yml` | `dotnet format` is clean |
| `ABI - Controls/BASE/HEAD/Difference` | `ci-compat.yml` | no unintended public-ABI break |
| `OpenAPI - Controls`, `Semantic Diff` | `openapi-pull-request.yml` | OpenAPI contract compatibility |
| `NuGet - Dependency Audit` | `dependency-audit.yml` | no vulnerable/banned packages |
| `Current tree`, `Complete history and baseline`, `Controls`, `Sanitized report` | `secret-scan.yml` | no leaked secrets |
| `SDK Provenance` | `sdk-provenance.yml` | build SDK matches `global.json` |
| `Analyze csharp`, `Analyze actions`, `CodeQL` | `ci-codeql-analysis.yml` | static security analysis |

`Tests` (`ci-tests.yml`) is the exception to the push rule: it runs on pull
requests and by hand only. `master` moves only by fast-forward to a pull
request head that passed it, so a push run would re-test the same tree.
`ci-test-caches.yml` seeds on `master` the NuGet and native-package caches its
pull-request jobs restore.

## Running the server

See the **Development → Running the server** section of the [README](README.md).
