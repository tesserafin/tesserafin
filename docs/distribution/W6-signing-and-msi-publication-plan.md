# Windows signing and MSI publication — state and plan

Status: **plan, owner decisions recorded 2026-11-05** (§6). It creates no
certificate, no key, no secret and no signing job, and changes no workflow. The signing decision of
[W0 §11](W0-windows-server.md) and [W5-A0 §D](W5-A0-acceptance-contract.md) stands
and is not reopened here; this document only turns it into an executable sequence
and lists the decisions it is waiting on.

Measured 2026-11-05 against release `v1.1.0` and `master` `0eff74dd97`.

## 1. Where things stand

### 1.1 Release `v1.1.0` checksum coverage

`ci/release-checksums/verify_release_checksums.py --repo tesserafin/tesserafin --tag v1.1.0`
compares every attached asset with the release's own `SHA256SUMS*.txt` files and
with the digest GitHub computed for the uploaded bytes:

| Status | Asset | Note |
| --- | --- | --- |
| OK | 5 `linux-x64` assets (`.deb`, `.rpm`, `.tar.gz`, FFmpeg runtime, FFmpeg corresponding source) | listed digest = GitHub digest |
| UNLISTED | `tesserafin-server_1.0.0-1_arm64.deb` | GitHub digest `a0a4367a…` |
| UNLISTED | `tesserafin-server-1.0.0-1.aarch64.rpm` | GitHub digest `64b3d9fc…` |
| UNLISTED | `tesserafin-server-1.0.0-linux-arm64.tar.gz` | GitHub digest `db196bcb…` |
| UNLISTED | `tesserafin-server_1.0.0_win-x64.zip` | GitHub digest `c1f6261c…` = `zipSha256` in `ci/windows/w5/accepted-unsigned-zip.json`; pinned only in the release body |
| ABSENT | `tesserafin-server_1.0.0-2_amd64.deb`, `tesserafin-server-1.0.0-2.x86_64.rpm` | listed in `SHA256SUMS-linux-x64.txt`, never attached |

No digest disagrees. The gaps are coverage gaps: there is no
`SHA256SUMS-linux-arm64.txt` and no `SHA256SUMS-win-x64.txt`, and the `linux-x64`
file names two `-2` packages that the release does not carry.

### 1.2 Why the MSI is not attached

This is by construction, not an omission at release time:

