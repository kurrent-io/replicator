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

Every client method Replicator uses (`AppendToStreamAsync`, `DeleteAsync`, `SetStreamMetadataAsync`, `GetStreamMetadataAsync`, `ReadStreamAsync`, `ReadAllAsync`, `SubscribeToAllAsync`) also takes an optional per-call `userCredentials`, which takes precedence over `DefaultCredentials`.

This is the integration seam, in two layers:

- **Per-call token (primary).** Replicator acquires the token itself before each call and passes `userCredentials: new UserCredentials(token)` (a bearer credential). The header then carries exactly that token, and the code that made the call knows which token was sent. That is what lets it report acceptance or rejection of a specific token value.
- **Fallback hook.** `DefaultCredentials` is set to a sentinel bearer credential and `GetAuthenticationHeaderValue` is replaced. For the sentinel it returns `"Bearer " + await tokenSource.GetAccessToken(None)`. For any other credential it returns `credentials.ToString()` unchanged. This only covers a call site that forgets to pass per-call credentials, so the request is never sent unauthenticated.

No gRPC interceptor or custom `HttpMessageHandler` is needed, and the client's TLS handling (`tls`, `tlsVerifyCert`, `tlsCaFile`) stays untouched.

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
| `IAccessTokenSource` | `ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct)`, `void Invalidate(AccessTokenLease lease)` and `void ReportAccepted(AccessTokenLease lease)`. `AccessTokenLease` is `(string Value, long Generation)`. The source increments `Generation` when the cached token *value* changes, and on every rejection-state transition: a quarantine starts, a probe lease is granted, a probe is accepted, or a lease expires. A refresh or file re-read that yields the *same* value does **not** increment it. So a rejection of a value that is still current is never lost to a routine reload, while a report from before a quarantine or probe is recognisably stale. A report is applied only if `lease.Generation` equals the current generation (which also implies `lease.Value` equals the current value); otherwise it is ignored and logged at Debug. `ReportAccepted` ends a rejected-token probe. `Invalidate(lease)` (if current) marks that token as **rejected** and drops it from the cache. The next `GetAccessToken` refreshes (client credentials) or re-reads the file (token file), subject to the same single-flight and cooldown rules. Rejected-token quarantine applies to both sources (see "Rejected tokens"). |
| `ClientCredentialsTokenSource` | Token request, response parsing, caching, single-flight refresh. Takes an `HttpMessageHandler` (injectable for tests), a `TimeProvider` and the application shutdown token. |
| `TokenFileSource` | Reads and caches the token file; reloads on interval or on `Invalidate`. Takes a `TimeProvider`. |
| `TokenGate` | `WaitForToken(IAccessTokenSource, CancellationToken)`: retries token acquisition with capped exponential backoff and rate-limited logging until it succeeds or is cancelled. Takes a `TimeProvider` for testing. |
| `GrpcAuthContext` | Per-side handle passed to reader/writer code: the side's `IAccessTokenSource?` plus the application shutdown token. `GrpcAuthContext.None` is the no-auth instance. Two operations: `ValueTask<CallAuth> AcquireCredentials(ct)` runs `TokenGate.WaitForToken` with `ct` linked to the shutdown token and returns `CallAuth(UserCredentials? Credentials, AccessTokenLease? Lease)` (both `null` without OAuth, so the client keeps using the connection string's credentials exactly as today), plus `ReportAccepted(CallAuth)` / `ReportFailure(CallAuth, Exception)` for long-lived calls that cannot use `Run`; `Run<T>(Func<CallAuth, CancellationToken, Task<T>> call, ct)` executes one bounded gRPC operation with token recovery (see "Token-aware calls"). Every call site passes `callAuth.Credentials` as the client method's `userCredentials` argument. Call sites that must remember which lease a result belongs to (the subscription) keep the `CallAuth` they were handed. |
| `AuthFailure` | `static bool IsTokenFailure(Exception)`: true if the exception chain (`InnerException`, `AggregateException` members, `RpcException.Status.DebugException`) contains `OAuthTokenException`, or an `RpcException` with `StatusCode.Unauthenticated` (token missing, expired or rejected). `PermissionDenied` is not a token failure (retrying with a new token does not help), and is handled separately where it matters (scavenge filter). |
| `GrpcAuthentication` | `static void Apply(EventStoreClientSettings settings, IAccessTokenSource source)` — sets the sentinel `DefaultCredentials` and replaces `OperationOptions.GetAuthenticationHeaderValue` with the fallback hook (sentinel → fetch from source; anything else → `credentials.ToString()`). Also `IAccessTokenSource? Create(GrpcAuthOptions)` factory. |

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
- Non-2xx responses are errors, reported as described under "no usable token" below (status and sanitised `error` code only).
- Redirects are not followed (`AllowAutoRedirect = false`). Any 3xx response is an error naming the configured endpoint, because following a 307/308 would replay the client secret or assertion to another origin.
- Each request has a 30-second timeout.

