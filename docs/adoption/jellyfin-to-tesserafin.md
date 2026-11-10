# Moving from Jellyfin to Tesserafin: watch history and resume points

This is the operator path under
[#144](https://github.com/tesserafin/tesserafin/issues/144), from preparing the accounts
to checking the result in the player. It moves four fields per account and per media
item: **watched**, **play count**, **favourite** and **resume position**. It does not
create accounts, move passwords or permissions, or copy media.

The detailed safety rules live in the [capture and simulation guide](jellyfin-dry-run.md)
and the [state-application guide](jellyfin-state-apply.md). Read both before a real
import.

## Tested versions

| Part | Version tested |
| --- | --- |
| Source | Jellyfin **10.10.7** (official image `jellyfin/jellyfin@sha256:7ae36aab…`). Other versions are refused. |
| Target | Tesserafin Server **0.3.0** with its bundled production Web client. |
| Web client | Must accept a 0.3.0 server. Web `0.3.0` (main `497a0a43c9`) does. The image built from master `ac6570e75f` still bundles Web `a9a362eec7`, which shows **"Server update required"** and cannot sign in. See [Known limits](#known-limits). |
| Browser | Chromium 149 (Playwright 1.61.1), French UI. |

## 1. Prepare

1. **Media.** Both servers must see the same files. The folders can differ
   (for example `/srv/films` in Jellyfin and `/library/films` in Tesserafin).
   Copying media is your job, before the import. Let Tesserafin finish its library scan.
2. **Accounts.** Create every target account in Tesserafin first, with its own
   password. The import only maps onto accounts that already exist.
3. **Quiet servers.** Nobody plays, scans or edits anything on Tesserafin from the
   first capture to the end of the import. Also keep Jellyfin quiet during its capture.
4. **Backup.** Take a Tesserafin backup you have checked
   ([backup and recovery](../backup-recovery.md)). Rollback restores the
   imported fields only. It is not a database restore.
5. **Tokens.** Put one administrator API token per server in a private file
   (`chmod 600`). The tools never ask for a password.

## 2. Capture and map

```sh
python3 tools/adoption/jellyfin_dry_run.py capture --product jellyfin \
  --url https://jellyfin.example --token-file /private/jf-token --output /private/source.json
python3 tools/adoption/jellyfin_dry_run.py capture --product tesserafin \
  --url https://tesserafin.example --token-file /private/tf-token --output /private/target.json
```

Write `/private/mapping.json`. Take the user IDs from the two snapshots. Matching
names do not count. Each path pair replaces a source folder prefix with the target
folder that holds the same files:

```json
{
  "users": {"<jellyfin user id>": "<tesserafin user id>"},
  "paths": [{"source": "/srv/films", "target": "/library/films"}]
}
```

## 3. Dry run and review

```sh
python3 tools/adoption/jellyfin_dry_run.py plan --source /private/source.json \
  --target /private/target.json --mapping /private/mapping.json --output /private/simulation.json
python3 tools/adoption/jellyfin_state_apply.py prepare --source /private/source.json \
  --target /private/target.json --mapping /private/mapping.json --output /private/plan.json
```

`prepare` prints `review-plan-sha256=…`. Read each operation (account, file,
before and after values) and the `simulationCounts`. These statuses are **reported
and never written**:

- `ambiguous-source-path`: two Jellyfin items land on the same target file, for
  example a duplicate kept in an archive folder. Neither history is imported.
- `target-state-conflict`: Tesserafin already holds a different value, such as
  another resume position. It is kept.
- `nonempty-target-not-supported`: Tesserafin already holds a value for this item,
  even one that agrees with Jellyfin, such as a favourite. Nothing is merged.
- `item-unmatched`, `provider-review`, `resume-runtime-conflict`: there is no single
  safe file match. Fix the path mapping or leave the item out.

## 4. Apply

```sh
python3 tools/adoption/jellyfin_state_apply.py apply --plan /private/plan.json \
  --approve-sha256 <reviewed digest> --journal /private/journal \
  --url https://tesserafin.example --token-file /private/tf-token --quiescent-target
```

Running the same command again with the same journal changes nothing. Keep the
journal: rollback needs it.

## 5. Check in the player

Do this **after** you are happy with the import. From here on, playback writes
progress, and rollback refuses to overwrite it.

1. Sign in to Tesserafin Web as each imported account.
2. On the home page, open **Continuer de regarder** (*Continue Watching*) and
   select a film.
3. The button reads **Reprendre à m:ss** (*Resume from*). Check that the time is the
   one Jellyfin showed. Press it.
4. The video must start at that time and keep playing. Check a second account: each
   must get its own position.

## Rollback

Before anyone plays on the target:

```sh
python3 tools/adoption/jellyfin_state_apply.py rollback --plan /private/plan.json \
  --approve-sha256 <reviewed digest> --journal /private/journal \
  --url https://tesserafin.example --token-file /private/tf-token --quiescent-target
```

Rollback restores the four fields to what the journal recorded before the import,
and only where the value is still the imported one. Running it again changes
nothing. Once rollback has started, that journal cannot apply again. Re-import with a
fresh capture, a new review and a new journal. For anything wider, restore the
backup.

## Evidence

`tools/adoption/rehearse_player_resume.py` replays this whole guide on throwaway
containers. The `Adoption state application` workflow runs it on pull requests that
touch the adoption tools. The setup is:

- Jellyfin 10.10.7, with media under `/srv/films` and `/srv/films-archive`;
- Tesserafin from the commit under test, with media under `/library/films`;
- two accounts on each side;
- 7-minute H.264/AAC fixtures with the media time burnt into the picture.

| Scenario | Result (local run, target `sha256:688ddb67…` = server `ac6570e75f` + Web `497a0a43c9`) |
| --- | --- |
| Dry run | 2 operations; one ambiguous duplicate, one conflicting position and one pre-filled favourite reported and omitted |
| Apply / re-apply / restart | Both accounts get their own state. Re-apply leaves state and journal unchanged. State survives a container restart. |
| Rollback ×2 | Every account/item returns to the pre-import state. The second run is a no-op. Jellyfin is unchanged. |
| Untouched items | The ambiguous duplicate and the pre-filled item are unchanged after apply |
| Player, account 1 | Imported 100 s. Button "Reprendre à 1:40". `<video>` starts at **100.03 s** and is at 104.27 s four seconds later (DirectPlay). |
| Player, account 2 | Imported 250 s. Button "Reprendre à 4:10". `<video>` starts at **250.03 s** and is at 254.16 s (DirectPlay). The decoded frame shows `00:04:10.100`. |
| Same run on the master image (Web `a9a362eec7`) | **Fails**: the Web client shows "Server update required" |

**Tolerance:** the start position must be within **±5 s** of the imported one. That
covers one keyframe interval (2 s in the fixtures) plus HLS segment alignment if the
player transcodes. The two accounts are 150 s apart, so a swapped account cannot
pass.

## Known limits

- **Master image and Web client.** The Dockerfile on master still pins web-assets
  `a9a362eec7` (pre-renumbering, minimum server 1.0.0). Until a 0.3.0 web-assets image
  is published and pinned, an image built from master cannot sign in at all. The
  rehearsal fails on master for that reason alone.
- Only Jellyfin 10.10.7 to Tesserafin 0.3.0. Only Movie, Episode and Audio items
  matched by path. At most 128 operations per plan.
- `LastPlayedDate`, likes, ratings, playlists, accounts, passwords, permissions and
  library settings are not moved.
- A target that already holds any value for an item is skipped, never merged.
- There is no lock on the servers. "Quiet" is your responsibility.
- Tested with direct play in one browser on fixture media. Other clients, real
  libraries and transcoding paths are not covered by this evidence.
