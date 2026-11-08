# Remote access R2 skeletons

This branch contains the first concrete skeletons for the managed publication path of #241.

## Delivered in this branch

- `GET /System/RemoteAccess/Diagnostics` (R1-P) — elevation required
- `PublicationInvariant` (R0-B)
- `IManagedCaddyService` + `ManagedCaddyService` (R2-L atomic apply/rollback)
- Process and network-config abstractions
- `CredentialStrengthGate` (D-5)
- `docker-compose.public.yml` + companion `caddy/Caddyfile` (R2-D)

## Still required before merge

- Map `INetworkConfigurationStore` onto the real server configuration manager
- Register the services in DI
- Persist the restore token across restarts
- Call both gates before any public apply
- Unit tests for the rollback paths

Refs #241.