Caching and refresh:

- Cached `(token, expiresAt)`. `expiresAt = requestStartTime + lifetime` (request start, not response time, to stay conservative).
- `GetAccessToken` returns the cached token if `now < expiresAt - refreshWindow`, where `refreshWindow = min(refreshBeforeExpirySeconds, lifetime / 2)`.
- Otherwise a refresh is needed. Refreshes are single-flight via a shared in-flight `Task`: the first caller starts the request and stores the task; every caller that arrives while it is running awaits the same task and observes the same result or exception. The stored task is cleared when it completes.
- If a refresh fails while the cached token is still usable (`now < expiresAt - 30s`), log a warning and return the cached token. After `Invalidate` the token is no longer cached, so there is nothing to fall back to and a failed refresh throws.
- If there is no usable token, throw `OAuthTokenException`. Its message contains only the HTTP status, the token endpoint host and the OAuth `error` code **only if it is in a fixed allowlist** (`invalid_request`, `invalid_client`, `invalid_grant`, `unauthorized_client`, `unsupported_grant_type`, `invalid_scope`, `server_error`, `temporarily_unavailable`); any other value is reported as `non-standard error (omitted)`. Integer values of Entra ID's `error_codes` array (e.g. `7000215`) are included, since integers cannot carry credentials. Provider-supplied `error_description` / `error_uri` and the response body are never included in messages or logs, because a server may echo submitted credentials in them. The full `error_description` is available only through a separate opt-in: logged at Debug level after redacting every occurrence of the client secret, the client assertion and any `access_token` value seen so far (replaced by `***`).
- Failure cooldown: after a failed refresh, calls within the next 5 seconds do not start a new request. They return the cached token if it is usable, otherwise rethrow the last failure immediately. This bounds the token-endpoint request rate during an outage to one per 5 seconds per side, and keeps queued callers from each waiting up to 30 seconds in turn.
- `GetAccessToken` honours its `CancellationToken` for the caller's wait. The shared request runs under its own `CancellationTokenSource` linked to the application shutdown token and the 30-second timeout, never to an individual caller's token, so one caller cancelling does not fail the others, and shutdown aborts an in-flight request.

Tokens are lazily fetched on first use, so a token-endpoint outage at startup does not crash DI construction.

### Token file flow

- On first call and whenever `now - lastReadAt >= tokenFileReloadSeconds`, read the file, trim, cache.
- Empty or missing file: if a previously read token exists and has not been rejected via `Invalidate`, log a warning and keep using it; otherwise throw `OAuthTokenException`.
- After `Invalidate`, a re-read that returns the rejected value is subject to the rejected-token quarantine below.

### Rejected tokens

Shared by both sources, so behaviour is the same for client credentials (where some providers re-issue the same cached token) and token files (where the file may not have rotated yet):

- `Invalidate(lease)` (if current) records the rejected token value and the time `rejectedAt`, and bumps the generation.
- For 60 seconds after `rejectedAt` (the quarantine), a newly acquired value equal to the rejected one is treated as "no new token yet": `GetAccessToken` throws `OAuthTokenException` ("the token source returned a token the server just rejected"), and `Run` backs off instead of resending a known-bad token. The stale-token fallbacks never return the rejected value during the quarantine either.
- After the quarantine the same value may be used again by **one probe at a time**. The first `GetAccessToken` after the quarantine takes a per-source probe lease and receives the value; every other caller keeps getting the quarantine `OAuthTokenException` (and backs off in `Run`) while the lease is held. The lease ends when:
  - the probing call succeeds: `Run` calls `source.ReportAccepted(lease)` with the probe's lease (the lease grant bumped the generation, so only the probe's own report matches), which clears the rejection record so all callers get the token again (server-side fix detected without rotation);
  - the probing call is rejected: `Invalidate(lease)` starts a new 60 s quarantine;
  - or after 60 s with neither, e.g. the prober was cancelled before sending. Expiry then counts as a new quarantine.
  The rejected value is therefore sent by at most one call per minute per side, even with concurrent writers, the prepare pipe's concurrent filter calls and the metrics reporter.
