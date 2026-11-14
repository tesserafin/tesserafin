# Playback job ownership and stop

Decision for #301, with the disposition of #302. Applies to every media route carrying `playbackCapability`.

## Who owns a job

A transcoding job has one owner, taken from one credential:

| Request presents | Owner user | Owner device |
| --- | --- | --- |
| Durable token | the token's user | the token's device claim |
| Capability, alone | the capability's user | the device of the session it was minted for |
| Capability + token of the same user | the capability's user | the device of the session it was minted for |
| Capability + any other durable credential | refused, 401 | no job |

The user of one credential is never combined with the device of another. That combination is what #301 observed: a job owned by (user A, device B), which neither party could stop.

## What a capability carries

Its user, session, play session, scope, item and media source, as already validated by `IPlaybackCredentialService`. It gains no device claim: the device is read through its session, so a capability whose session has ended resolves to no device and matches no job.

## A capability beside another credential

The capability stays a bearer value: alone, it is honoured for whoever holds the link. Beside a token of another user, or an API key, the request contradicts itself and is refused with the same undifferentiated 401 as any capability mismatch. A role grants no exemption. This is about a defined owner, not confidentiality: dropping the token still reaches what the capability grants, and the MEDIA-ACCESS checks on the item are unchanged.

## Who may stop a job

| Route | Stops |
| --- | --- |
| `DELETE Videos/ActiveEncodings`, `POST Sessions/Playing/Stopped` | the jobs of that play session the caller owns, user and device |
| `DELETE Playback/Sessions/{id}`, by the session's owner or an administrator | the jobs of that play session belonging to the session's user |

A play session id is client-chosen, so two users can use the same one: each plans a session of their own, and another user's job and capabilities under that id survive all of these routes.

## What a stop revokes

| Route | Capabilities of that play session revoked |
| --- | --- |
| `POST Sessions/Playing/Stopped` | the reporting session's user, on its device |
| `DELETE Playback/Sessions/{id}` | the session owner's, on all of that user's devices |
| Device revoked (`DELETE /Devices`, sign-out, other tokens revoked) | every capability of that device; its `Playback/Sessions` sessions end too (#413) |

The two differ on purpose, and only within one user. A stop report speaks for its device, as it does for jobs. A delete names the user's session, which any of that user's devices or an administrator may end. Between users there is no difference: nothing of another user ends. A revoked capability link answers 401 and starts nothing. A link carrying a durable token stays valid as long as that token: fetching it again is a new playback.

A revoked device ends what it held open with its token (#413). `SessionManager.Logout(Device)` raises `DeviceLoggedOut` before it returns. `PlaybackSessionManager` then removes every session planned for that device and that user, and stops that user's jobs under each session's play session, with their output, as a `DELETE` does. A session of the same user on another device plays on.

A client that disappears without stopping is covered by the existing ping timeout: 60 s for HLS.

## Known limits

- The session manager keeps one current session per play session id. After a second user plans under an id, job and lifecycle signals for that id are attributed to the newer session. Ownership, stop and revocation do not read that mapping.
- Minting a capability under an id whose running job belongs to another device answers 404, whoever owns that job. Two users sharing an id therefore fail closed if the second mints after the first's job started.
- A session the legacy pipeline tracked by itself has no request, so an administrator's delete removes it without stopping or revoking anything.

## #302: a Stop report that regresses

**Keep the current semantics.** `SessionManager.OnPlaybackStopped` writes the position the client reports, and no maximum is kept.

The data available at that point cannot separate the cases:

| Case | Last progress of the play session | Stop report |
| --- | --- | --- |
| Client reports a stale position | high | low |
| User seeks back, stops before the next progress report | high | low |
| User restarts from the beginning, stops after 3 s | high, if no report landed after the restart | low |

Stop and progress reports carry a play session, a position and no seek or restart marker, and progress arrives only periodically, so a backward seek inside that interval leaves no trace. Preserving the earlier position would resurrect a resume point the user deliberately abandoned in the last two rows, while the first row affects only clients that report incorrectly; the official Web client no longer does (tesserafin-web#198). A distinct rule would need a new client-sent signal, which no current consumer needs.
