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
| `GrpcAuthContext` | Per-side handle passed to reader/writer code: the side's `IAccessTokenSource?` plus the application shutdown token. `ValueTask EnsureToken(CancellationToken ct)` runs `TokenGate.WaitForToken` with `ct` linked to the shutdown token; a no-op when the side uses `connectionString`. `GrpcAuthContext.None` is the no-auth instance. |
| `AuthFailure` | `static bool IsTokenFailure(Exception)`: true if the exception chain (`InnerException`, `AggregateException` members, `RpcException.Status.DebugException`) contains `OAuthTokenException`, or an `RpcException` with `StatusCode.Unauthenticated` (token missing, expired or rejected). `PermissionDenied` is deliberately excluded: it is an authorization decision (possibly per-stream ACL), not a token problem, and keeps today's handling. |
| `GrpcAuthentication` | `static void Apply(EventStoreClientSettings settings, IAccessTokenSource source)` — sets the sentinel `DefaultCredentials` and replaces `OperationOptions.GetAuthenticationHeaderValue`. Also `IAccessTokenSource? Create(GrpcAuthOptions)` factory. |

### Wiring

- `replicator.Settings.EsdbSettings` gains `AuthSettings Auth { get; init; }` (bound from `auth`). `AuthSettings` is a settings-binding record in the host project; `Startup` maps it to `GrpcAuthOptions`.
- `GrpcConfigurator` gets a constructor `GrpcConfigurator(GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth, CancellationToken shutdown)` (mirroring how `TcpConfigurator` receives `pageSize`). `Startup` registers it with a factory that passes `IHostApplicationLifetime.ApplicationStopping` as `shutdown`, which fires before hosted services are stopped. `ConfigureReader` applies `readerAuth`; `ConfigureWriter` applies `sinkAuth`. The `IConfigurator` interface is unchanged.
- Inside `ConfigureEventStoreGrpc`: `EventStoreClientSettings.Create(connectionString)` → validate → if type is not `connectionString`, create the token source and `GrpcAuthentication.Apply` → build client. `GrpcEventReader` (and through it `Realtime` and `ScavengedEventsFilter`) and `GrpcEventWriter` receive the side's `GrpcAuthContext`. Each side gets its own token source instance; nothing is shared between reader and sink.
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

`GetAuthenticationHeaderValue` is always invoked by the client with `CancellationToken.None` (seen in the decompiled `EventStoreCallOptions`), so a token fetch inside the gRPC call path cannot be cancelled by the caller. The design therefore acquires tokens *before* every gRPC call Replicator makes, via `GrpcAuthContext.EnsureToken`, which waits out token-endpoint outages and is cancelled by application shutdown. In the normal case the header hook is then a cache hit. The hook still fetches if the cache is empty (a narrow race at expiry), and a failure there fails that call and is handled by the paths below.

Every wait introduced here is bound to the application shutdown token, so nothing outlives shutdown. No new disposal plumbing on `IEventReader` / `IEventWriter` is needed.

**Sink (writer).** Today, after `SinkPipe` exhausts its short retry policy (10 incremental retries, ~0.5 s), the writer shovel task faults and `Replicator` does not observe it, so the channels fill and replication stalls permanently. That stays as-is for KurrentDB outages (out of scope), but token-endpoint outages must not trigger it.

- `GrpcEventWriter.WriteEvent` calls `auth.EnsureToken(cancellationToken)` before each append, delete or metadata write. `PreparePipe` creates every `SinkContext` with `CancellationToken.None`, so the effective cancellation comes from the shutdown token linked inside `GrpcAuthContext`.
- While the token endpoint is down, the write waits with backoff (1 s, 2 s, 4 s … capped at 30 s) and logs a warning on the first failure and then at most once a minute. The bounded channels apply backpressure to the reader. When the endpoint recovers, the write proceeds with no restart, and recovery is logged.
- On shutdown during an outage, the wait throws `OperationCanceledException`. `ChannelExtensions.Shovel` already treats that as a normal stop, so the writer task completes.
- `Replicator`'s post-read drain loop (`while (sinkChannel.Reader.Count > 0)`) would then spin forever because nothing consumes the channel. It is changed to also exit when the writer task has completed, logging how many events were left unwritten; they are re-read from the last checkpoint on the next start (the checkpoint is only stored after a successful write, in `SinkPipe`).
- The prepare shovel can also be blocked: `PreparePipe` pushes into the bounded sink channel via `sinkChannel.Writer.WriteAsync(ctx, ctx.CancellationToken)`, and that token is `CancellationToken.None`. If the sink channel filled while the writer waited for a token, the prepare shovel stays blocked after the writer stops, and `Replicator` awaits `prepareTask` forever. `Replicator` is changed to create `writerCts` before the pipes and pass `writerCts.Token` to that channel write instead of `ctx.CancellationToken`. `Stop()` already cancels `writerCts`, so the blocked write throws `OperationCanceledException`, which `PreparePipe` already swallows, and the prepare shovel exits through its own (already cancelled) token. In a normal shutdown with a healthy writer nothing changes: the channel drains before `writerCts` is cancelled.