- The token is handed to the call that holds the lease and travels in that call's `userCredentials`; nothing else calls `GetAccessToken` for it. The sentinel fallback hook goes through `GetAccessToken` like any other caller, so even a forgotten call site cannot bypass the lease.
- Any different value is accepted immediately and clears the rejection record.
- No JWT parsing; the external process owns validity.

### Failure handling and recovery

`GetAuthenticationHeaderValue` is always invoked by the client with `CancellationToken.None` (seen in the decompiled `EventStoreCallOptions`), so a token fetch inside the gRPC call path cannot be cancelled by the caller. The design therefore acquires the token *before* every gRPC call, cancellably, and passes it as per-call `userCredentials`. The header hook then just formats that credential and never fetches. Only the sentinel fallback fetches, and no Replicator call site relies on it.

Two kinds of token failure are handled the same way:

- the token cannot be obtained (`OAuthTokenException`), or
- KurrentDB rejects the token it was sent (`RpcException` with `Unauthenticated`), e.g. a token file not yet re-read after rotation, or a token revoked early.

Both are usually transient, so neither may permanently stop replication. Every wait introduced here is bound to the application shutdown token, and the shared token request is too, so nothing outlives shutdown. No new disposal plumbing on `IEventReader` / `IEventWriter` is needed.

**Token-aware calls.** `GrpcAuthContext.Run(call, ct)` is used for every bounded (non-streaming-for-the-life-of-the-run) gRPC operation: appends, deletes, metadata writes, metadata reads, stream-size reads, `GetLastPosition` and subscribe. It loops:

1. `lease = source.GetAccessToken` via `TokenGate` (capped backoff 1 s, 2 s, 4 s … 30 s, cancellable by `ct` and shutdown). `creds = new UserCredentials(lease.Value)`.
2. Invoke `call(callAuth, ct)` where `callAuth = CallAuth(creds, lease)`; the call passes `callAuth.Credentials` as `userCredentials`, so the request header is exactly `Bearer <lease.Value>`.
3. On success: `source.ReportAccepted(lease)`.
4. If the call throws and `AuthFailure.IsTokenFailure(e)` is true: `source.Invalidate(lease)`, log a warning (first failure, then at most once a minute; recovery is logged once), wait the next backoff step, and go to 1.
5. Any other exception propagates unchanged, so existing behaviour for non-auth errors is preserved.

Because the token travels with the call and results are reported with the lease that call received, the probe lease (see "Rejected tokens") is consumed by exactly the call that sends the probe. Acceptance or rejection is attributed to the exact acquisition it belongs to. A late result from an older call, even one that carried the same token value, cannot clear a newer quarantine or reject a newer accepted probe.

