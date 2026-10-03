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
| `defaultTokenLifetimeSeconds` | no | Lifetime to assume when the token response has no `expires_in`. If unset and the provider omits `expires_in`, token acquisition fails with an error explaining this option. Must be > 0. |
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
| `TokenGate` | `WaitForToken(IAccessTokenSource, CancellationToken)`: retries token acquisition with capped exponential backoff and rate-limited logging until it succeeds or is cancelled. Takes a `TimeProvider` for testing. |
| `GrpcAuthentication` | `static void Apply(EventStoreClientSettings settings, IAccessTokenSource source)` — sets the sentinel `DefaultCredentials` and replaces `OperationOptions.GetAuthenticationHeaderValue`. Also `IAccessTokenSource? Create(GrpcAuthOptions)` factory. |

### Wiring

- `replicator.Settings.EsdbSettings` gains `AuthSettings Auth { get; init; }` (bound from `auth`). `AuthSettings` is a settings-binding record in the host project; `Startup` maps it to `GrpcAuthOptions`.
- `GrpcConfigurator` gets a constructor `GrpcConfigurator(GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth)` (mirroring how `TcpConfigurator` receives `pageSize`). `ConfigureReader` applies `readerAuth`; `ConfigureWriter` applies `sinkAuth`. The `IConfigurator` interface is unchanged.
- Inside `ConfigureEventStoreGrpc`: `EventStoreClientSettings.Create(connectionString)` → validate → if type is not `connectionString`, create the token source and `GrpcAuthentication.Apply` → build client. `GrpcEventReader` and `GrpcEventWriter` receive the side's `IAccessTokenSource?` (null for `connectionString`) for `TokenGate`. Each side gets its own token source instance; nothing is shared between reader and sink.
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

Response handling (RFC 6749 §5.1):

- HTTP 200 with JSON body. `access_token` is required and non-empty. `token_type` is required and must equal `Bearer` (case-insensitive); any other value or absence is rejected.
- `expires_in` must be a positive integer number of seconds (JSON number or numeric string, as some providers send it), at most 86 400 (24 h; larger values are clamped and logged once). Zero, negative, fractional, malformed or overflowing values are rejected.
- If `expires_in` is absent, the configured `defaultTokenLifetimeSeconds` is used. If that is not configured either, the response is rejected with an error telling the operator to set it. There is no silent default.
- Non-2xx responses are errors; the OAuth `error` / `error_description` fields are included in the exception message when the body is a JSON error response.
- Redirects are not followed (`AllowAutoRedirect = false`). Any 3xx response is an error naming the configured endpoint, because following a 307/308 would replay the client secret or assertion to another origin.
- Each request has a 30-second timeout.

Caching and refresh:

- Cached `(token, expiresAt)`. `expiresAt = requestStartTime + lifetime` (request start, not response time, to stay conservative).
- `GetAccessToken` returns the cached token if `now < expiresAt - refreshWindow`, where `refreshWindow = min(refreshBeforeExpirySeconds, lifetime / 2)`.
- Otherwise a refresh is needed. Refreshes are single-flight via a shared in-flight `Task`: the first caller starts the request and stores the task; every caller that arrives while it is running awaits the same task and observes the same result or exception. The stored task is cleared when it completes.
- If a refresh fails while the cached token is still usable (`now < expiresAt - 30s`), log a warning and return the cached token.
- If there is no usable token, throw `OAuthTokenException` (message includes HTTP status and OAuth `error` / `error_description` when present; never the request body, secret, assertion or token).
- Failure cooldown: after a failed refresh, calls within the next 5 seconds do not start a new request. They return the cached token if it is usable, otherwise rethrow the last failure immediately. This bounds the token-endpoint request rate during an outage to one per 5 seconds per side, and keeps queued callers from each waiting up to 30 seconds in turn.
- `GetAccessToken` honours its `CancellationToken` for the caller's wait. The shared request itself is cancelled only by its 30-second timeout or disposal, so one caller cancelling does not fail the others.

Tokens are lazily fetched on first use, so a token-endpoint outage at startup does not crash DI construction.

### Token file flow

- On first call and whenever `now - lastReadAt >= tokenFileReloadSeconds`, read the file, trim, cache.
- Empty or missing file: if a previously read token exists, log a warning and keep using it; otherwise throw `OAuthTokenException`.
- No JWT parsing; the external process owns validity.

