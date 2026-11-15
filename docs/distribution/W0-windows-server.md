# W0 — Native Windows server distribution

Architecture freeze for the `win-x64` Tesserafin Server distribution that
Tesserafin 1.1 blocks on. Tracker: #234. Linux precedent: #225 / #228 and
[L0-linux-packages.md](L0-linux-packages.md). FFmpeg precedent:
[F0-ffmpeg-runtime.md](F0-ffmpeg-runtime.md).

**This document decides. It does not implement.** No Windows packaging, no
Windows Service integration, no Windows FFmpeg runtime and no installer ships
with it. The only code it lands is the disposable probe harness under
`ci/windows/w0/` and the workflow that runs it, and nothing either produces is
ever a production input.

Measured on `origin/master` at `f7479d504ffeb2e895dcafc454ce55420ecc7fec`,
server version `1.0.0`.

---

## 1. Frozen owner ruling

Owner ruling, 2026-08-12, restated here so the boundary cannot drift while
W1–W5 are implemented:

* Tesserafin 1.1 **must** include a native `win-x64` server distribution. It is
  a blocking deliverable, not a stretch goal.
* Tesserafin Server runs **natively** on Windows. Tesserafin Web **in a
  browser** is its interface.
* There is **no** native Windows desktop client — no Electron, no WinUI, no
  WebView shell. Any proposal that produces one is out of scope by ruling, not
  by preference.
* `win-arm64` may be anticipated architecturally but is **not** promised for
  1.1. See §14.
* The Android → iOS → TV native-client sequence is unchanged by this work.

The Linux contract accepted in #225 / #228 is the standard, not a ceiling to
relax: one bundled package per architecture, a Tesserafin-owned FFmpeg runtime
built from pinned source, bit-for-bit reproducibility across two independent
clean builds, strict provenance manifests, separate licence boundaries and a
corresponding-source sidecar.

---

## 2. Measured baseline

Every claim in this section was produced by
`.github/workflows/w0-windows-probe.yml` on a GitHub-hosted `windows-latest`
runner. The runner image label, image version, architecture, PowerShell versions
and installed .NET SDKs are captured verbatim in the `host.identity` fact of
`baseline.json` in that run's `w0-evidence-baseline` artifact, so the baseline is
attributable to an exact image rather than to "a Windows machine".

The probe deliberately never asserts a single green line where a pair is
available. Where a measurement passes only because the runner supplied
something, the negative control that removes it sits beside it.

### Measured values

| | |
| --- | --- |
| runner image | `win25-vs2026`, version `20260803.193.1` |
| OS | Windows NT 10.0.26100, x64 |
| PowerShell | 7.6.4 (pwsh) and 10.0.26100.33158 (Windows PowerShell) |
| .NET SDKs present | 8.0.129 – 10.0.400 (11 SDKs) |
| `ffmpeg` on the image | **none** |
| self-contained publish | 522 files, 284,254,832 bytes, ~73 s |
| `tesserafin.exe` | PE x64, self-contained confirmed from the delivered tree |
| server startup | `Startup complete` in ~22 s from a cold state directory |
| `/` | `302`, `Location: web/` |
| `/web/index.html` | `200`, references the hashed `main.tesserafin…bundle.js` |
| `/health` after `/` | reaches `200` ~4.3s later -- a measured lag, not a failure |
| Web payload digest | matches the pinned `4148c4bc…` |
| stock server under the SCM | **error 1053** after 7 s, 0 orphaned processes |
| Generic Host spike under the SCM | `IsWindowsService()=True`, `StartAsync` and `StopAsync` both ran |
| FFmpeg spike | 95/95 fork patches applied, ffmpeg 7.1.4 + ffprobe, PE x64, smoke pass (139,833 bytes) |
| FFmpeg import closure | Windows system DLLs and `api-ms-win-crt-*` only |
| MSI reproducibility | **not** reproducible across two identical builds |
| ZIP reproducibility | reproducible |
| WiX | 6.0.2+b3f3403, MS-RL (§5.4) |

### 2.1 The existing full test suite on Windows

The `Tests` workflow already carries a `windows-latest` leg, reachable only by
`workflow_dispatch`. Dispatching it on the frozen master (run `31593891107`)
did **not** yield a Windows verdict:

* `run-tests (macos-latest)` failed one test of 201 —
  `OpenApiXmlDocumentationOrderTests.XmlDocumentationFiles_ReadsTopLevelXmlInCanonicalOrder`,
  `Assert.Equal() Failure: Collections differ at index 0`;
* the matrix sets `fail-fast: true`, so the Linux and Windows legs were
  cancelled mid-test-run.

Two things follow, and they are separate. First, the Windows runner itself is
sound: `Set up job`, `checkout`, `setup-dotnet` and the NuGet cache all
succeeded before the cancellation, which retires "no native Windows x64 runner
can execute" as a blocker. Second, the Windows test verdict cannot come from
that workflow while the macOS leg fails first, so the W0 probe workflow runs the
**identical** command (`dotnet test --solution Tesserafin.sln --configuration Release
--filter 'Category!=Smoke'`) in its own `windows-tests` job and reports it as W0
evidence.

The macOS failure is a **pre-existing defect on master and outside W0 scope**.
It is recorded here because it is collation-shaped and therefore a named risk
for Windows: an OpenAPI contract that depends on filesystem enumeration order
would produce a different document on a different host, and the `windows-tests`
job is what says whether Windows agrees with Linux.

### 2.2 Self-contained publish

`dotnet publish Tesserafin.Server/Tesserafin.Server.csproj --configuration
Release --runtime win-x64 --self-contained true` succeeds on stock master with
no production change. `Tesserafin.Server.csproj` already declares
`AssemblyName=tesserafin`, so the apphost is `tesserafin.exe`, and it already
declares `ApplicationIcon=Tesserafin.Server.ico`.

The probe does not take "self-contained" from the build log. It reads the
delivered tree: `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll` and
`System.Private.CoreLib.dll` must be present and
`tesserafin.runtimeconfig.json` must declare no shared framework. It reads the
architecture out of the COFF machine word of `tesserafin.exe` and of every
delivered native library, so a wrong-architecture publish is rejected on its
bytes rather than on whether it happened to start.

The full publish inventory — every delivered path, its SHA-256, the tree
digest, the native-library list with per-file architecture — is in the
`publish.selfcontained` fact.

### 2.3 Startup, hostile paths and relocation

The probe copies the publish into a directory whose name contains spaces,
accented Latin, an em dash and CJK, starts it there, then **moves the same
tree** to a different depth and starts it again. Both runs use fresh, isolated
`--datadir`, `--configdir`, `--cachedir` and `--logdir`, because a probe that
shares state cannot distinguish a fresh install from an upgrade and W0 needs
both answers.

`StartupOptions.cs` already carries every path argument the distribution needs:
`-d/--datadir`, `-c/--configdir`, `-C/--cachedir`, `-l/--logdir`,
`-w/--webdir`, `--nowebclient`, `--ffmpeg`, `--service`, `--package-name`,
`--published-server-url`. Nothing has to be added for W2 to place state
correctly.

Readiness is `/` answering, not `/System/Info/Public` answering: the latter is
served by the startup `SetupServer` long before the application host is up. A
cold first start creates the database and applies every migration, so the probe
allows 600 s: the first hosted run timed out at 180 s while migrations were
still running and read as "this build does not start", which is not what was
measured.

**Readiness has three separate traps, and W0 walked into all three.** They are
written out because W2's acceptance suite inherits every one of them, and each
made a *failing* server look like a *starting* one or the reverse.

1. **The port is not in Kestrel configuration or the environment.**
   `ApplicationHost` reads `NetworkConfiguration.InternalHttpPort`, persisted as
   `<configdir>/network.xml` (store key `network`, default 8096), and the server
   logs only `Kestrel is listening on 0.0.0.0` with no port in it. Seeding the
   file is a request, not a guarantee, so the probe enumerates the **process's
   own listening TCP ports**. Without that, a run where the server logged
   `Startup complete` in 22 s was recorded as "did not start".
