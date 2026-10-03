# OAuth authentication for KurrentDB gRPC reader and sink

Date: 2026-10-03
Status: Draft (for review)

## Problem

A customer runs KurrentDB with OAuth authentication (Microsoft Entra ID as the identity provider). With OAuth enabled, KurrentDB no longer accepts basic username/password credentials, so Replicator cannot read from or write to that cluster: today the only way to authenticate the gRPC client is `user:pass@` in the connection string.

Replicator is a headless service, so it must authenticate as a machine identity (no user, no browser).

## Goals

1. Replicator authenticates to a KurrentDB cluster using an OAuth 2.0 access token, for the gRPC protocol.
2. Provider-agnostic: works with any OAuth 2.0 authorization server that supports the client credentials grant (Entra ID, Keycloak, Okta, Auth0, …). Entra ID is the first target and is documented explicitly; nothing in code is Entra-specific.
3. Two token sources:
   - **Client credentials** — Replicator obtains and refreshes tokens itself (RFC 6749 §4.4), authenticating to the token endpoint with a client secret or a client assertion (RFC 7523; covers Entra workload identity federation).
   - **Token file** — an external process keeps a valid access token in a file; Replicator reads it.
4. Reader (source) and sink (target) are configured completely independently. Any combination is valid: basic + OAuth, OAuth + basic, OAuth + OAuth with different providers/clients, basic + basic.
5. Tokens are refreshed before expiry without restarting Replicator, including across long-running reads and the realtime subscription.
6. Secrets and tokens are never logged.

## Non-goals