### Failure handling and recovery

Note from the decompiled client: `GetAuthenticationHeaderValue` is always invoked with `CancellationToken.None`, so any token fetch that happens inside the gRPC call path cannot be cancelled by the caller. The design therefore acquires tokens *before* starting gRPC calls wherever a caller can wait, and treats the header hook as a cache read in the normal case.

**Sink (writer).** Today, after `SinkPipe` exhausts its short retry policy (10 incremental retries, ~0.5 s), the writer task faults and `Replicator` does not observe it, so the channels fill and replication stalls permanently. That is an existing behaviour for KurrentDB outages and stays out of scope, but token-endpoint outages must not trigger it. When the sink uses OAuth, `GrpcEventWriter` receives the side's `IAccessTokenSource` and, before every write, calls `TokenGate.WaitForToken(source, cancellationToken)`:

- Calls `GetAccessToken(ct)`. On `OAuthTokenException` it logs a warning (first failure, then at most once a minute while it continues) and retries with exponential backoff (1 s, 2 s, 4 s … capped at 30 s) until a token is obtained or `ct` is cancelled.
- On success after failures, logs that sink authentication recovered.
- The write then runs; the header hook finds the freshly cached token.

The write blocks while the token endpoint is down, which applies backpressure through the existing bounded channels. When the endpoint recovers, replication resumes with no restart. Non-token failures are unchanged.

**Reader.** A token failure fails the `ReadAllAsync` call. `ReaderPipe` logs it and stops, and `Replicator` restarts the read from the last checkpoint after `RestartDelayInSeconds` (when `RunContinuously`). No change is needed. The reader also calls `TokenGate.WaitForToken` once at the start of `ReadEvents` so a startup outage is logged clearly and waited out instead of turned into a restart loop.

**Realtime subscription.** `Realtime.Start` currently sets `_started = true` before `SubscribeToAllAsync` succeeds, and `HandleDrop` resubscribes via an unobserved `Task.Run(Start)`. A failed resubscribe leaves `_started` stuck at `true`, so the subscription is never restored. This is a latent bug that token failures make likely, and it is fixed as part of this work:

- `_started` is set only after `SubscribeToAllAsync` succeeds; on failure it stays `false`, and the exception propagates to the caller (`ReadEvents`, which is already covered by the restart loop).
- `HandleDrop` (any reason except `Disposed`) starts an observed resubscribe loop: `WaitForToken`, then subscribe, retrying with the same backoff on any failure, logging each failure. A `CancellationTokenSource` owned by `Realtime` cancels it on dispose. At most one resubscribe loop runs at a time.
- `Realtime` gets an internal constructor that takes a `Func<CancellationToken, Task<StreamSubscription>>` subscribe delegate so the loop is unit-testable without a server.

**Long-lived calls and token expiry.** Credentials are evaluated when a call starts. If KurrentDB terminates `ReadAllAsync` or the subscription when the token expires, the paths above recover with a fresh token. Whether it does is confirmed in the manual end-to-end test. If it causes frequent restarts, proactively recycling the read before expiry is a follow-up.

### Validation (startup, fail fast)

Collected per side and reported together with the side name (`reader` / `sink`), e.g. `Invalid reader auth configuration: clientId is required; tls must be enabled for OAuth`.

- `type` is not `connectionString` and `protocol` is not `grpc` → error.
- OAuth type and the connection string contains credentials (`DefaultCredentials != null` after parsing) → error (ambiguous; remove `user:pass@`).
- OAuth type and `ConnectivitySettings.Insecure` (`tls=false`) → error (bearer tokens are not sent over insecure channels).
- `oauthClientCredentials`: `tokenEndpoint` absolute URL with `https` (or `http` + loopback); `clientId` non-empty; exactly one of `clientSecret` / `clientSecretFile` / `clientAssertionFile`; `clientSecretFile` exists and is non-empty; `clientAssertionFile` exists (content checked at request time since it rotates); `clientAuthentication` ∈ {`post`, `basic`}; `refreshBeforeExpirySeconds` ≥ 0; `defaultTokenLifetimeSeconds` > 0 when set; `additionalParameters` does not contain reserved keys.
- `oauthTokenFile`: `tokenFile` set; `tokenFileReloadSeconds` > 0. File existence is not required at startup (the sidecar may write it after the pod starts).
- OAuth-only options set while `type` is `connectionString` → warning (likely misconfiguration), not error.

