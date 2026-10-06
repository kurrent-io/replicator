# Changelog
All notable changes to this project will be documented in this files.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]
### Added
- Replicator docs (hugo site) to repository. [replicator#90](https://github.com/EventStore/replicator/pull/90)
- Replicator docs workflows. [replicator#90](https://github.com/EventStore/replicator/pull/90)
- OAuth 2.0 authentication for the gRPC reader and sink (client credentials or token file), configured independently per side, with automatic token refresh and recovery. Helm values for extra env, volumes, service account and pod labels.

### Changed
- Updated workflows to add clear naming separation between replicator (app) and replicator (docs) workflows. [replicator#90](https://github.com/EventStore/replicator/pull/90)
- Updated hugo site to use hugo modules, removed blog content. [replicator#90](https://github.com/EventStore/replicator/pull/90)
- When environment variables are printed at startup, the values of connection strings, auth settings (except `auth.type`) and any value containing credentials (a URI with user info such as a MongoDB checkpoint connection string, or `DefaultUserCredentials=`) are masked as `***`.
- The realtime subscription used for scavenging resubscribes reliably after drops, and its metadata cache is rebuilt after a gap.
- Replication no longer hangs on shutdown when the writer has stopped; the metrics reporter keeps running after a failed position read.

### Fixed
- With `scavenge: true`, replication no longer stalls on a stream that has metadata but no events, such as the system streams KurrentDB 26 creates at startup (`$connectors-mngt/state-projection`): the scavenge filter treats it as empty instead of failing with StreamNotFound. Streams without metadata no longer log a warning per event. Auth failures on these reads still stop replication when OAuth is enabled. (DEV-1903)