2. **A redirect is an answer.** `Invoke-WebRequest -MaximumRedirection 0` raises
   "maximum redirection count exceeded" on a 3xx, a class `-SkipHttpErrorCheck`
   does not cover — and `/` redirects to the web client, so that was every run.
   The probe uses `HttpClient` with `AllowAutoRedirect` disabled, which returns
   the 302 as a value.
3. **The startup `SetupServer` binds the real port early and answers *every*
   path with `503` and `{"status":"starting",…}`.** A probe that accepts "any
   status" therefore measures the setup server. Readiness is "`/` answers with
   something other than `503`"; requiring `200` would be wrong in the other
   direction, since that asserts a routing decision rather than liveness.
4. **A live process is part of readiness, not a single sample.** Even the `503`
   rule was not enough, and neither was checking `HasExited` once at the top of
   the wait loop. The setup server answered with a non-`503` in the same instant
   the application host was tearing itself down over `FfmpegException`, so the
   probe caught the one response the dying process managed to serve before it
   died — and the **no-FFmpeg negative control reported a started server** while
   its own log showed `FfmpegException: Failed to find valid ffmpeg` and the host
   disposing. A negative control that passes for the wrong reason is worse than
   no control at all. Readiness now requires the process to still be running
   **three seconds after** it answers, not merely at the instant it answers.

**And `/health` lags `/`.** Once `/` answers `302` the health report still says
`{"status":"starting","version":"1.0.0","database":"healthy"}` for a further
interval, because the startup tasks have not finished. The probe measures that
lag rather than sampling once and calling the endpoint broken. **It is a contract
detail W4 needs:** a service readiness gate or an installer that keys on `/`
will declare the server ready early.

**The port is discovered from the process, not assumed.** The listening port is
not a Kestrel setting and not an environment variable: `ApplicationHost` reads
`NetworkConfiguration.InternalHttpPort`, persisted as `<configdir>/network.xml`
(store key `network`, default 8096). Seeding that file is a request rather than a
guarantee, and the server logs only `Kestrel is listening on 0.0.0.0` with no
port, so the probe asks the process itself which TCP ports it bound. Without
that, a run where the server logged `Startup complete` in 22 s was still recorded
as "did not start", because the prober was knocking on a port nothing was
listening on. A distribution probe that cannot tell those two apart is worthless,
and **W2's acceptance suite must discover the port the same way.**

Two things this exercise turned up in the **server**, neither of which W0 fixes:

* `BaseApplicationPaths.MakeSanityCheckOrThrow` writes a marker file into each
  of the configuration, cache, log and data directories and refuses to start if
  it finds the wrong one. It is a good guard — it is what caught a mis-split
  path immediately rather than fifty lines later — but the markers are still
  named **`.reefin-config`, `.reefin-log`** and so on, from before the rename.
  On Linux they are hidden dotfiles inside package-managed directories. On
  Windows there are no hidden dotfiles by convention, so they become visible
  files with the old product name inside `%ProgramData%\Tesserafin\Server\`.
  W2 should rename them, and must do so with a migration that recognises the old
  marker, or every existing installation fails its own sanity check on upgrade.
* Those directories are therefore not interchangeable and not shareable. The
  layout in §9.1 gives each its own directory for that reason.

### 2.4 Endpoints and the Web payload

`/` and `/health` (mapped in `Tesserafin.Server/Startup.cs`) are checked from
the **relocated** tree, never from the build tree. The Web bootstrap assertion
is deliberately stricter than a 200: the entry document must reference the
hashed `main.tesserafin…bundle.js`, so a 200 that returns the setup page fails
it.

The payload itself is the pinned one. `ci/package/assemble-payload.sh` obtains
it with `docker create` + `docker cp` from the Linux OCI image
`ghcr.io/tesserafin/tesserafin-web-assets@sha256:6150380052c8…`
(web commit `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a`) and verifies it against
`WEB_PAYLOAD_SHA256`
=`4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f`.

**A `windows-latest` runner cannot run Linux containers, so that step is not
reusable as written.** The W0 workflow proves the payload can nonetheless reach
Windows without weakening provenance: a Linux job extracts it, verifies the
same digest with the same `pkg_tree_digest`, and hands it over. Using an Actions
artifact for that handover is acceptable **only** because this is probe
evidence; §8 forbids it in production.

### 2.5 FFmpeg — the runner has none, and that is the whole point

`MediaEncoder.SetFFmpegPath` resolves in order: `--ffmpeg` / environment, then
`EncoderAppPath` in `encoding.xml`, then bare `ffmpeg` on `PATH`. On success it
writes the validated path back as `EncoderAppPathDisplay`. The probe reads that
file — the server's own answer to "which binary did you choose" — rather than
grepping the log.

To make the answer falsifiable the probe copies the encoder to a directory that
is **not** on `PATH`, passes it with `--ffmpeg`, and asserts the recorded path
is that copy. A `PATH` fallback would record a different string and fail.

The probe expected to have to record the runner's preinstalled encoder as an
unacceptable baseline dependency. **It could not: `win25-vs2026` ships no
`ffmpeg` at all.** `Get-Command ffmpeg.exe` resolves to nothing.

That single fact is the sharpest thing in this document. `FfmpegException` is
fatal at startup, so the stock Windows server is **unstartable out of the box on
a clean Windows host** — not degraded, not missing hardware acceleration,
unstartable. It is why W1 is a blocking deliverable rather than a refinement,
and it is why no ordering of W1–W5 that ships a server before an encoder is
viable.

The encoder the baseline actually uses is therefore the one **W0 built itself**
(§7.2): compiled on a Windows runner from the pinned upstream commit by the
native MSYS2 toolchain, handed to the baseline job as a short-retention probe
artifact. That keeps the entire chain inside W0 and inside the pin rather than
depending on whatever an image happens to preinstall. It is emphatically **not**
the accepted Tesserafin Windows runtime — it is bounded by `--disable-autodetect`
and carries none of the required component closure — and the evidence says so in
a field rather than in a footnote.

The paired negative control still runs: `PATH` scrubbed of every
`ffmpeg.exe`-bearing directory and no `--ffmpeg` given, capturing the fatal
startup verbatim. What it captured:

```
System.ComponentModel.Win32Exception (2): An error occurred trying to start process 'ffmpeg'
[ERR] MediaEncoder: FFmpeg: Failed version check: ffmpeg
Tesserafin.Common.FfmpegException: Failed to find valid ffmpeg
   at Tesserafin.Server.Core.ApplicationHost.RunStartupTasksAsync()
   at Tesserafin.Server.Program.StartServer(...)
