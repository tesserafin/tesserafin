# Remote access R2 skeletons

This branch contains the first concrete skeletons for the managed publication path of #241.

## Delivered in this branch

- `GET /System/RemoteAccess/Diagnostics` (R1-P) — elevation required
- `PublicationInvariant` (R0-B) + unit tests
- `IManagedCaddyService` + `ManagedCaddyService` (R2-L atomic apply/rollback)
- Process and network-config abstractions
- `CredentialStrengthGate` (D-5) + unit tests
- DI registration (`AddManagedRemoteAccess`) wired in `Startup`
- Rollback tests for validate/reload failure paths
- `docker-compose.public.yml` + companion `caddy/Caddyfile` (R2-D)

## Still required before merge

- Map `INetworkConfigurationStore` onto the real server configuration manager
- Persist the restore token across restarts
- Call both gates before any public apply in the production path
- Real `IProcessRunner` implementation (Process.Start with redirected output)
- Integration test against a disposable Caddy (optional, post-MVP)

Refs #241.