The loop has no attempt limit: a persistent rejection (for example a wrong audience in KurrentDB's OAuth config) shows up as a repeating warning and stalled progress, and replication resumes on its own once the token or the server config is fixed. Retrying is safe: a call rejected as `Unauthenticated` was not executed by the server.

**Sink (writer).** Today, after `SinkPipe` exhausts its short retry policy (10 incremental retries, ~0.5 s), the writer shovel task faults and `Replicator` does not observe it, so the channels fill and replication stalls permanently. That stays as-is for non-auth KurrentDB failures (out of scope), but token failures must not trigger it.

- `GrpcEventWriter` runs each append, delete and metadata write through `auth.Run(...)`. `PreparePipe` creates every `SinkContext` with `CancellationToken.None`, so the effective cancellation comes from the shutdown token linked inside `GrpcAuthContext`.
- During a token outage or rejection the write waits; the bounded channels apply backpressure to the reader. When tokens work again, the write proceeds with no restart.
- On shutdown during an outage, the wait throws `OperationCanceledException`. `ChannelExtensions.Shovel` already treats that as a normal stop, so the writer task completes.
- `Replicator`'s post-read drain loop (`while (sinkChannel.Reader.Count > 0)`) would then spin forever because nothing consumes the channel. It is changed to also exit when the writer task has completed, logging how many events were left unwritten; they are re-read from the last checkpoint on the next start (the checkpoint is only stored after a successful write, in `SinkPipe`).
- The prepare shovel can also be blocked: `PreparePipe` pushes into the bounded sink channel via `sinkChannel.Writer.WriteAsync(ctx, ctx.CancellationToken)`, and that token is `CancellationToken.None`. If the sink channel filled while the writer waited, the prepare shovel stays blocked after the writer stops, and `Replicator` awaits `prepareTask` forever. `Replicator` is changed to create `writerCts` before the pipes and pass `writerCts.Token` to that channel write instead of `ctx.CancellationToken`. `Stop()` already cancels `writerCts`, so the blocked write throws `OperationCanceledException`, which `PreparePipe` already swallows, and the prepare shovel exits through its own (already cancelled) token. In a normal shutdown with a healthy writer nothing changes: the channel drains before `writerCts` is cancelled.

**Reader.** `GrpcEventReader` calls `auth.AcquireCredentials(cancellationToken)` before starting the long-running `ReadAllAsync` and passes them as `userCredentials`. Once the first event (or end of stream) is received, it calls `auth.ReportAccepted(callAuth)`. A token failure during that read (at start, or if the server ends the stream) fails the enumeration. `ReadEvents` catches it, calls `auth.ReportFailure(callAuth, e)` (which invalidates the lease if `e` is a token failure), and rethrows. `ReaderPipe` logs it and stops, and `Replicator` restarts from the last checkpoint after `RestartDelayInSeconds`, acquiring credentials again. No pipeline change is needed. `GetLastPosition` (a bounded backwards read) goes through `auth.Run`.

**Metrics reporter.** `Replicator.Report` calls `reader.GetLastPosition` every `ReportMetricsFrequencyInSeconds` and catches only `OperationCanceledException`, so any other exception ends the reporter for the rest of the run. `GetLastPosition` now waits out token failures. In addition, `Report` is changed to catch and log (warning, rate-limited to once a minute) any other exception per iteration and continue, so a transient failure, auth or not, no longer stops metrics permanently.

**Reader auxiliary calls (scavenge filter).** `ScavengedEventsFilter` issues `GetStreamMetadataAsync` and `ReadStreamAsync` calls during the read. Today `StreamMetaCache.GetOrAddStreamMeta` turns *any* exception into `null`, and the filter treats `null` as "keep the event". If an auth problem took that path with `scavenge: true` (the Helm default), deleted or expired events would be replicated.

- Both auxiliary calls go through `auth.Run(...)` with `CancellationToken.None` as the caller token; the shutdown token linked inside the context makes them cancellable. Token failures are therefore waited out and retried inside the call and never reach `StreamMetaCache` (except as `OperationCanceledException` at shutdown).
- For OAuth readers, `StreamMetaCache.GetOrAddStreamMeta` must never turn an auth problem into "keep the event". It gets a constructor flag `failClosedOnAuthErrors`, set only for OAuth readers. With the flag, an exception where `IsTokenFailure(e)` is true, or an `RpcException` with `PermissionDenied`, is rethrown instead of returning `null`. A `PermissionDenied` on stream metadata means the token's role cannot see that stream's metadata, so the filter cannot decide safely. The prepare step then fails: `PreparePipe` retries 10 times, then the prepare shovel faults and replication stalls with the error logged. That is fail-closed, and it is a configuration error (the replicator identity must be mapped to `$admins` or have metadata read on all streams) that the docs call out. Restart Replicator after fixing roles.
- Without the flag (`connectionString` readers: basic auth or anonymous), today's warn-and-keep behaviour is unchanged for every exception, so existing deployments behave exactly as before.
- `GetOrAddStreamSize` already propagates exceptions, so it fails closed through the same path.

**Realtime subscription.** Today `Realtime.Start` sets `_started = true` before `SubscribeToAllAsync` succeeds, and `HandleDrop` resubscribes via an unobserved `Task.Run(Start)`. A failed resubscribe leaves `_started` stuck at `true`, so the subscription is never restored, and a reader restart during a pending resubscribe could open a second subscription. This latent bug becomes likely with tokens, so `Realtime` is reworked around one serialized operation:

- State, guarded by one lock: the current published `Attempt?` and a single in-flight `Task? _subscribing`.
- Each subscribe attempt is represented by an `Attempt` object, created inside the `auth.Run` callback *before* `SubscribeToAllAsync` is called. It holds the `CallAuth` the callback received, a `Dropped` flag, and later the `StreamSubscription`. The `subscriptionDropped` callback passed to `SubscribeToAllAsync` is a closure over that `Attempt`, so a drop is always correlated with the attempt (and lease) that produced it, however early it fires.
- `EnsureSubscribed(CancellationToken ct)` is the only way to subscribe. Under the lock: if an attempt is published and not dropped, return. If `_subscribing` is running, await it (with `ct` applied via `WaitAsync`). Otherwise start `_subscribing = SubscribeLoop()` and await it.
- `SubscribeLoop()` runs attempts through `auth.Run`, which handles token failures and invalidation, and additionally retries any other failure with the same capped backoff, logging each, until an attempt is published or the shutdown token is cancelled. When `SubscribeToAllAsync` returns, under the lock: if `attempt.Dropped` is already set (the drop beat the registration), the returned subscription is disposed and not published. The callback then throws an internal `SubscriptionDroppedEarlyException`, so `Run` does not call `ReportAccepted` for a lease whose subscription already failed, and the loop backs off and tries again (a token failure was already reported by `HandleDrop`). Otherwise the attempt is published and `_subscribing` is cleared.
- `HandleDrop(attempt, reason, exception)` (any reason except `Disposed`): first `auth.ReportFailure(attempt.CallAuth, exception)`. Then, under the lock, set `attempt.Dropped`.
  - If `attempt` is the published one, unpublish it and call `EnsureSubscribed(CancellationToken.None)` without awaiting it.
  - If it is still pending, do nothing more: the running loop sees `Dropped` and retries.
  - If it is neither (an old attempt), ignore it.
  The loop is bound to shutdown, and any exception other than cancellation is caught and logged inside it, so nothing is unobserved.
- `Start()` (called from `ReadEvents`) becomes `EnsureSubscribed(cancellationToken)`. Concurrent foreground and recovery callers share one attempt, and there is never more than one live subscription.
- `Realtime` gets an internal constructor taking a subscribe delegate `Func<CallAuth, Action<SubscriptionDroppedReason, Exception?>, CancellationToken, Task<StreamSubscription>>`, `GrpcAuthContext` and `TimeProvider`, so all of this, including drop timing, is unit-testable without a server.

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
  - one caller cancelling does not cancel the shared request for others; cancelling the shutdown token aborts an in-flight request
  - `Invalidate(lease)` forces the next call to refresh, still single-flight and subject to cooldown
  - after `Invalidate(lease)`, a failing token endpoint causes `GetAccessToken` to throw rather than fall back to the rejected token
  - quarantine: the endpoint re-issuing the rejected value within 60 s throws; after 60 s the same value is returned (probe); a different value is returned immediately
  - same-value reload does not lose a rejection: call 1 gets A (gen 1); the reload timer re-reads unchanged A (still gen 1) and a client-credentials refresh re-issues A (still gen 1); call 1 is then rejected, so `Invalidate` applies, quarantine starts, and no call is handed A until the quarantine ends (verified for both sources)
  - out-of-order reports with a re-issued value: call 1 gets A (gen 1); A is rejected via another call (quarantine, gen 2); after the quarantine the probe gets A (gen 3) and is accepted. A late `Invalidate` from call 1 (gen 1) does not re-quarantine A. Conversely, a late `ReportAccepted` from gen 1 arriving during the gen-2 quarantine does not clear it.
  - probe lease: with 10 concurrent callers at the quarantine boundary exactly one receives the rejected value and the rest throw; `ReportAccepted` releases the token to all; `Invalidate` during the probe restarts quarantine; `Invalidate`/`ReportAccepted` with a stale generation are ignored; an unreported lease expires after 60 s
  - `error` equal to the submitted client secret is not in the allowlist and is reported as `non-standard error (omitted)`; allowlisted codes and integer `error_codes` are reported
  - an error response whose `error_description`, `error_uri` and body echo the client secret and client assertion: exception message and all logs at Info/Warning contain neither; Debug output contains them only as `***`; a non-conforming `error` value is omitted
  - refresh failure with valid cached token returns cached token; with expired token throws `OAuthTokenException` carrying the HTTP status and `error` code
  - response validation: missing/empty `access_token`; missing or non-Bearer `token_type`; `expires_in` zero, negative, fractional, malformed, numeric string, > 24 h (clamped); missing `expires_in` with and without `defaultTokenLifetimeSeconds`
  - 3xx response is an error and the redirect target receives no request
  - assertion file re-read per request
  - exception messages and logs contain no secret/assertion/token
- `TokenGate`: backoff sequence and cap, cancellation, rate-limited logging, recovery log.
- `GrpcAuthContext`: `AcquireCredentials` returns `null` for `None`; cancelled by either the caller token or the shutdown token. `Run`: calls `ReportAccepted(lease)` after success with the lease the call used; retries `OAuthTokenException` and `Unauthenticated` with invalidation and backoff until success; propagates `PermissionDenied` and other exceptions immediately; shutdown ends the loop.
- `AuthFailure.IsTokenFailure`: direct `OAuthTokenException`, wrapped in `RpcException.Status.DebugException` and `AggregateException`, `Unauthenticated`; negative cases including `PermissionDenied`.
- `GrpcEventWriter` over a capturing-handler client with a token source that fails N times then succeeds: the write waits during failures, then is sent with the new token. Cancelling the shutdown token during the outage ends the wait with `OperationCanceledException`.
- Rejected-token recovery: `GrpcEventWriter` with a `TokenFileSource` whose file holds token A, over a capturing stub handler that records requests and answers with gRPC status `Unauthenticated` while it sees `Bearer A`. The test rotates the file to token B. Expected: the write is retried, the file is re-read via `Invalidate(leaseA)` without waiting for the reload interval, and the next request carries `Bearer B`. A second case removes the file after the rejection, then writes token B later: no request carries `Bearer A` after the rejection, and the write succeeds with B. A third case leaves A in the file and switches the stub to accept A after the rejection (a server-side fix): after the 60 s quarantine (advanced with `FakeTimeProvider`) A is sent once more and the write succeeds.
- Pipeline acceptance test through `Replicator.Replicate` with a fake reader producing events and a test `IEventWriter` that runs each write through `GrpcAuthContext.Run` like `GrpcEventWriter`, using a token source that fails, then recovers: all events are written after recovery with no restart. A second case uses a sink `BufferSize` of 1 and a prepare buffer larger than the event count, so that the sink channel is full and the prepare shovel is blocked on `WriteAsync` when shutdown is triggered during the outage. `Replicate` must return within a bounded time (no drain-loop or `prepareTask` hang), the unwritten count is logged, and the checkpoint is not advanced past unwritten events.
- Metrics reporter: `GetLastPosition` waits during a token outage and resumes after recovery; a non-cancellation exception from `GetLastPosition` is logged and the next iteration still runs.
- `StreamMetaCache` / `ScavengedEventsFilter`: with `failClosedOnAuthErrors`, `PermissionDenied` and token failures propagate instead of returning `null`, so the event is never kept on an auth problem; other failures still return `null`; without the flag (basic-auth reader), every failure including `Unauthenticated` and `PermissionDenied` still returns `null` exactly as today; a transient `Unauthenticated` on the metadata call is retried inside `auth.Run` and the filter then decides correctly; each auxiliary call acquires credentials through `auth.Run`, so a token outage that starts after the read began makes the filter wait, then filter correctly after recovery.
- `Realtime` early drop: the test delegate invokes the drop callback (reason `ServerError`, `Unauthenticated`) before its returned task completes. The attempt's lease is reported as failed, the returned subscription is disposed and never published, and the loop resubscribes; afterwards exactly one live subscription is published.
- `Realtime` lease attribution: subscribe with A (gen 1); the source then moves to token B (gen 2) for other callers; the subscription drops with `Unauthenticated`. `HandleDrop` reports failure with the stored gen-1 lease, which is stale and ignored, so B is not invalidated, and the resubscribe uses B. In a second case the source is still on A (gen 1) when the drop arrives: A is invalidated and quarantined, and the resubscribe waits for a different token.
- `Realtime` via the subscribe delegate: a failed initial subscribe leaves it retryable; a drop followed by failing resubscribes keeps retrying, then subscribes once the source recovers; a foreground `Start` during a pending resubscribe shares the same attempt and the delegate is called once per attempt (never two live subscriptions); cancelling the shutdown token ends the loop.
- `TokenFileSource`: `Invalidate(lease)` forces an immediate re-read; after it, a missing/empty file or a file still holding the rejected value throws during the quarantine, the same value is probed again after it, and a different value is accepted at once; reload interval, trimming, missing/empty file with and without previous token.
- `GrpcAuthOptionsValidator`: every rule above, including the combined-errors message.
- Per-call credentials through the real client: `GrpcEventWriter` (append, delete, set-metadata), `GrpcEventReader.GetLastPosition`, `ScavengedEventsFilter` metadata and stream-size reads, and the start of `ReadAllAsync` each send `authorization: Bearer <token handed out by Run/AcquireCredentials>`; a test hook that asserts the sentinel fallback is never invoked on these paths.
- Probe end-to-end: quarantine token A, stub that rejects A, then switches to accepting A. After 60 s (FakeTimeProvider), with 5 concurrent writes queued: exactly one HTTP request carries `Bearer A`, it succeeds, `ReportAccepted` releases A, and the other writes then send `Bearer A` and succeed.
- `GrpcAuthentication.Apply` fallback end-to-end through the real client: create an `EventStoreClient` for `esdb://localhost:2113?tls=true` with `CreateHttpMessageHandler` set to a capturing stub handler, invoke `AppendToStreamAsync` and `ReadAllAsync`, assert the captured request carries `authorization: Bearer <token from fake source>` and that a token change is reflected on the next call. (The stub returns a gRPC error response; the test only inspects the request.)
- `EnvConfigProvider` redaction: gRPC connection string with user-info, TCP connection string with `DefaultUserCredentials`, `Auth:ClientSecret`, `Auth:AdditionalParameters:*` are masked; `Auth:Type` and unrelated keys are printed.
- Configuration binding: YAML with reader basic + sink OAuth binds to independent `GrpcAuthOptions`, and vice versa.

Existing container-based tests keep running unchanged (`connectionString` default).

Manual end-to-end (documented in the PR, not automated — the KurrentDB OAuth plugin needs a licence and a real IdP): KurrentDB with OAuth + Entra ID as sink, basic-auth KurrentDB/EventStoreDB as reader; replicate, run past a token expiry, confirm continued replication and observe whether the subscription/read is terminated at expiry.

## Risks

- **Sink stall on KurrentDB outage** (pre-existing, unchanged): a sink write failure that is not a token failure still exhausts `SinkPipe` retries and stalls replication. Out of scope; noted for a follow-up.
- **Persistent token rejection** (`Unauthenticated`, e.g. wrong audience): replication pauses with repeating warnings and resumes by itself once fixed, whether the fix is a new token or a server-side change (detected by the once-a-minute probe). Intended; documented in troubleshooting.
- **Fail-closed stall on metadata `PermissionDenied`** (OAuth readers only): stalls replication with errors logged rather than copying events the filter cannot check; needs a restart after fixing roles. Intended; documented.
- **Stream termination at token expiry** may cause restart churn on long reads; mitigated by existing restart logic and checked in the manual test.
- **Sentinel credential reliance** on `GetAuthenticationHeaderValue` being honoured: it is public API on the current client; a future move to `KurrentDB.Client` must re-verify it. Covered by the end-to-end header test so an upgrade that breaks it fails CI.
- **Role mapping** is the most likely customer misconfiguration (token accepted but `PermissionDenied`); the docs cover it and `PermissionDenied` errors are already logged by the reader/writer.
