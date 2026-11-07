# Installing Tesserafin on Windows (`win-x64`)

Tracker: [#276](https://github.com/tesserafin/tesserafin/issues/276).
Refs [#129](https://github.com/tesserafin/tesserafin/issues/129).

Tesserafin 1.1 names two native Windows forms of the same server:

| Form | For | Needs an administrator |
| --- | --- | --- |
| **portable ZIP** — `tesserafin-server_<version>_win-x64.zip` | running the server from a directory you choose, with state you place yourself | no, to run it from a console |
| **MSI** — installs the server and the Windows service `Tesserafin` | a machine-wide installation managed by Windows Installer | yes |

Both carry the same self-contained server, the same bundled Tesserafin Web and
the same Tesserafin FFmpeg runtime, at the same relative paths
([W0 §6](W0-windows-server.md), [W2-A2 §1](W2-A2-server-zip.md)). Only
`win-x64` exists. There is no `win-arm64` build.

This document describes only what the W2, W3 and W4 slices measured on
`windows-latest`. Where something was not measured, it says so rather than
describing how it ought to behave.

**Download.** No supported download is available until the `1.0.0` launch. The
pre-launch `0.2.0` release (published as `v1.1.0`) is scheduled for deletion.
It carried the unsigned portable ZIP
`tesserafin-server_1.0.0_win-x64.zip`, SHA-256
`c1f6261cb770bd3dcbf255f4dd15b287a7289adad5a619d31725c158cd20e2d8` (the W5-A1
pin, §1). It is not Authenticode-signed. The MSI is not attached to that
release, so no MSI is published; section 3 describes a form you cannot download.

---

## 1. Before you start

* **The interface is Tesserafin Web, in a browser.** There is no native desktop
  client. Once the server is running, open `http://localhost:8096/` on the same
  machine; `/` redirects to `/web/`, where onboarding starts. From another
  device, use the machine's address instead of `localhost`.
* **The artifacts are unsigned.** No Authenticode signature exists yet (W0 §11;
  vendor UNDECIDED, [W5-A0 §D](W5-A0-acceptance-contract.md)). Unsigned
  artifacts remain usable: Windows may show a SmartScreen or "unknown publisher"
  prompt, and enterprise policy may block unsigned executables outright. This
  document does not describe how to sign anything.
* **Check what you received.** The unsigned ZIP accepted by W5-A1 is pinned in
  [`ci/windows/w5/accepted-unsigned-zip.json`](../../ci/windows/w5/accepted-unsigned-zip.json):
  `tesserafin-server_1.0.0_win-x64.zip`, SHA-256
  `c1f6261cb770bd3dcbf255f4dd15b287a7289adad5a619d31725c158cd20e2d8`, assembled
  at commit `41d3411c9838feec6650dd10ec89a1f198aafdeb`. That digest identifies
  the ZIP built from that commit. A ZIP built from any other commit has a
  different digest, because the archive records its own commit
  ([W5-A1 §1](W5-A1-unsigned-acceptance.md)). The MSI has **no** pinned digest
  and no bit-identical claim.

  ```powershell
  Get-FileHash -Algorithm SHA256 .\tesserafin-server_1.0.0_win-x64.zip
  ```

* **Hardware acceleration.** No D3D11VA, DXVA2, QSV, NVENC or AMF runtime claim
  is made on Windows; the hosted runners that measured these forms have no GPU.

---

## 2. Portable ZIP

### 2.1 Unpack

Extract the archive anywhere. It holds exactly one top-level directory,
`tesserafin-server_<version>_win-x64\`:

```
tesserafin-server_<version>_win-x64\
├── tesserafin.exe
├── tesserafin-server-service.ps1
├── web\
├── ffmpeg\bin\ffmpeg.exe
└── licenses\
    ├── LICENSE
    ├── provenance.json
    └── ffmpeg\sbom.cdx.json
```

The tree is relocatable: W2-A3 started it from a path containing spaces, an
accented letter, an em dash and CJK characters, moved it to a different depth,
and started it again ([W2-A3 §1](W2-A3-relocate-start.md)).

### 2.2 Run

**The ZIP ships no state.** It contains no configuration, database, cache or
logs, and no `%ProgramData%` layout — that layout is the MSI's. Give all four
directories explicitly on every start, **outside** the extracted tree — a later version is a
new tree, and anything kept inside the old one is left behind with it. Pass
`--webdir` and `--ffmpeg` explicitly as well, pointing inside the extracted tree,
exactly as W2-A3 measured:

```powershell
$pkg   = 'C:\Tesserafin\tesserafin-server_1.0.0_win-x64'
$state = 'D:\TesserafinState'

& "$pkg\tesserafin.exe" `
    --datadir   "$state\data" `
    --configdir "$state\config" `
    --cachedir  "$state\cache" `
    --logdir    "$state\log" `
    --webdir    "$pkg\web" `
    --ffmpeg    "$pkg\ffmpeg\bin\ffmpeg.exe"
```

Paths above are examples; choose your own.

**Readiness.** While the server is starting, every path answers `503` with
`{"status":"starting",…}`. That is not a failure. A first start runs database
migrations and has been measured still migrating at 180 s on a hosted runner
([W0 §2.3](W0-windows-server.md)). The server is ready when `/` answers with
something other than `503` — normally a redirect to `/web/`. The port is
`8096` unless `network.xml` in the configuration directory says otherwise.

**Stopping.** Stop the console process with `Ctrl+C`. A clean exit after a
successful start returns `0`; a fatal startup, such as one with no usable FFmpeg,
exits non-zero ([W3-A0 §1.3](W3-A0-service-host.md)).

### 2.3 Optional: register the service from the ZIP

`tesserafin-server-service.ps1`, beside `tesserafin.exe`, registers, starts,
stops and removes the service `Tesserafin` against the extracted tree
([W2-A5](W2-A5-service-script.md)). Run it from an elevated PowerShell 7.2 or
later:

```powershell
& "$pkg\tesserafin-server-service.ps1" register `
    -DataDir "$state\data" -ConfigDir "$state\config" `
    -CacheDir "$state\cache" -LogDir "$state\log"
& "$pkg\tesserafin-server-service.ps1" start
& "$pkg\tesserafin-server-service.ps1" stop
& "$pkg\tesserafin-server-service.ps1" remove
```

`register` refuses relative state directories and state directories inside the
package. W3-A0 ran this script unmodified on a real Service Control Manager:
the service reached `Running`, answered on its port, and stopped with exit code
`0` ([W3-A0 §2.2](W3-A0-service-host.md), control P).

What the script is **not** (W0 §6, W2-A5 §3):

* not a second installer — no repair, no rollback, no Add/Remove Programs entry;
* it does not register the `NT SERVICE\Tesserafin` identity, apply the W0 §9.3
  ACLs, or write the machine-wide stop timeout. Those are the MSI's;
* it records the extracted directory in the service's `binPath`. If you move
  the tree, `remove` and then `register` again. `register` refuses a service
  that already exists.

Use either the ZIP's script or the MSI on one machine, not both: both register a
service named `Tesserafin`.

---

## 3. MSI and the Windows service

Every command below is the form the W4 probes ran — `msiexec` with
`/qn /norestart /l*v <log>` — from an elevated prompt. The log is what explains
a non-zero exit.

### 3.1 Install

```powershell
msiexec /i Tesserafin.msi /qn /norestart /l*v install.log
```

To install the binaries somewhere other than the default, add
`INSTALLFOLDER="<path>"`. Measured on install ([W4-A0](W4-A0-wix-skeleton.md),
[W4-A2](W4-A2-service-recovery.md), [W4-A3](W4-A3-programdata-acls.md),
[W4-A6](W4-A6-eventlog-source.md)):

| What | Where / value |
| --- | --- |
| binaries, `web\`, `ffmpeg\`, `licenses\` | `INSTALLFOLDER`, default `%ProgramFiles%\Tesserafin\Server\` |
| configuration, data, cache, logs | `%ProgramData%\Tesserafin\Server\config`, `data`, `cache`, `log` |
| service | `Tesserafin`, display name `Tesserafin Server`, Automatic (Delayed Start) |
| service account | `NT SERVICE\Tesserafin`, a virtual service account, not an administrator |
| service arguments | `--service`, the four `%ProgramData%` directories, `--webdir` and `--ffmpeg` under `INSTALLFOLDER` |
| recovery | restart after 60 s on the first and second failure; no action on the third |
| ACLs | `NT SERVICE\Tesserafin` read and execute on `INSTALLFOLDER`, Modify on the four state directories; Administrators and SYSTEM Full; no `Users` write; inheritance broken at `%ProgramData%\Tesserafin\` |
| Event Log | source `Tesserafin` under `Application`, for service start and stop |

**The installer does not start the service.** A fresh install leaves it
installed, enabled and stopped (W0 §10). Start it when you decide to:

```powershell
sc.exe start Tesserafin
sc.exe stop Tesserafin
```

W4-A6 measured exactly this `sc start` / `sc stop` pair on an installed package:
`Running`, then `Stopped` with no orphaned process, with lifecycle events under
the `Tesserafin` source. Then open `http://localhost:8096/`. The readiness notes
in §2.2 apply: `503` means still starting.

The virtual account authenticates to the network as the machine account
([W0 §9.2](W0-windows-server.md)). Media on an SMB share that the machine
account cannot read needs a different service identity; no slice measured one.

### 3.2 Upgrade

Stop the service, then install the newer MSI over the older one:

```powershell
sc.exe stop Tesserafin
msiexec /i Tesserafin-newer.msi /qn /norestart /l*v upgrade.log
sc.exe start Tesserafin
```

Measured on a real `MajorUpgrade` pair with one frozen `UpgradeCode`,
`0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05`
([W4-A4 §1](W4-A4-major-upgrade.md), [W4-A5 §1](W4-A5-remember-installfolder.md)):

* the older product is removed and the newer one installed — one product, not two;
* the binaries under `INSTALLFOLDER` are replaced with the newer package's;
* everything in the four `%ProgramData%` state directories survives, byte for byte;
* the service stays registered, with the same account, arguments and recovery
  policy, and is **stopped** afterwards — start it yourself;
* **`INSTALLFOLDER` is remembered.** An upgrade run with no `INSTALLFOLDER` on
  its command line installs into the directory the first install used, not the
  default. Passing a *different* `INSTALLFOLDER` on an upgrade to move an
  installation was not measured.

Only upgrades from a **stopped** service were measured, which is why the
sequence above stops it first. Downgrade was recorded once and graded on
nothing: do not rely on it either way.

### 3.3 Uninstall

```powershell
sc.exe stop Tesserafin
msiexec /x Tesserafin.msi /qn /norestart /l*v uninstall.log
```

Measured ([W4-A0 §4](W4-A0-wix-skeleton.md), [W4-A6](W4-A6-eventlog-source.md)):
uninstall removes the service, the binaries and the Event Log source, and
**keeps** the four `%ProgramData%\Tesserafin\Server\` state directories and
their contents. Removing your data is a manual step: delete
`%ProgramData%\Tesserafin\` yourself, once you are sure. The installer never does
it.

### 3.4 Not covered

* **Repair.** Not exercised by any slice, and not documented here.
* **Signing.** No signed MSI or executable exists yet.
* **Add/Remove Programs.** Uninstalling from Settings was not driven by any
  slice; `msiexec /x` is what was measured.
