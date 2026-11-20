# SignPath Foundation application — readiness dossier

Status: **prepared, not submitted.** The owner submits the application at
<https://signpath.org/apply>; nothing here contacts SignPath.

Decision source: owner ruling 2026-11-05 on #386: no paid signing for now, so
Windows artifacts are signed through the free SignPath Foundation OSS programme.
The conditions below are quoted from <https://signpath.org/terms> as read on
2026-11-05. Re-read them before submitting; the page states that parts are still
draft.

## 1. Conditions and Tesserafin's status

| Condition (signpath.org/terms) | Status | Evidence / gap |
| --- | --- | --- |
| **No malware / PUP** | met | Media server; no bundled offers, no adware. |
| **OSI licence, no commercial dual licensing, for all components** | met, to re-check at application time | Server and Web are GPL-2.0. Bundled FFmpeg runtime is LGPL/GPL, built from source (`docs/distribution/F0-ffmpeg-runtime.md`). .NET runtime is MIT. Confirm the shipped third-party notice list has no non-OSI component. |
| **No proprietary code** (system libraries allowed) | met, to re-check | Same list as above. |
| **Maintained** | met | Active commit history on `tesserafin/tesserafin`. |
| **Released in the form to be signed** | **gap** | An **unsigned** public Windows release (ZIP and/or MSI) must exist before applying. The existing `1.0.0` / `v1.1.0` releases are being reclassified as 0.1 / 0.2 (#386). Plan: publish an unsigned 0.x pre-release with the win-x64 ZIP **and** MSI, then apply. The MSI needs §2 of the plan (retained, accepted MSI) first. |
| **Documented on the download page** | **gap** | The download page must describe what the software does. Owner of that page: `tesserafin-website`. |
| **MFA for every team member, on SignPath and GitHub** | **gap** | The org setting "Require two-factor authentication" is **off** for `tesserafin` (`two_factor_requirement_enabled: false`). Turn it on (org settings → Authentication security). |
| **Roles: authors, reviewers, approvers** | to declare | Today one maintainer holds all three roles (org has 1 member, team `core`). That is allowed as long as it is stated; see §2. |
| **Code signing policy on home + download pages** | **gap** | Text drafted in §2; goes on `tesserafin-website` and the GitHub Release notes. |
| **Privacy:** no transfer to systems the user did not choose, otherwise policy + install-time display + opt-out | **to verify** | List every outbound connection the server makes **by default** (metadata providers enabled on new libraries, image providers, plugin repository, update checks). If any happens without the operator choosing it, it needs a privacy policy shown during installation and an opt-out, or the default changes. |
| **Announce system changes** | met | MSI installs a Windows service and sets ProgramData ACLs, as documented in `install-windows.md`. Re-check that the installer UI says so. |
| **Provide uninstallation** | met | `install-windows.md` §3.3 (`msiexec /x`, Add/Remove Programs). ZIP: document "stop the service and delete the folder" next to it. |
| **Verifiable automated build; manual approval per release** | met by design | GitHub Actions on GitHub-hosted runners; reproducible unsigned ZIP (W2/W5). Manual approval doubles the `release-signing` environment gate (#386 decision 5). |

## 2. Draft "Code signing policy" (for the website and release pages)

> ### Code signing policy
>
> Free code signing provided by [SignPath.io](https://about.signpath.io),
> certificate by [SignPath Foundation](https://signpath.org).
>
> - Committers and reviewers: [Tesserafin core team](https://github.com/orgs/tesserafin/teams/core)
> - Approvers: [Owners](https://github.com/orgs/tesserafin/people?query=role%3Aowner)
>
> Only Windows artifacts built by this project's GitHub Actions workflows from the
> `tesserafin/tesserafin` repository are signed. Each signed release is approved
> manually by an approver.
>
> **Privacy policy:** This program will not transfer any information to other
> networked systems unless specifically requested by the user or the person
> installing or operating it.

The privacy sentence is SignPath's suggested wording. Use it **only** if the
privacy row in §1 is verified; otherwise link a real privacy policy that names
each default connection (metadata and image providers) and how to turn it off.

## 3. Application answers (draft)

| Field | Draft answer |
| --- | --- |
| Project | Tesserafin — self-hosted media server (server + web client), GPL-2.0 |
| Repository | <https://github.com/tesserafin/tesserafin> (server, Windows packaging), <https://github.com/tesserafin/tesserafin-web> (bundled web client) |
| Artifacts to sign | `tesserafin.exe` and the first-party .NET assemblies in the win-x64 portable ZIP, and the win-x64 MSI |
| Build system | GitHub Actions, GitHub-hosted `windows-latest` runners; unsigned ZIP is bit-for-bit reproducible across two runners |
| Release cadence | owner to fill |
| Download page | owner to fill (tesserafin.org download page) |
| Team | one maintainer (all roles) — owner to confirm |

## 4. Order of operations

1. Turn on required 2FA for the `tesserafin` org.
2. Verify the privacy row (§1) and choose the policy wording (§2).
3. Publish the code signing policy and a functional description on the download page.
4. Publish an unsigned 0.x Windows pre-release (ZIP + MSI).
5. Submit the application (owner).
6. On acceptance: replace the self-signed step of the test pipeline with SignPath's
   GitHub action, behind the `release-signing` environment.
7. First signed release: the official **1.0** launch.
