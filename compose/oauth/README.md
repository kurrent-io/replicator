# Replicator OAuth: local end-to-end test bed

Runs a test identity provider and three KurrentDB nodes in Docker, then runs Replicator from this branch on the host against them. It follows KurrentDB's "Testing with a local identity server" guide (`docs/server/security/user-authentication.md` in the KurrentDB repo) and uses the same IdentityServer 4 image as KurrentDB's own OAuth tests.

| Service | Host port | Auth |
|:--|:--|:--|
| `idsrv4` (IdentityServer 4) | `5080` (http), `5001` (https) | issues tokens; client `replicator` / secret `replicator-secret` gets role `$admins`, 5-minute tokens |
| `kurrentdb-basic` | `2113` | basic, `admin:changeit` |
| `kurrentdb-oauth` | `2114` | OAuth only |
| `kurrentdb-oauth2` | `2115` | OAuth only |

All nodes use TLS with a test CA generated into `certs/`. Replicator trusts it through `tlsCaFile` in the connection strings. Data is in memory, so restarting a node empties it.

## Setup

1. OAuth in KurrentDB needs a license key. Copy `.env.example` to `.env` and set `KURRENTDB_LICENSE_KEY`. `.env` is git-ignored.
2. Generate certificates and start everything:

   ```bash
   ./scripts/gen-certs.sh
   docker compose up -d
   ```

3. Check the OAuth nodes picked up the IdP. Their logs should show "OAuthAuthentication ... plugin enabled" and "Issuer signing keys have been retrieved":

   ```bash
   docker compose logs kurrentdb-oauth | grep -i -E "oauth|signing keys|licen"
   ```

## Scenarios

Each scenario folder holds a Replicator `config/appsettings.yaml`. Run it with `./scripts/run-replicator.sh <scenario>`, which builds Replicator in Release and runs it with the scenario folder as the working directory, with the HTTP API on `localhost:5005`. Delete `scenarios/<name>/checkpoint` to start a scenario from scratch.

Helpers:
- `./scripts/write-events.sh <basic|oauth|oauth2> <stream> <count> [delay-seconds]` appends events over HTTP.
- `./scripts/stream-head.sh <node> <stream>` prints the last event number.
- `./scripts/get-token.sh` prints a token for the `replicator` client.

Use a fresh stream name for each run.

### 1. Basic source → OAuth sink (client credentials, secret file)

```bash
./scripts/write-events.sh basic s1-a 100
./scripts/run-replicator.sh 1-basic-to-oauth        # in another terminal
./scripts/stream-head.sh oauth s1-a                 # expect 99
```

To test across token expiry, keep writing while Replicator runs for at least 3 token lifetimes (5 minutes each):

```bash
./scripts/write-events.sh basic s1-long 1000 1
```

Expected: Replicator logs "access token acquired" about every 2.5 minutes, no write failures, and the target stream head matches the source.

### 2. OAuth source (client credentials) → basic sink

```bash
./scripts/write-events.sh oauth s2-a 100
./scripts/run-replicator.sh 2-oauth-to-basic
./scripts/stream-head.sh basic s2-a                 # expect 99
```

For the token-expiry run, write slowly for at least 15 minutes (`./scripts/write-events.sh oauth s2-long 1000 1`). Then record which of these happened:
- (a) No reader restart or subscription drop happened at token expiry.
- (b) The read and/or the realtime subscription were ended at expiry and restarted with a fresh token.

Either way the target must end up with every event.

### 3. OAuth source (client credentials) → OAuth sink (token file, rotated)

```bash
./scripts/refresh-token-file.sh 60 &                # keeps tokens/sink.token fresh
./scripts/write-events.sh oauth s3-a 100
./scripts/run-replicator.sh 3-oauth-to-oauth-tokenfile
./scripts/stream-head.sh oauth2 s3-a                # expect 99
```

### 4. Token endpoint outage

While scenario 1 or 2 is replicating, run:

```bash
docker compose stop idsrv4
```

Wait past a token lifetime (more than 5 minutes), then:

```bash
docker compose start idsrv4
```

Expected: rate-limited warnings, replication pauses, then resumes on its own with no process restart.

### 5. Rejected token / misconfiguration

- **Wrong audience.** Change `Audience` in `kurrentdb/oauth.conf` and run `docker compose up -d --force-recreate kurrentdb-oauth`. Each rejection fetches a new token, because idsrv4 issues a new value every time, so retries settle at one every 30 s. Expected: KurrentDB rejects the token as Unauthenticated. Replicator logs a warning, quarantines the token, re-tests it once a minute, and recovers once the audience is fixed.
- **Missing role.** Remove the `Claims` from the `replicator` client in `idsrv4/idsrv4.conf.json`, then recreate `idsrv4` and restart the OAuth node so it fetches idsrv4's new signing key.
  - Sink: with KurrentDB's default ACLs, any authenticated identity can append to user streams, so plain writes still succeed. Metadata writes and deletes need `$admins`.
  - Reader: reading `$all` needs `$admins`. Replicator retries the subscription with backoff and logs PermissionDenied warnings, and replication doesn't start until the role is restored.

## Known issues

- Recreating `idsrv4` generates a new signing key. Restart the OAuth nodes afterwards so they fetch it; otherwise every token is rejected as Unauthenticated.

## Teardown

```bash
docker compose down
```

`certs/`, `tokens/` and checkpoints are git-ignored; delete them to start clean.