- OAuth for the TCP protocol (EventStore TCP clients; KurrentDB's OAuth applies to gRPC only), Kafka sink, or the HTTP transform.
- Interactive flows (authorization code, device code), refresh tokens, mTLS client authentication to the token endpoint, OIDC discovery (`authority` lookup). The token endpoint URL is configured directly.
- Custom CA trust for the token endpoint (system trust store is used; can be added later).
- Upgrading from `EventStore.Client.Grpc.Streams` 23.3.8 to `KurrentDB.Client`.
- Securing Replicator's own HTTP API.

## Background: how the gRPC client applies credentials

Verified by decompiling `EventStore.Client` 23.3.8 (`EventStoreCallOptions`, `EventStoreClientOperationOptions`):

- Every call (unary and streaming, including `ReadAllAsync` and `SubscribeToAllAsync`) builds `CallOptions` with `CallCredentials.FromInterceptor(...)` **iff** `userCredentials ?? settings.DefaultCredentials` is non-null.
- That interceptor sets the `authorization` header to the result of `await settings.OperationOptions.GetAuthenticationHeaderValue(credentials, ct)`. The default implementation returns `credentials.ToString()` (`Basic …` or `Bearer …`).
- `GetAuthenticationHeaderValue` is a public, settable `Func<UserCredentials, CancellationToken, ValueTask<string>>`, evaluated **per call, asynchronously**.

This is the integration seam: set `DefaultCredentials` to a sentinel bearer credential (so the client attaches call credentials) and replace `GetAuthenticationHeaderValue` with a function that returns `"Bearer " + await tokenSource.GetAccessToken(ct)`. No gRPC interceptor, custom `HttpMessageHandler`, or change to the reader/writer code is needed, and the client's TLS handling (`tls`, `tlsVerifyCert`, `tlsCaFile`) stays untouched.

`CallCredentials` are only sent over TLS channels by Grpc.Net.Client. OAuth therefore requires `tls=true` (the default); see Validation.

Existing call sites (`GrpcEventReader`, `GrpcEventWriter`, `Realtime`, `ConnectionExtensions`, `ScavengedEventsFilter`) pass no per-call credentials, so they all fall back to `DefaultCredentials` and pick up the token automatically.

## Configuration

A new optional `auth` section under both `replicator.reader` and `replicator.sink`. Each side is bound and validated independently; absence of the section means today's behaviour.

```yaml
replicator:
  reader:
    protocol: grpc
    connectionString: "esdb://source.example.com:2113?tls=true"
    auth:
      type: oauthClientCredentials
      tokenEndpoint: "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token"
      clientId: "<replicator-app-client-id>"
      clientSecretFile: /var/run/secrets/replicator/reader-client-secret
      scope: "api://<kurrentdb-app-id>/.default"
  sink:
    protocol: grpc
    connectionString: "esdb://admin:changeit@target.example.com:2113?tls=true"
    # no auth section: basic credentials from the connection string, as today
```

### `auth.type`

| Value | Meaning |
|---|---|
| `connectionString` (default) | Current behaviour. Credentials, if any, come from the connection string (`user:pass@`). |
| `oauthClientCredentials` | Replicator fetches and caches access tokens from `tokenEndpoint` using the client credentials grant. |
| `oauthTokenFile` | Replicator reads the access token from `tokenFile`. |

Matching is case-insensitive. An unknown value fails startup.

### Options for `oauthClientCredentials`

| Option | Required | Description |
|---|---|---|
| `tokenEndpoint` | yes | Absolute token endpoint URL. Must be `https`, except `http` is allowed for loopback hosts (local testing). |
| `clientId` | yes | OAuth client id. |
| `clientSecret` | one of three | Client secret value. Intended for env var injection (`REPLICATOR_READER_AUTH_CLIENTSECRET`). |
| `clientSecretFile` | one of three | Path to a file containing the client secret (e.g. mounted Kubernetes Secret). Read once at startup; trailing whitespace trimmed. |
| `clientAssertionFile` | one of three | Path to a file containing a signed JWT client assertion (RFC 7523), e.g. the Azure Workload Identity projected token (`AZURE_FEDERATED_TOKEN_FILE`). Re-read on every token request because the platform rotates it. |
| `clientAuthentication` | no | How a client secret is sent: `post` (default; `client_id`/`client_secret` form fields) or `basic` (HTTP Basic header, RFC 6749 §2.3.1). Ignored with `clientAssertionFile`. |
| `scope` | no | Space-separated scopes sent as `scope`. For Entra ID: `api://<app-id-uri>/.default`. |
| `additionalParameters` | no | Map of extra form fields added to the token request, e.g. `audience` (Auth0, Okta) or `resource` (Entra v1 / ADFS). Must not override `grant_type`, `client_id`, `client_secret`, `client_assertion`, `client_assertion_type`, or `scope`; doing so fails startup. |
| `refreshBeforeExpirySeconds` | no | Refresh the token when it has fewer than this many seconds left. Default `300`. Effective value is capped at half the token lifetime, so short-lived tokens still get reused. |

Exactly one of `clientSecret`, `clientSecretFile`, `clientAssertionFile` must be set.

### Options for `oauthTokenFile`

| Option | Required | Description |
|---|---|---|
| `tokenFile` | yes | Path to a file containing the raw access token (no `Bearer ` prefix; trailing whitespace trimmed). |
| `tokenFileReloadSeconds` | no | How often the file is re-read. Default `30`. |

### Environment variables

The existing `EnvConfigProvider` maps `REPLICATOR_A_B_C` to `Replicator:A:B:C`, so all options are settable via env vars without code changes, e.g. `REPLICATOR_SINK_AUTH_TYPE=oauthClientCredentials`, `REPLICATOR_SINK_AUTH_CLIENTSECRET=…`. `additionalParameters` entries map as `REPLICATOR_SINK_AUTH_ADDITIONALPARAMETERS_AUDIENCE=…`.

## Design

### Components

All new code in `src/Kurrent.Replicator.KurrentDb/Auth/`. Each unit has a single job and is testable in isolation.

| Unit | Responsibility |
|---|---|
| `GrpcAuthOptions` (record) | Plain options for one side: `Type` enum plus the fields above. No binding logic. |
| `GrpcAuthOptionsValidator` | Validates a `GrpcAuthOptions` against its parsed `EventStoreClientSettings`; returns all errors (not just the first). |
| `IAccessTokenSource` | `ValueTask<string> GetAccessToken(CancellationToken ct)`. |
| `ClientCredentialsTokenSource` | Token request, response parsing, caching, single-flight refresh. Takes an `HttpMessageHandler` (injectable for tests) and a `TimeProvider`. |
| `TokenFileSource` | Reads and caches the token file; reloads on interval. Takes a `TimeProvider`. |
| `GrpcAuthentication` | `static void Apply(EventStoreClientSettings settings, IAccessTokenSource source)` — sets the sentinel `DefaultCredentials` and replaces `OperationOptions.GetAuthenticationHeaderValue`. Also `IAccessTokenSource? Create(GrpcAuthOptions)` factory. |

### Wiring

- `replicator.Settings.EsdbSettings` gains `AuthSettings Auth { get; init; }` (bound from `auth`). `AuthSettings` is a settings-binding record in the host project; `Startup` maps it to `GrpcAuthOptions`.
- `GrpcConfigurator` gets a constructor `GrpcConfigurator(GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth)` (mirroring how `TcpConfigurator` receives `pageSize`). `ConfigureReader` applies `readerAuth`; `ConfigureWriter` applies `sinkAuth`. The `IConfigurator` interface is unchanged.
- Inside `ConfigureEventStoreGrpc`: `EventStoreClientSettings.Create(connectionString)` → validate → if type is not `connectionString`, create the token source and `GrpcAuthentication.Apply` → build client. Each side gets its own token source instance; nothing is shared between reader and sink.
- `Startup` rejects a non-default `auth.type` when that side's `protocol` is not `grpc`.

### Client credentials token flow

Request: `POST tokenEndpoint`, `application/x-www-form-urlencoded`:

- `grant_type=client_credentials`
- `scope` if configured
- client authentication:
  - secret + `post`: `client_id`, `client_secret` form fields
  - secret + `basic`: `Authorization: Basic base64(urlencode(client_id):urlencode(client_secret))`
  - assertion: `client_id`, `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`, `client_assertion=<file contents>`
- `additionalParameters`

Response: JSON with `access_token` (required), `token_type` (if present must be `Bearer`, case-insensitive), `expires_in` (seconds; if absent, assume 3600 and log a warning once).

Caching and refresh:

- Cached `(token, expiresAt)`. `expiresAt = requestStartTime + expires_in` (request start, not response time, to stay conservative).
- `GetAccessToken` returns the cached token if `now < expiresAt - refreshWindow`, where `refreshWindow = min(refreshBeforeExpirySeconds, lifetime / 2)`.
- Otherwise one caller performs the refresh (guarded by `SemaphoreSlim`); concurrent callers await the same refresh.
- If a refresh fails while the cached token is still valid (`now < expiresAt - 30s`), log a warning and return the cached token; the next call retries. If there is no usable token, throw `OAuthTokenException` (message includes HTTP status and the OAuth `error` / `error_description` fields when present, never the request body or secrets).
- Token requests have a 30-second timeout. No retry loop inside the source: a token failure surfaces as a failed gRPC call (the exception thrown from `GetAuthenticationHeaderValue` fails the call), and existing error handling applies unchanged:
  - Reader: `ReaderPipe` logs the error and stops; `Replicator` restarts the read from the last checkpoint after `RestartDelayInSeconds` (when `RunContinuously`).
  - Sink: `SinkPipe`'s retry policy (10 incremental retries, ~0.5 s total) applies, then the failure propagates exactly as a KurrentDB write failure does today.
  - Realtime subscription: `Realtime.HandleDrop` resubscribes.

A token-endpoint outage therefore behaves the same as an outage of the corresponding KurrentDB cluster does today. Making sink retries outage-tolerant is out of scope.

Tokens are lazily fetched on first use, so a token-endpoint outage at startup does not crash DI construction.

### Token file flow

- On first call and whenever `now - lastReadAt >= tokenFileReloadSeconds`, read the file, trim, cache.
- Empty or missing file: if a previously read token exists, log a warning and keep using it; otherwise throw `OAuthTokenException`.
- No JWT parsing; the external process owns validity.

### Long-lived calls

Credentials are evaluated when a call starts. `ReadAllAsync` and `SubscribeToAllAsync` are long-lived server-streaming calls; if KurrentDB terminates a stream when its token expires, the existing paths recover with a fresh token:

- `Realtime.HandleDrop` resubscribes (new call → new header).
- A failed `ReadAllAsync` enumeration is logged by `ReaderPipe`, and `Replicator` restarts the read from the last checkpoint after `RestartDelayInSeconds`.

No change to these paths is planned. Whether KurrentDB actually terminates streams on token expiry is to be confirmed in the manual end-to-end test (see Testing); if it causes noisy restarts, a follow-up can proactively recycle the read before expiry.

### Validation (startup, fail fast)

Collected per side and reported together with the side name (`reader` / `sink`), e.g. `Invalid reader auth configuration: clientId is required; tls must be enabled for OAuth`.

- `type` is not `connectionString` and `protocol` is not `grpc` → error.
- OAuth type and the connection string contains credentials (`DefaultCredentials != null` after parsing) → error (ambiguous; remove `user:pass@`).
- OAuth type and `ConnectivitySettings.Insecure` (`tls=false`) → error (bearer tokens are not sent over insecure channels).
- `oauthClientCredentials`: `tokenEndpoint` absolute URL with `https` (or `http` + loopback); `clientId` non-empty; exactly one of `clientSecret` / `clientSecretFile` / `clientAssertionFile`; `clientSecretFile` exists and is non-empty; `clientAssertionFile` exists (content checked at request time since it rotates); `clientAuthentication` ∈ {`post`, `basic`}; `refreshBeforeExpirySeconds` ≥ 0; `additionalParameters` does not contain reserved keys.
- `oauthTokenFile`: `tokenFile` set; `tokenFileReloadSeconds` > 0. File existence is not required at startup (the sidecar may write it after the pod starts).
- OAuth-only options set while `type` is `connectionString` → warning (likely misconfiguration), not error.

### Logging and secrets

- Never log tokens, client secrets, client assertions, or token-request bodies.
- Info: token acquired for `<side>`, with expiry time and token endpoint host. Debug: cache hits/refresh decisions.
- `EnvConfigProvider` currently prints every `REPLICATOR_*` variable value to stdout. Change it to redact values whose final key segment is `ClientSecret` or `Password`, and to mask the user-info part of keys ending in `ConnectionString` (`esdb://admin:***@host`). This fixes an existing leak of basic-auth passwords and prevents the new secret from leaking.

## Helm chart

Needed so secrets and identity can be injected without putting them in the configmap:

- `extraEnv` (list of `EnvVar`) and `extraEnvFrom` (list of `EnvFromSource`) on the container.
- `extraVolumes` / `extraVolumeMounts` for secret or token files.
- `serviceAccountName` and `podLabels` (Azure Workload Identity needs a federated service account and the `azure.workload.identity/use: "true"` pod label).
- `replicator.reader.auth` / `replicator.sink.auth` pass through to the generated `appsettings.yaml` like other keys; values docs warn against putting `clientSecret` there.

All new values default to empty, so existing installs render identically.

## Documentation

- New page `docs/src/content/docs/deployment/authentication.mdx`: auth types, option reference, KurrentDB role requirements, and three worked examples:
  1. Entra ID with client secret (app registration for KurrentDB exposing an app ID URI and an app role mapped to `$admins`; app registration for Replicator granted that role; `scope: api://<kurrentdb-app>/.default`).
  2. Entra ID with AKS Workload Identity (`clientAssertionFile: /var/run/secrets/azure/tokens/azure-identity-token`, federated credential, Helm `serviceAccountName` + `podLabels`).
  3. Generic provider (Keycloak/Okta) with `additionalParameters.audience`, and a token-file example.
- `deployment/configuration.mdx`: add the `auth.*` rows to the options table and link to the new page.
- `features/readers.mdx` / `features/sinks.mdx`: one-line note that gRPC supports OAuth.
- `CHANGELOG.md` entry.

Required permissions are called out: reading `$all`, reading stream metadata, writing to arbitrary streams, setting metadata and deleting streams require the token's role claim to map to `$admins` (or ACLs granting equivalent rights) on each cluster.

## Testing

Unit tests (TUnit, `test/Kurrent.Replicator.Tests/Auth/`), using a stub `HttpMessageHandler` and `FakeTimeProvider`:

- `ClientCredentialsTokenSource`
  - request shape for each client authentication mode (post, basic, assertion), with `scope` and `additionalParameters`
  - caches token; refreshes inside the refresh window; caps window at half lifetime
  - concurrent callers trigger exactly one token request
  - refresh failure with valid cached token returns cached token; with expired token throws `OAuthTokenException` carrying `error`/`error_description`
  - missing `access_token`, non-Bearer `token_type`, missing `expires_in` default
  - assertion file re-read per request
  - exception messages and logs contain no secret/assertion/token
- `TokenFileSource`: reload interval, trimming, missing/empty file with and without previous token.
- `GrpcAuthOptionsValidator`: every rule above, including the combined-errors message.
- `GrpcAuthentication.Apply` end-to-end through the real client: create an `EventStoreClient` for `esdb://localhost:2113?tls=true` with `CreateHttpMessageHandler` set to a capturing stub handler, invoke `AppendToStreamAsync` and `ReadAllAsync`, assert the captured request carries `authorization: Bearer <token from fake source>` and that a token change is reflected on the next call. (The stub returns a gRPC error response; the test only inspects the request.)
- `EnvConfigProvider` redaction.
- Configuration binding: YAML with reader basic + sink OAuth binds to independent `GrpcAuthOptions`, and vice versa.

Existing container-based tests keep running unchanged (`connectionString` default).

Manual end-to-end (documented in the PR, not automated — the KurrentDB OAuth plugin needs a licence and a real IdP): KurrentDB with OAuth + Entra ID as sink, basic-auth KurrentDB/EventStoreDB as reader; replicate, run past a token expiry, confirm continued replication and observe whether the subscription/read is terminated at expiry.

## Risks

- **Stream termination at token expiry** may cause restart churn on long reads; mitigated by existing restart logic and checked in the manual test.
- **Sentinel credential reliance** on `GetAuthenticationHeaderValue` being honoured: it is public API on the current client; a future move to `KurrentDB.Client` must re-verify it. Covered by the end-to-end header test so an upgrade that breaks it fails CI.
- **Role mapping** is the most likely customer misconfiguration (token accepted but `PermissionDenied`); the docs cover it and `PermissionDenied` errors are already logged by the reader/writer.