**Reader.** `GrpcEventReader` calls `auth.EnsureToken(cancellationToken)` before starting `ReadAllAsync`, and before the backwards `ReadAllAsync` in `GetLastPosition` (used by the metrics reporter). A token failure in the middle of the long-running read (after the call started) fails the enumeration. `ReaderPipe` logs it and stops, and `Replicator` restarts from the last checkpoint after `RestartDelayInSeconds`, which goes through `EnsureToken` again. No pipeline change is needed.

**Metrics reporter.** `Replicator.Report` calls `reader.GetLastPosition` every `ReportMetricsFrequencyInSeconds` and catches only `OperationCanceledException`, so any other exception ends the reporter for the rest of the run. With the gate above, a token outage makes `GetLastPosition` wait instead of throwing. In addition, `Report` is changed to catch and log (warning, rate-limited to once a minute) any other exception per iteration and continue, so a transient failure, auth or not, no longer stops metrics permanently.

**Reader auxiliary calls (scavenge filter).** `ScavengedEventsFilter` issues `GetStreamMetadataAsync` and `ReadStreamAsync` calls during the read. Today `StreamMetaCache.GetOrAddStreamMeta` turns *any* exception into `null`, and the filter treats `null` as "keep the event". If an auth failure took that path with `scavenge: true` (the Helm default), deleted or expired events would be replicated.

- `ScavengedEventsFilter` calls `auth.EnsureToken(CancellationToken.None)` before each metadata or stream-size call. The shutdown token linked inside the context still makes it cancellable. During a token-endpoint outage the filter waits instead of falling through.
- When the reader uses OAuth, `StreamMetaCache.GetOrAddStreamMeta` rethrows exceptions where `AuthFailure.IsTokenFailure(e)` is true, instead of returning `null`. `StreamMetaCache` gets a constructor flag `failClosedOnTokenFailure`, set only for OAuth readers. For `connectionString` readers (basic auth or anonymous), and for every other exception including `PermissionDenied`, today's warn-and-keep behaviour is unchanged, so existing deployments behave exactly as before. An auth failure there (a race at expiry, or the server rejecting the token) then fails the prepare step: `PreparePipe` retries it 10 times, and if it keeps failing the prepare shovel faults and replication stalls with the error logged. That fails closed: scavenged events are never copied because of an auth problem. A persistent server-side rejection is an operator configuration error (role mapping) and is documented as such.
- `GetOrAddStreamSize` already propagates exceptions, so it fails closed through the same path.

**Realtime subscription.** Today `Realtime.Start` sets `_started = true` before `SubscribeToAllAsync` succeeds, and `HandleDrop` resubscribes via an unobserved `Task.Run(Start)`. A failed resubscribe leaves `_started` stuck at `true`, so the subscription is never restored, and a reader restart during a pending resubscribe could open a second subscription. This latent bug becomes likely with tokens, so `Realtime` is reworked around one serialized operation:

- State: the current `StreamSubscription?` and a single in-flight `Task? _subscribing`, both guarded by a lock.
- `EnsureSubscribed(CancellationToken ct)` is the only way to subscribe. Under the lock: if a subscription is active, return. If `_subscribing` is running, await it (with `ct` applied via `WaitAsync`). Otherwise start `_subscribing = SubscribeLoop()` and await it.
- `SubscribeLoop()` repeats `auth.EnsureToken`, then `SubscribeToAllAsync`, retrying with the same capped backoff and logging each failure, until it succeeds or the shutdown token is cancelled. On success it stores the subscription and clears `_subscribing`.
- `Start()` (called from `ReadEvents`) becomes `EnsureSubscribed(cancellationToken)`. Concurrent foreground and recovery callers therefore share one attempt, and there is never more than one subscription.
- `HandleDrop` (any reason except `Disposed`) clears the stored subscription under the lock and calls `EnsureSubscribed(CancellationToken.None)` without awaiting it. The loop is still bound to shutdown, and any exception other than cancellation is caught and logged inside the loop, so nothing is unobserved.
- `Realtime` gets an internal constructor taking a `Func<CancellationToken, Task<StreamSubscription>>` subscribe delegate, `GrpcAuthContext` and `TimeProvider`, so all of this is unit-testable without a server.

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
- `GrpcAuthContext`: no-op for `None`; cancelled by either the caller token or the shutdown token.
- `AuthFailure.IsTokenFailure`: direct `OAuthTokenException`, wrapped in `RpcException.Status.DebugException` and `AggregateException`, `Unauthenticated`; negative cases including `PermissionDenied`.
- `GrpcEventWriter` over a capturing-handler client with a token source that fails N times then succeeds: the write waits during failures, then is sent with the new token. Cancelling the shutdown token during the outage ends the wait with `OperationCanceledException`.
- Pipeline acceptance test through `Replicator.Replicate` with a fake reader producing events and a test `IEventWriter` that gates each write on `GrpcAuthContext.EnsureToken` like `GrpcEventWriter`, using a token source that fails, then recovers: all events are written after recovery with no restart. A second case uses a sink `BufferSize` of 1 and a prepare buffer larger than the event count, so that the sink channel is full and the prepare shovel is blocked on `WriteAsync` when shutdown is triggered during the outage. `Replicate` must return within a bounded time (no drain-loop or `prepareTask` hang), the unwritten count is logged, and the checkpoint is not advanced past unwritten events.
- Metrics reporter: `GetLastPosition` waits during a token outage and resumes after recovery; a non-cancellation exception from `GetLastPosition` is logged and the next iteration still runs.
- `StreamMetaCache` / `ScavengedEventsFilter`: with `failClosedOnTokenFailure`, a token failure (`OAuthTokenException`, `Unauthenticated`) from the metadata call propagates instead of returning `null`, so the event is not kept; `PermissionDenied` and other failures still return `null`; without the flag (basic-auth reader), `Unauthenticated` still returns `null` exactly as today; `EnsureToken` is awaited before each auxiliary call, so a token outage that starts after the read began makes the filter wait, then filter correctly after recovery.
- `Realtime` via the subscribe delegate: a failed initial subscribe leaves it retryable; a drop followed by failing resubscribes keeps retrying, then subscribes once the source recovers; a foreground `Start` during a pending resubscribe shares the same attempt and the delegate is called once per attempt (never two live subscriptions); cancelling the shutdown token ends the loop.
- `TokenFileSource`: reload interval, trimming, missing/empty file with and without previous token.
- `GrpcAuthOptionsValidator`: every rule above, including the combined-errors message.
- `GrpcAuthentication.Apply` end-to-end through the real client: create an `EventStoreClient` for `esdb://localhost:2113?tls=true` with `CreateHttpMessageHandler` set to a capturing stub handler, invoke `AppendToStreamAsync` and `ReadAllAsync`, assert the captured request carries `authorization: Bearer <token from fake source>` and that a token change is reflected on the next call. (The stub returns a gRPC error response; the test only inspects the request.)
- `EnvConfigProvider` redaction: gRPC connection string with user-info, TCP connection string with `DefaultUserCredentials`, `Auth:ClientSecret`, `Auth:AdditionalParameters:*` are masked; `Auth:Type` and unrelated keys are printed.
- Configuration binding: YAML with reader basic + sink OAuth binds to independent `GrpcAuthOptions`, and vice versa.

Existing container-based tests keep running unchanged (`connectionString` default).

Manual end-to-end (documented in the PR, not automated — the KurrentDB OAuth plugin needs a licence and a real IdP): KurrentDB with OAuth + Entra ID as sink, basic-auth KurrentDB/EventStoreDB as reader; replicate, run past a token expiry, confirm continued replication and observe whether the subscription/read is terminated at expiry.

## Risks

- **Sink stall on KurrentDB outage** (pre-existing, unchanged): a sink write failure that is not a token failure still exhausts `SinkPipe` retries and stalls replication. Out of scope; noted for a follow-up.
- **Fail-closed stall on persistent token rejection** (OAuth readers only): a token the server rejects as unauthenticated stalls replication in the scavenge filter or sink with errors logged, rather than replicating incorrectly. Intended; documented in troubleshooting.
- **Stream termination at token expiry** may cause restart churn on long reads; mitigated by existing restart logic and checked in the manual test.
- **Sentinel credential reliance** on `GetAuthenticationHeaderValue` being honoured: it is public API on the current client; a future move to `KurrentDB.Client` must re-verify it. Covered by the end-to-end header test so an upgrade that breaks it fails CI.
- **Role mapping** is the most likely customer misconfiguration (token accepted but `PermissionDenied`); the docs cover it and `PermissionDenied` errors are already logged by the reader/writer.