[INF] ApplicationHost: Disposing CoreAppHost
```

**And the process then exits with code 0.** That is a finding W3 has to act on,
not a curiosity. A Windows service whose process dies during startup but returns
0 tells the Service Control Manager the service **stopped normally**: no failure
action fires, no configured restart happens, and nothing is written to the event
log to say the server never came up. An operator would see a service that is
simply not running, with no recorded reason.

W0 does not fix this — the service host boundary is W3's (§4) — but W3's exit
contract must map a fatal startup failure onto a **non-zero** exit code so the
SCM can see it, and W3's acceptance must include that negative control. Recorded
here because it is invisible until the server runs under the SCM, and by then it
looks like an installer defect rather than an exit-code contract.

### 2.6 Shutdown, deferred rather than faked

Graceful console shutdown is **not measurable from this harness**, and W0 says so
instead of reporting a number that means nothing. The server is started sharing
the runner's console, so it has no window and `CloseMainWindow` is a no-op;
delivering `CTRL_C_EVENT` to a shared console group would kill the probe along
with it. Every stop therefore degrades to a kill after the timeout, and a kill
measures the operating system, not the server.

The number W3 actually needs — the **worst-case** stop, with a transcode running,
under the SCM — is a W3 measurement. What W0 does establish is that the mechanism
exists: the Generic Host spike ran `IHostedService.StopAsync` under a real
`sc stop` (§4). The Linux side already shows post-transcode shutdown taking tens
of seconds, so W3's stop timeout must come from that worst case and never from a
quiescent server.

### 2.7 The server is not test-green on native Windows

Running the identical `Tests` command on `windows-latest` produces, on both this
branch and its base commit, exactly:

| | count |
| --- | --- |
| total | **3,992** |
| passed | **3,911** |
| failed | **72** |
| skipped | **0** |
| `dotnet test` exit code | **1** |

Read off the `Counters` element of all 21 TRX files, not estimated. An earlier
draft of this section said "roughly 3,560", which was both approximate and
wrong, and gave a family breakdown (67 + 2 + 2) that did not add up to its own
72. The corrected figures below are measured and do.

The 72 failures fall in four assemblies:

| Assembly | Failed |
| --- | --- |
| `Tesserafin.Server.Tests` | 69 |
| `Tesserafin.Controller.Tests` | 1 |
| `Tesserafin.MediaEncoding.Tests` | 1 |
| `Tesserafin.Server.Integration.Tests` | 1 |

Every other assembly is green. The failures fall into three families, and
**68 + 2 + 2 = 72** with nothing left over — the gate rejects any failure that
matches none of them:

1. **Log-forging guards under CRLF (the 68-test bulk).** Every
   `…LogTests.*_WritesExactlyOnePhysicalRecord` assertion in
   `Tesserafin.Server.Tests.LogForging` fails on Windows. These tests count
   *physical* log records, and `Environment.NewLine` is `\r\n` on Windows and
   `\n` on Linux, so a hostile value containing a bare `\r` splits differently
   than the Linux-tuned assertion expects. This family is security-adjacent —
   it is the log-injection guard — so "the test is Linux-shaped" must be
   *proven* rather than assumed before it is written off. Whether the guard or
   the assertion is wrong on Windows is a real question W0 does not answer.
2. **Child-process standard input.**
   `TranscodingJobStopTests.Stop_ProcessReadsQFromStdin_ExitsGracefullyWithoutBeingKilled`
   and `FfmpegProcessRunnerTests.RunProbeAsync_StandardInput_IsWrittenToChildProcess`.
   This is the `q`-on-stdin graceful-stop path, which is exactly the mechanism a
   Windows Service stop has to rely on to end a transcode cleanly. **W3 depends
   on this working**, so it is not a cosmetic failure.
3. **Cross-host OpenAPI contract.**
   `OpenApiXmlDocumentationOrderTests.XmlDocumentationFiles_ReadsTopLevelXmlInCanonicalOrder`
   and `OpenApiContractTests.CommittedContract_MatchesRunningServer`. This is the
   same family as the macOS failure in §2.1: the canonical XML documentation
   order does not survive a third host, so the generated contract diverges.

All 72 are **pre-existing on the base commit**, and that is asserted rather than
asserted-about: the probe workflow runs the identical command on the head and on
the pinned base commit, keeps both TRX sets, and compares the failing-test
**set** — not its size. The two sets are identical, with nothing failing only on
the head and nothing failing only on the base.

That comparison is the gate. Adding a failure, removing one, renaming one, or
producing one that matches none of the three families above fails the job. The
suite itself is never reported as passing: it is red on both trees, and what is
asserted is that an architecture-only change leaves it red in exactly the same
places. `ci/windows/w0/Compare-W0TestBaseline.ps1` carries the logic and its own
negative controls.

They are recorded here because a Windows distribution whose server fails 72
tests on the platform it is being distributed for is not a distribution anyone
should sign. **Closing families 2 and 3 is a precondition for W3 and W5
respectively**, and family 1 needs a ruling on whether the guard or the
assertion is at fault. This deserves its own tracked issue; W0 names it rather
than absorbing it.

---

## 3. Gap inventory

The four buckets #234 asks for, plus blocked and deferred. Each fact in
`baseline.json`, `service.json` and `installer.json` carries exactly one of
these, enforced at recording time.

### Already working on stock master

| | |
| --- | --- |
| `dotnet publish --runtime win-x64 --self-contained true` | succeeds unchanged |
| `AssemblyName=tesserafin` → `tesserafin.exe` | already correct |
| `ApplicationIcon` | already present |
| every `--datadir`/`--configdir`/`--cachedir`/`--logdir`/`--webdir`/`--ffmpeg` argument | already exists |
| `/health` | already mapped |
| `--ffmpeg` actually overrides `PATH` | proven from `encoding.xml` |
| server startup, relocation, `/`, `/web/index.html`, `/health` | all pass, given an FFmpeg (§2.5) |
| stock server under the SCM | fails cleanly with error 1053, no orphan (§4) — the exact, bounded gap W3 closes |

### Working only with a test-host dependency

| | |
| --- | --- |
| Web bootstrap | requires a payload extracted by a Linux job, because Windows cannot run the Linux container the packaging path uses |

### Missing

| | |
| --- | --- |
| Windows Service host boundary | no `UseWindowsService`, no `WindowsServiceLifetime`, no `Microsoft.Extensions.Hosting.WindowsServices` anywhere in the tree |
| Tesserafin-owned Windows FFmpeg runtime | nothing in `ci/ffmpeg/**` targets Windows, and no Windows host ships one — the server cannot start without it |
| renamed directory sanity markers | still `.reefin-*`, and visible files on Windows (§2.3) |
| Windows packaging | no MSI, no WiX, no Inno, no MSIX, no portable ZIP |
| Windows lifecycle acceptance | no install/upgrade/repair/uninstall suite |
| Windows signing configuration | none, by ruling (§11) |
| daemonless Web-payload acquisition on Windows | the Linux path needs a Linux container runtime |

### Not a gap in the distribution, but red on the platform

72 of 3,992 tests fail on native Windows, identically on this branch and its
base (§2.7). None of them is a missing distribution surface, so none belongs in
the table above — but two of the three families are preconditions for W3 and W5.

### Blocked

Nothing in W0 is blocked. Every hard-stop condition in the tracker was tested
and none fired. §13 lists the risks that could still block W1–W5.

### Deliberately deferred

`win-arm64` (§14); an automatic updater; any hardware-acceleration **runtime**
claim; `libplacebo` and the Vulkan shader toolchain, which F0 already defers on
Linux and which Windows inherits.

---

## 4. Selected service-host architecture

**Selected: direct .NET Generic Host Windows Service integration in
`Tesserafin.Server`.**

This is a measurement, not a preference. The probe establishes both halves:

1. The unmodified `tesserafin.exe`, registered with the SCM and started,
   **fails with error 1053 after 7 seconds** — "the service did not respond to
   the start or control request in a timely fashion". That is the SCM's verdict
   on a plain console executable that never calls
   `StartServiceCtrlDispatcher`. The probe also recorded **zero orphaned
   `tesserafin` processes**, which matters: an orphan would be worse than a clean
   failure, because an installer would then be uninstalling a service whose
   process still holds the database. This is a missing boundary in the
   **server**. No installer technology can paper over it, and treating it as an
   installer problem is the specific mistake #234 names.
2. A disposable, throwaway Generic Host with `AddWindowsService`, published
   self-contained `win-x64` and registered with the SCM, was driven through real
   `sc start` and `sc stop`. It reported `IsWindowsService() == True` and ran
   `IHostedService.StartAsync` **and** `StopAsync` — `lifecycleObserved = true`.
   That is what makes "the smallest maintainable design is sufficient" a fact
   rather than an expectation.

A dedicated first-party service host is therefore **rejected**: it would add a
second executable, a second lifetime and a second failure surface to obtain
behaviour the existing host already supports. An opaque third-party service
wrapper (NSSM, WinSW and similar) is **rejected outright** — it inserts an
unaudited binary between the SCM and the server, owns the stop semantics that
determine whether a transcode is truncated, and adds a redistribution and
provenance obligation for a component Tesserafin does not build. #234 permits an
exception only for an exceptional recorded reason; none exists.

### The service contract

W3's contract must also make a **fatal startup failure visible to the SCM**: the
unmodified server exits 0 when `FfmpegException` kills startup (§2.5), which the
SCM reads as a normal stop. A non-zero exit on fatal startup is part of the
boundary W3 closes, and part of what its acceptance must prove.


| | |
| --- | --- |
| service name | `Tesserafin` |
| display name | `Tesserafin Server` |
| description | `Tesserafin media server. Manage it at http://localhost:8096.` |
| startup mode | `Automatic (Delayed Start)` |
| executable | `%ProgramFiles%\Tesserafin\Server\tesserafin.exe` |
| arguments | `--service --configdir "%ProgramData%\Tesserafin\Server\config" --datadir "…\data" --cachedir "…\cache" --logdir "…\log" --webdir "%ProgramFiles%\Tesserafin\Server\web" --ffmpeg "%ProgramFiles%\Tesserafin\Server\ffmpeg\ffmpeg.exe"` |
| stop timeout | 120 s, derived from the worst observed shutdown, not from §2.6 |
| recovery | restart after 60 s on first and second failure; no action on the third, so a crash loop is visible rather than hidden |
| logging | the application's own rolling file sink under the log directory, plus the Windows Event Log for service-lifecycle events only |
| identity | `NT SERVICE\Tesserafin` (§9) |

`--webdir` and `--ffmpeg` are **always** passed explicitly, exactly as the Linux
unit does, so the service can never silently fall back to a `PATH` encoder or to
a stale web directory. `--nowebclient` is never used.

**No service implementation lands in W0-A.** W3 implements it.

---

## 5. Installer technology

### 5.1 Decision

**Selected: a per-machine MSI built with the WiX toolset, plus the mandatory
portable ZIP.**

### 5.2 The experiment

The tracker forbids choosing by preference and treats "installer selection
lacks a real lifecycle experiment" as a hard stop, so `probe-installer.ps1`
makes the candidates do the work on a native Windows host. Both MSI and the
portable ZIP are driven through the same sequence with a disposable payload:

unattended clean install → service start attempted with no ACL grant → read and
execute granted to the service SID → service start attempted again →
retained-data sentinel written → in-place major upgrade → deliberate file
removal → repair → refused downgrade → silent uninstall → retained-data re-read.

The two-phase service start is not decoration. The first hosted attempt started
the service **inside** the install transaction and the install failed with
"Error 1920. Service … failed to start", which MSI rolled back to 1603 — hiding
install, upgrade, repair and uninstall behind a single identity problem. Splitting
it makes the identity question answerable on its own (§9.2).

The exact WiX version used is recorded in the evidence, and its licence is read
**out of the resolved package** rather than asserted from memory — see §5.4,
which is the one place where reading the artifact instead of trusting a summary
changed the answer.

### 5.3 Scoring

| Criterion | MSI (WiX) | Portable ZIP + first-party script | MSIX | Inno Setup |
| --- | --- | --- | --- | --- |
| unattended clean install | yes, `msiexec /i /qn` | yes | **no** — needs a trusted certificate | yes |
| native Windows Service lifecycle | yes, `ServiceInstall`/`ServiceControl` inside the transaction | only by shelling to `sc.exe` | restricted packaged-service model | only by shelling to `sc.exe` |
| deterministic in-place upgrade | yes, `MajorUpgrade` + stable `UpgradeCode` | script-defined | yes | weak |
| repair | yes, `msiexec /f` | **no** | yes | **no** |
| rollback on failed upgrade | yes, transactional | **no** | yes | **no** |
| retained-data policy | expressible in the package (`Permanent` component) | script-defined | constrained by the package container | script-defined |
| per-machine least-privilege ACLs | yes | yes | **no** — constrained identity model | yes |
| Authenticode support | yes | yes (over the ZIP contents) | mandatory, not optional | yes |
| silent uninstall | yes, `msiexec /x /qn` | script-defined | yes | yes |
| long-term automation / maintenance | `wix` is a `dotnet tool`, pinned like any package | trivial | tooling churn | external installer, not a `dotnet tool` |
| open-source redistribution | MS-RL source; the maintenance fee attaches to the build tool, never to its output (§5.4) | n/a | n/a | permissive, but see below |
| reproducible unsigned output | **measured** — payload and every authored table deterministic; bounded exception for two container fields (§5.6) | **measured** — bit-for-bit | not reached | not reached |

### 5.4 The WiX licence, read rather than assumed

`wix` 6.0.2 declares `license type="file"` in its `.nuspec`, not an SPDX
expression, so the probe copies the referenced text into the evidence. What it
says matters, and a summary would have got it wrong in both directions:

* the Software is licensed under the **Microsoft Reciprocal License**, an
  OSI-approved open-source licence;
* layered on top is an **Open Source Maintenance Fee** agreement that applies
  **only to the Binary Release** — the pre-compiled `wix` tool — and **only to
  Users that generate revenue by the Software**. Non-revenue-generating use is
  exempt by name;
* the agreement states explicitly that the Fee "is not a license fee", that on
  any conflict "the OSI License shall govern", and that it "does not restrict
  the User from obtaining or redistributing binaries from other sources or
  self-compiling them".

Two consequences for this decision. First, Tesserafin's use is exempt as
written. Second — and this is the part that survives any future change in the
project's revenue position — **the fee attaches to the build tool, never to its
output.** No obligation propagates to the MSI that WiX produces or to anyone who
downloads it. Even in the worst case the remedy is documented in the agreement
itself: compile the toolset from MS-RL source. That is a bounded, first-party
mitigation rather than a dependency Tesserafin cannot escape.

It is still recorded as a **watch item** for W4: the toolset licence is now
something a build must pin and re-read, exactly like a component checksum.

### 5.5 Rejections, on recorded facts

**MSIX** is rejected and the experiment is *unperformable inside W0's
invariants*, which is a stronger statement than "not performed": an MSIX package
cannot be installed unattended unless it is signed by a certificate the machine
trusts, and W0 is forbidden to create any certificate or signing secret.
Independently, MSIX services are restricted to packaged Windows services with a
constrained identity model and cannot express the per-machine ACL grants and
arbitrary `%ProgramData%` layout this distribution needs.

**Inno Setup** is rejected on format properties, not on taste. It has no native
Windows Service installation — a service is registered by shelling out to
`sc.exe` from `[Run]`, which is the first-party-script candidate wearing a
different wrapper — no repair mode, and no transactional rollback of a failed
upgrade. Three of those are required criteria, so it cannot win regardless of
how an experiment turned out. It is also not preinstalled on the runner image,
and obtaining it would need either an unpinned Chocolatey dependency —
explicitly forbidden — or a pinned third-party download whose only purpose would
be to confirm a disqualification already established.

### 5.6 Reproducible unsigned output, measured — and the one bounded exception

The Linux rule is that an unsigned deliverable must be **bit-for-bit
reproducible**. The MSI is the only artifact in this distribution that does not
reach it, and the honest form of that statement is narrow, not a shrug. An
earlier draft of this section said the difference was "structural, not a bug"
and moved the burden elsewhere. That is a weakening of the standard by
assertion, so the question was reopened and answered by experiment.

#### The first measurement was not measuring WiX

The lifecycle MSI's payload comes from `dotnet publish`, which is not itself
guaranteed deterministic across builds or runners without its own
`<Deterministic>` and path-mapping controls. Two MSIs differing for that reason
would say nothing about the installer format. So a second, **isolated**
experiment builds a probe MSI from a fixed-content payload with a fixed
modification time and a pinned `ProductCode`, twice, into two separate
intermediate folders sharing no compiled object.

(`ProductCode`, not `Id`: in WiX v4 and later `Package/@Id` is an *identifier*
and rejects a GUID with `WIX0014`. The first run of the experiment failed
exactly that way, which is why the attribute is named here from the compiler's
answer rather than from memory.)

#### What two fixed-input builds actually produce

| | result |
| --- | --- |
| raw SHA-256 | `8f608446…` vs `44bab68d…` — **differ** |
| decompiled authored tables | **byte-identical**, 0 diff lines |
| `wix msi transform` between them | `WIX0227: The transform being built did not contain any differences so it could not be created` |
| `Package/@PackageCode` settable | **no** — `WIX0004: unexpected attribute` |
| portable ZIP, two builds | `899d995f…` vs `899d995f…` — **reproducible** |

Three of those lines are the ruling.

`wix msi decompile` reconstructs every authored table — `Directory`,
`Component`, `File`, `Registry`, `ServiceInstall`, `ServiceControl`, the
`Feature` tree — and never re-emits the OLE summary-information stream. The two
decompiled sources are identical, so **every security-relevant table and the
payload are deterministically identical**, with the container fields excluded by
construction rather than by an assumption about which fields differ.

Windows Installer's own transform engine agrees, and this is the stronger
statement: asked to express the difference between the two databases as an MST,
it reports that there **is no difference to express**. That is not the probe
grading its own homework — it is the platform's diff mechanism returning empty.

#### The irreducible fields, enumerated

The residual byte delta is confined to:

1. **the Package Code** (summary-information `PID_REVNUMBER`), which Windows
   Installer defines as identifying a *package instance* and regenerates per
   build. WiX 6.0.2 exposes no attribute to pin it — measured, not assumed: the
   probe compiles a `Package/@PackageCode` every run and records that the
   compiler rejects it with `WIX0004`;
2. **OLE container and stream timestamps** written by the storage layer.

Pinning the Package Code would also be **harmful, not merely impossible**: two
packages with differing content and an identical Package Code are precisely the
condition the installer's caching and repair logic must be able to tell apart.

#### The ruling

The MSI is accepted with an **explicit, bounded architectural exception**:
its payload and every authored table are proven deterministic, and its
irreducible delta is the two container fields named above. This is **not**
"functional equivalence" — nothing about the comparison is behavioural, and no
table is exempted. The exception is bounded by the two instruments above, and
W4 must keep both in its acceptance suite so the boundary cannot silently widen.

The bit-for-bit standard itself is **not relaxed**: the ZIP and the FFmpeg
runtime component remain bit-for-bit reproducible with no exception at all
(§8), and W4 records the input digests in the MSI's provenance so the container
is traceable through its contents. Signing remains a later transformation over
an accepted unsigned digest (§11).

---

## 6. Portable ZIP contract

The ZIP is **mandatory regardless of the installer decision**. It is the
artifact the reproducibility proof runs on and the only form that needs no
administrator.

* Name: `tesserafin-server_<version>_win-x64.zip`.
* One top-level directory, `tesserafin-server_<version>_win-x64/`, so extraction
  never scatters files into the current directory.
* Contents: the self-contained server, the bundled Web payload, the Tesserafin
  FFmpeg runtime, licences, SBOM and the provenance manifest — the same set the
  MSI installs, at the same relative paths.
* **Relocatable.** Proven by moving the tree and starting it again (§2.3); the
  server must bake its own location into nothing.
* Ships **no** state. Configuration, database, cache and logs are always given
  by argument.
* Entries stored with fixed modes and a clamped mtime derived from
  `SOURCE_DATE_EPOCH`, ordered deterministically, so two clean builds produce
  identical bytes.
* Carries a first-party PowerShell script that registers, starts, stops and
  removes the service for operators who prefer the ZIP. That script is a
  convenience over the same contract as §4 — it is **not** a second installer
  and gets no repair, rollback or Add/Remove Programs entry, which are
  properties of the format that no script can add.

---

## 7. Windows FFmpeg source-build design

### 7.1 The pinned tree already has a native Windows mechanism

The accepted Linux runtime — `7.1.4-tesserafin.1`, upstream
`jellyfin/jellyfin-ffmpeg` at `d4590e12452f94d40e413caecb34b08de608353b` — is
**precedent, not a Windows binary input**. Nothing built for Linux crosses over.

Inspecting the pinned tree at that exact commit shows three Windows build
mechanisms:

1. `Dockerfile.win64.in` / `Dockerfile.win64.make` / `docker-build-win64.sh` /
   `cross-win64.meson` — a **mingw-w64 cross build inside `ubuntu:noble`**
   (`FF_TOOLCHAIN=x86_64-w64-mingw32`, `--target-os=mingw32`,
   `--cross-prefix=`). Produces Windows binaries; is not a native Windows build.
2. `builder/` (`images/base-win64`, `variants/win64-gpl.sh`) — also a
   container cross build.
3. **`msys2/build.sh` + `msys2/PKGBUILD/**`, driven by
   `.github/workflows/_meta_win_clang_portable.yaml`, which runs
   `runs-on: windows-latest` under MSYS2 `CLANG64` with
   `mingw-w64-clang-x86_64-toolchain`.** It builds every media dependency from
   source through 36 ordered `PKGBUILD`s via `makepkg-mingw`, applies **the same
   `debian/patches/series` quilt set the Linux build applies**, runs
   `./configure --cc=clang … && make`, and emits `ffmpeg.exe` and `ffprobe.exe`.

Mechanism 3 is a **native Windows source build of the pinned fork**. The
hard-stop condition "the pinned FFmpeg source has no viable Windows-native build
path" therefore does **not** fire, and no Jellyfin release asset, no downloaded
precompiled FFmpeg and no Wine is needed anywhere in the design.

That the Linux and Windows targets share one patch series is the load-bearing
property: the `tonemapx` filter `EncoderValidator` requires — the whole reason
`components.json` pins a fork rather than upstream FFmpeg — arrives on Windows
by the same mechanism it arrives on Linux.

### 7.2 The feasibility spike

`ci/windows/w0/spike-ffmpeg.sh` runs **one disposable native spike** on
`windows-latest` under MSYS2 `CLANG64`, pinned to the same
`msys2/setup-msys2` commit the upstream tree itself pins. It fetches the pinned
commit and **asserts** the resolved SHA, applies the quilt series, configures,
compiles `ffmpeg.exe` and `ffprobe.exe`, records `-version`, `-buildconf`,
encoders, decoders, filters, protocols and hwaccels, inspects PE architecture
and the import table, and runs a software **encode → probe → decode** smoke.

**Result, on `windows-latest`:** all **95** patches in the fork's series applied
cleanly, `ffmpeg` **7.1.4** and `ffprobe` built, both **PE32+ x86-64**, and the
software encode -> probe -> decode smoke **passed** (139,833 bytes encoded,
`mpeg4` video plus native `aac` audio, re-probed and decoded on the same host).

The import closure is worth stating explicitly, because it is the Windows
redistribution question in concrete form. Both executables import **only**
Windows system DLLs and the UCRT forwarders — `KERNEL32`, `USER32`, `GDI32`,
`SHELL32`, `SHLWAPI`, `ole32`, `OLEAUT32`, `WS2_32`, `AVICAP32`, `bcrypt` and
`api-ms-win-crt-*`. There is no MinGW runtime DLL, no `libwinpthread`, no
`libc++`, no OpenSSL and nothing from MSYS2 in the closure. `bcrypt` being
present and no OpenSSL DLL being present is exactly what building with
`--enable-schannel` (§7.3) predicts and an OpenSSL build would not — the TLS
backend decision is corroborated by the closure it was expected to produce
rather than merely declared. A static native build therefore delivers as two
files with no redistributable to ship alongside them, which is a materially
better position than the Linux side's `DT_NEEDED` closure.

It is bounded by `--disable-autodetect` **on purpose**. That proves the build
*mechanism* and stops MSYS2's ambient packages from silently entering the link,
which would make the result unattributable. The full component closure is
**design** in W0 and **build** in W1. The spike is probe evidence and is
explicitly **not** the accepted Windows runtime; its JSON says so in a field.

No downloaded precompiled FFmpeg, no Wine, no system FFmpeg substitution, no
unpinned MSYS2/vcpkg/Chocolatey dependency. **No hardware claim of any kind** —
a hosted runner has no GPU, and the spike enables no hardware backend.

### 7.3 The W1 design

W1 produces `ffmpeg.exe` and `ffprobe.exe` plus their complete DLL closure,
built natively from pinned source, delivering:

* software decode and encode for everything `EncoderValidator._requiredEncoders`
  and `_requiredDecoders` name — `libx264`, `libx265`, `libsvtav1`, `libdav1d`,
  `libvpx`, `libmp3lame`, `libopus`, `libvorbis`, FFmpeg's native AAC;
* the required filters — `libzimg`/`zscale`, `libass`, `libfreetype`,
  `libfribidi`, `libharfbuzz`, `fontconfig`, and `tonemapx` from the fork;
* the protocols the server needs, including `https`;
* Windows hardware capability **compiled** where legally and technically
  supported — `dxva2`, `d3d11va`, `d3d12va`, `amf`, `libvpl` (QSV), and the
  NVIDIA stack via `ffnvcodec`. Compiled capability is not a runtime claim and
  is never advertised as one;
* a capability manifest, SBOM, notices, corresponding source and exact toolchain
  provenance, on the F0 model.

Deltas W1 must apply to upstream's `msys2/build.sh`, each one traceable to an
existing Tesserafin rule:

| Delta | Why |
| --- | --- |
| drop `--enable-lto=thin` | `ci/ffmpeg/ffmpeg-configure.txt` bans LTO: partitioning depends on job count and link order and is the largest single threat to bit-for-bit reproducibility |
| drop `--enable-libfdk-aac`, add `--disable-nonfree` | `components.json` classifies `fdk-aac` nonfree; FFmpeg's native AAC encoder covers the requirement |
| drop `libbluray`, `libopenmpt`, `libtheora`, `libwebp`, `chromaprint`, `fftw`, `gmp`, `libxml2` | already excluded by `components.json` with a recorded reason each; Windows does not get a wider set than Linux by accident |
| `--enable-schannel`, **not** `--enable-openssl` | **Decided in W0, not deferred to W1.** Upstream's own native Windows mechanism at the exact pinned commit builds with `--enable-schannel` (`msys2/build.sh`) and never enables OpenSSL or GnuTLS — this is what the pinned tree's build script does, not an inference about it. The spike now builds with the same flag (§7.2) and its PE import closure is `bcrypt`-only with no OpenSSL DLL, which is what Schannel's use of the platform's native `bcrypt`/`crypt32` predicts and OpenSSL would not. Schannel is also the smaller closure: no third-party TLS library to ship, patch, or track CVEs for on Windows. The cost is accepted explicitly — Linux keeps its own TLS stack, so the two targets do not share one implementation. W1 inherits this decision and does not remake it |
| omit `libva`, `libdrm` | Linux-only by construction |
| **pin the MSYS2 toolchain** | see §7.4 |

`ci/ffmpeg/components.json` gains a Windows architecture dimension rather than a
parallel file, so one document remains the single source of component identity
for every target.

### 7.4 The MSYS2 pinning requirement

`msys2/setup-msys2` pins the **action**; `pacman` is a **rolling repository** and
pins nothing. Two clean builds a week apart can resolve different `clang`,
different `nasm` and different runtime libraries, and would not be bit-for-bit
identical — which would break §8 before it started.

**W1 must pin the MSYS2 toolchain by exact package identity.** That is not an
aspiration: the route exists, was checked against the primary source, and is
frozen here as W1's entry contract.

The MSYS2 repository database is itself the pin material. Every package entry in
`clang64.db` publishes `%NAME%`, `%VERSION%`, `%ARCH%`, `%FILENAME%` and
`%SHA256SUM%`. Verified end to end while writing this section: the entry for
`mingw-w64-clang-x86_64-nasm` declares version `3.02-1`, arch `any`, and
SHA-256 `b338c604…1961e`; downloading the exact `%FILENAME%` and hashing it
reproduces that digest. Identity and integrity therefore come from the
repository's own metadata rather than from trusting whatever `pacman` resolves
on the day.

W1's entry contract:

1. **Resolve once, record everything.** At pin time, capture `%NAME%`,
   `%VERSION%`, `%ARCH%`, `%FILENAME%` and `%SHA256SUM%` for every package in
   the transitive toolchain set, into `ci/ffmpeg/components.json` beside the
   source pins — the same role `RPM_BUILDER_IMAGE` plays for Linux packaging.
2. **Mirror to an immutable store.** `repo.msys2.org` serves only current
   versions and prunes superseded ones, so a URL alone is not a durable pin. The
   verified package files are mirrored into a Tesserafin-owned immutable
   location. The recorded SHA-256 is what makes that mirror trustworthy — the
   mirror is a cache, never the authority.
3. **Verify before installing, always.** Every package file is checked against
   its recorded digest before it is allowed near the build. A mismatch is a hard
   stop, not a warning.
4. **Install from the pinned files only** — `pacman -U` over the local set.
   **No `pacman -Syu`, no `pacman -S`, no `update: true`** against a live
   repository at build time. The set must be transitively complete, because
   nothing may be resolved from the network to fill a gap.
5. **No shared prefix between the two reproducibility builds.** Separate
   machines, separate MSYS2 prefixes, separate dependency prefixes, no shared
   compiled objects and no shared staged runtime — otherwise the second build is
   partly a copy of the first and proves nothing.
6. **Provenance and corresponding source close over the toolchain too**, not
   only over FFmpeg's own sources.

Until that contract is implemented, W1's two clean builds cannot be guaranteed
bit-for-bit identical. **This remains the single largest technical risk in W1**
(§13) — but it is now a bounded implementation task with a proven mechanism
rather than an open question.

The W0 spike itself deliberately does **not** implement this: it runs
`setup-msys2` with `update: true` and unpinned package names, and records every
resolved package version in `toolchain.txt` precisely so the gap is measured.
The spike proves the build mechanism; it is not, and does not claim to be, a
reproducible build.

---

## 8. Reproducibility and the modular component boundary

The Linux rule is inherited without relaxation.

1. **Two clean `win-x64` builds on two separate native Windows runners.** Not
   two builds on one runner; not one build compared to a cache.
2. **Nothing shared between them.** No compiled objects, no dependency prefix,
   no staged runtime, no incremental mode. Each side fetches pinned source and
   builds from scratch.
3. **Complete delivered-path comparison before any digest comparison.** The
   ordered relative-path list is compared first, so "a file is missing" is
   distinguishable from "a file differs". `Get-W0TreeDigest` implements exactly
   this and its behaviour is unit-tested.
4. **Bit-for-bit identity of every unsigned delivered byte.** Functional
   equivalence is not accepted, at any point, for any component. Weakening this
   to equivalence is a hard stop.
5. **Immutable component identity**, binding together: build revision; upstream
   commit; every component commit and checksum; the applied patch set; toolchain
   identities including the pinned MSYS2 package set; target architecture; the
   capability manifest; licences and SBOM; and the corresponding-source digest.
6. **Packaging consumes the FFmpeg component only after acceptance and only by
   exact digest.** The MSI and the ZIP name a digest; they do not rebuild it and
   they do not accept "the latest".
7. **No expiring Actions artifact is ever a production input.** The W0 workflow
   uses one for the Web payload handover; that is permitted **only** because W0
   is probe evidence, and W2 replaces it with a daemonless digest-pinned OCI pull
   on the Windows host.

Exactly one artifact carries an exception to clause 4, and it is bounded and
enumerated rather than delegated: the MSI's payload and every authored database
table are proven deterministic across two fixed-input builds, and its residual
delta is confined to the Package Code and OLE container timestamps — neither
pinnable in the toolset, the former harmful to pin (§5.6). That is a named
exception to byte identity for two container fields, **not** a downgrade to
functional equivalence and **not** a transfer of the burden to another artifact.

Every other unsigned deliverable — the portable ZIP, the FFmpeg runtime
component and its corresponding source — is bit-for-bit reproducible with no
exception. The MSI additionally records its input digests, so the container is
traceable through contents that are themselves reproducible.

### Signing is a later transformation

* Reproducibility is proven on **unsigned** artifacts. A signature is applied
  **after** acceptance, never before.
* Authenticode signatures embed a certificate and a countersigned timestamp and
  are **not** expected to be reproducible. Requiring identical signed bytes
  would be a false requirement, and asserting it would quietly convert the
  reproducibility gate into a rubber stamp.
* Every signed artifact must remain traceable to the **exact accepted unsigned
  digest**, recorded in its provenance manifest, so a signed download can be
  proven to be the artifact that was accepted.
* Certificate custody, SHA-256 timestamping and the verification procedure are
  specified in §11.
* **No certificate and no signing secret is created in W0.**

---

## 9. Filesystem, identity and ACL contract

### 9.1 Layout

| What | Where | Owner |
| --- | --- | --- |
| application binaries | `%ProgramFiles%\Tesserafin\Server\` | installer |
| bundled Web payload | `%ProgramFiles%\Tesserafin\Server\web\` | installer |
| FFmpeg runtime | `%ProgramFiles%\Tesserafin\Server\ffmpeg\` | installer |
| licences, SBOM, provenance | `%ProgramFiles%\Tesserafin\Server\licenses\` | installer |
| configuration | `%ProgramData%\Tesserafin\Server\config\` | **operator** |
| database and persistent state | `%ProgramData%\Tesserafin\Server\data\` | **operator** |
| cache / transcode workspace | `%ProgramData%\Tesserafin\Server\cache\` | **operator** |
| logs | `%ProgramData%\Tesserafin\Server\log\` | **operator** |

The split is the point: everything under `%ProgramFiles%` is package-owned and
replaced wholesale on upgrade; everything under `%ProgramData%` is operator-owned
and never touched by the package. It mirrors L0's `/usr/lib` versus `/var/lib`
and `/etc` split.

### 9.2 Identity

**`NT SERVICE\Tesserafin`**, a virtual service account. Non-administrator, by
requirement.

Selected only after proving the properties, not after preferring the name. The
probe creates a service with `obj= NT SERVICE\<name>` and then **grants that SID
`Modify` on a real directory**, which is the property that matters: the account
is created by the SCM with the service and removed with it, so an installer can
grant per-machine least-privilege ACLs without inventing, storing or rotating a
password.

**The measured consequence, arrived at by being wrong twice.**

The service first refused to start at all. Started inside the MSI transaction it
failed with error 1920, rolling the install back to 1603; registered and started
separately it failed with 1053. The obvious explanation — a virtual service
account is not a member of `Users` and so inherits no right to execute from
`%ProgramFiles%` — was tested directly by granting read and execute on the
install directory and starting again. **It still failed.**

The deciding factor turned out to be neither the account nor the grant but
**where the process writes**. It was writing a file next to its own binaries,
and a correctly locked-down `%ProgramFiles%` refuses that — as it should. Moving
that write to `%ProgramData%` made the service start immediately, and
`aclGrantIsWhatMattered` correctly reports `false`: with the write in the right
place, the default `%ProgramData%` ACL is already permissive enough, and the
explicit grants that follow are not what rescued it.

Two conclusions, and they pull in different directions, which is why both are
stated:

* **For W3:** the service must never need to write inside `%ProgramFiles%`.
  Anything in the startup path that defaults to `AppContext.BaseDirectory` — a
  log sink, a marker file, a scratch file — will fail exactly this way, and it
  will fail *only* in the installed configuration and never in a developer's
  build tree. That is the worst shape a defect can have, and it is why W3 must
  be acceptance-tested from an installed layout rather than from a publish
  directory.
* **For W4:** that the default `%ProgramData%` ACL happens to be permissive
  enough is **not** a reason to rely on it. It is permissive because it lets any
  authenticated user create files there, which is not a property this
  distribution wants for a directory holding the database. §9.3 still breaks
  inheritance and grants explicitly — the grants are for least privilege, not
  for making the service start. `LocalService` is rejected because it is shared with unrelated
services; `LocalSystem` is rejected outright as administrator-equivalent; a
managed local user is rejected because it introduces a credential the installer
would have to create and the operator would have to maintain.

Network access: the account authenticates to the network as the machine account,
which is sufficient for a server that listens locally and reaches out over HTTP.
An operator whose media lives on an SMB share needs a domain identity instead;
W4 must document that as a supported deviation rather than pretend the default
covers it.

### 9.3 ACLs

| Path | Grant |
| --- | --- |
| `%ProgramFiles%\Tesserafin\Server\` | `NT SERVICE\Tesserafin`: **read and execute only**. The service must not be able to rewrite its own binaries or its own FFmpeg |
| `%ProgramData%\Tesserafin\Server\config` | `NT SERVICE\Tesserafin`: Modify |
| `…\data`, `…\cache`, `…\log` | `NT SERVICE\Tesserafin`: Modify |
| all of the above | Administrators and SYSTEM: Full. `Users`: no inherited write |

Inheritance is broken at `%ProgramData%\Tesserafin\` so a permissive parent ACL
cannot silently widen access to the database.

---

## 10. Install, upgrade, repair, uninstall

| Operation | Contract |
| --- | --- |
| **fresh installation** | creates the layout, applies the ACLs, registers the service under `NT SERVICE\Tesserafin`, and leaves it **installed and enabled but not started** — an operator decides when a media server begins serving, exactly as L0 does |
| **service start / stop** | through the SCM only. Stop waits up to the §4 timeout for a clean shutdown before the SCM escalates |
| **upgrade** | stops the service, replaces everything under `%ProgramFiles%`, leaves `%ProgramData%` **untouched**, re-applies ACLs, restarts the service only if it was running |
| **downgrade** | **refused.** `MajorUpgrade` blocks it with an explicit message. An operator who genuinely wants an older build uninstalls first and accepts the database-schema consequences |
| **repair** | `msiexec /f` restores missing or altered package-owned files without touching `%ProgramData%` |
| **ordinary uninstall** | removes the service and everything under `%ProgramFiles%`. **Keeps configuration, database, cache and logs.** The retained-data component is `Permanent` in the package, so this is a property of the package rather than a custom action that could be skipped |
| **explicit data removal** | never automatic, never a checkbox that defaults on. Documented as a manual deletion of `%ProgramData%\Tesserafin\`, performed by the operator |

The normal uninstall **must not** silently delete the database, the
configuration or any media library. Package management removes what the package
installed; everything the server produced belongs to the operator.

### The synthetic upgrade test

As L0 does: build a synthetic **lower** version and the real **higher** one,
install the lower, write **sentinels** into configuration and into the database
directory, upgrade, then read the sentinels back and assert both survived and
that exactly one product is registered afterwards. The probe already runs this
shape against the disposable payload; W4 runs it against the real package. An
upgrade path asserted rather than exercised is not an upgrade path.

---

## 11. Signing decision

**Decision: Tesserafin 1.1 ships an Authenticode-signed MSI and signed
executables, and the signing infrastructure is stood up in W5 — not in W0.**

* Both `tesserafin.exe` (and the delivered first-party binaries) and the MSI are
  signed. Signing the MSI alone leaves SmartScreen and enterprise policy looking
  at unsigned executables inside it.
* **SHA-256** digests and an **RFC 3161 SHA-256 timestamp** from a public
  timestamp authority, so signatures outlive the certificate.
* Certificate custody: an organisation-validated code-signing certificate held
  in a hardware token or a cloud signing service. The private key never exists
  as a file in a repository, in an Actions secret, or on a developer machine.
  Signing runs in a job that is reachable only from `master`, never from a fork,
  never from `pull_request_target`.
* Verification: `signtool verify /pa /all` plus an explicit assertion that the
  signed artifact's **unsigned** predecessor digest matches the accepted digest
  in the provenance manifest.
* Unsigned artifacts remain publishable and usable; signing is an addition to
  the accepted artifact, never a precondition for producing it.
* **W0 creates no certificate, no key, no secret and no signing job.**

---

## 12. Acceptance matrix for W1

W1 delivers the Windows FFmpeg runtime. It is accepted when, and only when:

| # | Gate |
| --- | --- |
| 1 | built natively on `windows-latest` (or a native Windows x64 equivalent), from the pinned upstream commit, with the resolved SHA asserted |
| 2 | the fork's `debian/patches/series` applies cleanly and `tonemapx` is present in `-filters` |
| 3 | every component in `components.json` applicable to `win-x64` builds from its pinned source and checksum; no `pacman` media package enters the link |
| 4 | the MSYS2 toolchain satisfies the six-clause entry contract in §7.4 — every package's name, version, architecture, filename and SHA-256 recorded in `components.json`; files mirrored immutably and digest-verified before installation; installed with `pacman -U` from the pinned set, never `-Syu` or `-S` against a live repository |
| 4b | the runtime is built with `--enable-schannel` (§7.3) and its import closure contains no OpenSSL, GnuTLS or other third-party TLS library |
| 5 | `ffmpeg.exe` and `ffprobe.exe` are PE x64 and their import table resolves entirely within the delivered tree plus OS DLLs |
| 6 | every required encoder, decoder, filter and protocol from `EncoderValidator` is present |
| 7 | a software encode → probe → decode smoke passes on the same host |
| 8 | **no** hardware runtime claim; compiled capability is recorded as compiled capability |
| 9 | two clean builds on two separate native Windows runners produce an identical delivered-path set and identical SHA-256 for every delivered byte |
| 10 | capability manifest, SBOM, notices, licence texts and a corresponding-source sidecar with a recorded digest all ship |
| 11 | the licence expression is computed from the actual closure, not copied from Linux; `--enable-gpl --enable-version3` makes the runtime GPL-3.0-or-later while the server stays GPL-2.0-or-later, and the two boundaries stay separate |
| 12 | the accepted digest is recorded so W2 and W4 can consume it by digest alone |

---

## 13. Risks and hard blockers

**No hard blocker fired in W0.** Every hard-stop condition in the tracker was
tested: a native Windows x64 runner executes; the server publishes
self-contained; the Web payload is consumable with its provenance intact; the
pinned FFmpeg source has a native Windows build path; licence and
corresponding-source closure is preserved; the installer decision rests on a real
lifecycle experiment; no candidate needs a proprietary or unredistributable
dependency; the proposed service identity can be granted its ACLs; nothing was
weakened to functional equivalence; no precompiled third-party FFmpeg is
required; scope did not expand into a desktop client.

Open risks, ranked:

1. **MSYS2 is a rolling repository (§7.4).** Highest technical risk in W1. Until
   the toolchain is pinned by exact package identity, two clean builds cannot be
   guaranteed bit-for-bit identical, and the reproducibility gate is the whole
   contract. The mechanism is no longer unknown -- §7.4 freezes a verified
   contract (repository-published per-package SHA-256, mirrored immutably,
   `pacman -U` from pinned files, never `-Syu`) -- but implementing it is W1
   work and the risk stands until it is done.
2. **MSI bytes are not bit-for-bit reproducible (§5.6).** Bounded, not waved
   through: the payload and every authored table are proven identical across two
   fixed-input builds, Windows Installer's own transform engine reports no
   difference between the two databases, and the residual delta is the Package
   Code and OLE container timestamps -- neither pinnable in WiX 6.0.2, and the
   former harmful to pin. It must never be re-described as "the MSI reproduces",
   and W4 must keep both instruments so the exception cannot widen.
3. **72 of 3,992 tests fail on native Windows (§2.7).** Pre-existing -- proven by
   an exact failing-set comparison against the base commit, not assumed -- and
   outside W0 scope, but not cosmetic. The child-process stdin family is the
   `q`-on-stdin graceful-stop path **W3 depends on**; the OpenAPI family is a
   cross-host contract divergence **W5 depends on**; and the 68-test log-forging
   family is a security guard whose Windows behaviour is unproven either way.
   Confirmed on Windows what §2.1 already showed on macOS: the canonical XML
   documentation order does not survive a third host.
4. **Web payload acquisition on Windows.** The Linux path needs a Linux
   container runtime. W2 must implement a daemonless digest-pinned OCI pull;
   until it does, the payload crosses a boundary the production path will not
   have.
5. **Worst-case shutdown.** §2.6 measures a floor with no transcode running. A
   stop timeout derived from it would truncate a live transcode.
6. **Virtual account and network storage (§9.2).** Sufficient for local media;
   an SMB library needs a documented domain-identity deviation.

Schannel versus OpenSSL is **decided, not a residual risk**: §7.3 records
`--enable-schannel`, evidenced by upstream's own native Windows build script at
the pinned commit and corroborated by the spike's `bcrypt`-only, no-OpenSSL
import closure.

---

## 14. `win-arm64`, explicitly deferred

**Anticipated architecturally. Not promised for 1.1. Not built in W1–W5.**

The pieces exist: .NET publishes `win-arm64` self-contained, and the pinned
FFmpeg tree carries `msys2/buildarm64.sh` driven on `windows-11-arm` with
`CLANGARM64`. The design keeps the door open — `components.json` gains an
architecture dimension rather than a Windows-specific fork, and nothing in §6,
§8, §9 or §10 hard-codes x64.

What is deliberately **not** done: no `win-arm64` artifact, no `win-arm64`
acceptance, no `win-arm64` reproducibility proof and no statement to users that
one is coming. `components.json` already disables AMF and `libvpl` on arm64 for
Linux and the same restriction applies here, so an arm64 runtime would have a
different capability manifest — which is a reason to promise nothing until it
has been measured.

---

## 15. Implementation decomposition

Bounded, sequenced, and **none of it begins in W0-A**.

| | Scope | Done when |
| --- | --- | --- |
| **W1** | Windows FFmpeg runtime: native MSYS2 CLANG64 build from pinned source, pinned toolchain, full component closure, capability manifest, SBOM, corresponding source | the §12 matrix passes and an accepted digest is recorded |
| **W2** | Portable self-contained server ZIP: publish, bundled Web payload acquired daemonlessly by digest, FFmpeg consumed by accepted digest, deterministic archive, relocation proof | two clean builds on two runners are byte-identical and the ZIP starts after relocation |
| **W3** | Windows Service integration: Generic Host `AddWindowsService` in `Tesserafin.Server`, the §4 contract, worst-case stop timeout, Event Log lifecycle events | `sc start` / `sc stop` drive a clean lifecycle with no orphan, proven on a native host |
| **W4** | The MSI and lifecycle: WiX package, `ServiceInstall`/`ServiceControl`, §9 ACLs, §10 semantics, the synthetic sentinel upgrade test | install → upgrade → repair → uninstall all pass with sentinels intact |
| **W5** | Independent acceptance, signing readiness and 1.1 integration: cross-runner reproducibility gates, provenance verification, certificate custody and the signing job, release integration | acceptance runs green on evidence that no build job produced |

W0-A ends here. It selects, it freezes, and it hands W1 a specification it can
be held to.