### Logging and secrets

- Never log tokens, client secrets, client assertions, or token-request bodies.
- Info: token acquired for `<side>`, with expiry time and token endpoint host. Debug: cache hits/refresh decisions.
- `EnvConfigProvider` currently prints every `REPLICATOR_*` variable value to stdout, which already leaks basic-auth passwords (both `esdb://user:pass@` and the TCP form `DefaultUserCredentials=admin:changeit`). Change it to print `***` instead of the value for any key that ends with `ConnectionString` or contains an `Auth` segment (except `…:Auth:Type`). Key names are still printed so operators can see what was set.

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
  - concurrent callers trigger exactly one token request, both when it succeeds and when it fails (all callers see the same exception)
  - failure cooldown: calls within 5 s of a failure make no request and rethrow; after the cooldown a new request is made
  - one caller cancelling does not cancel the shared request for others
  - refresh failure with valid cached token returns cached token; with expired token throws `OAuthTokenException` carrying `error`/`error_description`
  - response validation: missing/empty `access_token`; missing or non-Bearer `token_type`; `expires_in` zero, negative, fractional, malformed, numeric string, > 24 h (clamped); missing `expires_in` with and without `defaultTokenLifetimeSeconds`
  - 3xx response is an error and the redirect target receives no request
  - assertion file re-read per request
  - exception messages and logs contain no secret/assertion/token
- `TokenGate`: backoff sequence and cap, cancellation, rate-limited logging, recovery log.
- Sink recovery acceptance test: `GrpcEventWriter` over a capturing-handler client with a token source that fails N times then succeeds; the write blocks during failures, then is sent with the new token; cancelling during the outage ends the wait promptly.
- `Realtime`: a failed initial subscribe leaves it restartable; a drop followed by failing resubscribes (token source down) keeps retrying, then subscribes once the source recovers; dispose stops the loop; never two concurrent loops.
- `TokenFileSource`: reload interval, trimming, missing/empty file with and without previous token.
- `GrpcAuthOptionsValidator`: every rule above, including the combined-errors message.
- `GrpcAuthentication.Apply` end-to-end through the real client: create an `EventStoreClient` for `esdb://localhost:2113?tls=true` with `CreateHttpMessageHandler` set to a capturing stub handler, invoke `AppendToStreamAsync` and `ReadAllAsync`, assert the captured request carries `authorization: Bearer <token from fake source>` and that a token change is reflected on the next call. (The stub returns a gRPC error response; the test only inspects the request.)
- `EnvConfigProvider` redaction: gRPC connection string with user-info, TCP connection string with `DefaultUserCredentials`, `Auth:ClientSecret`, `Auth:AdditionalParameters:*` are masked; `Auth:Type` and unrelated keys are printed.
- Configuration binding: YAML with reader basic + sink OAuth binds to independent `GrpcAuthOptions`, and vice versa.

Existing container-based tests keep running unchanged (`connectionString` default).

Manual end-to-end (documented in the PR, not automated — the KurrentDB OAuth plugin needs a licence and a real IdP): KurrentDB with OAuth + Entra ID as sink, basic-auth KurrentDB/EventStoreDB as reader; replicate, run past a token expiry, confirm continued replication and observe whether the subscription/read is terminated at expiry.

## Risks

- **Sink stall on KurrentDB outage** (pre-existing, unchanged): a sink write failure that is not a token failure still exhausts `SinkPipe` retries and stalls replication. Out of scope; noted for a follow-up.
- **Stream termination at token expiry** may cause restart churn on long reads; mitigated by existing restart logic and checked in the manual test.
- **Sentinel credential reliance** on `GetAuthenticationHeaderValue` being honoured: it is public API on the current client; a future move to `KurrentDB.Client` must re-verify it. Covered by the end-to-end header test so an upgrade that breaks it fails CI.
- **Role mapping** is the most likely customer misconfiguration (token accepted but `PermissionDenied`); the docs cover it and `PermissionDenied` errors are already logged by the reader/writer.
