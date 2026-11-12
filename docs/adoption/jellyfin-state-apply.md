# Experimental existing-account state application

This is a bounded follow-up to the [read-only capture and simulation](jellyfin-dry-run.md)
under [#144](https://github.com/tesserafin/tesserafin/issues/144).
It applies reviewed state to **existing, explicitly mapped accounts and existing,
unambiguous media**. It creates no account and transfers no password, permission,
media file or library configuration. The selected adapters remain Jellyfin
10.10.7 and the current-contract Tesserafin server reporting application 0.3.0 (1.0.0 before the pre-launch renumbering, c206867206).

## Selected rules

- Only `Played`, `PlayCount`, `IsFavorite` and `PlaybackPositionTicks` are written.
  `LastPlayedDate` is excluded: the current nullable update DTO cannot clear it
  on rollback. Likes, rating and the user-data key are preserved and checked.
- Only an empty target state is eligible. Differing nonempty state, even a
  favourite alone, is not merged or replaced. Unchanged/date-only candidates
  and ineligible planner records are counted but omitted from the apply plan.
- At most 128 operations are allowed in one reviewed plan. User mappings stay
  one-to-one; path/provider ambiguity and conflicting or unknown runtimes remain
  refusals. A saved position must be within the known equal source/target runtime.
- The target server ID, product/version, account ID/name and exact media
  ID/type/path/provider IDs/runtime are checked before execution. A fresh
  per-user inventory must still have one matching media path. Paths are compared
  lexically; no source or target media file is opened by this tool.

**The API has no compare-and-swap or transaction spanning these requests.** Stop
all target playback, scanning and user-state edits while capturing, reviewing,
applying or rolling back. The server must remain running to answer the API.
`--quiescent-target` acknowledges this operational condition; the tool does not
enforce it. A writer can change state between a GET and POST, or change it and
restore the same value unnoticed. There is no atomicity or concurrent-writer
guarantee. Do not use this prototype on an active household server.

## Prepare and review offline

Capture fresh source/target snapshots and write an explicit mapping as described
in the capture guide. Keep a separately verified target backup. Use new private
filenames; real snapshots, plans and journals contain private identities, media
paths and history and must not be attached to public issues or committed.

```sh
python3 tools/adoption/jellyfin_state_apply.py prepare \
  --source /private/source.json --target /private/target.json \
  --mapping /private/mapping.json --output /private/apply-plan.json
```

The command prints `review-plan-sha256=...`. Inspect every operation's account,
media identity, before/after values, excluded fields and omitted record counts.
The SHA-256 covers the canonical complete plan, including hashes of both snapshots
and the mapping. It is an explicit review token, not a signature or proof of source
trust. Any plan edit requires a newly calculated and reviewed digest.

## Apply, restart and rollback

Use the reviewed 64-character digest in place of `REVIEWED_DIGEST`. The token file
contains an existing elevated target token only and should be owner-readable.
The tool never signs in or accepts a password. HTTPS validates certificates;
loopback HTTP is permitted and trusted LAN HTTP requires `--allow-http`.

```sh
python3 tools/adoption/jellyfin_state_apply.py apply \
  --plan /private/apply-plan.json --approve-sha256 REVIEWED_DIGEST \
  --journal /private/import-journal --url https://your-tesserafin.example \
  --token-file /private/tesserafin-token --quiescent-target
```

Before each POST, the tool reads the current state, refuses a conflict and
publishes an immutable intent file, fsyncing the private file and directory.
The POST sets exact values rather than incrementing counts; a subsequent GET
must confirm the state and unchanged guarded fields. Successful observations
are recorded separately. Outputs omit tokens and API error bodies.

Re-run the same command, plan, digest and journal after an ordinary API failure.
An intent with live state matching its before value may be retried; matching
the intended after value reconciles an uncertain POST result without a duplicate
write. An applied marker with changed live state is refused, even if another
client cleared the values. Keep the journal; starting a new journal is not a
way to bypass a refusal.

```sh
python3 tools/adoption/jellyfin_state_apply.py rollback \
  --plan /private/apply-plan.json --approve-sha256 REVIEWED_DIGEST \
  --journal /private/import-journal --url https://your-tesserafin.example \
  --token-file /private/tesserafin-token --quiescent-target
```

Rollback visits intents in reverse order and restores only this journal's
recorded fields, only when live state still matches the intended after state.
Detected intervening edits are preserved by refusing the operation. It does not
undo API logs, access timestamps, database row allocation or other server side
effects. It is state recovery, not a database restore. Once rollback begins,
apply cannot resume in that journal; rollback itself can be resumed.

The journal directory must be owned by the invoking user and private (`0700`);
its regular files are owner-only (`0600`), published exclusively without
replacement. Symlinks, unexpected entries, a different plan and an existing
`.lock` refuse execution. On process death a stale `.lock` can remain. Confirm
that no process is active, preserve and inspect the journal and live target,
then remove **only the empty lock directory** with `rmdir`; do not delete intents
or completion markers. This implementation uses POSIX ownership/fsync/hard-link
facilities; native Windows execution is not qualified.

Requests, bytes and elapsed time share the capture tool's bounds. Exhaustion or
network errors can leave a partly applied plan, explicitly recoverable through
its journal; they never imply all-or-nothing execution.

## Evidence and limits

The Python suite exercises real loopback HTTP with two distinct mapped users,
exact saved position, idempotent apply/rollback, uncertain POST outcomes,
intent/publication failures, changed media/account/server identity, private
journals and refusal to follow credential-bearing redirects. Negative controls
must distinguish the two users and detect lost state; an in-memory fake API is
not evidence of the actual server's DTO semantics.

The [operator guide](jellyfin-to-tesserafin.md) walks the whole path, and
`rehearse_player_resume.py` replays it up to a real Resume in the Web player.

Actual source-image → target-image rehearsal is a separate acceptance gate.
Record exact image IDs/digests, candidate source and selected source version,
and prove before/apply/restart/rollback preservation before promoting this tool
for household use. A same-image backup recovery and a historical forward schema
migration are different proofs. Neither this document nor loopback tests claim
cross-version account migration, every media format or
complete Jellyfin preservation.
