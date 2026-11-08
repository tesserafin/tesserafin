# Remote access R2 skeletons

This branch contains the first concrete skeletons for the managed publication path of #241.

## Delivered in this branch

- `GET /System/RemoteAccess/Diagnostics` (R1-P) — elevation required
- `PublicationInvariant` (R0-B) + unit tests
- `IManagedCaddyService` + `ManagedCaddyService` (R2-L atomic apply/rollback)
- `GatedManagedCaddyService` that enforces both gates before Apply
- `SystemProcessRunner` (real Process.Start implementation)
- `FileRestoreTokenStore` for crash-survivable restore snapshots
- Process and network-config abstractions
- `ServerNetworkConfigApplier` skeleton (in-memory restore token)
- `CredentialStrengthGate` (D-5) + unit tests
- DI registration (`AddManagedRemoteAccess`) wired in `Startup`
- Admin publication controller (`GET/POST/DELETE /System/RemoteAccess/Publication`) + tests
- Rollback tests for validate/reload failure paths
- `docker-compose.public.yml` + companion `caddy/Caddyfile` (R2-D)

## Still required before merge

- Map `INetworkConfigurationStore` onto the real server configuration manager
- Wire `GatedManagedCaddyService` as the public `IManagedCaddyService` once facts/password sources exist
- Integration test against a disposable Caddy (optional, post-MVP)

Refs #241.