* `w4-windows-msi.yml` runs on `pull_request` only and **uploads no artifact**
  (header: "it uploads no artifact, publishes nothing, and creates no tag or
  release"). Each job builds its own MSI from the PR head and discards it.
* W4 made **no bit-identical MSI claim**: W0 §8 carries a bounded exception for
  the Package Code and OLE container timestamps. So, unlike the ZIP, there is no
  accepted MSI digest to pin, and a rebuild cannot be matched to a prior build by
  hash alone.
* Hence no retained, accepted MSI artifact exists for the `v1.1.0` cut, and the
  release notes say so ("MSI not attached (no retained W4 artifact on this cut)").
  `install-windows.md` already tells users that §3 describes a form they cannot
  download.

### 1.3 Signing

Nothing is Authenticode-signed. Vendor UNDECIDED. Custody, timestamping and
verification requirements are fixed by W0 §11 / W5-A0 §D (non-exportable key,
no key material in Actions secrets, signing reachable only from `master`,
SHA-256 + RFC 3161 timestamp, signed artifact names the accepted unsigned digest).

## 2. What publishing the MSI needs

An MSI can be attached only once there is a single, named, retained MSI whose
provenance is recorded. Proposed, in order:

1. **Identity rule for the MSI.** Because the container is not byte-reproducible,
   the identity of an accepted MSI is: its own SHA-256 **plus** the digests of its
   payload and authored tables (already proven deterministic in W4) recorded in a
   manifest, `ci/windows/w6/accepted-msi.json`, mirroring
   `accepted-unsigned-zip.json`. Accepting "the" MSI means accepting one specific
   build and its manifest — not "any build of this commit".
2. **A producing workflow** reachable only from `master` / `workflow_dispatch`
   (never `pull_request`), building the MSI from the accepted ZIP inputs by
   digest, verifying payload digests against the W4 contract, and uploading it
   once. It runs the existing W4 install/upgrade/uninstall probes on that exact
   file before acceptance.
3. **Owner acceptance** of that one artifact (run id + digest), committed as the
   manifest in step 1.
4. **Attach** the MSI to the release together with `SHA256SUMS-win-x64.txt`
   (ZIP + MSI) and its manifest.

Until then the release must keep saying the MSI is not attached.

**Owner ruling (2026-11-05): rule accepted.** The MSI's identity is the one retained
build plus its payload/table manifest; the published MSI is that retained file,
never a rebuild.

## 3. Checksum completion checklist (x64 + arm64)

**Owner ruling (2026-11-05):** repairing the existing `v1.1.0` release is not a
priority; the next release comes first. The items below therefore apply to the
**next** release, and the `v1.1.0` gaps in §1.1 stay as recorded.

- [ ] `SHA256SUMS-linux-arm64.txt` for the arm64 assets. The digests must be
      taken from the **build run's own output** (or recomputed from a fresh
      download and cross-checked with GitHub's `digest` field), not copied from
      this document.
- [ ] `SHA256SUMS-win-x64.txt` containing the ZIP line (equal to the accepted
      ZIP manifest) and the MSI line.
- [ ] Every sums file lists exactly the attached assets (no `-2`-style leftovers).
- [ ] Gate future releases with
      `verify_release_checksums.py --repo … --tag … --require-complete --strict-absent`
      after upload; exit 0 is the release's "checksums complete" evidence.

Editing release assets is an owner action (W5-A4: tag and GitHub Release are
owner-only). This document does not do it.

## 4. Authenticode options

Constraint filter from W5-A0 §D: OV-level (or equivalent) identity, non-exportable
key in HSM/token or cloud service, no key material in Actions, CI-usable.

| Option | Key custody | CI fit | Cost (indicative, verify before buying) | Fit |
| --- | --- | --- | --- | --- |
| **Azure Artifact Signing** (formerly Trusted Signing) | Microsoft-managed HSM; short-lived certificates; Entra/OIDC federated identity — no stored secret | `azure/trusted-signing-action` / `signtool` dlib on `windows-latest` | ~USD 9.99/month Basic, 5 000 signatures | Best custody/CI fit. **Eligibility is the risk:** public-trust identity validation is limited to organisations in the US, Canada, EU and UK (individuals: reportedly US/Canada only). Needs a registered legal entity with verifiable history. |
| **SignPath Foundation** (OSS programme) | SignPath HSM; signing via SignPath's GitHub integration with origin verification | Good; designed for GitHub Actions and enforces "built from this repo/branch" | Free for qualifying OSS | Strong fit for an OSS project without a legal entity; the certificate is in SignPath Foundation's name, not Tesserafin's, and admission is a review process. |
| **SignPath (commercial)** with own OV cert | HSM at SignPath | Good | Paid plan + certificate | Fit if an entity exists but Azure eligibility fails. |
| **DigiCert KeyLocker / SSL.com eSigner / Sectigo cloud** with OV cert | Vendor cloud HSM | Workable (vendor CLI / `signtool` via KSP) | Certificate (~hundreds USD/yr) + per-signature or seat fees | Fits custody; costlier; vendor-specific credentials in CI must be request-only. |
| **Hardware token (USB) OV cert** | FIPS token | Poor: needs a self-hosted runner with the token attached | Certificate + token | Does not fit: CI runs only on GitHub-hosted runners. |
| **Self-signed test certificate** | Generated per run, discarded | Trivial | Free | **Test-only.** Exercises the signing *pipeline* (sign, `signtool verify`, unsigned-digest assertion) in PR CI. Never published, never trusted outside the test machine. |

EV certificates no longer give instant SmartScreen reputation (Microsoft removed
that in 2024), so OV is sufficient.

**Owner ruling (2026-11-05): no paid option for now → SignPath Foundation.**
Azure Artifact Signing is a paid subscription and needs an eligible legal entity;
it stays the upgrade path if Tesserafin later has both. The SignPath Foundation
application is prepared in
[`signpath-foundation-application.md`](signpath-foundation-application.md).
Consequences of this choice:

* the Windows publisher shown to users is **"SignPath Foundation"**, not Tesserafin;
* signing runs through SignPath's GitHub integration, which verifies that each
  artifact is an automated build of this repository — this satisfies W0 §11
  custody (no key material anywhere in the project);
* every signing request needs manual approval in SignPath, which doubles the
  `release-signing` environment gate;
* SignPath requires the project to be **already released in the form to be
  signed** — an unsigned public release must exist before the application.

Land the **self-signed test pipeline first** (§5), so the vendor step is the only
thing that changes when SignPath accepts the project.

## 5. Prepared workflow shape (not wired)

Proposed `w6-sign-windows.yml`, documented here rather than committed so no
required check changes. Sketch:

```yaml
on:
  workflow_dispatch:
    inputs:
      accepted_manifest: { description: path to accepted-*.json, required: true }
permissions: { contents: read }
jobs:
  sign:
    if: github.ref == 'refs/heads/master'          # W0 §11: master only
    runs-on: windows-latest
    environment: release-signing                   # required reviewers = owner
    permissions: { contents: read, id-token: write }   # OIDC to the signing service
    steps:
      - uses: actions/checkout@<pinned sha>
        with: { persist-credentials: false }
      # 1. fetch the ACCEPTED unsigned artifact by digest (OCI / retained asset),
      #    assert sha256 == manifest.zipSha256 (or msiSha256) before signing
      # 2. sign first-party PE files inside, re-pack per W2-A2, then sign the MSI
      #    (vendor step: azure/trusted-signing-action@<sha> or SignPath submit)
      # 3. signtool verify /pa /all on every signed file
      # 4. write signed-provenance.json: { unsignedSha256, signedSha256,
      #    certificateThumbprint, timestampAuthority, runId }
      # 5. upload signed artifact + provenance (short retention); owner attaches
```

Order matters for the ZIP: signing the `.exe`/`.dll` files changes the ZIP bytes,
so the signed ZIP gets a new digest that must point back to the accepted unsigned
digest in `signed-provenance.json` (W0 §8). The unsigned ZIP stays published.

A PR-side variant with a throwaway self-signed certificate (no `environment`, no
`id-token`) can run the same steps 2–4 to keep the pipeline tested.

## 6. Owner decisions (2026-11-05)

1. **Signing identity:** no paid option for now → **SignPath Foundation** (§4).
2. **MSI identity rule** (§2.1): **accepted**.
3. **`v1.1.0` checksum repair:** not a priority; the checklist (§3) applies from
   the next release.
4. **Signing starts with the next release.** The current `1.0.0` / `v1.1.0`
   releases are not considered usable and will not be signed.
5. **`release-signing` environment** with the owner as required reviewer:
   **accepted**.

Versioning: the owner wants the public, presentable launch to be **1.0**, and the
current `1.0.0` / `v1.1.0` releases to be treated as **0.1** / **0.2**. How the
existing tags and releases are renamed or removed is a separate, not yet executed
decision (see #386). Signing therefore targets the official 1.0 launch, and the
SignPath "already released" condition is met by an unsigned 0.x pre-release.

## 7. Next concrete actions

1. SignPath Foundation application (checklist in
   [`signpath-foundation-application.md`](signpath-foundation-application.md)).
2. Self-signed test pipeline PR (§5 variant), no secrets.
3. `w6` MSI producing workflow + `accepted-msi.json` (§2).
4. SignPath onboarding once accepted (review can take weeks — apply well before 1.0).
5. Release checksum gate (§3, last item) added to the release procedure.
