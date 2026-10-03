# OAuth Authentication for KurrentDB gRPC Reader and Sink — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Replicator authenticate its gRPC reader and sink to KurrentDB with OAuth 2.0 access tokens (client-credentials grant or a token file), configured independently per side, refreshing and recovering from token failures without restarts.

**Architecture:** A per-side `IAccessTokenSource` (client credentials or token file) built on a shared `TokenState` that owns generations, rejected-token quarantine and the probe lease. A per-side `GrpcAuthContext` acquires a token *before* every gRPC call and passes it as the client's per-call `userCredentials`; `Run` retries token failures with backoff and reports acceptance/rejection by lease. A sentinel `DefaultCredentials` plus the client's public `GetAuthenticationHeaderValue` hook is a fallback only. `Realtime`, `StreamMetaCache`, `ScavengedEventsFilter`, `GrpcEventReader`, `GrpcEventWriter` and `Replicator` are adjusted so token failures never stall or corrupt replication.

**Tech Stack:** .NET 9 (C#, nullable enabled), `EventStore.Client.Grpc.Streams` 23.3.8, Grpc.Net.Client, LibLog → Serilog, TUnit 0.19 + Microsoft Testing Platform, `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`), Helm, Astro/Starlight docs.

**Spec:** `docs/superpowers/specs/2026-10-03-oauth-authentication-design.md` (reviewed clean by the Codex spec-review flow). Read it before starting; this plan argues from it and references its sections.

## Global Constraints

- Target framework stays `net9.0`; do not upgrade `EventStore.Client.Grpc.Streams` (stays `23.3.8`) or migrate to `KurrentDB.Client`.
- All new production code lives in `src/Kurrent.Replicator.KurrentDb/Auth/`, namespace `Kurrent.Replicator.KurrentDb.Auth`, except host settings (`src/replicator/Settings/`) and the listed edits to existing files.
- Config keys (camelCase in YAML, case-insensitive): `auth.type` ∈ `connectionString` (default) | `oauthClientCredentials` | `oauthTokenFile`; `tokenEndpoint`, `clientId`, `clientSecret`, `clientSecretFile`, `clientAssertionFile`, `clientAuthentication` (`post` default | `basic`), `scope`, `additionalParameters`, `defaultTokenLifetimeSeconds`, `refreshBeforeExpirySeconds` (default `300`), `tokenFile`, `tokenFileReloadSeconds` (default `30`).
- Fixed constants: token request timeout 30 s; failure cooldown 5 s; stale-token fallback margin 30 s; rejected-token quarantine 60 s; probe lease timeout 60 s; `expires_in` clamp 86 400 s; retry backoff 1 s, 2 s, 4 s … capped at 30 s; rate-limited warnings at most once per 60 s.
- Reserved `additionalParameters` keys: `grant_type`, `client_id`, `client_secret`, `client_assertion`, `client_assertion_type`, `scope`.
- Allowlisted OAuth `error` codes: `invalid_request`, `invalid_client`, `invalid_grant`, `unauthorized_client`, `unsupported_grant_type`, `invalid_scope`, `server_error`, `temporarily_unavailable`; anything else is reported as `non-standard error (omitted)`.
- Never log or put into exception messages: access tokens, client secrets, client assertions, token-request bodies, `error_description`, `error_uri` or raw token-endpoint response bodies (Debug-level `error_description` only after redaction to `***`).
- `connectionString` mode must behave exactly as today, apart from the `Realtime` bug fix, `StreamMetaCache` live-state and `Replicator` shutdown/drain fixes, which apply to all modes.
- Logging in new `Auth` classes resolves the logger per call (`static ILog Log => LogProvider.GetLogger(typeof(X))`), so tests that swap `Serilog.Log.Logger` capture output.
- Commit after each task with messages ending in `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

### Running unit tests locally

The test project targets net9; this machine has only .NET 10 runtimes, and the net9 apphost is missing. Build without an apphost and roll forward:

```bash
dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q
```

```bash
DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/AuthFailureTests/*"
```

Replace `AuthFailureTests` with the test class named in each step. Use `"/*/*/*/*"` for everything. The existing container tests (`ValuePartitionerTests`, `ChaserCheckpointSeedingTests`) need Docker; exclude them locally with `--treenode-filter "/*/Kurrent.Replicator.Tests.Auth/*/*"` (all new tests live in namespace `Kurrent.Replicator.Tests.Auth`). On a machine with a net9 runtime, `dotnet test` works too.

## Review Focus

1. **Token value containing characters that are legal in tokens but awkward in headers/forms** (e.g. `+`, `/`, `=`, `%`, spaces in secrets). Expect secrets to round-trip exactly through `post` and `basic` client authentication. Pinned in Task 4 (`Basic_auth_form_encodes_reserved_characters`, `Post_auth_encodes_reserved_characters`).
2. **Token file written non-atomically** (empty file or a trailing newline mid-rotation). Expect the previous token to keep working and the new one to be picked up on the next read. Pinned in Task 3 (`Empty_file_mid_rotation_keeps_previous_token`, `Trailing_newline_is_trimmed`).
3. **Clock jumps / very short token lifetimes** (`expires_in` of 1–60 s). Expect no tight refresh loop: refresh window capped at half the lifetime, and single-flight still holds. Pinned in Task 5 (`Very_short_lifetime_does_not_refresh_on_every_call`).
4. **Both sides pointing at the same cluster with different auth** (reader basic, sink OAuth to the same host). Expect fully independent clients and headers. Pinned in Task 11 (`Reader_basic_and_sink_oauth_send_independent_headers`).
5. **Environment-variable-only configuration** (no YAML `auth` section, everything via `REPLICATOR_SINK_AUTH_*`, including `ADDITIONALPARAMETERS_AUDIENCE`). Expect binding identical to YAML. Pinned in Task 13 (`Env_vars_bind_auth_including_additional_parameters`).

---

## File Structure

**Create (production, `src/Kurrent.Replicator.KurrentDb/Auth/`):**
- `AssemblyInfo.cs` (in `src/Kurrent.Replicator.KurrentDb/`) — `InternalsVisibleTo("Kurrent.Replicator.Tests")`.
- `OAuthTokenException.cs` — the single token-failure exception type.
- `AccessTokenLease.cs` — `AccessTokenLease` record struct and `IAccessTokenSource`.
- `AuthFailure.cs` — classify exceptions (`IsTokenFailure`, `IsPermissionDenied`).
- `GrpcAuthOptions.cs` — options record + enums.
- `RateLimitedWarning.cs` — "log at most once per interval" helper.
- `TokenState.cs` — generation, quarantine, probe lease (shared by both sources).
- `TokenFileSource.cs` — `oauthTokenFile`.
- `TokenEndpointClient.cs` — one token request: form building, response parsing, sanitised errors.
- `ClientCredentialsTokenSource.cs` — caching, refresh window, single-flight, cooldown, fallback.
- `TokenGate.cs` — wait-for-token with backoff.
- `GrpcAuthContext.cs` — `CallAuth`, `AcquireCredentials`, `Run`, `ReportAccepted`, `ReportFailure`.
- `GrpcAuthOptionsValidator.cs` — startup validation + warnings.
- `GrpcAuthentication.cs` — sentinel + fallback hook, token-source factory.

**Modify (production):**
- `src/Kurrent.Replicator.KurrentDb/Kurrent.Replicator.KurrentDb.csproj` — `Using` for the Auth namespace.
- `src/Kurrent.Replicator.KurrentDb/Configurator.cs` — per-side auth.
- `src/Kurrent.Replicator.KurrentDb/GrpcEventWriter.cs` — calls through `auth.Run`.
- `src/Kurrent.Replicator.KurrentDb/GrpcEventReader.cs` — per-call creds, `GetLastPosition` via `Run`, wiring.
- `src/Kurrent.Replicator.KurrentDb/Realtime.cs` — rewritten (attempt model).
- `src/Kurrent.Replicator.KurrentDb/StreamMetaCache.cs` — fail-closed flag, live state, epoch.
- `src/Kurrent.Replicator.KurrentDb/EventFilters.cs` — `ScavengedEventsFilter` through `auth.Run`.
- `src/Kurrent.Replicator.KurrentDb/ConnectionExtensions.cs` — credentials + cancellation parameters.
- `src/Kurrent.Replicator/Replicator.cs` — prepare→sink write token, drain loop, resilient reporter.
- `src/replicator/Settings/ReplicatorSettings.cs`, new `src/replicator/Settings/AuthSettings.cs` — binding + mapping.
- `src/replicator/Settings/EnvConfigProvider.cs` — redaction.
- `src/replicator/Startup.cs` — wiring.
- `charts/replicator/templates/statefulset.yaml`, `charts/replicator/values.yaml`.
- Docs: `docs/src/content/docs/deployment/authentication.mdx` (new), `deployment/configuration.mdx`, `features/readers.mdx`, `features/sinks.mdx`, `CHANGELOG.md`.

**Tests (`test/Kurrent.Replicator.Tests/Auth/`, namespace `Kurrent.Replicator.Tests.Auth`):**
- `Support/` — `LogCapture.cs`, `TimeDriver.cs`, `ControllableTokenSource.cs`, `FakeKurrentDbHandler.cs`, `StubTokenEndpoint.cs`, `TestEvents.cs`.
- One test file per unit: `AuthFailureTests.cs`, `TokenStateTests.cs`, `TokenFileSourceTests.cs`, `TokenEndpointClientTests.cs`, `ClientCredentialsTokenSourceTests.cs`, `TokenGateTests.cs`, `GrpcAuthContextTests.cs`, `GrpcAuthOptionsValidatorTests.cs`, `GrpcAuthenticationTests.cs`, `GrpcEventWriterAuthTests.cs`, `StreamMetaCacheTests.cs`, `ScavengedEventsFilterAuthTests.cs`, `RealtimeTests.cs`, `GrpcEventReaderAuthTests.cs`, `GrpcConfiguratorTests.cs`, `ReplicatorShutdownTests.cs`, `EnvConfigProviderTests.cs`, `AuthSettingsBindingTests.cs`.

---

### Task 1: Foundations — test support, exception, lease contract, options record, failure classification

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/AssemblyInfo.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/OAuthTokenException.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/AccessTokenLease.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/AuthFailure.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthOptions.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/RateLimitedWarning.cs`
- Modify: `src/Kurrent.Replicator.KurrentDb/Kurrent.Replicator.KurrentDb.csproj`
- Modify: `test/Kurrent.Replicator.Tests/Kurrent.Replicator.Tests.csproj`
- Create: `test/Kurrent.Replicator.Tests/Auth/Support/LogCapture.cs`
- Create: `test/Kurrent.Replicator.Tests/Auth/Support/TimeDriver.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/AuthFailureTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class OAuthTokenException(string message, Exception? inner = null) : Exception`
  - `public readonly record struct AccessTokenLease(string Value, long Generation)`
  - `public interface IAccessTokenSource { ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct); void Invalidate(AccessTokenLease lease); void ReportAccepted(AccessTokenLease lease); }`
  - `public static class AuthFailure { bool IsTokenFailure(Exception); bool IsPermissionDenied(Exception); }`
  - `public enum GrpcAuthType { ConnectionString, OAuthClientCredentials, OAuthTokenFile }`, `public enum ClientAuthenticationMethod { Post, Basic }`, `public sealed record GrpcAuthOptions` (properties listed in code below; `static GrpcAuthOptions Default`, `bool IsOAuth`)
  - `public sealed class RateLimitedWarning(TimeProvider time, TimeSpan interval) { bool ShouldLog(); }`
  - Test support: `LogCapture` (`[NotInParallel("global-logger")]` users), `TimeDriver.Drive(Task, FakeTimeProvider, TimeSpan? step = null)` / `Drive<T>`.

- [ ] **Step 1: Add assembly attribute, global using, test packages**

`src/Kurrent.Replicator.KurrentDb/AssemblyInfo.cs` (the project sets `GenerateAssemblyInfo=false`, so the MSBuild `InternalsVisibleTo` item would be ignored):

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kurrent.Replicator.Tests")]
```

In `src/Kurrent.Replicator.KurrentDb/Kurrent.Replicator.KurrentDb.csproj`, add to the existing `<ItemGroup>` with `<Using>` items:

```xml
        <Using Include="Kurrent.Replicator.KurrentDb.Auth" />
        <Using Include="Kurrent.Replicator.Shared.Logging" />
```

In `test/Kurrent.Replicator.Tests/Kurrent.Replicator.Tests.csproj`, add to the package `<ItemGroup>`:

```xml
        <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.4.0"/>
```

- [ ] **Step 2: Create the contract types**

`src/Kurrent.Replicator.KurrentDb/Auth/OAuthTokenException.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>
/// A token could not be obtained, or the token source refuses to hand one out (quarantine, probe in progress).
/// Messages never contain tokens, secrets, assertions or provider-supplied free text.
/// </summary>
public sealed class OAuthTokenException(string message, Exception? inner = null) : Exception(message, inner);
```

`src/Kurrent.Replicator.KurrentDb/Auth/AccessTokenLease.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>A token value handed to exactly one call, tagged with the source generation it was issued under.</summary>
public readonly record struct AccessTokenLease(string Value, long Generation) {
    public override string ToString() => $"AccessTokenLease(Generation={Generation})";
}

public interface IAccessTokenSource {
    ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct);

    /// <summary>KurrentDB rejected the token carried by this lease. Synchronous, no I/O, never calls out.</summary>
    void Invalidate(AccessTokenLease lease);

    /// <summary>A call carrying this lease succeeded. Synchronous, no I/O, never calls out.</summary>
    void ReportAccepted(AccessTokenLease lease);
}
```

Note: `ToString` is overridden so a lease logged by accident never prints the token.

`src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthOptions.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

public enum GrpcAuthType { ConnectionString, OAuthClientCredentials, OAuthTokenFile }

public enum ClientAuthenticationMethod { Post, Basic }

public sealed record GrpcAuthOptions {
    public static GrpcAuthOptions Default { get; } = new();

    public GrpcAuthType Type { get; init; } = GrpcAuthType.ConnectionString;

    // oauthClientCredentials
    public string?                             TokenEndpoint               { get; init; }
    public string?                             ClientId                    { get; init; }
    public string?                             ClientSecret                { get; init; }
    public string?                             ClientSecretFile            { get; init; }
    public string?                             ClientAssertionFile         { get; init; }
    public ClientAuthenticationMethod          ClientAuthentication        { get; init; } = ClientAuthenticationMethod.Post;
    public string?                             Scope                       { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalParameters        { get; init; } = new Dictionary<string, string>();
    public int?                                DefaultTokenLifetimeSeconds { get; init; }
    public int                                 RefreshBeforeExpirySeconds  { get; init; } = 300;

    // oauthTokenFile
    public string? TokenFile              { get; init; }
    public int     TokenFileReloadSeconds { get; init; } = 30;

    public bool IsOAuth => Type != GrpcAuthType.ConnectionString;

    public override string ToString() => $"GrpcAuthOptions(Type={Type})"; // never print secrets
}
```

`src/Kurrent.Replicator.KurrentDb/Auth/RateLimitedWarning.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>Returns true the first time and then at most once per interval.</summary>
public sealed class RateLimitedWarning(TimeProvider time, TimeSpan interval) {
    readonly object  _lock = new();
    DateTimeOffset? _last;

    public bool ShouldLog() {
        lock (_lock) {
            var now = time.GetUtcNow();

            if (_last is { } last && now - last < interval) return false;

            _last = now;

            return true;
        }
    }

    public void Reset() {
        lock (_lock) _last = null;
    }
}
```

- [ ] **Step 3: Create test support**

`test/Kurrent.Replicator.Tests/Auth/Support/LogCapture.cs`:

```csharp
using System.Collections.Concurrent;
using Kurrent.Replicator.Tests.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Kurrent.Replicator.Tests.Auth.Support;

/// <summary>
/// Captures everything logged through Serilog's global logger while alive. Tests using it must be
/// [NotInParallel("global-logger")] because Log.Logger is process-global.
/// </summary>
public sealed class LogCapture : ILogEventSink, IDisposable {
    readonly ConcurrentQueue<LogEvent> _events = new();
    readonly ILogger                   _previous = Log.Logger;

    public LogCapture() {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(this)
            .WriteTo.TestOutput()
            .CreateLogger();
    }

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyList<(LogEventLevel Level, string Text)> Events
        => _events.Select(e => (e.Level, e.RenderMessage() + " " + e.Exception)).ToList();

    public IEnumerable<string> TextAtOrAbove(LogEventLevel level) => Events.Where(e => e.Level >= level).Select(e => e.Text);

    public string AllText => string.Join("\n", Events.Select(e => e.Text));

    public void Dispose() => Log.Logger = _previous;
}
```

`test/Kurrent.Replicator.Tests/Auth/Support/TimeDriver.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth.Support;

public static class TimeDriver {
    /// <summary>Advances fake time in steps until the task completes (or the step budget runs out).</summary>
    public static async Task<T> Drive<T>(Task<T> task, FakeTimeProvider time, TimeSpan? step = null, int maxSteps = 5000) {
        await Drive((Task)task, time, step, maxSteps);

        return await task;
    }

    public static async Task Drive(Task task, FakeTimeProvider time, TimeSpan? step = null, int maxSteps = 5000) {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++) {
            time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Advances fake time until the condition holds.</summary>
    public static async Task Until(Func<bool> condition, FakeTimeProvider time, TimeSpan? step = null, int maxSteps = 5000) {
        for (var i = 0; i < maxSteps && !condition(); i++) {
            time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        if (!condition()) throw new TimeoutException("Condition not reached");
    }
}
```

- [ ] **Step 4: Write the failing AuthFailure tests**

`test/Kurrent.Replicator.Tests/Auth/AuthFailureTests.cs`:

```csharp
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Auth;

public class AuthFailureTests {
    static RpcException Rpc(StatusCode code, Exception? debug = null) => new(new Status(code, "x", debug));

    [Test]
    public async Task Token_failures_are_recognised() {
        await Assert.That(AuthFailure.IsTokenFailure(new OAuthTokenException("x"))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.Unauthenticated))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(new NotAuthenticatedException("x", Rpc(StatusCode.Unauthenticated)))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.Internal, new OAuthTokenException("x")))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(new AggregateException(new InvalidOperationException(), new OAuthTokenException("x")))).IsTrue();
        await Assert.That(AuthFailure.IsTokenFailure(new InvalidOperationException("outer", new OAuthTokenException("x")))).IsTrue();
    }

    [Test]
    public async Task Non_token_failures_are_not_token_failures() {
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.PermissionDenied))).IsFalse();
        await Assert.That(AuthFailure.IsTokenFailure(new AccessDeniedException("x", Rpc(StatusCode.PermissionDenied)))).IsFalse();
        await Assert.That(AuthFailure.IsTokenFailure(Rpc(StatusCode.Unavailable))).IsFalse();
        await Assert.That(AuthFailure.IsTokenFailure(new OperationCanceledException())).IsFalse();
    }

    [Test]
    public async Task Permission_denied_is_recognised() {
        await Assert.That(AuthFailure.IsPermissionDenied(Rpc(StatusCode.PermissionDenied))).IsTrue();
        await Assert.That(AuthFailure.IsPermissionDenied(new AccessDeniedException("x", Rpc(StatusCode.PermissionDenied)))).IsTrue();
        await Assert.That(AuthFailure.IsPermissionDenied(Rpc(StatusCode.Unauthenticated))).IsFalse();
    }

    [Test]
    public async Task Self_referencing_chains_terminate() {
        var agg = new AggregateException(new AggregateException(new AggregateException(new InvalidOperationException())));
        await Assert.That(AuthFailure.IsTokenFailure(agg)).IsFalse();
    }
}
```

- [ ] **Step 5: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: build FAILS with `The name 'AuthFailure' does not exist`.

- [ ] **Step 6: Implement AuthFailure**

`src/Kurrent.Replicator.KurrentDb/Auth/AuthFailure.cs`:

```csharp
using Grpc.Core;

namespace Kurrent.Replicator.KurrentDb.Auth;

public static class AuthFailure {
    /// <summary>Token missing, expired or rejected: retrying with a (new) token may help.</summary>
    public static bool IsTokenFailure(Exception e)
        => Chain(e).Any(x => x is OAuthTokenException or NotAuthenticatedException or RpcException { StatusCode: StatusCode.Unauthenticated });

    /// <summary>Authorization decision: a new token does not help.</summary>
    public static bool IsPermissionDenied(Exception e)
        => Chain(e).Any(x => x is AccessDeniedException or RpcException { StatusCode: StatusCode.PermissionDenied });

    static IEnumerable<Exception> Chain(Exception root) {
        var pending = new Stack<Exception>();
        pending.Push(root);
        var visited = 0;

        while (pending.Count > 0 && visited++ < 64) {
            var e = pending.Pop();

            yield return e;

            if (e is AggregateException agg) {
                foreach (var inner in agg.InnerExceptions) pending.Push(inner);
            }
            else if (e.InnerException is { } inner) {
                pending.Push(inner);
            }

            if (e is RpcException rpc && rpc.Status.DebugException is { } debug) pending.Push(debug);
        }
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q` then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/AuthFailureTests/*"`
Expected: 4 tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb test/Kurrent.Replicator.Tests
git commit -m "feat(auth): token-failure classification and auth contracts

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: TokenState — generations, quarantine, probe lease

The heart of the rejected-token rules ("Rejected tokens" in the spec). Both token sources delegate to it.

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/TokenState.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/TokenStateTests.cs`

**Interfaces:**
- Consumes: `AccessTokenLease`, `OAuthTokenException` (Task 1).
- Produces: `internal sealed class TokenState(TimeProvider time)` with:
  - `AccessTokenLease Accept(string value)` — call with every value a source is about to hand out (fresh fetch, file read, or cache hit). Returns the lease or throws `OAuthTokenException` (quarantined / probe held).
  - `bool Invalidate(AccessTokenLease lease)` — true if applied (current generation).
  - `bool ReportAccepted(AccessTokenLease lease)` — true if applied.
  - `bool IsRejected(string value)` — the value is under an active rejection record.
  - `bool HasRejection` — any rejection record active.
  - `long Generation`.
  - `static readonly TimeSpan Quarantine = 60 s`, `ProbeLeaseTimeout = 60 s`.

- [ ] **Step 1: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/TokenStateTests.cs`:

```csharp
using Kurrent.Replicator.KurrentDb.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenStateTests {
    readonly FakeTimeProvider _time = new();
    TokenState NewState() => new(_time);

    [Test]
    public async Task Same_value_does_not_bump_generation() {
        var s = NewState();
        var a1 = s.Accept("A");
        var a2 = s.Accept("A");
        await Assert.That(a2.Generation).IsEqualTo(a1.Generation);
    }

    [Test]
    public async Task New_value_bumps_generation() {
        var s = NewState();
        var a = s.Accept("A");
        var b = s.Accept("B");
        await Assert.That(b.Generation).IsGreaterThan(a.Generation);
    }

    [Test]
    public async Task Invalidate_current_starts_quarantine() {
        var s = NewState();
        var a = s.Accept("A");
        await Assert.That(s.Invalidate(a)).IsTrue();
        await Assert.That(() => s.Accept("A")).Throws<OAuthTokenException>();
        _time.Advance(TimeSpan.FromSeconds(59));
        await Assert.That(() => s.Accept("A")).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Different_value_is_accepted_immediately_and_clears_rejection() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        var b = s.Accept("B");
        await Assert.That(b.Value).IsEqualTo("B");
        await Assert.That(s.HasRejection).IsFalse();
    }

    [Test]
    public async Task Same_value_reload_does_not_lose_rejection() {
        var s = NewState();
        var call1 = s.Accept("A");
        s.Accept("A"); // routine reload / reissue of the same value
        s.Accept("A");
        await Assert.That(s.Invalidate(call1)).IsTrue();
        await Assert.That(() => s.Accept("A")).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task After_quarantine_exactly_one_probe_gets_the_value() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));

        var results = Enumerable.Range(0, 10).Select(_ => {
            try { return (AccessTokenLease?)s.Accept("A"); } catch (OAuthTokenException) { return null; }
        }).ToList();

        await Assert.That(results.Count(r => r is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task Accepted_probe_releases_value_to_everyone() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");
        await Assert.That(s.ReportAccepted(probe)).IsTrue();
        await Assert.That(s.Accept("A").Value).IsEqualTo("A");
        await Assert.That(s.HasRejection).IsFalse();
    }

    [Test]
    public async Task Rejected_probe_restarts_quarantine() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");
        await Assert.That(s.Invalidate(probe)).IsTrue();
        _time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(() => s.Accept("A")).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Unreported_probe_lease_expires_and_counts_as_new_quarantine() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");
        _time.Advance(TimeSpan.FromSeconds(61)); // lease expired -> new quarantine starts now
        await Assert.That(() => s.Accept("A")).Throws<OAuthTokenException>();
        await Assert.That(s.ReportAccepted(probe)).IsFalse(); // stale after expiry
        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.That(s.Accept("A").Value).IsEqualTo("A"); // next probe
    }

    [Test]
    public async Task Stale_reports_are_ignored() {
        var s = NewState();
        var call1 = s.Accept("A");                 // gen 1
        s.Invalidate(s.Accept("A"));               // another call rejects -> quarantine, gen 2
        await Assert.That(s.ReportAccepted(call1)).IsFalse(); // late success from gen 1 does not clear quarantine
        await Assert.That(() => s.Accept("A")).Throws<OAuthTokenException>();

        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");                 // gen 3
        s.ReportAccepted(probe);                   // gen 4
        await Assert.That(s.Invalidate(call1)).IsFalse(); // late rejection from gen 1 ignored
        await Assert.That(s.Accept("A").Value).IsEqualTo("A");
    }

    [Test]
    public async Task IsRejected_reports_the_quarantined_value() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        await Assert.That(s.IsRejected("A")).IsTrue();
        await Assert.That(s.IsRejected("B")).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, `TokenState` not found.

- [ ] **Step 3: Implement TokenState**

`src/Kurrent.Replicator.KurrentDb/Auth/TokenState.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>
/// Generation, rejected-token quarantine and probe lease, shared by both token sources.
/// All members are synchronous and hold only this object's lock.
/// </summary>
sealed class TokenState(TimeProvider time) {
    public static readonly TimeSpan Quarantine        = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProbeLeaseTimeout = TimeSpan.FromSeconds(60);

    readonly object _lock = new();

    string?        _current;
    long           _generation;
    string?        _rejected;
    DateTimeOffset _rejectedAt;
    bool           _probeHeld;
    DateTimeOffset _probeGrantedAt;

    public long Generation { get { lock (_lock) return _generation; } }

    public bool HasRejection { get { lock (_lock) return _rejected != null; } }

    public bool IsRejected(string value) {
        lock (_lock) return _rejected == value;
    }

    public AccessTokenLease Accept(string value) {
        lock (_lock) {
            var now = time.GetUtcNow();
            ExpireProbe(now);

            if (_rejected != null && value == _rejected) {
                if (now - _rejectedAt < Quarantine)
                    throw new OAuthTokenException("The token source returned a token the server just rejected; waiting for a new token");

                if (_probeHeld)
                    throw new OAuthTokenException("A previously rejected token is being re-tested by another call; waiting for the result");

                _probeHeld      = true;
                _probeGrantedAt = now;
                _current        = value;
                _generation++;

                return new(value, _generation);
            }

            if (_rejected != null) {
                _rejected  = null;
                _probeHeld = false;
            }

            if (value != _current) {
                _current = value;
                _generation++;
            }

            return new(value, _generation);
        }
    }

    public bool Invalidate(AccessTokenLease lease) {
        lock (_lock) {
            if (lease.Generation != _generation) return false;

            _rejected   = lease.Value;
            _rejectedAt = time.GetUtcNow();
            _probeHeld  = false;
            _current    = null;
            _generation++;

            return true;
        }
    }

    public bool ReportAccepted(AccessTokenLease lease) {
        lock (_lock) {
            if (lease.Generation != _generation) return false;

            if (_probeHeld && _rejected == lease.Value) {
                _rejected  = null;
                _probeHeld = false;
                _generation++;
            }

            return true;
        }
    }

    void ExpireProbe(DateTimeOffset now) {
        if (!_probeHeld || now - _probeGrantedAt < ProbeLeaseTimeout) return;

        _probeHeld  = false;
        _rejectedAt = now; // expiry counts as a new quarantine
        _current    = null;
        _generation++;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q` then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/TokenStateTests/*"`
Expected: 11 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/Auth/TokenState.cs test/Kurrent.Replicator.Tests/Auth/TokenStateTests.cs
git commit -m "feat(auth): token generations, quarantine and probe lease

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: TokenFileSource

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/TokenFileSource.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/TokenFileSourceTests.cs`

**Interfaces:**
- Consumes: `TokenState`, `IAccessTokenSource`, `OAuthTokenException`.
- Produces: `public sealed class TokenFileSource(string path, TimeSpan reloadInterval, TimeProvider time, string side) : IAccessTokenSource`.

Behaviour (spec "Token file flow" + "Rejected tokens"): read on first call and when `now - lastReadAt >= reloadInterval`; also on every call while a rejection record is active (so a rotated file is picked up immediately after a rejection); trim; missing/empty → keep previous token unless it was rejected, else throw.

- [ ] **Step 1: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/TokenFileSourceTests.cs`:

```csharp
using Kurrent.Replicator.KurrentDb.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenFileSourceTests {
    readonly FakeTimeProvider _time = new();
    readonly string           _path = Path.Combine(Path.GetTempPath(), $"replicator-token-{Guid.NewGuid():N}");

    TokenFileSource NewSource(int reloadSeconds = 30) => new(_path, TimeSpan.FromSeconds(reloadSeconds), _time, "sink");

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Test]
    public async Task Reads_and_trims() {
        await File.WriteAllTextAsync(_path, "  A \n");
        var lease = await NewSource().GetAccessToken(default);
        await Assert.That(lease.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Trailing_newline_is_trimmed() {
        await File.WriteAllTextAsync(_path, "eyJhbGciOi.abc.def\r\n");
        await Assert.That((await NewSource().GetAccessToken(default)).Value).IsEqualTo("eyJhbGciOi.abc.def");
    }

    [Test]
    public async Task Missing_file_without_previous_token_throws() {
        await Assert.That(async () => await NewSource().GetAccessToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Does_not_reread_before_interval() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        await source.GetAccessToken(default);
        await File.WriteAllTextAsync(_path, "B");
        _time.Advance(TimeSpan.FromSeconds(29));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A");
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("B");
    }

    [Test]
    public async Task Empty_file_mid_rotation_keeps_previous_token() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        await source.GetAccessToken(default);
        await File.WriteAllTextAsync(_path, "");
        _time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A");
        File.Delete(_path);
        _time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A");
    }

    [Test]
    public async Task Invalidate_forces_immediate_reread_and_accepts_new_value() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        var a      = await source.GetAccessToken(default);
        await File.WriteAllTextAsync(_path, "B");
        source.Invalidate(a);
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("B");
    }

    [Test]
    public async Task After_invalidate_missing_or_same_value_throws_during_quarantine_then_probes() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        source.Invalidate(await source.GetAccessToken(default));

        await Assert.That(async () => await source.GetAccessToken(default)).Throws<OAuthTokenException>(); // still A
        File.Delete(_path);
        await Assert.That(async () => await source.GetAccessToken(default)).Throws<OAuthTokenException>(); // missing, no fallback to A

        await File.WriteAllTextAsync(_path, "A");
        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A"); // probe
    }

    [Test]
    public async Task Same_value_reload_does_not_lose_rejection() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        var call1  = await source.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(30));
        await source.GetAccessToken(default); // timer re-read of unchanged A
        source.Invalidate(call1);
        await Assert.That(async () => await source.GetAccessToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Honours_cancellation() {
        await File.WriteAllTextAsync(_path, "A");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.That(async () => await NewSource().GetAccessToken(cts.Token)).Throws<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, `TokenFileSource` not found.

- [ ] **Step 3: Implement**

`src/Kurrent.Replicator.KurrentDb/Auth/TokenFileSource.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class TokenFileSource(string path, TimeSpan reloadInterval, TimeProvider time, string side) : IAccessTokenSource {
    static ILog Log => LogProvider.GetLogger(typeof(TokenFileSource));

    readonly TokenState _state = new(time);
    readonly object     _lock  = new();

    string?         _cached;
    DateTimeOffset? _lastReadAt;
    bool            _forceReload = true;

    public ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        string? value;

        lock (_lock) {
            var now = time.GetUtcNow();

            if (_forceReload || _state.HasRejection || _lastReadAt is null || now - _lastReadAt >= reloadInterval) {
                var read = TryRead();
                _lastReadAt  = now;
                _forceReload = false;

                if (read != null) {
                    _cached = read;
                }
                else if (_cached != null && !_state.IsRejected(_cached)) {
                    Log.Warn("{Side}: token file {Path} is missing or empty; keeping the previously read token", side, path);
                }
                else {
                    _cached = null;
                }
            }

            value = _cached;
        }

        if (value == null) throw new OAuthTokenException($"{side}: token file {path} is missing or empty");

        return ValueTask.FromResult(_state.Accept(value));
    }

    public void Invalidate(AccessTokenLease lease) {
        if (!_state.Invalidate(lease)) {
            Log.Debug("{Side}: ignoring stale token rejection report", side);

            return;
        }

        lock (_lock) {
            _forceReload = true;
            if (_cached == lease.Value) _cached = null;
        }
    }

    public void ReportAccepted(AccessTokenLease lease) => _state.ReportAccepted(lease);

    string? TryRead() {
        try {
            var text = File.ReadAllText(path).Trim();

            return text.Length == 0 ? null : text;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }
}
```

Note: `FileNotFoundException` and `DirectoryNotFoundException` derive from `IOException`.

Note on the "missing after rejection" case: `Invalidate` nulls `_cached`, so a missing file afterwards yields `value == null` and throws. A file still holding the rejected value reaches `_state.Accept`, which throws during quarantine and grants the probe after it.

- [ ] **Step 4: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/TokenFileSourceTests/*"`
Expected: 9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/Auth/TokenFileSource.cs test/Kurrent.Replicator.Tests/Auth/TokenFileSourceTests.cs
git commit -m "feat(auth): token file source

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: TokenEndpointClient — one token request, strict parsing, sanitised errors

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/TokenEndpointClient.cs`
- Create: `test/Kurrent.Replicator.Tests/Auth/Support/StubTokenEndpoint.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/TokenEndpointClientTests.cs`

**Interfaces:**
- Consumes: `GrpcAuthOptions`, `ClientAuthenticationMethod`, `OAuthTokenException`.
- Produces:
  - `internal sealed record TokenResponse(string AccessToken, TimeSpan Lifetime)`
  - `internal sealed class TokenEndpointClient : IDisposable` with ctor `(GrpcAuthOptions options, HttpMessageHandler handler, string side)`, `Task<TokenResponse> RequestToken(CancellationToken ct)`, `string EndpointHost`, `static SocketsHttpHandler CreateDefaultHandler()` (with `AllowAutoRedirect = false`).
  - `StubTokenEndpoint : HttpMessageHandler` (tests): `Func<HttpRequestMessage, string, HttpResponseMessage> Respond`, `List<(HttpRequestMessage Request, string Body)> Requests`, helpers `static HttpResponseMessage Json(HttpStatusCode, string)`, `static string Token(string value, object? expiresIn = 3600, string? tokenType = "Bearer")`, `TaskCompletionSource? Gate` (when set, requests wait for it).

- [ ] **Step 1: Create the stub token endpoint**

`test/Kurrent.Replicator.Tests/Auth/Support/StubTokenEndpoint.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kurrent.Replicator.Tests.Auth.Support;

public sealed class StubTokenEndpoint : HttpMessageHandler {
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => Json(HttpStatusCode.OK, Token("A"));

    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    /// <summary>When set, every request waits for it before responding.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Count => Requests.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));

        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);

        return Respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Token(string value, object? expiresIn = 3600, string? tokenType = "Bearer") {
        var dict = new Dictionary<string, object?> { ["access_token"] = value };
        if (tokenType != null) dict["token_type"] = tokenType;
        if (expiresIn != null) dict["expires_in"] = expiresIn;

        return JsonSerializer.Serialize(dict);
    }

    public static Dictionary<string, string> Form(string body)
        => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
}
```

- [ ] **Step 2: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/TokenEndpointClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenEndpointClientTests {
    readonly StubTokenEndpoint _stub = new();

    static GrpcAuthOptions Options(Func<GrpcAuthOptions, GrpcAuthOptions>? configure = null) {
        var o = new GrpcAuthOptions {
            Type          = GrpcAuthType.OAuthClientCredentials,
            TokenEndpoint = "https://idp.example.com/oauth2/token",
            ClientId      = "replicator",
            ClientSecret  = "s3cr3t",
            Scope         = "api://kurrentdb/.default"
        };

        return configure?.Invoke(o) ?? o;
    }

    TokenEndpointClient Client(GrpcAuthOptions o) => new(o, _stub, "sink");

    [Test]
    public async Task Post_auth_sends_form_with_secret() {
        var o = Options(x => x with { AdditionalParameters = new Dictionary<string, string> { ["audience"] = "kdb" } });
        var r = await Client(o).RequestToken(default);

        await Assert.That(r.AccessToken).IsEqualTo("A");
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(3600));
        var (req, body) = _stub.Requests.Single();
        var form        = StubTokenEndpoint.Form(body);
        await Assert.That(req.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(req.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/x-www-form-urlencoded");
        await Assert.That(form["grant_type"]).IsEqualTo("client_credentials");
        await Assert.That(form["client_id"]).IsEqualTo("replicator");
        await Assert.That(form["client_secret"]).IsEqualTo("s3cr3t");
        await Assert.That(form["scope"]).IsEqualTo("api://kurrentdb/.default");
        await Assert.That(form["audience"]).IsEqualTo("kdb");
        await Assert.That(req.Headers.Authorization).IsNull();
    }

    [Test]
    public async Task Post_auth_encodes_reserved_characters() {
        await Client(Options(x => x with { ClientSecret = "a+b/c=d%e f&g" })).RequestToken(default);
        var form = StubTokenEndpoint.Form(_stub.Requests.Single().Body);
        await Assert.That(form["client_secret"]).IsEqualTo("a+b/c=d%e f&g");
    }

    [Test]
    public async Task Basic_auth_form_encodes_reserved_characters() {
        await Client(Options(x => x with { ClientAuthentication = ClientAuthenticationMethod.Basic, ClientId = "my app", ClientSecret = "a+b:c" })).RequestToken(default);
        var (req, body) = _stub.Requests.Single();
        var form        = StubTokenEndpoint.Form(body);
        await Assert.That(form.ContainsKey("client_secret")).IsFalse();
        await Assert.That(form.ContainsKey("client_id")).IsFalse();
        await Assert.That(req.Headers.Authorization!.Scheme).IsEqualTo("Basic");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization.Parameter!));
        await Assert.That(decoded).IsEqualTo("my+app:a%2Bb%3Ac"); // RFC 6749 2.3.1: form-urlencode each part
    }

    [Test]
    public async Task Assertion_is_reread_on_every_request() {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "jwt-1\n");
        var client = Client(Options(x => x with { ClientSecret = null, ClientAssertionFile = file }));
        await client.RequestToken(default);
        await File.WriteAllTextAsync(file, "jwt-2");
        await client.RequestToken(default);

        var forms = _stub.Requests.Select(r => StubTokenEndpoint.Form(r.Body)).ToList();
        await Assert.That(forms[0]["client_assertion"]).IsEqualTo("jwt-1");
        await Assert.That(forms[1]["client_assertion"]).IsEqualTo("jwt-2");
        await Assert.That(forms[0]["client_assertion_type"]).IsEqualTo("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        await Assert.That(forms[0]["client_id"]).IsEqualTo("replicator");
        await Assert.That(forms[0].ContainsKey("client_secret")).IsFalse();
        File.Delete(file);
    }

    [Test]
    public async Task Secret_file_is_read_once_and_trimmed() {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "from-file\n");
        var client = Client(Options(x => x with { ClientSecret = null, ClientSecretFile = file }));
        await File.WriteAllTextAsync(file, "changed");
        await client.RequestToken(default);
        await Assert.That(StubTokenEndpoint.Form(_stub.Requests.Single().Body)["client_secret"]).IsEqualTo("from-file");
        File.Delete(file);
    }

    [Test]
    [Arguments("{\"token_type\":\"Bearer\",\"expires_in\":3600}")]                         // no access_token
    [Arguments("{\"access_token\":\"\",\"token_type\":\"Bearer\",\"expires_in\":3600}")]   // empty
    [Arguments("{\"access_token\":\"A\",\"expires_in\":3600}")]                            // no token_type
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"mac\",\"expires_in\":3600}")]     // wrong type
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":-5}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":1.5}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":\"soon\"}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":99999999999999999999}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\"}")]                      // no expires_in, no default
    [Arguments("not json")]
    public async Task Invalid_responses_are_rejected(string json) {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, json);
        await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Numeric_string_expires_in_and_case_insensitive_bearer_are_accepted() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", "120", "bearer"));
        var r = await Client(Options()).RequestToken(default);
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(120));
    }

    [Test]
    public async Task Missing_expires_in_uses_configured_default() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", expiresIn: null));
        var r = await Client(Options(x => x with { DefaultTokenLifetimeSeconds = 900 })).RequestToken(default);
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(900));
    }

    [Test]
    public async Task Lifetime_over_24h_is_clamped() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", 200000));
        var r = await Client(Options()).RequestToken(default);
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(86400));
    }

    [Test]
    public async Task Redirect_is_an_error_and_not_followed() {
        _stub.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://evil.example.com/steal") } };
        var ex = await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("redirect");
        await Assert.That(_stub.Count).IsEqualTo(1);
        await Assert.That(TokenEndpointClient.CreateDefaultHandler().AllowAutoRedirect).IsFalse();
    }

    [Test]
    public async Task Allowlisted_error_and_integer_error_codes_are_reported() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\",\"error_codes\":[7000215],\"error_description\":\"AADSTS7000215: Invalid client secret\"}");
        var ex = await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("400");
        await Assert.That(ex.Message).Contains("invalid_client");
        await Assert.That(ex.Message).Contains("7000215");
        await Assert.That(ex.Message).Contains("idp.example.com");
        await Assert.That(ex.Message).DoesNotContain("AADSTS7000215: Invalid");
    }

    [Test]
    public async Task Error_echoing_the_secret_is_omitted() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.Unauthorized, "{\"error\":\"s3cr3t\",\"error_description\":\"bad s3cr3t\",\"error_uri\":\"https://x/s3cr3t\"}");
        var ex = await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).DoesNotContain("s3cr3t");
        await Assert.That(ex.Message).Contains("non-standard error (omitted)");
    }
}
```

- [ ] **Step 3: Add the log-redaction test (separate class because it swaps the global logger)**

Append to the same file:

```csharp
[NotInParallel("global-logger")]
public class TokenEndpointClientLoggingTests {
    [Test]
    public async Task Provider_text_is_never_logged_above_debug_and_is_redacted_at_debug() {
        using var logs = new LogCapture();
        var assertionFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(assertionFile, "assertion-xyz");

        var stub = new StubTokenEndpoint {
            Respond = (_, _) => StubTokenEndpoint.Json(
                HttpStatusCode.BadRequest,
                "{\"error\":\"invalid_client\",\"error_description\":\"echo assertion-xyz\",\"error_uri\":\"https://x/assertion-xyz\"}"
            )
        };

        var options = new GrpcAuthOptions {
            Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp.example.com/t", ClientId = "c", ClientAssertionFile = assertionFile
        };

        var ex = await Assert.That(async () => await new TokenEndpointClient(options, stub, "sink").RequestToken(default)).Throws<OAuthTokenException>();

        await Assert.That(ex!.ToString()).DoesNotContain("assertion-xyz");
        await Assert.That(string.Join("\n", logs.TextAtOrAbove(Serilog.Events.LogEventLevel.Information))).DoesNotContain("assertion-xyz");
        await Assert.That(logs.AllText).DoesNotContain("assertion-xyz");
        await Assert.That(logs.AllText).Contains("echo ***");
        File.Delete(assertionFile);
    }
}
```

Add `using Serilog.Events;` is not needed because the level is fully qualified above.

- [ ] **Step 4: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, `TokenEndpointClient` not found.

- [ ] **Step 5: Implement**

`src/Kurrent.Replicator.KurrentDb/Auth/TokenEndpointClient.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kurrent.Replicator.KurrentDb.Auth;

sealed record TokenResponse(string AccessToken, TimeSpan Lifetime) {
    public override string ToString() => $"TokenResponse(Lifetime={Lifetime})";
}

sealed class TokenEndpointClient : IDisposable {
    static ILog Log => LogProvider.GetLogger(typeof(TokenEndpointClient));

    const           string   AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    static readonly TimeSpan MaxLifetime   = TimeSpan.FromSeconds(86400);

    static readonly HashSet<string> AllowedErrors = [
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client",
        "unsupported_grant_type", "invalid_scope", "server_error", "temporarily_unavailable"
    ];

    readonly GrpcAuthOptions _options;
    readonly string          _side;
    readonly HttpClient      _http;
    readonly Uri             _endpoint;
    readonly string?         _secret;
    readonly HashSet<string> _seenTokens = [];
    string?                  _lastAssertion;
    bool                     _clampWarned;

    public TokenEndpointClient(GrpcAuthOptions options, HttpMessageHandler handler, string side) {
        _options  = options;
        _side     = side;
        _endpoint = new Uri(options.TokenEndpoint!, UriKind.Absolute);
        _http     = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _secret   = options.ClientSecret ?? (options.ClientSecretFile != null ? File.ReadAllText(options.ClientSecretFile).Trim() : null);
    }

    public string EndpointHost => _endpoint.Host;

    public static SocketsHttpHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    public async Task<TokenResponse> RequestToken(CancellationToken ct) {
        using var request = await BuildRequest(ct).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        var status = (int)response.StatusCode;

        if (status is >= 300 and < 400)
            throw new OAuthTokenException($"{_side}: token endpoint {EndpointHost} returned HTTP {status} (redirect); redirects are not followed");

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw ErrorResponse(status, body);

        return Parse(body);
    }

    async Task<HttpRequestMessage> BuildRequest(CancellationToken ct) {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };

        if (!string.IsNullOrWhiteSpace(_options.Scope)) form.Add(new("scope", _options.Scope));

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        if (_options.ClientAssertionFile != null) {
            var assertion = (await File.ReadAllTextAsync(_options.ClientAssertionFile, ct).ConfigureAwait(false)).Trim();

            if (assertion.Length == 0) throw new OAuthTokenException($"{_side}: client assertion file {_options.ClientAssertionFile} is empty");

            _lastAssertion = assertion;
            form.Add(new("client_id", _options.ClientId!));
            form.Add(new("client_assertion_type", AssertionType));
            form.Add(new("client_assertion", assertion));
        }
        else if (_options.ClientAuthentication == ClientAuthenticationMethod.Basic) {
            var raw = $"{WebUtility.UrlEncode(_options.ClientId)}:{WebUtility.UrlEncode(_secret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else {
            form.Add(new("client_id", _options.ClientId!));
            form.Add(new("client_secret", _secret!));
        }

        foreach (var (key, value) in _options.AdditionalParameters) form.Add(new(key, value));

        request.Content = new FormUrlEncodedContent(form);

        return request;
    }

    TokenResponse Parse(string body) {
        JsonDocument doc;

        try {
            doc = JsonDocument.Parse(body);
        } catch (JsonException) {
            throw new OAuthTokenException($"{_side}: token endpoint {EndpointHost} returned a response that is not valid JSON");
        }

        using (doc) {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) throw Invalid("the response is not a JSON object");

            if (!root.TryGetProperty("access_token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tokenEl.GetString()))
                throw Invalid("access_token is missing or empty");

            if (!root.TryGetProperty("token_type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                !string.Equals(typeEl.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw Invalid("token_type must be Bearer");

            var token = tokenEl.GetString()!;
            lock (_seenTokens) _seenTokens.Add(token);

            return new(token, Lifetime(root));
        }
    }

    TimeSpan Lifetime(JsonElement root) {
        if (!root.TryGetProperty("expires_in", out var el)) {
            if (_options.DefaultTokenLifetimeSeconds is { } fallback) return TimeSpan.FromSeconds(fallback);

            throw Invalid("expires_in is missing; set auth.defaultTokenLifetimeSeconds to the lifetime your provider documents");
        }

        long seconds;

        if (el.ValueKind == JsonValueKind.Number) {
            if (!el.TryGetInt64(out seconds)) throw Invalid("expires_in is not a whole number of seconds");
        }
        else if (el.ValueKind == JsonValueKind.String) {
            if (!long.TryParse(el.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) throw Invalid("expires_in is not a whole number of seconds");
        }
        else {
            throw Invalid("expires_in is not a number");
        }

        if (seconds <= 0) throw Invalid("expires_in must be positive");

        if (seconds > MaxLifetime.TotalSeconds) {
            if (!_clampWarned) {
                _clampWarned = true;
                Log.Warn("{Side}: token lifetime {Seconds}s exceeds 24h; treating it as 24h", _side, seconds);
            }

            return MaxLifetime;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    OAuthTokenException Invalid(string reason) => new($"{_side}: token endpoint {EndpointHost} returned an invalid token response: {reason}");

    OAuthTokenException ErrorResponse(int status, string body) {
        string? error       = null;
        string? description = null;
        var     codes       = new List<long>();

        try {
            using var doc  = JsonDocument.Parse(body);
            var       root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object) {
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
                if (root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();

                if (root.TryGetProperty("error_codes", out var c) && c.ValueKind == JsonValueKind.Array) {
                    foreach (var item in c.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var code)) codes.Add(code);
                }
            }
        } catch (JsonException) {
            // body is not JSON: report status only
        }

        var message = new StringBuilder($"{_side}: token request to {EndpointHost} failed: HTTP {status}");

        if (error != null) message.Append(", error=").Append(AllowedErrors.Contains(error) ? error : "non-standard error (omitted)");
        if (codes.Count > 0) message.Append(", error_codes=").Append(string.Join(",", codes));

        if (description != null && Log.IsDebugEnabled()) Log.Debug("{Side}: token endpoint error_description: {Description}", _side, Redact(description));

        return new(message.ToString());
    }

    string Redact(string text) {
        var secrets = new List<string>();
        if (!string.IsNullOrEmpty(_secret)) secrets.Add(_secret);
        if (!string.IsNullOrEmpty(_lastAssertion)) secrets.Add(_lastAssertion);
        lock (_seenTokens) secrets.AddRange(_seenTokens);

        return secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length).Aggregate(text, (t, s) => t.Replace(s, "***", StringComparison.Ordinal));
    }

    public void Dispose() => _http.Dispose();
}
```

Note: the expected `Basic` value in the test (`my+app:a%2Bb%3Ac`) is what `WebUtility.UrlEncode` produces (`+` for space, upper-case hex). RFC 6749 §2.3.1 requires `application/x-www-form-urlencoded` encoding of each part.

- [ ] **Step 6: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/TokenEndpointClient*/*"`
Expected: all PASS (including the 11 parameterised invalid-response cases).

- [ ] **Step 7: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/Auth/TokenEndpointClient.cs test/Kurrent.Replicator.Tests/Auth
git commit -m "feat(auth): token endpoint client with strict parsing and sanitised errors

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: ClientCredentialsTokenSource — cache, refresh window, single-flight, cooldown, fallback

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/ClientCredentialsTokenSource.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/ClientCredentialsTokenSourceTests.cs`

**Interfaces:**
- Consumes: `TokenEndpointClient`, `TokenResponse`, `TokenState`, `GrpcAuthOptions`.
- Produces: `public sealed class ClientCredentialsTokenSource : IAccessTokenSource, IDisposable` with ctor `(GrpcAuthOptions options, string side, HttpMessageHandler handler, TimeProvider time, CancellationToken shutdown)`; constants `RequestTimeout = 30 s`, `FailureCooldown = 5 s`, `FallbackMargin = 30 s`.

Rules (spec "Caching and refresh"): cached if `now < expiresAt - min(refreshBefore, lifetime/2)`; shared in-flight task (`Task.Run`, treat completed task as none); cooldown 5 s after failure; fallback to cached token if `now < expiresAt - 30 s` and not invalidated; shared request cancelled by shutdown or 30 s timeout, never by a caller.

- [ ] **Step 1: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/ClientCredentialsTokenSourceTests.cs`:

```csharp
using System.Net;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class ClientCredentialsTokenSourceTests {
    readonly FakeTimeProvider        _time     = new();
    readonly StubTokenEndpoint       _stub     = new();
    readonly CancellationTokenSource _shutdown = new();

    static readonly GrpcAuthOptions Options = new() {
        Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp.example.com/t", ClientId = "c", ClientSecret = "s"
    };

    ClientCredentialsTokenSource NewSource(GrpcAuthOptions? o = null) => new(o ?? Options, "sink", _stub, _time, _shutdown.Token);

    void Respond(params string[] tokens) {
        var i = 0;
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token(tokens[Math.Min(i++, tokens.Length - 1)]));
    }

    void Fail() => _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":\"temporarily_unavailable\"}");

    [Test]
    public async Task Caches_until_refresh_window() {
        Respond("A", "B");
        var s = NewSource();
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
        _time.Advance(TimeSpan.FromSeconds(3299)); // 3600 - 300 - 1
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("B");
        await Assert.That(_stub.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Very_short_lifetime_does_not_refresh_on_every_call() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", 60));
        var s = NewSource(); // refreshBefore 300 > lifetime: window capped at 30s
        await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(29));
        await s.GetAccessToken(default);
        await s.GetAccessToken(default);
        await Assert.That(_stub.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_callers_share_one_successful_request() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource();
        var s     = NewSource();
        var calls = Enumerable.Range(0, 10).Select(_ => s.GetAccessToken(default).AsTask()).ToList();
        await Task.Delay(50);
        _stub.Gate.SetResult();
        var leases = await Task.WhenAll(calls);
        await Assert.That(_stub.Count).IsEqualTo(1);
        await Assert.That(leases.Select(l => l.Value).Distinct().Single()).IsEqualTo("A");
    }

    [Test]
    public async Task Concurrent_callers_share_one_failing_request_and_its_exception() {
        Fail();
        _stub.Gate = new TaskCompletionSource();
        var s     = NewSource();
        var calls = Enumerable.Range(0, 10).Select(_ => s.GetAccessToken(default).AsTask()).ToList();
        await Task.Delay(50);
        _stub.Gate.SetResult();

        foreach (var call in calls) await Assert.That(async () => await call).Throws<OAuthTokenException>();
        await Assert.That(_stub.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cooldown_suppresses_requests_for_five_seconds() {
        Fail();
        var s = NewSource();
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        _time.Advance(TimeSpan.FromSeconds(4));
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        await Assert.That(_stub.Count).IsEqualTo(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        Respond("A");
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
        await Assert.That(_stub.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Refresh_failure_falls_back_to_usable_cached_token() {
        Respond("A");
        var s = NewSource();
        await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(3400)); // inside refresh window, 200s before expiry
        Fail();
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
    }

    [Test]
    public async Task Refresh_failure_with_expired_token_throws_with_status_and_code() {
        Respond("A");
        var s = NewSource();
        await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(3580)); // within 30s of expiry: not usable
        Fail();
        var ex = await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("503");
        await Assert.That(ex.Message).Contains("temporarily_unavailable");
    }

    [Test]
    public async Task Invalidate_forces_refresh_and_disables_fallback() {
        Respond("A");
        var s = NewSource();
        var a = await s.GetAccessToken(default);
        s.Invalidate(a);
        Fail();
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
        await Assert.That(_stub.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Reissued_rejected_token_is_quarantined_then_probed() {
        Respond("A");
        var s = NewSource();
        s.Invalidate(await s.GetAccessToken(default));
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>(); // endpoint re-issues A
        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.That((await s.GetAccessToken(default)).Value).IsEqualTo("A");
    }

    [Test]
    public async Task Same_value_refresh_does_not_lose_rejection() {
        Respond("A");
        var s     = NewSource();
        var call1 = await s.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(3300)); // refresh re-issues A, same generation
        var again = await s.GetAccessToken(default);
        await Assert.That(again.Generation).IsEqualTo(call1.Generation);
        s.Invalidate(call1);
        await Assert.That(async () => await s.GetAccessToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Caller_cancellation_does_not_cancel_shared_request() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource();
        var s = NewSource();
        using var cts = new CancellationTokenSource();
        var cancelled = s.GetAccessToken(cts.Token).AsTask();
        var other     = s.GetAccessToken(default).AsTask();
        await Task.Delay(20);
        await cts.CancelAsync();
        await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
        _stub.Gate.SetResult();
        await Assert.That((await other).Value).IsEqualTo("A");
        await Assert.That(_stub.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_aborts_in_flight_request() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource(); // never released
        var s    = NewSource();
        var call = s.GetAccessToken(default).AsTask();
        await Task.Delay(20);
        await _shutdown.CancelAsync();
        await Assert.That(async () => await call.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Request_times_out_after_30_seconds() {
        Respond("A");
        _stub.Gate = new TaskCompletionSource(); // never released
        var s    = NewSource();
        var call = s.GetAccessToken(default).AsTask();
        await Task.Delay(20);
        _time.Advance(TimeSpan.FromSeconds(31));
        var ex = await Assert.That(async () => await call.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("timed out");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, `ClientCredentialsTokenSource` not found.

- [ ] **Step 3: Implement**

`src/Kurrent.Replicator.KurrentDb/Auth/ClientCredentialsTokenSource.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class ClientCredentialsTokenSource : IAccessTokenSource, IDisposable {
    static ILog Log => LogProvider.GetLogger(typeof(ClientCredentialsTokenSource));

    public static readonly TimeSpan RequestTimeout  = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FallbackMargin  = TimeSpan.FromSeconds(30);

    sealed record Cached(string Value, DateTimeOffset ExpiresAt, TimeSpan Lifetime);

    readonly GrpcAuthOptions     _options;
    readonly string              _side;
    readonly TimeProvider        _time;
    readonly CancellationToken   _shutdown;
    readonly TokenEndpointClient _client;
    readonly TokenState          _state;
    readonly object              _lock = new();

    Cached?               _cached;
    Task<Cached>?         _inflight;
    DateTimeOffset?       _lastFailureAt;
    OAuthTokenException?  _lastFailure;

    public ClientCredentialsTokenSource(GrpcAuthOptions options, string side, HttpMessageHandler handler, TimeProvider time, CancellationToken shutdown) {
        _options  = options;
        _side     = side;
        _time     = time;
        _shutdown = shutdown;
        _client   = new TokenEndpointClient(options, handler, side);
        _state    = new TokenState(time);
    }

    public async ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Task<Cached> task;

        lock (_lock) {
            var now = _time.GetUtcNow();

            if (_cached is { } c && now < c.ExpiresAt - RefreshWindow(c)) return _state.Accept(c.Value);

            if (_inflight is null || _inflight.IsCompleted) {
                if (_lastFailureAt is { } failedAt && now - failedAt < FailureCooldown) {
                    if (Usable(now) is { } fallback) return _state.Accept(fallback);

                    throw new OAuthTokenException(_lastFailure!.Message, _lastFailure);
                }

                _inflight = Task.Run(Refresh, CancellationToken.None);
            }

            task = _inflight;
        }

        try {
            var token = await task.WaitAsync(ct).ConfigureAwait(false);

            return _state.Accept(token.Value);
        } catch (OAuthTokenException) {
            string? fallback;
            lock (_lock) fallback = Usable(_time.GetUtcNow());

            if (fallback == null) throw;

            Log.Warn("{Side}: token refresh failed; using the current token until it nears expiry", _side);

            return _state.Accept(fallback);
        }
    }

    public void Invalidate(AccessTokenLease lease) {
        if (!_state.Invalidate(lease)) {
            Log.Debug("{Side}: ignoring stale token rejection report", _side);

            return;
        }

        lock (_lock) {
            if (_cached?.Value == lease.Value) _cached = null;
        }
    }

    public void ReportAccepted(AccessTokenLease lease) => _state.ReportAccepted(lease);

    TimeSpan RefreshWindow(Cached c) {
        var configured = TimeSpan.FromSeconds(Math.Max(0, _options.RefreshBeforeExpirySeconds));
        var half       = c.Lifetime / 2;

        return configured < half ? configured : half;
    }

    string? Usable(DateTimeOffset now)
        => _cached is { } c && now < c.ExpiresAt - FallbackMargin && !_state.IsRejected(c.Value) ? c.Value : null;

    async Task<Cached> Refresh() {
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, timeout.Token);
        var       started = _time.GetUtcNow();

        try {
            var response = await _client.RequestToken(linked.Token).ConfigureAwait(false);
            var cached   = new Cached(response.AccessToken, started + response.Lifetime, response.Lifetime);

            lock (_lock) {
                _cached        = cached;
                _lastFailureAt = null;
                _lastFailure   = null;
            }

            Log.Info("{Side}: access token acquired from {Host}, expires at {ExpiresAt:O}", _side, _client.EndpointHost, cached.ExpiresAt);

            return cached;
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
            throw;
        } catch (Exception e) {
            var failure = e switch {
                OAuthTokenException o      => o,
                OperationCanceledException => new OAuthTokenException($"{_side}: token request to {_client.EndpointHost} timed out after {RequestTimeout.TotalSeconds:0}s", e),
                _                          => new OAuthTokenException($"{_side}: token request to {_client.EndpointHost} failed: {e.GetType().Name}", e)
            };

            lock (_lock) {
                _lastFailureAt = _time.GetUtcNow();
                _lastFailure   = failure;
            }

            throw failure;
        }
    }

    public void Dispose() => _client.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/ClientCredentialsTokenSourceTests/*"`
Expected: 13 tests PASS. If `Request_times_out_after_30_seconds` hangs, check that the timeout CTS is built with `_time` (`new CancellationTokenSource(TimeSpan, TimeProvider)`), so `FakeTimeProvider.Advance` fires it.

- [ ] **Step 5: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/Auth/ClientCredentialsTokenSource.cs test/Kurrent.Replicator.Tests/Auth/ClientCredentialsTokenSourceTests.cs
git commit -m "feat(auth): client credentials token source

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 6: TokenGate and GrpcAuthContext

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/TokenGate.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthContext.cs`
- Create: `test/Kurrent.Replicator.Tests/Auth/Support/ControllableTokenSource.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/TokenGateTests.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/GrpcAuthContextTests.cs`

**Interfaces:**
- Consumes: `IAccessTokenSource`, `AccessTokenLease`, `AuthFailure`, `RateLimitedWarning`, `TokenState` (test support only).
- Produces:
  - `public sealed class TokenGate(TimeProvider time, string side)` with `ValueTask<AccessTokenLease> WaitForToken(IAccessTokenSource source, CancellationToken ct)` and `static TimeSpan Backoff(int attempt)` (1, 2, 4, 8, 16, 30, 30 … seconds).
  - `public readonly record struct CallAuth(UserCredentials? Credentials, AccessTokenLease? Lease)`.
  - `public sealed class GrpcAuthContext` with ctor `(IAccessTokenSource? source, CancellationToken shutdown, TimeProvider time, string side)`, `static GrpcAuthContext None`, `bool Enabled`, `CancellationToken Shutdown`, `string Side`, `TimeProvider Time`, `ValueTask<CallAuth> AcquireCredentials(CancellationToken ct)`, `void ReportAccepted(CallAuth)`, `void ReportFailure(CallAuth, Exception?)`, `Task<T> Run<T>(Func<CallAuth, CancellationToken, Task<T>> call, CancellationToken ct)`, `Task Run(Func<CallAuth, CancellationToken, Task> call, CancellationToken ct)`.
  - Test support `ControllableTokenSource(TimeProvider time) : IAccessTokenSource` — `string? Value` (null → throws `OAuthTokenException`), `ConcurrentQueue<(AccessTokenLease Lease, bool Applied)> Invalidated`, `ConcurrentQueue<AccessTokenLease> Accepted`, `int Calls`, `long Generation`, `Action<AccessTokenLease>? OnInvalidate`.

`Run` passes the caller's `ct` (not the shutdown-linked token) to `call`, so a graceful shutdown never cancels an in-flight write; only waits (token acquisition, backoff) are bound to shutdown.

- [ ] **Step 1: Create the controllable token source**

`test/Kurrent.Replicator.Tests/Auth/Support/ControllableTokenSource.cs`:

```csharp
using System.Collections.Concurrent;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Auth.Support;

/// <summary>A token source whose current value the test controls, with the real TokenState rules.</summary>
public sealed class ControllableTokenSource(TimeProvider time) : IAccessTokenSource {
    readonly TokenState _state = new(time);
    int                 _calls;

    public volatile string? Value;

    public ConcurrentQueue<(AccessTokenLease Lease, bool Applied)> Invalidated { get; } = new();
    public ConcurrentQueue<AccessTokenLease>                       Accepted    { get; } = new();
    public Action<AccessTokenLease>?                               OnInvalidate { get; set; }

    public int  Calls      => Volatile.Read(ref _calls);
    public long Generation => _state.Generation;

    public ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct) {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        var value = Value ?? throw new OAuthTokenException("test: token unavailable");

        return ValueTask.FromResult(_state.Accept(value));
    }

    public void Invalidate(AccessTokenLease lease) {
        var applied = _state.Invalidate(lease);
        Invalidated.Enqueue((lease, applied));
        OnInvalidate?.Invoke(lease);
    }

    public void ReportAccepted(AccessTokenLease lease) {
        Accepted.Enqueue(lease);
        _state.ReportAccepted(lease);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/TokenGateTests.cs`:

```csharp
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenGateTests {
    readonly FakeTimeProvider _time = new();

    [Test]
    public async Task Backoff_doubles_and_caps_at_30_seconds() {
        var delays = Enumerable.Range(0, 8).Select(i => TokenGate.Backoff(i).TotalSeconds).ToArray();
        await Assert.That(delays).IsEquivalentTo(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 });
    }

    [Test]
    public async Task Waits_until_source_recovers() {
        var source = new ControllableTokenSource(_time);
        var wait   = new TokenGate(_time, "sink").WaitForToken(source, default).AsTask();
        await TimeDriver.Until(() => source.Calls >= 3, _time);
        await Assert.That(wait.IsCompleted).IsFalse();
        source.Value = "A";
        var lease = await TimeDriver.Drive(wait, _time);
        await Assert.That(lease.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Cancellation_ends_the_wait() {
        var source = new ControllableTokenSource(_time);
        using var cts = new CancellationTokenSource();
        var wait = new TokenGate(_time, "sink").WaitForToken(source, cts.Token).AsTask();
        await TimeDriver.Until(() => source.Calls >= 2, _time);
        await cts.CancelAsync();
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    }
}

[NotInParallel("global-logger")]
public class TokenGateLoggingTests {
    [Test]
    public async Task Warnings_are_rate_limited_and_recovery_is_logged_once() {
        using var logs   = new LogCapture();
        var       time   = new FakeTimeProvider();
        var       side   = $"side-{Guid.NewGuid():N}";
        var       source = new ControllableTokenSource(time);
        var       wait   = new TokenGate(time, side).WaitForToken(source, default).AsTask();

        await TimeDriver.Until(() => time.GetUtcNow() - time.Start >= TimeSpan.FromSeconds(200), time, TimeSpan.FromSeconds(5));
        source.Value = "A";
        await TimeDriver.Drive(wait, time);

        var warnings = logs.Events.Count(e => e.Level == LogEventLevel.Warning && e.Text.Contains(side));
        await Assert.That(warnings).IsLessThanOrEqualTo(5); // first + at most once per minute over ~200s
        await Assert.That(warnings).IsGreaterThanOrEqualTo(1);
        await Assert.That(logs.Events.Count(e => e.Level == LogEventLevel.Information && e.Text.Contains(side) && e.Text.Contains("acquired again"))).IsEqualTo(1);
    }
}
```

Note: `FakeTimeProvider.Start` is the provider's initial time.

`test/Kurrent.Replicator.Tests/Auth/GrpcAuthContextTests.cs`:

```csharp
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcAuthContextTests {
    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly ControllableTokenSource _source;
    readonly GrpcAuthContext         _ctx;

    public GrpcAuthContextTests() {
        _source = new(_time) { Value = "A" };
        _ctx    = new(_source, _shutdown.Token, _time, "sink");
    }

    static NotAuthenticatedException Unauthenticated() => new("no", new RpcException(new Status(StatusCode.Unauthenticated, "no")));

    [Test]
    public async Task None_passes_no_credentials_and_calls_once() {
        var calls = 0;
        CallAuth seen = default;
        var result = await GrpcAuthContext.None.Run((a, _) => { calls++; seen = a; return Task.FromResult(42); }, default);
        await Assert.That(result).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(seen.Credentials).IsNull();
        await Assert.That((await GrpcAuthContext.None.AcquireCredentials(default)).Credentials).IsNull();
        await Assert.That(GrpcAuthContext.None.Enabled).IsFalse();
    }

    [Test]
    public async Task AcquireCredentials_returns_bearer_with_lease() {
        var auth = await _ctx.AcquireCredentials(default);
        await Assert.That(auth.Credentials!.ToString()).IsEqualTo("Bearer A");
        await Assert.That(auth.Lease!.Value.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Run_reports_acceptance_with_the_lease_the_call_used() {
        AccessTokenLease? used = null;
        await _ctx.Run((a, _) => { used = a.Lease; return Task.CompletedTask; }, default);
        await Assert.That(_source.Accepted.Single()).IsEqualTo(used!.Value);
    }

    [Test]
    public async Task Run_passes_the_caller_token_to_the_call() {
        using var caller = new CancellationTokenSource();
        CancellationToken seen = default;
        await _ctx.Run((_, c) => { seen = c; return Task.CompletedTask; }, caller.Token);
        await Assert.That(seen).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task Run_retries_token_rejection_with_invalidation_and_new_token() {
        var headers = new List<string>();

        var run = _ctx.Run((a, _) => {
            headers.Add(a.Credentials!.ToString());
            if (headers.Count == 1) {
                _source.Value = "B";
                throw Unauthenticated();
            }

            return Task.FromResult(1);
        }, default);

        await TimeDriver.Drive(run, _time);
        await Assert.That(headers).IsEquivalentTo(new[] { "Bearer A", "Bearer B" });
        await Assert.That(_source.Invalidated.Single().Applied).IsTrue();
        await Assert.That(_source.Invalidated.Single().Lease.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Run_propagates_permission_denied_and_other_errors_immediately() {
        var calls = 0;
        var denied = new AccessDeniedException("no", new RpcException(new Status(StatusCode.PermissionDenied, "no")));
        await Assert.That(async () => await _ctx.Run((_, _) => { calls++; throw denied; }, default)).Throws<AccessDeniedException>();
        await Assert.That(async () => await _ctx.Run((_, _) => { calls++; throw new InvalidOperationException(); }, default)).Throws<InvalidOperationException>();
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(_source.Invalidated).IsEmpty();
    }

    [Test]
    public async Task Shutdown_ends_the_retry_loop() {
        _source.Value = null;
        var run = _ctx.Run((_, _) => Task.FromResult(1), default);
        await TimeDriver.Until(() => _source.Calls >= 2, _time);
        await _shutdown.CancelAsync();
        await Assert.That(async () => await run).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ReportFailure_only_invalidates_on_token_failures() {
        var auth = await _ctx.AcquireCredentials(default);
        _ctx.ReportFailure(auth, new InvalidOperationException());
        _ctx.ReportFailure(auth, null);
        await Assert.That(_source.Invalidated).IsEmpty();
        _ctx.ReportFailure(auth, Unauthenticated());
        await Assert.That(_source.Invalidated.Single().Applied).IsTrue();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, `TokenGate` / `GrpcAuthContext` / `CallAuth` not found.

- [ ] **Step 4: Implement TokenGate**

`src/Kurrent.Replicator.KurrentDb/Auth/TokenGate.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class TokenGate(TimeProvider time, string side) {
    static ILog Log => LogProvider.GetLogger(typeof(TokenGate));

    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    readonly RateLimitedWarning _warn = new(time, TimeSpan.FromSeconds(60));

    public static TimeSpan Backoff(int attempt) {
        var seconds = attempt >= 5 ? MaxBackoff.TotalSeconds : Math.Pow(2, attempt);

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    public async ValueTask<AccessTokenLease> WaitForToken(IAccessTokenSource source, CancellationToken ct) {
        var failures = 0;

        while (true) {
            ct.ThrowIfCancellationRequested();

            try {
                var lease = await source.GetAccessToken(ct).ConfigureAwait(false);

                if (failures > 0) {
                    Log.Info("{Side}: access token acquired again after {Failures} failed attempts", side, failures);
                    _warn.Reset();
                }

                return lease;
            } catch (OAuthTokenException e) {
                if (_warn.ShouldLog()) Log.Warn("{Side}: no access token available ({Reason}); retrying with backoff", side, e.Message);

                await Task.Delay(Backoff(failures++), time, ct).ConfigureAwait(false);
            }
        }
    }
}
```

- [ ] **Step 5: Implement GrpcAuthContext**

`src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthContext.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>Credentials for one gRPC call, plus the lease they were issued under. Both null without OAuth.</summary>
public readonly record struct CallAuth(UserCredentials? Credentials, AccessTokenLease? Lease);

public sealed class GrpcAuthContext {
    static ILog Log => LogProvider.GetLogger(typeof(GrpcAuthContext));

    public static GrpcAuthContext None { get; } = new(null, CancellationToken.None, TimeProvider.System, "none");

    readonly IAccessTokenSource? _source;
    readonly TokenGate           _gate;
    readonly RateLimitedWarning  _warn;

    public GrpcAuthContext(IAccessTokenSource? source, CancellationToken shutdown, TimeProvider time, string side) {
        _source  = source;
        Shutdown = shutdown;
        Time     = time;
        Side     = side;
        _gate    = new TokenGate(time, side);
        _warn    = new RateLimitedWarning(time, TimeSpan.FromSeconds(60));
    }

    public bool              Enabled  => _source != null;
    public CancellationToken Shutdown { get; }
    public TimeProvider      Time     { get; }
    public string            Side     { get; }

    public async ValueTask<CallAuth> AcquireCredentials(CancellationToken ct) {
        if (_source == null) return default;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown);
        var       lease  = await _gate.WaitForToken(_source, linked.Token).ConfigureAwait(false);

        return new(new UserCredentials(lease.Value), lease);
    }

    public void ReportAccepted(CallAuth auth) {
        if (_source != null && auth.Lease is { } lease) _source.ReportAccepted(lease);
    }

    public void ReportFailure(CallAuth auth, Exception? error) {
        if (_source != null && auth.Lease is { } lease && error != null && AuthFailure.IsTokenFailure(error)) _source.Invalidate(lease);
    }

    public async Task<T> Run<T>(Func<CallAuth, CancellationToken, Task<T>> call, CancellationToken ct) {
        if (_source == null) return await call(default, ct).ConfigureAwait(false);

        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown);
        var       failures = 0;

        while (true) {
            var auth = await AcquireCredentials(linked.Token).ConfigureAwait(false);

            try {
                var result = await call(auth, ct).ConfigureAwait(false);
                ReportAccepted(auth);

                if (failures > 0) {
                    Log.Info("{Side}: KurrentDB accepted the access token again after {Failures} failed attempts", Side, failures);
                    _warn.Reset();
                }

                return result;
            } catch (Exception e) when (AuthFailure.IsTokenFailure(e) && !linked.IsCancellationRequested) {
                ReportFailure(auth, e);

                if (_warn.ShouldLog()) Log.Warn("{Side}: gRPC call failed with a token error ({Error}); retrying with backoff", Side, e.Message);

                await Task.Delay(TokenGate.Backoff(failures++), Time, linked.Token).ConfigureAwait(false);
            }
        }
    }

    public Task Run(Func<CallAuth, CancellationToken, Task> call, CancellationToken ct)
        => Run<bool>(async (a, c) => {
                await call(a, c).ConfigureAwait(false);

                return true;
            },
            ct
        );
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/TokenGate*/*"` and the same with `GrpcAuthContextTests`.
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/Auth test/Kurrent.Replicator.Tests/Auth
git commit -m "feat(auth): token gate and per-call auth context

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Options validation, fallback hook, source factory, fake KurrentDB handler

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthOptionsValidator.cs`
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthentication.cs`
- Create: `test/Kurrent.Replicator.Tests/Auth/Support/FakeKurrentDbHandler.cs`
- Create: `test/Kurrent.Replicator.Tests/Auth/Support/TestEvents.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/GrpcAuthOptionsValidatorTests.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/GrpcAuthenticationTests.cs`

**Interfaces:**
- Consumes: `GrpcAuthOptions`, `IAccessTokenSource`, `ClientCredentialsTokenSource`, `TokenFileSource`, `TokenEndpointClient.CreateDefaultHandler()`.
- Produces:
  - `public static class GrpcAuthOptionsValidator` — `IReadOnlyList<string> Validate(GrpcAuthOptions, EventStoreClientSettings)`, `IReadOnlyList<string> Warnings(GrpcAuthOptions)`, `string? ValidateProtocol(GrpcAuthOptions, string? protocol)`, `void EnsureValid(string side, GrpcAuthOptions, EventStoreClientSettings)` (throws `InvalidOperationException("Invalid {side} auth configuration: e1; e2")`).
  - `public static class GrpcAuthentication` — `FallbackUsage Apply(EventStoreClientSettings, IAccessTokenSource)`, `IAccessTokenSource? CreateSource(GrpcAuthOptions, string side, TimeProvider, CancellationToken shutdown)`; `public sealed class FallbackUsage { int Count }`.
  - Test support `FakeKurrentDbHandler : HttpMessageHandler` — `ConcurrentQueue<Seen> Requests` (`record Seen(string Path, string? Authorization)`), `Func<Seen, Task<HttpResponseMessage>> Respond`, constants `AppendPath`, `ReadPath`, `DeletePath`, helpers `TrailersOnly(StatusCode)`, `AppendSuccess()`, `static EventStoreClient Client(FakeKurrentDbHandler, string connectionString = "esdb://localhost:2113?tls=true", Action<EventStoreClientSettings>? configure = null)`.
  - Test support `TestEvents` — `Proposed(stream)`, `Meta(stream)`, `Delete(stream)`, `Original(stream, eventNumber)`.

- [ ] **Step 1: Create the fake KurrentDB handler and test events**

`test/Kurrent.Replicator.Tests/Auth/Support/FakeKurrentDbHandler.cs`:

```csharp
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using EventStore.Client;
using Grpc.Core;

namespace Kurrent.Replicator.Tests.Auth.Support;

/// <summary>
/// Stands in for KurrentDB at the HTTP/2 level: records the path and authorization header of every gRPC call
/// and answers with a configurable gRPC response. Server-feature discovery is answered with Unimplemented so the
/// client uses the classic Append RPC.
/// </summary>
public sealed class FakeKurrentDbHandler : HttpMessageHandler {
    public const string AppendPath = "/event_store.client.streams.Streams/Append";
    public const string ReadPath   = "/event_store.client.streams.Streams/Read";
    public const string DeletePath = "/event_store.client.streams.Streams/Delete";

    public sealed record Seen(string Path, string? Authorization);

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>Default: every call fails with Unavailable (enough for header-capture tests).</summary>
    public Func<Seen, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(TrailersOnly(StatusCode.Unavailable));

    public IReadOnlyList<string?> AuthorizationsFor(string path) => Requests.Where(r => r.Path == path).Select(r => r.Authorization).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.AbsolutePath;

        if (path.StartsWith("/event_store.client.server_features.", StringComparison.Ordinal)) return TrailersOnly(StatusCode.Unimplemented);

        if (request.Content != null) await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var auth = request.Headers.TryGetValues("authorization", out var values) ? values.FirstOrDefault() : null;
        var seen = new Seen(path, auth);
        Requests.Enqueue(seen);

        return await Respond(seen);
    }

    public static HttpResponseMessage TrailersOnly(StatusCode code) {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = new Version(2, 0), Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.Headers.Add("grpc-status", ((int)code).ToString());
        response.Headers.Add("grpc-message", code.ToString());

        return response;
    }

    /// <summary>AppendResp { success { current_revision = 0, position { commit = 1, prepare = 1 } } }</summary>
    public static HttpResponseMessage AppendSuccess() => Unary([0x0A, 0x08, 0x08, 0x00, 0x1A, 0x04, 0x08, 0x01, 0x10, 0x01]);

    static HttpResponseMessage Unary(byte[] message) {
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame, 5);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = new Version(2, 0), Content = new ByteArrayContent(frame) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.TrailingHeaders.Add("grpc-status", "0");

        return response;
    }

    /// <summary>Append succeeds for the listed bearer tokens and fails Unauthenticated for anything else; other calls: Unavailable / Unauthenticated.</summary>
    public static Func<Seen, Task<HttpResponseMessage>> AcceptBearer(params string[] tokens)
        => seen => {
            var ok = tokens.Any(t => seen.Authorization == $"Bearer {t}");

            return Task.FromResult(
                !ok                         ? TrailersOnly(StatusCode.Unauthenticated) :
                seen.Path == AppendPath     ? AppendSuccess() :
                                              TrailersOnly(StatusCode.Unavailable)
            );
        };

    public static EventStoreClient Client(FakeKurrentDbHandler handler, string connectionString = "esdb://localhost:2113?tls=true", Action<EventStoreClientSettings>? configure = null) {
        var settings = EventStoreClientSettings.Create(connectionString);
        settings.CreateHttpMessageHandler = () => handler;
        configure?.Invoke(settings);

        return new EventStoreClient(settings);
    }
}
```

`test/Kurrent.Replicator.Tests/Auth/Support/TestEvents.cs`:

```csharp
using System.Diagnostics;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Tests.Auth.Support;

public static class TestEvents {
    static EventDetails Details(string stream) => new(stream, Guid.NewGuid(), "TestEvent", ContentTypes.Json);

    static readonly TracingMetadata Tracing = new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());

    public static ProposedEvent Proposed(string stream, long eventNumber = 0)
        => new(Details(stream), "{}"u8.ToArray(), null, new LogPosition(eventNumber, (ulong)eventNumber + 1), eventNumber);

    public static ProposedMetaEvent Meta(string stream)
        => new(Details(stream), new StreamMetadata(10, null, null, null, null), new LogPosition(0, 1), 0);

    public static ProposedDeleteStream Delete(string stream) => new(Details(stream), new LogPosition(0, 1), 0);

    public static OriginalEvent Original(string stream, long eventNumber, DateTimeOffset? created = null)
        => new(created ?? DateTimeOffset.UtcNow, Details(stream), "{}"u8.ToArray(), null, new LogPosition(eventNumber, (ulong)eventNumber + 1), eventNumber, Tracing);
}
```

- [ ] **Step 2: Write the failing validator tests**

`test/Kurrent.Replicator.Tests/Auth/GrpcAuthOptionsValidatorTests.cs`:

```csharp
using EventStore.Client;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcAuthOptionsValidatorTests {
    static readonly EventStoreClientSettings Tls = EventStoreClientSettings.Create("esdb://localhost:2113?tls=true");

    static GrpcAuthOptions Cc(Func<GrpcAuthOptions, GrpcAuthOptions>? f = null) {
        var o = new GrpcAuthOptions { Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp/t", ClientId = "c", ClientSecret = "s" };

        return f?.Invoke(o) ?? o;
    }

    [Test]
    public async Task Connection_string_mode_is_always_valid() {
        var basic = EventStoreClientSettings.Create("esdb://admin:changeit@localhost:2113?tls=false");
        await Assert.That(GrpcAuthOptionsValidator.Validate(GrpcAuthOptions.Default, basic)).IsEmpty();
    }

    [Test]
    public async Task Valid_client_credentials_pass() => await Assert.That(GrpcAuthOptionsValidator.Validate(Cc(), Tls)).IsEmpty();

    [Test]
    [Arguments("esdb://admin:changeit@localhost:2113?tls=true", "credentials")]
    [Arguments("esdb://localhost:2113?tls=false", "tls")]
    public async Task Connection_string_conflicts_are_errors(string cs, string expected) {
        var errors = GrpcAuthOptionsValidator.Validate(Cc(), EventStoreClientSettings.Create(cs));
        await Assert.That(string.Join(";", errors)).Contains(expected);
    }

    [Test]
    public async Task Client_credentials_rules() {
        async Task Expect(GrpcAuthOptions o, string fragment)
            => await Assert.That(string.Join(";", GrpcAuthOptionsValidator.Validate(o, Tls))).Contains(fragment);

        await Expect(Cc(o => o with { TokenEndpoint = null }), "tokenEndpoint is required");
        await Expect(Cc(o => o with { TokenEndpoint = "http://idp.example.com/t" }), "https");
        await Expect(Cc(o => o with { TokenEndpoint = "/relative" }), "https");
        await Expect(Cc(o => o with { ClientId = " " }), "clientId is required");
        await Expect(Cc(o => o with { ClientSecret = null }), "exactly one of");
        await Expect(Cc(o => o with { ClientSecretFile = "/x" }), "exactly one of");
        await Expect(Cc(o => o with { ClientSecret = null, ClientSecretFile = "/does/not/exist" }), "clientSecretFile");
        await Expect(Cc(o => o with { ClientSecret = null, ClientAssertionFile = "/does/not/exist" }), "clientAssertionFile");
        await Expect(Cc(o => o with { RefreshBeforeExpirySeconds = -1 }), "refreshBeforeExpirySeconds");
        await Expect(Cc(o => o with { DefaultTokenLifetimeSeconds = 0 }), "defaultTokenLifetimeSeconds");
        await Expect(Cc(o => o with { AdditionalParameters = new Dictionary<string, string> { ["Scope"] = "x", ["audience"] = "y" } }), "Scope");
    }

    [Test]
    public async Task Loopback_http_is_allowed() {
        await Assert.That(GrpcAuthOptionsValidator.Validate(Cc(o => o with { TokenEndpoint = "http://127.0.0.1:8080/t" }), Tls)).IsEmpty();
        await Assert.That(GrpcAuthOptionsValidator.Validate(Cc(o => o with { TokenEndpoint = "http://localhost:8080/t" }), Tls)).IsEmpty();
    }

    [Test]
    public async Task Empty_secret_file_is_an_error() {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "  \n");
        var errors = GrpcAuthOptionsValidator.Validate(Cc(o => o with { ClientSecret = null, ClientSecretFile = file }), Tls);
        await Assert.That(string.Join(";", errors)).Contains("clientSecretFile");
        File.Delete(file);
    }

    [Test]
    public async Task Token_file_rules() {
        var ok = new GrpcAuthOptions { Type = GrpcAuthType.OAuthTokenFile, TokenFile = "/not/yet/written" };
        await Assert.That(GrpcAuthOptionsValidator.Validate(ok, Tls)).IsEmpty(); // existence not required at startup
        await Assert.That(string.Join(";", GrpcAuthOptionsValidator.Validate(ok with { TokenFile = null }, Tls))).Contains("tokenFile is required");
        await Assert.That(string.Join(";", GrpcAuthOptionsValidator.Validate(ok with { TokenFileReloadSeconds = 0 }, Tls))).Contains("tokenFileReloadSeconds");
    }

    [Test]
    public async Task EnsureValid_reports_all_errors_with_side() {
        var ex = await Assert.That(() => GrpcAuthOptionsValidator.EnsureValid("reader", Cc(o => o with { ClientId = null, TokenEndpoint = null }), Tls))
            .Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).StartsWith("Invalid reader auth configuration: ");
        await Assert.That(ex.Message).Contains("clientId is required");
        await Assert.That(ex.Message).Contains("tokenEndpoint is required");
    }

    [Test]
    public async Task Protocol_must_be_grpc_for_oauth() {
        await Assert.That(GrpcAuthOptionsValidator.ValidateProtocol(Cc(), "tcp")).IsNotNull();
        await Assert.That(GrpcAuthOptionsValidator.ValidateProtocol(Cc(), "GRPC")).IsNull();
        await Assert.That(GrpcAuthOptionsValidator.ValidateProtocol(GrpcAuthOptions.Default, "tcp")).IsNull();
    }

    [Test]
    public async Task Oauth_options_with_connection_string_type_warn() {
        var warnings = GrpcAuthOptionsValidator.Warnings(GrpcAuthOptions.Default with { ClientId = "c", TokenFile = "/t" });
        await Assert.That(string.Join(";", warnings)).Contains("clientId");
        await Assert.That(string.Join(";", warnings)).Contains("tokenFile");
        await Assert.That(GrpcAuthOptionsValidator.Warnings(Cc())).IsEmpty();
    }
}
```

- [ ] **Step 3: Write the failing GrpcAuthentication tests**

`test/Kurrent.Replicator.Tests/Auth/GrpcAuthenticationTests.cs`:

```csharp
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcAuthenticationTests {
    static EventData Event() => new(Uuid.NewUuid(), "T", "{}"u8.ToArray());

    [Test]
    public async Task Fake_handler_append_succeeds_with_basic_credentials() {
        // Sanity check of the hand-encoded AppendResp: if this fails, fix FakeKurrentDbHandler before anything else.
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) };
        var client  = FakeKurrentDbHandler.Client(handler, "esdb://admin:changeit@localhost:2113?tls=true");
        var result  = await client.AppendToStreamAsync("s", StreamState.Any, [Event()]);
        await Assert.That(result.LogPosition.CommitPosition).IsEqualTo(1UL);
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath).Single()).StartsWith("Basic ");
    }

    [Test]
    public async Task Fallback_hook_fetches_from_source_when_no_per_call_credentials() {
        var source   = new ControllableTokenSource(new FakeTimeProvider()) { Value = "A" };
        var handler  = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) };
        FallbackUsage usage = null!;
        var client   = FakeKurrentDbHandler.Client(handler, configure: s => usage = GrpcAuthentication.Apply(s, source));

        await client.AppendToStreamAsync("s", StreamState.Any, [Event()]);
        source.Value = "B";
        await client.AppendToStreamAsync("s", StreamState.Any, [Event()]);

        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath)).IsEquivalentTo(new[] { "Bearer A", "Bearer B" });
        await Assert.That(usage.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Per_call_credentials_bypass_the_fallback() {
        var source  = new ControllableTokenSource(new FakeTimeProvider()) { Value = "A" };
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) };
        FallbackUsage usage = null!;
        var client  = FakeKurrentDbHandler.Client(handler, configure: s => usage = GrpcAuthentication.Apply(s, source));

        await client.AppendToStreamAsync("s", StreamState.Any, [Event()], userCredentials: new UserCredentials("X"));
        await client.AppendToStreamAsync("s", StreamState.Any, [Event()], userCredentials: new UserCredentials("u", "p"));

        var headers = handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath);
        await Assert.That(headers[0]).IsEqualTo("Bearer X");
        await Assert.That(headers[1]).StartsWith("Basic ");
        await Assert.That(usage.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Fallback_applies_to_streaming_reads() {
        var source  = new ControllableTokenSource(new FakeTimeProvider()) { Value = "A" };
        var handler = new FakeKurrentDbHandler();
        var client  = FakeKurrentDbHandler.Client(handler, configure: s => GrpcAuthentication.Apply(s, source));

        await Assert.That(async () => await client.ReadAllAsync(Direction.Forwards, Position.Start, 1).ToArrayAsync()).Throws<Exception>();
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).First()).IsEqualTo("Bearer A");
    }

    [Test]
    public async Task CreateSource_matches_the_type() {
        var time = TimeProvider.System;
        await Assert.That(GrpcAuthentication.CreateSource(GrpcAuthOptions.Default, "sink", time, default)).IsNull();
        await Assert.That(GrpcAuthentication.CreateSource(new() { Type = GrpcAuthType.OAuthTokenFile, TokenFile = "/t" }, "sink", time, default)).IsTypeOf<TokenFileSource>();

        var cc = new GrpcAuthOptions { Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp/t", ClientId = "c", ClientSecret = "s" };
        await Assert.That(GrpcAuthentication.CreateSource(cc, "sink", time, default)).IsTypeOf<ClientCredentialsTokenSource>();
    }
}
```

`ToArrayAsync` on `ReadStreamResult` comes from `System.Linq.Async`, which the client already references transitively; if the compiler cannot find it, add `<PackageReference Include="System.Linq.Async" Version="6.0.1"/>` to the test project.

- [ ] **Step 4: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, `GrpcAuthOptionsValidator` / `GrpcAuthentication` not found.

- [ ] **Step 5: Implement the validator**

`src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthOptionsValidator.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

public static class GrpcAuthOptionsValidator {
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) {
        "grant_type", "client_id", "client_secret", "client_assertion", "client_assertion_type", "scope"
    };

    public static IReadOnlyList<string> Validate(GrpcAuthOptions o, EventStoreClientSettings settings) {
        var errors = new List<string>();

        if (!o.IsOAuth) return errors;

        if (settings.DefaultCredentials != null)
            errors.Add("the connection string contains credentials (user:pass@); remove them when using OAuth");

        if (settings.ConnectivitySettings.Insecure)
            errors.Add("tls must be enabled for OAuth (bearer tokens are not sent over insecure channels)");

        if (o.Type == GrpcAuthType.OAuthClientCredentials) ValidateClientCredentials(o, errors);
        else ValidateTokenFile(o, errors);

        return errors;
    }

    static void ValidateClientCredentials(GrpcAuthOptions o, List<string> errors) {
        if (string.IsNullOrWhiteSpace(o.TokenEndpoint)) {
            errors.Add("tokenEndpoint is required");
        }
        else if (!Uri.TryCreate(o.TokenEndpoint, UriKind.Absolute, out var uri) ||
                 !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))) {
            errors.Add("tokenEndpoint must be an absolute https URL (http is only allowed for loopback hosts)");
        }

        if (string.IsNullOrWhiteSpace(o.ClientId)) errors.Add("clientId is required");

        var credentialCount = new[] { o.ClientSecret, o.ClientSecretFile, o.ClientAssertionFile }.Count(x => !string.IsNullOrEmpty(x));

        if (credentialCount != 1) errors.Add("exactly one of clientSecret, clientSecretFile or clientAssertionFile must be set");

        if (!string.IsNullOrEmpty(o.ClientSecretFile) && (!File.Exists(o.ClientSecretFile) || File.ReadAllText(o.ClientSecretFile).Trim().Length == 0))
            errors.Add($"clientSecretFile {o.ClientSecretFile} does not exist or is empty");

        if (!string.IsNullOrEmpty(o.ClientAssertionFile) && !File.Exists(o.ClientAssertionFile))
            errors.Add($"clientAssertionFile {o.ClientAssertionFile} does not exist");

        if (o.RefreshBeforeExpirySeconds < 0) errors.Add("refreshBeforeExpirySeconds must be >= 0");

        if (o.DefaultTokenLifetimeSeconds is <= 0) errors.Add("defaultTokenLifetimeSeconds must be > 0");

        var reserved = o.AdditionalParameters.Keys.Where(Reserved.Contains).ToList();

        if (reserved.Count > 0) errors.Add($"additionalParameters must not override {string.Join(", ", reserved)}");
    }

    static void ValidateTokenFile(GrpcAuthOptions o, List<string> errors) {
        if (string.IsNullOrWhiteSpace(o.TokenFile)) errors.Add("tokenFile is required");

        if (o.TokenFileReloadSeconds <= 0) errors.Add("tokenFileReloadSeconds must be > 0");
    }

    public static IReadOnlyList<string> Warnings(GrpcAuthOptions o) {
        if (o.IsOAuth) return [];

        var set = new List<string>();
        if (o.TokenEndpoint != null) set.Add("tokenEndpoint");
        if (o.ClientId != null) set.Add("clientId");
        if (o.ClientSecret != null) set.Add("clientSecret");
        if (o.ClientSecretFile != null) set.Add("clientSecretFile");
        if (o.ClientAssertionFile != null) set.Add("clientAssertionFile");
        if (o.Scope != null) set.Add("scope");
        if (o.AdditionalParameters.Count > 0) set.Add("additionalParameters");
        if (o.DefaultTokenLifetimeSeconds != null) set.Add("defaultTokenLifetimeSeconds");
        if (o.TokenFile != null) set.Add("tokenFile");

        return set.Count == 0 ? [] : [$"auth options {string.Join(", ", set)} are ignored because auth.type is connectionString"];
    }

    public static string? ValidateProtocol(GrpcAuthOptions o, string? protocol)
        => o.IsOAuth && !string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase)
            ? $"auth.type {o.Type} is only supported with protocol grpc (configured: {protocol ?? "none"})"
            : null;

    public static void EnsureValid(string side, GrpcAuthOptions o, EventStoreClientSettings settings) {
        var errors = Validate(o, settings);

        if (errors.Count > 0) throw new InvalidOperationException($"Invalid {side} auth configuration: {string.Join("; ", errors)}");
    }
}
```

- [ ] **Step 6: Implement GrpcAuthentication**

`src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthentication.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class FallbackUsage {
    int _count;

    public int Count => Volatile.Read(ref _count);

    internal void Increment() => Interlocked.Increment(ref _count);
}

public static class GrpcAuthentication {
    static ILog Log => LogProvider.GetLogger(typeof(GrpcAuthentication));

    /// <summary>Marks "no per-call credentials were passed"; never sent as-is.</summary>
    internal static readonly UserCredentials Sentinel = new("replicator-oauth-sentinel");

    public static FallbackUsage Apply(EventStoreClientSettings settings, IAccessTokenSource source) {
        var usage = new FallbackUsage();
        settings.DefaultCredentials = Sentinel;

        settings.OperationOptions.GetAuthenticationHeaderValue = async (credentials, ct) => {
            if (!ReferenceEquals(credentials, Sentinel)) return credentials.ToString();

            usage.Increment();
            Log.Debug("gRPC call made without per-call credentials; using the fallback token");
            var lease = await source.GetAccessToken(ct).ConfigureAwait(false);

            return $"Bearer {lease.Value}";
        };

        return usage;
    }

    public static IAccessTokenSource? CreateSource(GrpcAuthOptions options, string side, TimeProvider time, CancellationToken shutdown)
        => options.Type switch {
            GrpcAuthType.ConnectionString       => null,
            GrpcAuthType.OAuthClientCredentials => new ClientCredentialsTokenSource(options, side, TokenEndpointClient.CreateDefaultHandler(), time, shutdown),
            GrpcAuthType.OAuthTokenFile         => new TokenFileSource(options.TokenFile!, TimeSpan.FromSeconds(options.TokenFileReloadSeconds), time, side),
            _                                   => throw new ArgumentOutOfRangeException(nameof(options), options.Type, "Unknown auth type")
        };
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/GrpcAuth*/*"`
Expected: all PASS. If `Fake_handler_append_succeeds_with_basic_credentials` fails, print `handler.Requests` to see which RPC paths the client used (for example, a `BatchAppend` path means feature discovery was not answered as Unimplemented) and fix the fake before continuing. Tasks 8 and 11 depend on it.

- [ ] **Step 8: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/Auth test/Kurrent.Replicator.Tests/Auth
git commit -m "feat(auth): options validation, fallback header hook, source factory

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: GrpcEventWriter through `auth.Run`

**Files:**
- Modify: `src/Kurrent.Replicator.KurrentDb/GrpcEventWriter.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/GrpcEventWriterAuthTests.cs`

**Interfaces:**
- Consumes: `GrpcAuthContext.Run`, `CallAuth`, `FakeKurrentDbHandler`, `ControllableTokenSource`, `TokenFileSource`, `TestEvents`, `TimeDriver`.
- Produces: `public GrpcEventWriter(EventStoreClient client, GrpcAuthContext auth)`; the existing `GrpcEventWriter(EventStoreClient client)` stays (delegates to `GrpcAuthContext.None`), so the container tests compile unchanged.

- [ ] **Step 1: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/GrpcEventWriterAuthTests.cs`:

```csharp
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcEventWriterAuthTests {
    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly FakeKurrentDbHandler    _handler  = new();
    readonly string                  _file     = Path.Combine(Path.GetTempPath(), $"replicator-writer-{Guid.NewGuid():N}");

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_file)) File.Delete(_file);
    }

    (GrpcEventWriter Writer, FallbackUsage Usage) Writer(IAccessTokenSource source) {
        FallbackUsage usage  = null!;
        var           client = FakeKurrentDbHandler.Client(_handler, configure: s => usage = GrpcAuthentication.Apply(s, source));

        return (new GrpcEventWriter(client, new GrpcAuthContext(source, _shutdown.Token, _time, "sink")), usage);
    }

    [Test]
    public async Task Every_write_kind_sends_its_own_bearer_token() {
        var source           = new ControllableTokenSource(_time) { Value = "A" };
        var (writer, usage)  = Writer(source);
        _handler.Respond     = s => Task.FromResult(s.Path == FakeKurrentDbHandler.AppendPath ? FakeKurrentDbHandler.AppendSuccess() : FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable));

        await writer.WriteEvent(TestEvents.Proposed("s"), default);
        await writer.WriteEvent(TestEvents.Meta("s"), default); // metadata is an Append to $$s
        await Assert.That(async () => await writer.WriteEvent(TestEvents.Delete("s"), default)).Throws<Exception>();

        await Assert.That(_handler.Requests.Select(r => r.Authorization).Distinct().Single()).IsEqualTo("Bearer A");
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.DeletePath)).IsNotEmpty();
        await Assert.That(usage.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Write_waits_out_a_token_outage_then_sends_the_new_token() {
        var source          = new ControllableTokenSource(_time);
        var (writer, _)     = Writer(source);
        _handler.Respond    = FakeKurrentDbHandler.AcceptBearer("A");

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => source.Calls >= 3, _time);
        await Assert.That(write.IsCompleted).IsFalse();
        await Assert.That(_handler.Requests).IsEmpty();

        source.Value = "A";
        await TimeDriver.Drive(write, _time);
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath).Single()).IsEqualTo("Bearer A");
    }

    [Test]
    public async Task Shutdown_during_outage_ends_the_write_with_cancellation() {
        var source      = new ControllableTokenSource(_time);
        var (writer, _) = Writer(source);
        var write       = writer.WriteEvent(TestEvents.Proposed("s"), CancellationToken.None);
        await TimeDriver.Until(() => source.Calls >= 2, _time);
        await _shutdown.CancelAsync();
        await Assert.That(async () => await write.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Rejected_file_token_is_replaced_by_rotated_file_without_waiting_for_reload() {
        await File.WriteAllTextAsync(_file, "A");
        var source       = new TokenFileSource(_file, TimeSpan.FromHours(1), _time, "sink");
        var (writer, _)  = Writer(source);
        _handler.Respond = FakeKurrentDbHandler.AcceptBearer("B");

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        await File.WriteAllTextAsync(_file, "B");
        await TimeDriver.Drive(write, _time);

        var headers = _handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath);
        await Assert.That(headers.First()).IsEqualTo("Bearer A");
        await Assert.That(headers.Last()).IsEqualTo("Bearer B");
        await Assert.That(headers.Count(h => h == "Bearer A")).IsEqualTo(1);
    }

    [Test]
    public async Task Removed_file_after_rejection_never_resends_the_rejected_token() {
        await File.WriteAllTextAsync(_file, "A");
        var source       = new TokenFileSource(_file, TimeSpan.FromSeconds(30), _time, "sink");
        var (writer, _)  = Writer(source);
        _handler.Respond = FakeKurrentDbHandler.AcceptBearer("B");

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        File.Delete(_file);
        _time.Advance(TimeSpan.FromSeconds(120)); // past quarantine: file is missing, so nothing to probe
        await Task.Delay(50);
        await File.WriteAllTextAsync(_file, "B");
        await TimeDriver.Drive(write, _time);

        var headers = _handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath);
        await Assert.That(headers.Count(h => h == "Bearer A")).IsEqualTo(1);
        await Assert.That(headers.Last()).IsEqualTo("Bearer B");
    }

    [Test]
    public async Task Server_side_fix_is_detected_by_the_probe_after_quarantine() {
        await File.WriteAllTextAsync(_file, "A");
        var source      = new TokenFileSource(_file, TimeSpan.FromSeconds(30), _time, "sink");
        var (writer, _) = Writer(source);
        var acceptA     = false;
        _handler.Respond = s => Task.FromResult(Volatile.Read(ref acceptA) ? FakeKurrentDbHandler.AppendSuccess() : FakeKurrentDbHandler.TrailersOnly(StatusCode.Unauthenticated));

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        Volatile.Write(ref acceptA, true);
        await TimeDriver.Drive(write, _time);

        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath)).IsEquivalentTo(new[] { "Bearer A", "Bearer A" });
    }

    [Test]
    public async Task Only_one_concurrent_write_probes_a_quarantined_token() {
        await File.WriteAllTextAsync(_file, "A");
        var source      = new TokenFileSource(_file, TimeSpan.FromSeconds(30), _time, "sink");
        var (writer, _) = Writer(source);

        var acceptA   = false;
        var probeHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterFlip = 0;

        _handler.Respond = async s => {
            if (!Volatile.Read(ref acceptA)) return FakeKurrentDbHandler.TrailersOnly(StatusCode.Unauthenticated);

            if (Interlocked.Increment(ref afterFlip) == 1) await probeHold.Task;

            return FakeKurrentDbHandler.AppendSuccess();
        };

        var first = writer.WriteEvent(TestEvents.Proposed("s"), default); // gets rejected -> quarantine
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        Volatile.Write(ref acceptA, true);

        var others = Enumerable.Range(0, 5).Select(i => writer.WriteEvent(TestEvents.Proposed("s", i + 1), default)).ToList();

        await TimeDriver.Until(() => Volatile.Read(ref afterFlip) >= 1, _time);
        for (var i = 0; i < 40; i++) { _time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); } // others keep backing off
        await Assert.That(Volatile.Read(ref afterFlip)).IsEqualTo(1);

        probeHold.SetResult();
        await TimeDriver.Drive(Task.WhenAll(others.Append(first)), _time);
        await Assert.That(Volatile.Read(ref afterFlip)).IsEqualTo(6);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL, no `GrpcEventWriter(EventStoreClient, GrpcAuthContext)` constructor.

- [ ] **Step 3: Implement**

In `src/Kurrent.Replicator.KurrentDb/GrpcEventWriter.cs`, change the class header and the three operations (everything else unchanged):

```csharp
public class GrpcEventWriter(EventStoreClient client, GrpcAuthContext auth) : IEventWriter {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public GrpcEventWriter(EventStoreClient client) : this(client, GrpcAuthContext.None) { }
```

```csharp
            var result = await auth.Run(
                    (a, c) => client.AppendToStreamAsync(
                        proposedEvent.EventDetails.Stream,
                        StreamState.Any,
                        [Map(p)],
                        userCredentials: a.Credentials,
                        cancellationToken: c
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
```

```csharp
            var result = await auth.Run(
                    (a, c) => client.DeleteAsync(stream, StreamState.Any, userCredentials: a.Credentials, cancellationToken: c),
                    cancellationToken
                )
                .ConfigureAwait(false);
```

```csharp
            var result = await auth.Run(
                    (a, c) => client.SetStreamMetadataAsync(
                        meta.EventDetails.Stream,
                        StreamState.Any,
                        new(
                            meta.Data.MaxCount,
                            meta.Data.MaxAge,
                            ValueOrNull(meta.Data.TruncateBefore, x => new StreamPosition((ulong)x!)),
                            meta.Data.CacheControl,
                            ValueOrNull(
                                meta.Data.StreamAcl,
                                x => new StreamAcl(x.ReadRoles, x.WriteRoles, x.DeleteRoles, x.MetaReadRoles, x.MetaWriteRoles)
                            )
                        ),
                        userCredentials: a.Credentials,
                        cancellationToken: c
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/GrpcEventWriterAuthTests/*"`
Expected: 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb/GrpcEventWriter.cs test/Kurrent.Replicator.Tests/Auth/GrpcEventWriterAuthTests.cs
git commit -m "feat(auth): sink writes carry per-call tokens and recover from token failures

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: StreamMetaCache live state and fail-closed scavenge filter

**Files:**
- Modify: `src/Kurrent.Replicator.KurrentDb/StreamMetaCache.cs`
- Modify: `src/Kurrent.Replicator.KurrentDb/EventFilters.cs`
- Modify: `src/Kurrent.Replicator.KurrentDb/ConnectionExtensions.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/StreamMetaCacheTests.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/ScavengedEventsFilterAuthTests.cs`

**Interfaces:**
- Consumes: `AuthFailure`, `GrpcAuthContext.Run`, `CallAuth`.
- Produces:
  - `class StreamMetaCache(bool failClosedOnAuthErrors = false)` — new `bool IsLive`, `void MarkNotLive()`, `void MarkLive()`; existing `GetOrAddStreamMeta`, `GetOrAddStreamSize`, `UpdateStreamMeta`, `UpdateStreamLastEventNumber` keep their signatures. Starts **not live**.
  - `class ScavengedEventsFilter` — ctor `(EventStoreClient client, StreamMetaCache cache, GrpcAuthContext auth)` and internal ctor `(Func<string, CallAuth, CancellationToken, Task<StreamMeta>> readMeta, Func<string, CallAuth, CancellationToken, Task<StreamSize>> readSize, StreamMetaCache cache, GrpcAuthContext auth)`; `ValueTask<bool> Filter(BaseOriginalEvent)` unchanged.
  - `ConnectionExtensions.GetStreamSize(this EventStoreClient, string stream, UserCredentials? credentials = null, CancellationToken ct = default)` and `GetStreamMeta(...)` with the same extra parameters.

- [ ] **Step 1: Write the failing cache tests**

`test/Kurrent.Replicator.Tests/Auth/StreamMetaCacheTests.cs`:

```csharp
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Auth;

public class StreamMetaCacheTests {
    static readonly StreamMeta Meta = new(false, null, null, 0);

    static Exception Denied()        => new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x")));
    static Exception Unauthenticated() => new NotAuthenticatedException("x", new RpcException(new Status(StatusCode.Unauthenticated, "x")));

    [Test]
    public async Task Not_live_never_caches() {
        var cache = new StreamMetaCache();
        var calls = 0;
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await Assert.That(cache.IsLive).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Live_caches_and_MarkLive_clears() {
        var cache = new StreamMetaCache();
        cache.MarkLive();
        var calls = 0;
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await Assert.That(calls).IsEqualTo(1);

        await cache.GetOrAddStreamSize("s", _ => { calls++; return Task.FromResult(new StreamSize(5)); });
        cache.MarkNotLive();
        cache.MarkLive();
        await cache.GetOrAddStreamMeta("s", _ => { calls++; return Task.FromResult(Meta); });
        await cache.GetOrAddStreamSize("s", _ => { calls++; return Task.FromResult(new StreamSize(5)); });
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task Fetch_started_before_MarkLive_is_not_stored() {
        var cache = new StreamMetaCache();
        var slow  = new TaskCompletionSource<StreamMeta>();
        var fetch = cache.GetOrAddStreamMeta("s", _ => slow.Task);
        cache.MarkLive();
        slow.SetResult(Meta with { IsDeleted = true });
        await fetch;

        var fresh = await cache.GetOrAddStreamMeta("s", _ => Task.FromResult(Meta));
        await Assert.That(fresh!.IsDeleted).IsFalse();
    }

    [Test]
    public async Task Without_flag_every_failure_returns_null_as_today() {
        var cache = new StreamMetaCache();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw Denied())).IsNull();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw Unauthenticated())).IsNull();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw new InvalidOperationException())).IsNull();
    }

    [Test]
    public async Task With_flag_auth_failures_propagate_and_others_return_null() {
        var cache = new StreamMetaCache(failClosedOnAuthErrors: true);
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw Denied())).Throws<AccessDeniedException>();
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw Unauthenticated())).Throws<NotAuthenticatedException>();
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw new OAuthTokenException("x"))).Throws<OAuthTokenException>();
        await Assert.That(async () => await cache.GetOrAddStreamMeta("s", _ => throw new OperationCanceledException())).Throws<OperationCanceledException>();
        await Assert.That(await cache.GetOrAddStreamMeta("s", _ => throw new InvalidOperationException())).IsNull();
    }
}
```

- [ ] **Step 2: Write the failing filter tests**

`test/Kurrent.Replicator.Tests/Auth/ScavengedEventsFilterAuthTests.cs`:

```csharp
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class ScavengedEventsFilterAuthTests {
    readonly FakeTimeProvider        _time = new();
    readonly ControllableTokenSource _source;
    readonly GrpcAuthContext         _auth;

    public ScavengedEventsFilterAuthTests() {
        _source = new(_time) { Value = "A" };
        _auth   = new(_source, CancellationToken.None, _time, "reader");
    }

    static readonly StreamMeta Deleted = new(true, null, null, 0);
    static readonly StreamMeta Plain   = new(false, null, null, 0);

    static Task<StreamSize> NoSize(string s, CallAuth a, CancellationToken c) => Task.FromResult(new StreamSize(0));

    [Test]
    public async Task Oauth_permission_denied_fails_closed() {
        var filter = new ScavengedEventsFilter(
            (_, _, _) => throw new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x"))),
            NoSize, new StreamMetaCache(true), _auth
        );
        await Assert.That(async () => await filter.Filter(TestEvents.Original("s", 0))).Throws<AccessDeniedException>();
    }

    [Test]
    public async Task Basic_auth_permission_denied_keeps_today_behaviour() {
        var filter = new ScavengedEventsFilter(
            (_, _, _) => throw new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x"))),
            NoSize, new StreamMetaCache(), GrpcAuthContext.None
        );
        await Assert.That(await filter.Filter(TestEvents.Original("s", 0))).IsTrue();
    }

    [Test]
    public async Task Transient_unauthenticated_is_retried_and_the_filter_then_decides_correctly() {
        var calls = 0;
        var filter = new ScavengedEventsFilter(
            (_, _, _) => ++calls == 1
                ? throw new NotAuthenticatedException("x", new RpcException(new Status(StatusCode.Unauthenticated, "x")))
                : Task.FromResult(Deleted),
            NoSize, new StreamMetaCache(true), _auth
        );
        _source.OnInvalidate = _ => _source.Value = "B";

        var keep = await TimeDriver.Drive(filter.Filter(TestEvents.Original("s", 0)).AsTask(), _time);
        await Assert.That(keep).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Token_outage_after_read_began_makes_the_filter_wait() {
        var filter = new ScavengedEventsFilter((_, _, _) => Task.FromResult(Deleted), NoSize, new StreamMetaCache(true), _auth);
        await Assert.That(await filter.Filter(TestEvents.Original("s", 0))).IsFalse();

        _source.Value = null; // outage
        var pending = filter.Filter(TestEvents.Original("s", 1)).AsTask();
        await TimeDriver.Until(() => _source.Calls >= 4, _time);
        await Assert.That(pending.IsCompleted).IsFalse();
        _source.Value = "B";
        await Assert.That(await TimeDriver.Drive(pending, _time)).IsFalse();
    }

    [Test]
    public async Task Metadata_and_size_reads_carry_per_call_bearer() {
        var           handler = new FakeKurrentDbHandler();
        FallbackUsage usage   = null!;
        var           client  = FakeKurrentDbHandler.Client(handler, configure: s => usage = GrpcAuthentication.Apply(s, _source));
        var           filter  = new ScavengedEventsFilter(client, new StreamMetaCache(true), _auth);

        await filter.Filter(TestEvents.Original("s", 0)); // Unavailable is not an auth error -> null meta -> keep
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).IsEqualTo("Bearer A");
        await Assert.That(_source.Accepted).IsEmpty();
        await Assert.That(usage.Count).IsEqualTo(0); // per-call credentials, not the sentinel fallback
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL (`MarkLive`, new `StreamMetaCache` ctor, new `ScavengedEventsFilter` ctors missing).

- [ ] **Step 4: Implement StreamMetaCache**

Replace the top of `src/Kurrent.Replicator.KurrentDb/StreamMetaCache.cs` (class declaration through `GetOrAddStreamSize`), keeping `UpdateStreamMeta`, `UpdateStreamLastEventNumber`, `IsStreamDeleted` and the `StreamSize` / `StreamMeta` records unchanged:

```csharp
using System.Collections.Concurrent;
using Kurrent.Replicator.Shared.Logging;

namespace Kurrent.Replicator.KurrentDb;

/// <summary>
/// Metadata and last-event-number cache used by the scavenge filter. Only trusted while "live", i.e. while the
/// realtime subscription is delivering updates; otherwise every read goes to the server.
/// </summary>
class StreamMetaCache(bool failClosedOnAuthErrors = false) {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly ConcurrentDictionary<string, StreamSize> _streamsSize = new();
    readonly ConcurrentDictionary<string, StreamMeta> _streamsMeta = new();
    readonly object                                   _lock        = new();

    bool _live;
    long _epoch;

    public bool IsLive {
        get { lock (_lock) return _live; }
    }

    public void MarkNotLive() {
        lock (_lock) _live = false;
    }

    public void MarkLive() {
        lock (_lock) {
            _streamsMeta.Clear();
            _streamsSize.Clear();
            _epoch++;
            _live = true;
        }
    }

    public async Task<StreamMeta?> GetOrAddStreamMeta(string stream, Func<string, Task<StreamMeta>> getMeta) {
        try {
            return await GetOrFetch(_streamsMeta, stream, getMeta).ConfigureAwait(false);
        } catch (Exception e) when (!FailClosed(e)) {
            Log.Warn(e, "Unable to read metadata for stream {Stream}", stream);

            return null;
        }
    }

    public Task<StreamSize> GetOrAddStreamSize(string stream, Func<string, Task<StreamSize>> getSize)
        => GetOrFetch(_streamsSize, stream, getSize);

    bool FailClosed(Exception e)
        => failClosedOnAuthErrors && (e is OperationCanceledException || AuthFailure.IsTokenFailure(e) || AuthFailure.IsPermissionDenied(e));

    async Task<T> GetOrFetch<T>(ConcurrentDictionary<string, T> dict, string key, Func<string, Task<T>> fetch) {
        long epoch;

        lock (_lock) {
            epoch = _epoch;

            if (_live && dict.TryGetValue(key, out var cached)) return cached;
        }

        var value = await fetch(key).ConfigureAwait(false);

        lock (_lock) {
            if (_live && _epoch == epoch) dict.TryAdd(key, value);
        }

        return value;
    }
```

Remove the now-unused `using Kurrent.Replicator.Shared.Extensions;`.

- [ ] **Step 5: Implement ConnectionExtensions and ScavengedEventsFilter**

`src/Kurrent.Replicator.KurrentDb/ConnectionExtensions.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb;

static class ConnectionExtensions {
    public static async Task<StreamSize> GetStreamSize(this EventStoreClient client, string stream, UserCredentials? credentials = null, CancellationToken ct = default) {
        var read = client.ReadStreamAsync(Direction.Backwards, stream, StreamPosition.End, 1, userCredentials: credentials, cancellationToken: ct);
        var last = await read.ToArrayAsync(ct).ConfigureAwait(false);

        return new(last[0].OriginalEventNumber.ToInt64());
    }

    public static async Task<StreamMeta> GetStreamMeta(this EventStoreClient client, string stream, UserCredentials? credentials = null, CancellationToken ct = default) {
        var streamMeta = await client.GetStreamMetadataAsync(stream, userCredentials: credentials, cancellationToken: ct).ConfigureAwait(false);

        var streamDeleted = streamMeta.StreamDeleted || streamMeta.Metadata.TruncateBefore == StreamPosition.End;

        return new(
            streamDeleted,
            streamMeta.Metadata.MaxAge,
            streamMeta.Metadata.MaxCount,
            streamMeta.MetastreamRevision!.Value.ToInt64()
        );
    }
}
```

`src/Kurrent.Replicator.KurrentDb/EventFilters.cs`:

```csharp
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.KurrentDb;

class ScavengedEventsFilter(
        Func<string, CallAuth, CancellationToken, Task<StreamMeta>> readMeta,
        Func<string, CallAuth, CancellationToken, Task<StreamSize>> readSize,
        StreamMetaCache                                             cache,
        GrpcAuthContext                                             auth
    ) {
    public ScavengedEventsFilter(EventStoreClient client, StreamMetaCache cache, GrpcAuthContext auth)
        : this(
            (s, a, c) => client.GetStreamMeta(s, a.Credentials, c),
            (s, a, c) => client.GetStreamSize(s, a.Credentials, c),
            cache,
            auth
        ) { }

    public async ValueTask<bool> Filter(BaseOriginalEvent originalEvent) {
        var stream = originalEvent.EventDetails.Stream;

        var meta = await cache.GetOrAddStreamMeta(stream, s => auth.Run((a, c) => readMeta(s, a, c), CancellationToken.None)).ConfigureAwait(false);

        return meta == null || !meta.IsDeleted && !TtlExpired() && !await OverMaxCount().ConfigureAwait(false);

        bool TtlExpired() => meta.MaxAge.HasValue && originalEvent.Created < DateTime.Now - meta.MaxAge;

        // add the check timestamp, so we can check again if we get newer events (edge case)
        async Task<bool> OverMaxCount() {
            if (!meta.MaxCount.HasValue)
                return false;

            var streamSize = await cache.GetOrAddStreamSize(stream, s => auth.Run((a, c) => readSize(s, a, c), CancellationToken.None)).ConfigureAwait(false);

            return originalEvent.LogPosition.EventNumber < streamSize.LastEventNumber - meta.MaxCount;
        }
    }
}
```

`GrpcEventReader` still constructs `new(client, metaCache)`; it is updated in Task 11. To keep the build green now, change that line in `GrpcEventReader`'s constructor to `_filter = new(client, metaCache, GrpcAuthContext.None);`, and add `metaCache.MarkLive();` right after `_realtime = new(client, metaCache);` so the cache behaves as before until Task 10/11 wire the live state properly.

- [ ] **Step 6: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/StreamMetaCacheTests/*"` and the same for `ScavengedEventsFilterAuthTests`.
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb test/Kurrent.Replicator.Tests/Auth
git commit -m "feat(auth): fail-closed scavenge filter and live-state metadata cache

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 10: Realtime — serialized attempts, drop classification, cache coherence

Rewrites `Realtime` per the spec's "Realtime subscription" section. Read that section in full before starting; every bullet maps to code below.

**Files:**
- Modify (rewrite): `src/Kurrent.Replicator.KurrentDb/Realtime.cs`
- Modify: `src/Kurrent.Replicator.KurrentDb/GrpcEventReader.cs` (constructor + `Start` call only)
- Test: `test/Kurrent.Replicator.Tests/Auth/RealtimeTests.cs`

**Interfaces:**
- Consumes: `GrpcAuthContext` (`Run`, `ReportFailure`, `Shutdown`, `Time`), `CallAuth`, `StreamMetaCache.MarkLive/MarkNotLive`, `TokenGate.Backoff`.
- Produces:
  - `class Realtime` — public ctor `(EventStoreClient client, StreamMetaCache cache, GrpcAuthContext auth)`; internal ctor `(Realtime.Subscribe subscribe, StreamMetaCache cache, GrpcAuthContext auth)`; `Task Start(CancellationToken ct)`; `internal Action? OnUnpublishedUnderLock` (test hook); `internal bool IsPublished`.
  - `internal delegate Task<IDisposable> Subscribe(Func<ResolvedEvent, Task> onEvent, Action<SubscriptionDroppedReason, Exception?> onDropped, CallAuth auth, CancellationToken ct)` nested in `Realtime`.
  - `internal sealed class SubscriptionDroppedEarlyException : Exception`.

- [ ] **Step 1: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/RealtimeTests.cs`:

```csharp
using System.Collections.Concurrent;
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class RealtimeTests {
    sealed class FakeSubscription : IDisposable {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    sealed class Call {
        public required Func<ResolvedEvent, Task>                       OnEvent;
        public required Action<SubscriptionDroppedReason, Exception?>   OnDropped;
        public required CallAuth                                        Auth;
        public readonly TaskCompletionSource<IDisposable>               Result       = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly FakeSubscription                                Subscription = new();
        public string? Token => Auth.Lease?.Value;
        public void Succeed() => Result.SetResult(Subscription);
        public void Fail() => Result.SetException(new RpcException(new Status(StatusCode.Unavailable, "down")));
    }

    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly ControllableTokenSource _source;
    readonly ConcurrentQueue<Call>   _calls = new();
    readonly StreamMetaCache         _cache = new(failClosedOnAuthErrors: true);
    readonly Realtime                _realtime;

    public RealtimeTests() {
        _source = new(_time) { Value = "A" };
        var auth = new GrpcAuthContext(_source, _shutdown.Token, _time, "reader");

        _realtime = new Realtime(
            (onEvent, onDropped, a, _) => {
                var call = new Call { OnEvent = onEvent, OnDropped = onDropped, Auth = a };
                _calls.Enqueue(call);

                return call.Result.Task;
            },
            _cache,
            auth
        );
    }

    Call Nth(int n) => _calls.ElementAt(n);

    async Task<Call> WaitForCall(int n) {
        await TimeDriver.Until(() => _calls.Count > n, _time);

        return Nth(n);
    }

    async Task Publish(int n) {
        var start = _realtime.Start(default);
        (await WaitForCall(n)).Succeed();
        await TimeDriver.Drive(start, _time);
    }

    static NotAuthenticatedException Unauthenticated() => new("x", new RpcException(new Status(StatusCode.Unauthenticated, "x")));

    [Test]
    public async Task First_subscribe_marks_cache_live() {
        await Assert.That(_cache.IsLive).IsFalse();
        await Publish(0);
        await Assert.That(_cache.IsLive).IsTrue();
        await Assert.That(_realtime.IsPublished).IsTrue();
    }

    [Test]
    public async Task Failed_initial_subscribe_is_retried() {
        var start = _realtime.Start(default);
        (await WaitForCall(0)).Fail();
        (await WaitForCall(1)).Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Drop_then_failing_resubscribes_keep_retrying_until_success() {
        await Publish(0);
        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, new RpcException(new Status(StatusCode.Unavailable, "x")));
        await Assert.That(_cache.IsLive).IsFalse();
        (await WaitForCall(1)).Fail();
        (await WaitForCall(2)).Fail();
        (await WaitForCall(3)).Succeed();
        await TimeDriver.Until(() => _realtime.IsPublished, _time);
        await Assert.That(_cache.IsLive).IsTrue();
    }

    [Test]
    public async Task Foreground_start_during_pending_resubscribe_shares_the_attempt() {
        await Publish(0);
        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null);
        var pending = await WaitForCall(1);
        var start   = _realtime.Start(default);
        await Task.Delay(20);
        await Assert.That(_calls.Count).IsEqualTo(2);
        pending.Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Shutdown_ends_the_loop() {
        var start = _realtime.Start(default);
        (await WaitForCall(0)).Fail();
        await _shutdown.CancelAsync();
        await Assert.That(async () => await start.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Stale_callback_from_an_old_attempt_is_ignored() {
        await Publish(0);
        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null); // non-token drop
        (await WaitForCall(1)).Succeed();
        await TimeDriver.Until(() => _realtime.IsPublished, _time);

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated()); // delayed duplicate from A
        await Task.Delay(20);

        await Assert.That(_realtime.IsPublished).IsTrue();
        await Assert.That(_cache.IsLive).IsTrue();
        await Assert.That(_source.Invalidated).IsEmpty();
        await Assert.That(_calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Late_drop_of_failed_attempt_does_not_affect_the_pending_one() {
        var start = _realtime.Start(default);
        var a     = await WaitForCall(0);
        a.Fail();
        var b = await WaitForCall(1); // now pending
        a.OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated());
        await Assert.That(_source.Invalidated).IsEmpty();
        b.Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_realtime.IsPublished).IsTrue();
    }

    [Test]
    public async Task Drop_of_pending_attempt_is_handled() {
        var start = _realtime.Start(default);
        var a     = await WaitForCall(0);
        a.OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated()); // early drop
        await Assert.That(_source.Invalidated.Single().Applied).IsTrue();
        _source.Value = "B";
        a.Succeed();                                                           // the subscribe call still returns
        await Assert.That(a.Subscription.Disposed).IsTrue();
        var b = await WaitForCall(1);
        await Assert.That(b.Token).IsEqualTo("B");
        b.Succeed();
        await TimeDriver.Drive(start, _time);
        await Assert.That(_realtime.IsPublished).IsTrue();
        await Assert.That(_source.Accepted.Count(l => l.Value == "A")).IsEqualTo(0);
    }

    [Test]
    public async Task Rejected_token_is_quarantined_before_anyone_can_observe_unpublished_state() {
        await Publish(0);
        var invalidatedWhenUnpublished = false;
        _realtime.OnUnpublishedUnderLock = () => invalidatedWhenUnpublished = _source.Invalidated.Any(i => i.Applied);

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated());
        var start = _realtime.Start(default); // concurrent foreground start

        await Assert.That(invalidatedWhenUnpublished).IsTrue();
        for (var i = 0; i < 30; i++) { _time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(2); } // < quarantine
        await Assert.That(_calls.Count).IsEqualTo(1);                                                // A never handed out again

        _source.Value = "B";
        var b = await WaitForCall(1);
        await Assert.That(b.Token).IsEqualTo("B");
        b.Succeed();
        await TimeDriver.Drive(start, _time);
    }

    [Test]
    public async Task Drop_reports_with_the_subscription_lease_not_the_current_one() {
        await Publish(0);                    // subscribed with A
        _source.Value = "B";
        await _source.GetAccessToken(default); // source moves to B (new generation)

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, Unauthenticated());
        await Assert.That(_source.Invalidated.Single().Applied).IsFalse(); // stale lease, B untouched

        var next = await WaitForCall(1);
        await Assert.That(next.Token).IsEqualTo("B");
    }

    [Test]
    public async Task Cache_is_coherent_across_a_subscription_gap() {
        var serverMeta = new StreamMeta(false, null, null, 0);
        var metaReads  = 0;
        var filter = new ScavengedEventsFilter(
            (_, _, _) => { metaReads++; return Task.FromResult(serverMeta); },
            (_, _, _) => Task.FromResult(new StreamSize(10)),
            _cache,
            new GrpcAuthContext(_source, _shutdown.Token, _time, "reader")
        );

        await Publish(0);
        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsTrue(); // cached: no maxCount
        await filter.Filter(TestEvents.Original("s", 5));
        await Assert.That(metaReads).IsEqualTo(1);

        Nth(0).OnDropped(SubscriptionDroppedReason.ServerError, null);
        serverMeta = serverMeta with { MaxCount = 1 }; // changed while the subscription is down

        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsFalse(); // direct read sees the change
        await Assert.That(metaReads).IsEqualTo(2);

        (await WaitForCall(1)).Succeed();            // resubscribed; no events delivered yet
        await TimeDriver.Until(() => _realtime.IsPublished, _time);
        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsFalse(); // fresh fetch, not pre-gap cache
        await Assert.That(metaReads).IsEqualTo(3);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL (`Realtime` has no matching constructor / `IsPublished` / `OnUnpublishedUnderLock`).

- [ ] **Step 3: Implement Realtime**

Replace `src/Kurrent.Replicator.KurrentDb/Realtime.cs` entirely:

```csharp
using System.Text.Json;
using Kurrent.Replicator.KurrentDb.Internals;

namespace Kurrent.Replicator.KurrentDb;

sealed class SubscriptionDroppedEarlyException() : Exception("The subscription dropped before it was registered");

/// <summary>
/// Keeps StreamMetaCache current from a $all subscription. One serialized subscribe loop; drops are attributed to
/// the attempt (and token lease) that produced them. See the spec, "Realtime subscription".
/// </summary>
class Realtime {
    static ILog Log => LogProvider.GetLogger(typeof(Realtime));

    internal delegate Task<IDisposable> Subscribe(
        Func<ResolvedEvent, Task>                     onEvent,
        Action<SubscriptionDroppedReason, Exception?> onDropped,
        CallAuth                                      auth,
        CancellationToken                             ct
    );

    sealed class Attempt(CallAuth auth) {
        public CallAuth     Auth         { get; } = auth;
        public bool         Dropped      { get; set; }
        public IDisposable? Subscription { get; set; }
    }

    readonly Subscribe       _subscribe;
    readonly StreamMetaCache _cache;
    readonly GrpcAuthContext _auth;
    readonly object          _lock = new();

    Attempt? _published;
    Attempt? _pending;
    Task?    _subscribing;

    internal Action? OnUnpublishedUnderLock { get; set; }

    internal bool IsPublished {
        get { lock (_lock) return _published is { Dropped: false }; }
    }

    public Realtime(EventStoreClient client, StreamMetaCache cache, GrpcAuthContext auth)
        : this(
            async (onEvent, onDropped, a, ct) => await client.SubscribeToAllAsync(
                    FromAll.End,
                    (_, evt, _) => onEvent(evt),
                    subscriptionDropped: (_, reason, ex) => onDropped(reason, ex),
                    userCredentials: a.Credentials,
                    cancellationToken: ct
                )
                .ConfigureAwait(false),
            cache,
            auth
        ) { }

    internal Realtime(Subscribe subscribe, StreamMetaCache cache, GrpcAuthContext auth) {
        _subscribe = subscribe;
        _cache     = cache;
        _auth      = auth;
    }

    public Task Start(CancellationToken ct) => EnsureSubscribed(ct);

    Task EnsureSubscribed(CancellationToken ct) {
        Task loop;

        lock (_lock) {
            if (_published is { Dropped: false }) return Task.CompletedTask;

            if (_subscribing is null || _subscribing.IsCompleted) _subscribing = Task.Run(SubscribeLoop, CancellationToken.None);

            loop = _subscribing;
        }

        return loop.WaitAsync(ct);
    }

    async Task SubscribeLoop() {
        var failures = 0;

        while (true) {
            try {
                await _auth.Run(SubscribeOnce, _auth.Shutdown).ConfigureAwait(false);

                return;
            } catch (OperationCanceledException) when (_auth.Shutdown.IsCancellationRequested) {
                throw;
            } catch (Exception e) {
                Log.Warn(e, "Realtime subscription failed; retrying");
                await Task.Delay(TokenGate.Backoff(failures++), _auth.Time, _auth.Shutdown).ConfigureAwait(false);
            }
        }
    }

    async Task<bool> SubscribeOnce(CallAuth auth, CancellationToken ct) {
        var attempt = new Attempt(auth);

        lock (_lock) _pending = attempt;

        IDisposable subscription;

        try {
            subscription = await _subscribe(HandleEvent, (reason, ex) => HandleDrop(attempt, reason, ex), auth, ct).ConfigureAwait(false);
        } catch {
            lock (_lock) {
                if (ReferenceEquals(_pending, attempt)) _pending = null;
            }

            throw;
        }

        lock (_lock) {
            if (ReferenceEquals(_pending, attempt)) _pending = null;

            if (attempt.Dropped) {
                subscription.Dispose();

                throw new SubscriptionDroppedEarlyException();
            }

            attempt.Subscription = subscription;
            _published           = attempt;
            _cache.MarkLive();
        }

        Log.Info("Realtime subscription started");

        return true;
    }

    void HandleDrop(Attempt attempt, SubscriptionDroppedReason reason, Exception? exception) {
        if (reason == SubscriptionDroppedReason.Disposed) return;

        bool resubscribe;

        lock (_lock) {
            if (ReferenceEquals(attempt, _published)) {
                _auth.ReportFailure(attempt.Auth, exception);
                attempt.Dropped = true;
                _published      = null;
                _cache.MarkNotLive();
                OnUnpublishedUnderLock?.Invoke();
                resubscribe = true;
            }
            else if (ReferenceEquals(attempt, _pending)) {
                _auth.ReportFailure(attempt.Auth, exception);
                attempt.Dropped = true;
                resubscribe     = false;
            }
            else {
                return; // superseded attempt: ignore entirely
            }
        }

        Log.Warn(exception, "Realtime subscription dropped: {Reason}", reason);

        if (!resubscribe) return;

        _ = EnsureSubscribed(CancellationToken.None)
            .ContinueWith(
                t => {
                    if (t.Exception?.GetBaseException() is not OperationCanceledException)
                        Log.Error(t.Exception, "Realtime resubscription stopped");
                },
                TaskContinuationOptions.OnlyOnFaulted
            );
    }

    Task HandleEvent(ResolvedEvent re) {
        if (IsSystemEvent())
            return Task.CompletedTask;

        if (IsMetadataUpdate()) {
            var stream = re.OriginalStreamId[2..];
            var meta   = JsonSerializer.Deserialize<StreamMetadata>(re.Event.Data.Span, MetaSerialization.StreamMetadataJsonSerializerOptions);
            _cache.UpdateStreamMeta(stream, meta, re.OriginalEventNumber.ToInt64());
        }
        else {
            _cache.UpdateStreamLastEventNumber(re.OriginalStreamId, re.OriginalEventNumber.ToInt64());
        }

        return Task.CompletedTask;

        bool IsSystemEvent() => re.Event.EventType.StartsWith('$') && re.Event.EventType != Predefined.MetadataEventType;

        bool IsMetadataUpdate() => re.Event.EventType == Predefined.MetadataEventType;
    }
}
```

Keep the body of `HandleEvent` identical to today's (copy it from the current file if it differs from the above in any detail).

- [ ] **Step 4: Adjust GrpcEventReader to the new constructor**

In `src/Kurrent.Replicator.KurrentDb/GrpcEventReader.cs`:
- constructor: replace `_realtime = new(client, metaCache);` with `_realtime = new(client, metaCache, GrpcAuthContext.None);` and delete the temporary `metaCache.MarkLive();` line added in Task 9 (Realtime now marks the cache live on publish).
- `ReadEvents`: replace `await _realtime.Start();` with `await _realtime.Start(cancellationToken).ConfigureAwait(false);`.

- [ ] **Step 5: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/RealtimeTests/*"`
Expected: 11 tests PASS. Also rerun `"/*/Kurrent.Replicator.Tests.Auth/*/*"` to confirm nothing regressed.

- [ ] **Step 6: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb test/Kurrent.Replicator.Tests/Auth/RealtimeTests.cs
git commit -m "fix(realtime): serialized resubscription with drop attribution and cache coherence

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: GrpcEventReader per-call credentials and per-side GrpcConfigurator

**Files:**
- Modify: `src/Kurrent.Replicator.KurrentDb/GrpcEventReader.cs`
- Modify: `src/Kurrent.Replicator.KurrentDb/Configurator.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/GrpcEventReaderAuthTests.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/GrpcConfiguratorTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–10.
- Produces:
  - `GrpcEventReader` ctors: `(EventStoreClient client)` (unchanged behaviour), `(EventStoreClient client, GrpcAuthContext auth)`, `internal (EventStoreClient client, GrpcAuthContext auth, Func<StreamMetaCache, Realtime> realtimeFactory)`.
  - `GrpcConfigurator` ctors: `()`, `(GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth, CancellationToken shutdown)`, `internal (GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth, CancellationToken shutdown, Action<EventStoreClientSettings>? configureSettings, TimeProvider time)`.

- [ ] **Step 1: Write the failing reader tests**

`test/Kurrent.Replicator.Tests/Auth/GrpcEventReaderAuthTests.cs`:

```csharp
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcEventReaderAuthTests {
    readonly FakeTimeProvider     _time    = new();
    readonly FakeKurrentDbHandler _handler = new();
    readonly string               _file    = Path.Combine(Path.GetTempPath(), $"replicator-reader-{Guid.NewGuid():N}");

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_file)) File.Delete(_file);
    }

    FallbackUsage _usage = null!;

    GrpcEventReader Reader(IAccessTokenSource source) {
        var client = FakeKurrentDbHandler.Client(_handler, configure: s => _usage = GrpcAuthentication.Apply(s, source));
        var auth   = new GrpcAuthContext(source, CancellationToken.None, _time, "reader");

        // realtime subscription that succeeds immediately, so ReadEvents gets to the $all read
        return new GrpcEventReader(client, auth, cache => new Realtime((_, _, _, _) => Task.FromResult<IDisposable>(new MemoryStream()), cache, auth));
    }

    [Test]
    public async Task Read_all_carries_the_token_and_a_rejection_invalidates_it() {
        await File.WriteAllTextAsync(_file, "A");
        var source       = new TokenFileSource(_file, TimeSpan.FromHours(1), _time, "reader");
        var reader       = Reader(source);
        _handler.Respond = _ => Task.FromResult(FakeKurrentDbHandler.TrailersOnly(StatusCode.Unauthenticated));

        await Assert.That(async () => await reader.ReadEvents(LogPosition.Start, _ => ValueTask.CompletedTask, default)).Throws<Exception>();
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).IsEqualTo("Bearer A");

        await File.WriteAllTextAsync(_file, "B");
        _handler.Respond = _ => Task.FromResult(FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable));
        await Assert.That(async () => await reader.ReadEvents(LogPosition.Start, _ => ValueTask.CompletedTask, default)).Throws<Exception>();
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Last()).IsEqualTo("Bearer B"); // re-read immediately after rejection
        await Assert.That(_usage.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetLastPosition_retries_a_rejected_token_with_the_new_one() {
        await File.WriteAllTextAsync(_file, "A");
        var source = new TokenFileSource(_file, TimeSpan.FromHours(1), _time, "reader");
        var reader = Reader(source);
        _handler.Respond = s => {
            if (s.Authorization == "Bearer A") File.WriteAllText(_file, "B"); // rotate while rejecting A

            return Task.FromResult(FakeKurrentDbHandler.TrailersOnly(s.Authorization == "Bearer A" ? StatusCode.Unauthenticated : StatusCode.Unavailable));
        };

        var call = reader.GetLastPosition(default);
        await Assert.That(async () => await TimeDriver.Drive(call, _time)).Throws<Exception>(); // Unavailable with B is not a token error
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath)).IsEquivalentTo(new[] { "Bearer A", "Bearer B" });
        await Assert.That(_usage.Count).IsEqualTo(0);
    }
}
```

Note: `MemoryStream` is just a convenient `IDisposable` stand-in for the subscription.

- [ ] **Step 2: Write the failing configurator tests**

`test/Kurrent.Replicator.Tests/Auth/GrpcConfiguratorTests.cs`:

```csharp
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcConfiguratorTests {
    readonly string _file = Path.Combine(Path.GetTempPath(), $"replicator-cfg-{Guid.NewGuid():N}");

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_file)) File.Delete(_file);
    }

    [Test]
    public async Task Reader_basic_and_sink_oauth_send_independent_headers() {
        await File.WriteAllTextAsync(_file, "SINK-TOKEN");
        var handler  = new FakeKurrentDbHandler { Respond = s => Task.FromResult(s.Path == FakeKurrentDbHandler.AppendPath ? FakeKurrentDbHandler.AppendSuccess() : FakeKurrentDbHandler.TrailersOnly(Grpc.Core.StatusCode.Unavailable)) };
        var sinkAuth = new GrpcAuthOptions { Type = GrpcAuthType.OAuthTokenFile, TokenFile = _file };

        var configurator = new GrpcConfigurator(GrpcAuthOptions.Default, sinkAuth, CancellationToken.None, s => s.CreateHttpMessageHandler = () => handler, new FakeTimeProvider());

        var writer = configurator.ConfigureWriter("esdb://target.example.com:2113?tls=true");
        var reader = configurator.ConfigureReader("esdb://admin:changeit@target.example.com:2113?tls=true");

        await writer.WriteEvent(TestEvents.Proposed("s"), default);
        await Assert.That(async () => await reader.GetLastPosition(default)).Throws<Exception>();

        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath).Single()).IsEqualTo("Bearer SINK-TOKEN");
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).StartsWith("Basic ");
    }

    [Test]
    public async Task Invalid_side_configuration_names_the_side() {
        var oauth        = new GrpcAuthOptions { Type = GrpcAuthType.OAuthTokenFile, TokenFile = _file };
        var configurator = new GrpcConfigurator(oauth, GrpcAuthOptions.Default, CancellationToken.None);

        var ex = await Assert.That(() => configurator.ConfigureReader("esdb://admin:changeit@localhost:2113?tls=true")).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).StartsWith("Invalid reader auth configuration");

        await Assert.That(() => configurator.ConfigureWriter("esdb://admin:changeit@localhost:2113?tls=false")).ThrowsNothing(); // sink is basic
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL (missing constructors).

- [ ] **Step 4: Implement GrpcEventReader changes**

In `src/Kurrent.Replicator.KurrentDb/GrpcEventReader.cs`:

Fields and constructors:

```csharp
    readonly EventStoreClient      _client;
    readonly GrpcAuthContext       _auth;
    readonly ScavengedEventsFilter _filter;
    readonly Realtime              _realtime;

    public GrpcEventReader(EventStoreClient client) : this(client, GrpcAuthContext.None) { }

    public GrpcEventReader(EventStoreClient client, GrpcAuthContext auth) : this(client, auth, cache => new Realtime(client, cache, auth)) { }

    internal GrpcEventReader(EventStoreClient client, GrpcAuthContext auth, Func<StreamMetaCache, Realtime> realtimeFactory) {
        _log      = LogProvider.GetCurrentClassLogger();
        _debugLog = _log.IsDebugEnabled() ? _log : null;

        var metaCache = new StreamMetaCache(failClosedOnAuthErrors: auth.Enabled);
        _client   = client;
        _auth     = auth;
        _filter   = new(client, metaCache, auth);
        _realtime = realtimeFactory(metaCache);
    }
```

`ReadEvents` — acquire credentials before the long-running read, report on first response, report failures:

```csharp
    public async Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken) {
        var sequence     = 0;
        var lastPosition = 0L;

        _log.Info("Starting gRPC reader");

        await _realtime.Start(cancellationToken).ConfigureAwait(false);

        var (_, eventPosition) = fromLogPosition;

        var callAuth = await _auth.AcquireCredentials(cancellationToken).ConfigureAwait(false);
        var accepted = false;

        try {
            var read = _client.ReadAllAsync(
                Direction.Forwards,
                new(eventPosition, eventPosition),
                userCredentials: callAuth.Credentials,
                cancellationToken: cancellationToken
            );

            var enumerator = read.GetAsyncEnumerator(cancellationToken);

            do {
                using var activity = new Activity("read");
                activity.Start();

                var hasValue = await Metrics.MeasureValueTask(
                        () => enumerator.MoveNextAsync(cancellationToken),
                        ReplicationMetrics.ReadsHistogram,
                        ReplicationMetrics.ReadErrorsCount
                    )
                    .ConfigureAwait(false);

                if (!accepted) {
                    _auth.ReportAccepted(callAuth);
                    accepted = true;
                }

                if (!hasValue) break;

                // Keep the existing loop body from `var evt = enumerator.Current;` through
                // `await next(originalEvent).ConfigureAwait(false);` exactly as it is today.
            } while (true);
        } catch (Exception e) {
            _auth.ReportFailure(callAuth, e);

            throw;
        }

        _log.Info("Reached the end of the stream at {Position}", lastPosition);
    }
```

`GetLastPosition` through `Run`:

```csharp
    public Task<long?> GetLastPosition(CancellationToken cancellationToken)
        => _auth.Run(
            async (a, c) => {
                var events = await _client
                    .ReadAllAsync(Direction.Backwards, Position.End, 1, userCredentials: a.Credentials, cancellationToken: c)
                    .ToArrayAsync(c)
                    .ConfigureAwait(false);

                return (long?)events[0].OriginalPosition?.CommitPosition;
            },
            cancellationToken
        );
```

- [ ] **Step 5: Implement GrpcConfigurator**

Replace `src/Kurrent.Replicator.KurrentDb/Configurator.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb;

public class GrpcConfigurator : IConfigurator {
    static ILog Log => LogProvider.GetLogger(typeof(GrpcConfigurator));

    readonly GrpcAuthOptions                  _readerAuth;
    readonly GrpcAuthOptions                  _sinkAuth;
    readonly CancellationToken                _shutdown;
    readonly Action<EventStoreClientSettings>? _configureSettings;
    readonly TimeProvider                     _time;

    public GrpcConfigurator() : this(GrpcAuthOptions.Default, GrpcAuthOptions.Default, CancellationToken.None) { }

    public GrpcConfigurator(GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth, CancellationToken shutdown)
        : this(readerAuth, sinkAuth, shutdown, null, TimeProvider.System) { }

    internal GrpcConfigurator(
            GrpcAuthOptions                   readerAuth,
            GrpcAuthOptions                   sinkAuth,
            CancellationToken                 shutdown,
            Action<EventStoreClientSettings>? configureSettings,
            TimeProvider                      time
        ) {
        _readerAuth        = readerAuth;
        _sinkAuth          = sinkAuth;
        _shutdown          = shutdown;
        _configureSettings = configureSettings;
        _time              = time;
    }

    public string Protocol => "grpc";

    public IEventReader ConfigureReader(string connectionString) {
        var (client, auth) = Configure(connectionString, _readerAuth, "reader", follower: true);

        return new GrpcEventReader(client, auth);
    }

    public IEventWriter ConfigureWriter(string connectionString) {
        var (client, auth) = Configure(connectionString, _sinkAuth, "sink", follower: false);

        return new GrpcEventWriter(client, auth);
    }

    (EventStoreClient Client, GrpcAuthContext Auth) Configure(string connectionString, GrpcAuthOptions options, string side, bool follower) {
        var settings = EventStoreClientSettings.Create(connectionString);

        if (follower) settings.ConnectivitySettings.NodePreference = NodePreference.Follower;

        _configureSettings?.Invoke(settings);

        GrpcAuthOptionsValidator.EnsureValid(side, options, settings);

        foreach (var warning in GrpcAuthOptionsValidator.Warnings(options)) Log.Warn("{Side}: {Warning}", side, warning);

        var source = GrpcAuthentication.CreateSource(options, side, _time, _shutdown);

        if (source != null) {
            GrpcAuthentication.Apply(settings, source);
            Log.Info("{Side}: using {AuthType} authentication", side, options.Type);
        }

        return (new EventStoreClient(settings), new GrpcAuthContext(source, _shutdown, _time, side));
    }
}
```

`connectionString` sides also get a context with the real shutdown token (`source == null`), so the `Realtime` retry loop stops at shutdown for basic-auth readers too.

- [ ] **Step 6: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/Kurrent.Replicator.Tests.Auth/*/*"`
Expected: all Auth tests PASS. Also build the whole solution: `dotnet build Kurrent.Replicator.slnx -p:UseAppHost=false -v q` → 0 errors (Startup still calls `new GrpcConfigurator()` via DI; that is changed in Task 13).

- [ ] **Step 7: Commit**

```bash
git add src/Kurrent.Replicator.KurrentDb test/Kurrent.Replicator.Tests/Auth
git commit -m "feat(auth): per-side gRPC configurator and reader per-call credentials

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Replicator pipeline — cancellable prepare→sink handoff, drain exit, resilient metrics reporter

**Files:**
- Modify: `src/Kurrent.Replicator/Replicator.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/ReplicatorShutdownTests.cs`

**Interfaces:**
- Consumes: `Replicator.Replicate(...)` (signature unchanged), `GrpcAuthContext`, `ControllableTokenSource`, `TestEvents`.
- Produces: behaviour only — no API change.

Changes (spec "Sink (writer)" and "Metrics reporter"):
1. Create `writerCts` before the pipes; the prepare pipe's send delegate becomes `ctx => sinkChannel.Writer.WriteAsync(ctx, writerCts.Token)`.
2. Drain loop: `while (sinkChannel.Reader.Count > 0 && !writerTask.IsCompleted)`; afterwards, if the writer has stopped with items left, log a warning with the count.
3. On stopping: cancel `writerCts` **before** `sinkChannel.Writer.Complete()`, so a prepare write blocked on the full channel ends with `OperationCanceledException` (which `PreparePipe` swallows) rather than `ChannelClosedException`.
4. `Report`: catch non-cancellation exceptions per iteration, log at most once a minute, continue.

- [ ] **Step 1: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/ReplicatorShutdownTests.cs`:

```csharp
using System.Collections.Concurrent;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Prepare;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Sink;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth;

public class ReplicatorShutdownTests {
    sealed class FakeReader(int count, bool blockAfter) : IEventReader {
        public int    PositionCalls;
        public Func<int, long?>? Position { get; init; }

        public string Protocol => "fake";

        public async Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken) {
            for (var i = 0; i < count; i++) await next(TestEvents.Original("s", i));

            if (blockAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<long?> GetLastPosition(CancellationToken cancellationToken) {
            var n = Interlocked.Increment(ref PositionCalls);

            return Task.FromResult(Position?.Invoke(n) ?? count);
        }

        public ValueTask<bool> Filter(BaseOriginalEvent originalEvent) => ValueTask.FromResult(true);
    }

    sealed class AuthGatedWriter(GrpcAuthContext auth) : IEventWriter {
        public readonly ConcurrentQueue<long> Written = new();

        public Task Start() => Task.CompletedTask;

        public Task<long> WriteEvent(BaseProposedEvent proposedEvent, CancellationToken cancellationToken)
            => auth.Run((_, _) => {
                    Written.Enqueue(proposedEvent.SourceLogPosition.EventNumber);

                    return Task.FromResult(1L);
                },
                cancellationToken
            );
    }

    sealed class RecordingCheckpointStore : ICheckpointStore {
        public readonly ConcurrentQueue<LogPosition> Stored = new();

        public ValueTask<bool>        HasStoredCheckpoint(CancellationToken ct)        => ValueTask.FromResult(false);
        public ValueTask<LogPosition> LoadCheckpoint(CancellationToken ct)             => ValueTask.FromResult(LogPosition.Start);
        public ValueTask              Flush(CancellationToken ct)                      => ValueTask.CompletedTask;

        public ValueTask StoreCheckpoint(LogPosition logPosition, CancellationToken ct) {
            Stored.Enqueue(logPosition);

            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Sink_token_outage_pauses_replication_and_it_resumes_without_restart() {
        var time     = new FakeTimeProvider();
        var source   = new ControllableTokenSource(time); // no token yet
        var writer   = new AuthGatedWriter(new GrpcAuthContext(source, CancellationToken.None, time, "sink"));
        var store    = new RecordingCheckpointStore();

        var run = Replicator.Replicate(
            new FakeReader(5, blockAfter: false),
            writer,
            new SinkPipeOptions(1, 10),
            new PreparePipelineOptions(null, null, 1, 10),
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(false, false, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            CancellationToken.None
        );

        await TimeDriver.Until(() => source.Calls >= 5, time);
        await Assert.That(writer.Written).IsEmpty();
        source.Value = "A";
        await TimeDriver.Drive(run, time);

        await Assert.That(writer.Written.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Shutdown_during_outage_with_full_sink_channel_returns_promptly() {
        var time     = new FakeTimeProvider();
        var shutdown = new CancellationTokenSource();
        var stopping = new CancellationTokenSource();
        var source   = new ControllableTokenSource(time);
        var writer   = new AuthGatedWriter(new GrpcAuthContext(source, shutdown.Token, time, "sink"));
        var store    = new RecordingCheckpointStore();

        var run = Replicator.Replicate(
            new FakeReader(10, blockAfter: true),
            writer,
            new SinkPipeOptions(1, 1),                    // sink buffer of 1: fills immediately
            new PreparePipelineOptions(null, null, 1, 100), // prepare buffer larger than the event count
            new NoCheckpointSeeder(),
            store,
            new ReplicatorOptions(false, true, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            stopping.Token
        );

        await TimeDriver.Until(() => source.Calls >= 3, time);
        await Task.Delay(200); // let the prepare shovel block on the full sink channel

        await shutdown.CancelAsync(); // ApplicationStopping
        await stopping.CancelAsync(); // hosted service stop

        await run.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(writer.Written).IsEmpty();
        await Assert.That(store.Stored).IsEmpty();
    }

    [Test]
    public async Task Metrics_reporter_survives_a_failing_position_read() {
        using var stopping = new CancellationTokenSource();
        var reader = new FakeReader(0, blockAfter: true) { Position = n => n == 1 ? throw new InvalidOperationException("boom") : 1 };

        var run = Replicator.Replicate(
            reader,
            new AuthGatedWriter(GrpcAuthContext.None),
            new SinkPipeOptions(),
            new PreparePipelineOptions(null, null),
            new NoCheckpointSeeder(),
            new RecordingCheckpointStore(),
            new ReplicatorOptions(false, true, TimeSpan.Zero, TimeSpan.FromMilliseconds(20)),
            stopping.Token
        );

        await Task.Delay(500);
        await Assert.That(Volatile.Read(ref reader.PositionCalls)).IsGreaterThanOrEqualTo(3);
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
```

Note: `FakeReader.Position` throws inside `GetLastPosition` for the first call. `Task.FromResult(Position?.Invoke(n) ...)` evaluates the delegate synchronously, so the exception surfaces from `GetLastPosition` exactly as a failing gRPC read would.

- [ ] **Step 2: Run to verify failure**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/ReplicatorShutdownTests/*" --timeout 120s`
Expected: `Shutdown_during_outage_with_full_sink_channel_returns_promptly` FAILS with a timeout after 15 s (drain-loop hang), and `Metrics_reporter_survives_a_failing_position_read` FAILS (only 1 call). The first test may already pass.

- [ ] **Step 3: Implement**

In `src/Kurrent.Replicator/Replicator.cs`:

Move `var writerCts = new CancellationTokenSource();` to directly after `var sinkChannel = ...;` and change the prepare pipe:

```csharp
        var writerCts = new CancellationTokenSource();

        var readerPipe = new ReaderPipe(
            reader,
            checkpointStore,
            ctx => prepareChannel.Writer.WriteAsync(ctx, ctx.CancellationToken)
        );

        // writerCts (not ctx.CancellationToken, which is None) so a hand-off blocked on a full sink channel ends at shutdown
        var preparePipe = new PreparePipe(
            preparePipeOptions.Filter,
            preparePipeOptions.Transform,
            ctx => sinkChannel.Writer.WriteAsync(ctx, writerCts.Token)
        );
        var sinkPipe = new SinkPipe(writer, sinkPipeOptions, checkpointStore);
```

Drain loop and stop ordering:

```csharp
                while (sinkChannel.Reader.Count > 0 && !writerTask.IsCompleted) {
                    await checkpointStore.Flush(CancellationToken.None).ConfigureAwait(false);
                    Log.Info("Waiting for the sink pipe to exhaust ({Left} left)...", sinkChannel.Reader.Count);
                    await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                }

                if (writerTask.IsCompleted && sinkChannel.Reader.Count > 0) {
                    Log.Warn(
                        "Writer stopped with {Count} events not written; they will be read again from the last checkpoint",
                        sinkChannel.Reader.Count
                    );
                }

                await Flush().ConfigureAwait(false);

                if (stopping) {
                    await writerCts.CancelAsync();
                    sinkChannel.Writer.Complete();

                    break;
                }
```

`Report`:

```csharp
        async Task Report() {
            DateTimeOffset? lastWarning = null;

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    var position = await reader.GetLastPosition(stoppingToken).ConfigureAwait(false);

                    if (position.HasValue) {
                        ReplicationMetrics.LastSourcePosition.Set(position.Value);
                    }
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception e) {
                    var now = DateTimeOffset.UtcNow;

                    if (lastWarning is null || now - lastWarning > TimeSpan.FromMinutes(1)) {
                        lastWarning = now;
                        Log.Warn(e, "Unable to read the source position for metrics; will retry");
                    }
                }

                try {
                    await Task.Delay(replicatorOptions.ReportMetricsFrequency, stoppingToken).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }

            Log.Info("Reporting stopped");
        }
```

`Stop()` still calls `writerCts.Cancel()`; cancelling twice is harmless.

- [ ] **Step 4: Run tests to verify they pass**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/ReplicatorShutdownTests/*" --timeout 120s`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Kurrent.Replicator/Replicator.cs test/Kurrent.Replicator.Tests/Auth/ReplicatorShutdownTests.cs
git commit -m "fix(replicator): no shutdown hang on stalled writer; reporter survives read errors

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 13: Host configuration — binding, per-side wiring, environment-variable redaction

The host project (`src/replicator`) runs `yarn install` on Debug builds, so the test project must not reference it. Binding and redaction logic therefore live in library projects, and the host only wires them up.

**Files:**
- Create: `src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthSettings.cs`
- Create: `src/Kurrent.Replicator.Shared/ConfigRedaction.cs`
- Modify: `src/replicator/Settings/ReplicatorSettings.cs`
- Modify: `src/replicator/Settings/EnvConfigProvider.cs`
- Modify: `src/replicator/Startup.cs`
- Modify: `test/Kurrent.Replicator.Tests/Kurrent.Replicator.Tests.csproj`
- Test: `test/Kurrent.Replicator.Tests/Auth/AuthSettingsBindingTests.cs`
- Test: `test/Kurrent.Replicator.Tests/Auth/ConfigRedactionTests.cs`

**Interfaces:**
- Consumes: `GrpcAuthOptions`, `GrpcAuthOptionsValidator.ValidateProtocol`, `GrpcConfigurator(GrpcAuthOptions, GrpcAuthOptions, CancellationToken)`.
- Produces:
  - `public sealed record GrpcAuthSettings` (raw strings/ints bound from `auth`) with `GrpcAuthOptions ToOptions(string side)` (throws `InvalidOperationException("Invalid {side} auth configuration: ...")` for unknown `type` / `clientAuthentication`; lower-cases `additionalParameters` keys, last one wins).
  - `public static class ConfigRedaction { string? Display(string configKey, string? value); }` in namespace `Kurrent.Replicator.Shared`.
  - `EsdbSettings.Auth` (`GrpcAuthSettings`, default `new()`).

- [ ] **Step 1: Add test packages**

In `test/Kurrent.Replicator.Tests/Kurrent.Replicator.Tests.csproj`, add:

```xml
        <PackageReference Include="Microsoft.Extensions.Configuration" Version="9.0.4"/>
        <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="9.0.4"/>
```

- [ ] **Step 2: Write the failing tests**

`test/Kurrent.Replicator.Tests/Auth/AuthSettingsBindingTests.cs`:

```csharp
using Kurrent.Replicator.KurrentDb.Auth;
using Microsoft.Extensions.Configuration;

namespace Kurrent.Replicator.Tests.Auth;

public class AuthSettingsBindingTests {
    static GrpcAuthOptions Bind(Dictionary<string, string?> values, string side) {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var raw    = config.GetSection($"Replicator:{side}:Auth").Get<GrpcAuthSettings>() ?? new GrpcAuthSettings();

        return raw.ToOptions(side.ToLowerInvariant());
    }

    [Test]
    public async Task Missing_section_means_connection_string() {
        var options = Bind(new(), "Reader");
        await Assert.That(options.Type).IsEqualTo(GrpcAuthType.ConnectionString);
        await Assert.That(options.IsOAuth).IsFalse();
    }

    [Test]
    public async Task Reader_basic_and_sink_oauth_bind_independently() {
        var values = new Dictionary<string, string?> {
            ["Replicator:Reader:ConnectionString"]          = "esdb://admin:changeit@source:2113",
            ["Replicator:Sink:Auth:Type"]                   = "OAuthClientCredentials",
            ["Replicator:Sink:Auth:TokenEndpoint"]          = "https://login.microsoftonline.com/t/oauth2/v2.0/token",
            ["Replicator:Sink:Auth:ClientId"]               = "app",
            ["Replicator:Sink:Auth:ClientSecretFile"]       = "/secrets/sink",
            ["Replicator:Sink:Auth:ClientAuthentication"]   = "Basic",
            ["Replicator:Sink:Auth:Scope"]                  = "api://kdb/.default",
            ["Replicator:Sink:Auth:RefreshBeforeExpirySeconds"] = "120"
        };

        var reader = Bind(values, "Reader");
        var sink   = Bind(values, "Sink");

        await Assert.That(reader.Type).IsEqualTo(GrpcAuthType.ConnectionString);
        await Assert.That(sink.Type).IsEqualTo(GrpcAuthType.OAuthClientCredentials);
        await Assert.That(sink.ClientAuthentication).IsEqualTo(ClientAuthenticationMethod.Basic);
        await Assert.That(sink.ClientSecretFile).IsEqualTo("/secrets/sink");
        await Assert.That(sink.RefreshBeforeExpirySeconds).IsEqualTo(120);
        await Assert.That(sink.TokenFileReloadSeconds).IsEqualTo(30);
    }

    [Test]
    public async Task Reader_oauth_and_sink_basic_bind_independently() {
        var values = new Dictionary<string, string?> {
            ["Replicator:Reader:Auth:Type"]      = "oauthTokenFile",
            ["Replicator:Reader:Auth:TokenFile"] = "/var/run/token",
            ["Replicator:Sink:Auth:Type"]        = "connectionString"
        };

        await Assert.That(Bind(values, "Reader").TokenFile).IsEqualTo("/var/run/token");
        await Assert.That(Bind(values, "Sink").IsOAuth).IsFalse();
    }

    [Test]
    public async Task Env_vars_bind_auth_including_additional_parameters() {
        // EnvConfigProvider turns REPLICATOR_A_B into the key REPLICATOR:A:B (case preserved, lookups case-insensitive)
        var env = new Dictionary<string, string?> {
            ["REPLICATOR_SINK_AUTH_TYPE"]                          = "oauthClientCredentials",
            ["REPLICATOR_SINK_AUTH_TOKENENDPOINT"]                 = "https://idp/t",
            ["REPLICATOR_SINK_AUTH_CLIENTID"]                      = "c",
            ["REPLICATOR_SINK_AUTH_CLIENTSECRET"]                  = "s",
            ["REPLICATOR_SINK_AUTH_ADDITIONALPARAMETERS_AUDIENCE"] = "kurrentdb"
        };
        var values = env.ToDictionary(kv => kv.Key.Replace("_", ":"), kv => kv.Value);

        var sink = Bind(values, "Sink");
        await Assert.That(sink.Type).IsEqualTo(GrpcAuthType.OAuthClientCredentials);
        await Assert.That(sink.ClientSecret).IsEqualTo("s");
        await Assert.That(sink.AdditionalParameters["audience"]).IsEqualTo("kurrentdb");
    }

    [Test]
    public async Task Unknown_values_fail_with_the_side() {
        var badType = new Dictionary<string, string?> { ["Replicator:Reader:Auth:Type"] = "oauth" };
        var ex      = await Assert.That(() => Bind(badType, "Reader")).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).StartsWith("Invalid reader auth configuration");

        var badMethod = new Dictionary<string, string?> {
            ["Replicator:Sink:Auth:Type"] = "oauthClientCredentials", ["Replicator:Sink:Auth:ClientAuthentication"] = "jwt"
        };
        await Assert.That(() => Bind(badMethod, "Sink")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ToString_never_prints_secrets() {
        var raw = new GrpcAuthSettings { Type = "oauthClientCredentials", ClientSecret = "s3cr3t" };
        await Assert.That(raw.ToString()).DoesNotContain("s3cr3t");
        await Assert.That(raw.ToOptions("sink").ToString()).DoesNotContain("s3cr3t");
    }
}
```

`test/Kurrent.Replicator.Tests/Auth/ConfigRedactionTests.cs`:

```csharp
using Kurrent.Replicator.Shared;

namespace Kurrent.Replicator.Tests.Auth;

public class ConfigRedactionTests {
    [Test]
    [Arguments("REPLICATOR:READER:CONNECTIONSTRING", "esdb://admin:changeit@host:2113")]
    [Arguments("REPLICATOR:READER:CONNECTIONSTRING", "GossipSeeds=a:2113; DefaultUserCredentials=admin:changeit;")]
    [Arguments("REPLICATOR:SINK:AUTH:CLIENTSECRET", "s3cr3t")]
    [Arguments("REPLICATOR:SINK:AUTH:ADDITIONALPARAMETERS:AUDIENCE", "kdb")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb://u:p@mongo")]
    public async Task Sensitive_values_are_masked(string key, string value) {
        if (key.EndsWith("PATH")) {
            await Assert.That(ConfigRedaction.Display(key, value)).IsEqualTo(value); // not a connection string key: printed as before
            return;
        }

        await Assert.That(ConfigRedaction.Display(key, value)).IsEqualTo("***");
    }

    [Test]
    public async Task Auth_type_and_unrelated_keys_are_printed() {
        await Assert.That(ConfigRedaction.Display("REPLICATOR:SINK:AUTH:TYPE", "oauthTokenFile")).IsEqualTo("oauthTokenFile");
        await Assert.That(ConfigRedaction.Display("REPLICATOR:SINK:PARTITIONCOUNT", "4")).IsEqualTo("4");
        await Assert.That(ConfigRedaction.Display("REPLICATOR:SINK:AUTHORITY", "x")).IsEqualTo("x"); // segment match, not substring
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q`
Expected: FAIL (`GrpcAuthSettings`, `ConfigRedaction` not found).

- [ ] **Step 4: Implement GrpcAuthSettings**

`src/Kurrent.Replicator.KurrentDb/Auth/GrpcAuthSettings.cs`:

```csharp
namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>Raw `auth` section for one side, exactly as bound from YAML or environment variables.</summary>
public sealed record GrpcAuthSettings {
    public string?                    Type                        { get; init; }
    public string?                    TokenEndpoint               { get; init; }
    public string?                    ClientId                    { get; init; }
    public string?                    ClientSecret                { get; init; }
    public string?                    ClientSecretFile            { get; init; }
    public string?                    ClientAssertionFile         { get; init; }
    public string?                    ClientAuthentication        { get; init; }
    public string?                    Scope                       { get; init; }
    public Dictionary<string, string> AdditionalParameters        { get; init; } = new();
    public int?                       DefaultTokenLifetimeSeconds { get; init; }
    public int?                       RefreshBeforeExpirySeconds  { get; init; }
    public string?                    TokenFile                   { get; init; }
    public int?                       TokenFileReloadSeconds      { get; init; }

    public GrpcAuthOptions ToOptions(string side) {
        var type = (Type ?? "connectionString").Trim().ToLowerInvariant() switch {
            "connectionstring"       => GrpcAuthType.ConnectionString,
            "oauthclientcredentials" => GrpcAuthType.OAuthClientCredentials,
            "oauthtokenfile"         => GrpcAuthType.OAuthTokenFile,
            _ => throw new InvalidOperationException(
                $"Invalid {side} auth configuration: unknown auth.type '{Type}' (expected connectionString, oauthClientCredentials or oauthTokenFile)"
            )
        };

        var method = (ClientAuthentication ?? "post").Trim().ToLowerInvariant() switch {
            "post"  => ClientAuthenticationMethod.Post,
            "basic" => ClientAuthenticationMethod.Basic,
            _ => throw new InvalidOperationException(
                $"Invalid {side} auth configuration: unknown auth.clientAuthentication '{ClientAuthentication}' (expected post or basic)"
            )
        };

        var parameters = new Dictionary<string, string>();
        foreach (var (key, value) in AdditionalParameters) parameters[key.ToLowerInvariant()] = value;

        return new GrpcAuthOptions {
            Type                        = type,
            TokenEndpoint               = NullIfEmpty(TokenEndpoint),
            ClientId                    = NullIfEmpty(ClientId),
            ClientSecret                = NullIfEmpty(ClientSecret),
            ClientSecretFile            = NullIfEmpty(ClientSecretFile),
            ClientAssertionFile         = NullIfEmpty(ClientAssertionFile),
            ClientAuthentication        = method,
            Scope                       = NullIfEmpty(Scope),
            AdditionalParameters        = parameters,
            DefaultTokenLifetimeSeconds = DefaultTokenLifetimeSeconds,
            RefreshBeforeExpirySeconds  = RefreshBeforeExpirySeconds ?? 300,
            TokenFile                   = NullIfEmpty(TokenFile),
            TokenFileReloadSeconds      = TokenFileReloadSeconds ?? 30
        };
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public override string ToString() => $"GrpcAuthSettings(Type={Type})";
}
```

- [ ] **Step 5: Implement ConfigRedaction and use it in EnvConfigProvider**

`src/Kurrent.Replicator.Shared/ConfigRedaction.cs`:

```csharp
namespace Kurrent.Replicator.Shared;

/// <summary>Decides what may be printed for a configuration value.</summary>
public static class ConfigRedaction {
    public static string? Display(string configKey, string? value) {
        var segments = configKey.Split(':');

        if (segments[^1].Equals("ConnectionString", StringComparison.OrdinalIgnoreCase)) return "***";

        var authIndex = Array.FindIndex(segments, s => s.Equals("Auth", StringComparison.OrdinalIgnoreCase));

        if (authIndex < 0) return value;

        var isAuthType = authIndex == segments.Length - 2 && segments[^1].Equals("Type", StringComparison.OrdinalIgnoreCase);

        return isAuthType ? value : "***";
    }
}
```

`src/replicator/Settings/EnvConfigProvider.cs` — replace the provider and record:

```csharp
public class EnvConfigProvider : ConfigurationProvider {
    public override void Load() {
        var envVars = Environment.GetEnvironmentVariables();

        var vars = envVars.Cast<DictionaryEntry>()
            .Select(x => new EnvVar(x.Key.ToString()!, x.Value?.ToString()))
            .Where(x => x.Key.StartsWith("REPLICATOR_") && x.Value != null)
            .ToList();

        foreach (var v in vars) Console.WriteLine($"{v.ConfigKey} = {ConfigRedaction.Display(v.ConfigKey, v.Value)}");

        Data = vars.ToDictionary(x => x.ConfigKey, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    record EnvVar(string Key, string? Value) {
        public string ConfigKey => Key.Replace("_", ":");
    }
}
```

Add `using Kurrent.Replicator.Shared;` at the top of the file.

- [ ] **Step 6: Wire the host**

`src/replicator/Settings/ReplicatorSettings.cs` — add `using Kurrent.Replicator.KurrentDb.Auth;` and extend `EsdbSettings`:

```csharp
public record EsdbSettings {
    public string           ConnectionString { get; init; }
    public string           Protocol         { get; init; }
    public int              PageSize         { get; init; } = 1024;
    public GrpcAuthSettings Auth             { get; init; } = new();
}
```

`src/replicator/Startup.cs` — add `using Kurrent.Replicator.KurrentDb.Auth;` and replace the gRPC configurator registration:

```csharp
        var readerAuth = AuthFor("reader", replicatorOptions.Reader);
        var sinkAuth   = AuthFor("sink", replicatorOptions.Sink);

        services.AddSingleton<IConfigurator, TcpConfigurator>(_ => new(replicatorOptions.Reader.PageSize));
        services.AddSingleton<IConfigurator, GrpcConfigurator>(
            sp => new(readerAuth, sinkAuth, sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
        );
        services.AddSingleton<IConfigurator, KafkaConfigurator>(_ => new(replicatorOptions.Sink.Router));
```

and add the helper at the bottom of the `Startup` class:

```csharp
    static GrpcAuthOptions AuthFor(string side, EsdbSettings settings) {
        var options = (settings.Auth ?? new GrpcAuthSettings()).ToOptions(side);

        if (GrpcAuthOptionsValidator.ValidateProtocol(options, settings.Protocol) is { } error)
            throw new InvalidOperationException($"Invalid {side} auth configuration: {error}");

        return options;
    }
```

- [ ] **Step 7: Run tests and build the host**

Run: build, then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/*/AuthSettingsBindingTests/*"` and `ConfigRedactionTests`.
Expected: all PASS.

Run: `dotnet build src/replicator -c Release -p:UseAppHost=false -v q` (Release skips the yarn install target)
Expected: 0 errors.

Manual smoke test of fail-fast validation (no KurrentDB needed):

```bash
mkdir -p /tmp/replicator-smoke/config && printf 'replicator:\n  reader:\n    protocol: tcp\n    connectionString: "ConnectTo=tcp://localhost:1113"\n    auth:\n      type: oauthTokenFile\n      tokenFile: /tmp/t\n  sink:\n    protocol: grpc\n    connectionString: "esdb://localhost:2113"\n' > /tmp/replicator-smoke/config/appsettings.yaml
```

```bash
cd /tmp/replicator-smoke && DOTNET_ROLL_FORWARD=Major dotnet /Users/tony/dev/replicator/.claude/worktrees/replicator-oauth-auth-e66296/src/replicator/bin/Release/net9.0/replicator.dll
```

Expected: the process exits with `Invalid reader auth configuration: auth.type OAuthTokenFile is only supported with protocol grpc (configured: tcp)`.

- [ ] **Step 8: Commit**

```bash
git add src test/Kurrent.Replicator.Tests
git commit -m "feat(auth): per-side auth configuration, host wiring, env var redaction

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Helm chart — inject secrets, files and workload identity

**Files:**
- Modify: `charts/replicator/values.yaml`
- Modify: `charts/replicator/templates/statefulset.yaml`

**Interfaces:**
- Produces Helm values: `serviceAccountName`, `podLabels`, `extraEnv`, `extraEnvFrom`, `extraVolumes`, `extraVolumeMounts` (all empty by default). `replicator.reader.auth` / `replicator.sink.auth` already pass through `toYaml .Values.replicator` in `configmap.yaml`.

- [ ] **Step 1: Capture today's rendering**

```bash
helm template r charts/replicator > /tmp/replicator-before.yaml
```

- [ ] **Step 2: Add values**

Append to `charts/replicator/values.yaml` (top level):

```yaml
# Service account for the pod (e.g. one federated with Azure Workload Identity).
serviceAccountName: ""
# Extra labels on the pod template, e.g. azure.workload.identity/use: "true".
podLabels: {}
# Extra environment variables. Use these (not replicator.*.auth in this file) for secrets, e.g.
# - name: REPLICATOR_SINK_AUTH_CLIENTSECRET
#   valueFrom: { secretKeyRef: { name: replicator-oauth, key: client-secret } }
extraEnv: []
extraEnvFrom: []
# Extra volumes and mounts, e.g. a Secret holding clientSecretFile or a token file written by a sidecar.
extraVolumes: []
extraVolumeMounts: []
```

and document the `auth` block under `replicator.reader` / `replicator.sink` as a comment:

```yaml
replicator:
  reader:
    connectionString:
    protocol: tcp
    # auth:                       # optional, gRPC only. See docs: deployment/authentication
    #   type: oauthClientCredentials
    #   tokenEndpoint: https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token
    #   clientId: <client-id>
    #   clientSecretFile: /var/run/secrets/replicator/client-secret   # do not put clientSecret here
    #   scope: api://<kurrentdb-app>/.default
```

(Add the same commented block under `sink`.)

- [ ] **Step 3: Template the pod spec**

In `charts/replicator/templates/statefulset.yaml`:

Pod labels — after `tier: web` in `spec.template.metadata.labels`:

```yaml
{{- with .Values.podLabels }}
{{ toYaml . | indent 8 }}
{{- end }}
```

Service account — directly under `spec.template.spec:` (before `containers:`):

```yaml
{{- with .Values.serviceAccountName }}
      serviceAccountName: {{ . }}
{{- end }}
```

Container env — after `imagePullPolicy:`:

```yaml
{{- with .Values.extraEnv }}
        env:
{{ toYaml . | indent 10 }}
{{- end }}
{{- with .Values.extraEnvFrom }}
        envFrom:
{{ toYaml . | indent 10 }}
{{- end }}
```

Volume mounts — at the end of the container's `volumeMounts:` list (after the partitioner block):

```yaml
{{- with .Values.extraVolumeMounts }}
{{ toYaml . | indent 8 }}
{{- end }}
```

Volumes — at the end of `volumes:` (after the PVC volume):

```yaml
{{- with .Values.extraVolumes }}
{{ toYaml . | indent 6 }}
{{- end }}
```

- [ ] **Step 4: Verify rendering**

```bash
helm template r charts/replicator > /tmp/replicator-after.yaml && diff /tmp/replicator-before.yaml /tmp/replicator-after.yaml && echo "defaults unchanged"
```

Expected: `defaults unchanged`.

```bash
helm template r charts/replicator --set serviceAccountName=replicator-wi --set podLabels."azure\.workload\.identity/use"=true --set 'extraEnv[0].name=REPLICATOR_SINK_AUTH_TYPE' --set 'extraEnv[0].value=oauthClientCredentials' --set 'extraVolumes[0].name=oauth' --set 'extraVolumes[0].secret.secretName=replicator-oauth' --set 'extraVolumeMounts[0].name=oauth' --set 'extraVolumeMounts[0].mountPath=/var/run/secrets/replicator' --set replicator.sink.auth.type=oauthTokenFile --set replicator.sink.auth.tokenFile=/var/run/secrets/replicator/token
```

Expected: valid YAML containing `serviceAccountName: replicator-wi`, the pod label, the `env` entry, the extra volume and mount, and an `auth:` block under `sink:` in the ConfigMap. Pipe it through `kubectl apply --dry-run=client -f -` if `kubectl` is available.

- [ ] **Step 5: Commit**

```bash
git add charts/replicator
git commit -m "feat(helm): extra env, volumes, service account and pod labels for OAuth

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: Documentation and changelog

**Files:**
- Create: `docs/src/content/docs/deployment/authentication.mdx`
- Modify: `docs/src/content/docs/deployment/configuration.mdx`
- Modify: `docs/src/content/docs/features/readers.mdx`
- Modify: `docs/src/content/docs/features/sinks.mdx`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Write the authentication page**

`docs/src/content/docs/deployment/authentication.mdx`:

````mdx
---
title: Authentication
description: Authenticate the gRPC reader and sink with basic credentials or OAuth 2.0 access tokens.
sidebar:
  order: 2
---

Replicator authenticates to each KurrentDB cluster separately. The reader (source) and the sink (target) each have their own optional `auth` section, so any combination works: basic on one side and OAuth on the other, OAuth on both with different identity providers, or basic on both.

OAuth is supported for the `grpc` protocol only. It requires TLS (`tls=true`, the default), and the connection string must not contain `user:pass@`.

## Auth types

| `auth.type` | Use when |
|:--|:--|
| `connectionString` (default) | Basic credentials in the connection string, or no authentication. Behaves exactly as before. |
| `oauthClientCredentials` | Replicator gets tokens from your identity provider with the OAuth 2.0 client credentials grant and refreshes them itself. |
| `oauthTokenFile` | Something else (a sidecar, Vault agent, CI job) keeps a valid access token in a file; Replicator reads it. |

## Options

| Option | Type | Description |
|:--|:--|:--|
| `tokenEndpoint` | `oauthClientCredentials` | Token endpoint URL. Must be `https` (plain `http` only for `localhost`). |
| `clientId` | `oauthClientCredentials` | OAuth client ID of the Replicator identity. |
| `clientSecret` | `oauthClientCredentials` | Client secret. Set it through the `REPLICATOR_<SIDE>_AUTH_CLIENTSECRET` environment variable, not in the YAML file. |
| `clientSecretFile` | `oauthClientCredentials` | Path to a file with the client secret, e.g. a mounted Kubernetes Secret. |
| `clientAssertionFile` | `oauthClientCredentials` | Path to a signed JWT client assertion (RFC 7523), e.g. the Azure Workload Identity token file. Re-read on every token request. |
| `clientAuthentication` | `oauthClientCredentials` | `post` (default) sends the secret as form fields; `basic` sends it in an HTTP Basic header. |
| `scope` | `oauthClientCredentials` | Scopes to request. For Entra ID: `api://<kurrentdb-app-id-uri>/.default`. |
| `additionalParameters` | `oauthClientCredentials` | Extra token request fields, e.g. `audience` (Auth0, Okta) or `resource` (Entra v1, ADFS). Keys are lower-cased. |
| `defaultTokenLifetimeSeconds` | `oauthClientCredentials` | Lifetime to assume if your provider omits `expires_in`. Required in that case. |
| `refreshBeforeExpirySeconds` | `oauthClientCredentials` | Refresh this many seconds before expiry. Default `300`, capped at half the token lifetime. |
| `tokenFile` | `oauthTokenFile` | Path to a file containing only the access token. |
| `tokenFileReloadSeconds` | `oauthTokenFile` | How often to re-read the file. Default `30`. |

Exactly one of `clientSecret`, `clientSecretFile` and `clientAssertionFile` must be set. Every option can also be set with an environment variable, for example `REPLICATOR_SINK_AUTH_TOKENENDPOINT` or `REPLICATOR_SINK_AUTH_ADDITIONALPARAMETERS_AUDIENCE`.

## KurrentDB permissions

Replication reads `$all` and stream metadata on the source, and on the target it writes to any stream, sets stream metadata and deletes streams. Map the Replicator identity's role claim to `$admins` in the KurrentDB OAuth configuration on each cluster (or grant equivalent ACLs).

If the reader's token cannot read a stream's metadata (`PermissionDenied`), Replicator stops replicating with an error rather than copying events it cannot check against scavenge rules. Fix the role mapping and restart Replicator.

## How tokens are handled

- Every gRPC call carries a current token. Tokens are refreshed before they expire, without restarting Replicator.
- If the identity provider is unreachable, or KurrentDB rejects a token, replication pauses with a warning (logged at most once a minute) and resumes on its own once a valid token is available. A token KurrentDB rejected is not sent again for 60 seconds, and after that only one call re-tests it.
- The long-running `$all` read and the realtime subscription are authorized when they start. If KurrentDB ends them when the token expires, Replicator restarts them from the last checkpoint with a fresh token.
- Tokens, secrets and assertions are never logged.

## Example: Entra ID with a client secret

1. Register an application for KurrentDB. Expose an Application ID URI (e.g. `api://kurrentdb`) and create an app role (e.g. `Replicator`) that your KurrentDB OAuth configuration maps to `$admins`.
2. Register an application for Replicator, create a client secret, and grant it the `Replicator` app role on the KurrentDB application (admin consent required).
3. Configure the side that talks to the OAuth-enabled cluster:

```yaml
replicator:
  reader:
    protocol: grpc
    connectionString: "esdb://source.example.com:2113?tls=true"
    auth:
      type: oauthClientCredentials
      tokenEndpoint: "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token"
      clientId: "<replicator-app-client-id>"
      clientSecretFile: /var/run/secrets/replicator/client-secret
      scope: "api://kurrentdb/.default"
  sink:
    protocol: grpc
    connectionString: "esdb://admin:changeit@target.example.com:2113?tls=true"
```

## Example: Entra ID with AKS Workload Identity (no secret)

Federate the Replicator application with the Kubernetes service account, then point `clientAssertionFile` at the projected token:

```yaml
serviceAccountName: replicator-wi
podLabels:
  azure.workload.identity/use: "true"
replicator:
  sink:
    protocol: grpc
    connectionString: "esdb://target.example.com:2113?tls=true"
    auth:
      type: oauthClientCredentials
      tokenEndpoint: "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token"
      clientId: "<replicator-app-client-id>"
      clientAssertionFile: /var/run/secrets/azure/tokens/azure-identity-token
      scope: "api://kurrentdb/.default"
```

## Example: other providers

Keycloak, Okta, Auth0 and other OAuth 2.0 servers work the same way. Many need an `audience`:

```yaml
    auth:
      type: oauthClientCredentials
      tokenEndpoint: "https://idp.example.com/oauth2/token"
      clientId: replicator
      clientSecretFile: /var/run/secrets/replicator/client-secret
      additionalParameters:
        audience: kurrentdb
```

Token file written by another process:

```yaml
    auth:
      type: oauthTokenFile
      tokenFile: /var/run/secrets/kurrentdb/token
```

## Helm

Put secrets in Kubernetes Secrets and inject them with `extraEnv`, `extraEnvFrom`, `extraVolumes` and `extraVolumeMounts`; don't put `clientSecret` in your values file, because it ends up in a ConfigMap. See [Kubernetes](../kubernetes/).
````

- [ ] **Step 2: Update the configuration reference**

In `docs/src/content/docs/deployment/configuration.mdx`, add rows after `reader.pageSize` and after `sink.bufferSize`:

```markdown
| `reader.auth.*`                   | Optional reader [authentication](../authentication/) (gRPC only): `type` is `connectionString` (default), `oauthClientCredentials` or `oauthTokenFile` |
```

```markdown
| `sink.auth.*`                     | Optional sink [authentication](../authentication/) (gRPC only), configured independently of the reader                                                  |
```

- [ ] **Step 3: One-line notes in features**

Append to `docs/src/content/docs/features/readers.mdx` and `features/sinks.mdx`:

```markdown
The gRPC reader can authenticate with OAuth 2.0 access tokens instead of basic credentials. See [Authentication](../../deployment/authentication/).
```

(Use "gRPC sink" in `sinks.mdx`.)

- [ ] **Step 4: Changelog**

Under `## [Unreleased]` in `CHANGELOG.md`, add to `### Added`:

```markdown
- OAuth 2.0 authentication for the gRPC reader and sink (client credentials or token file), configured independently per side, with automatic token refresh and recovery. Helm values for extra env, volumes, service account and pod labels.
```

and to `### Changed`:

```markdown
- Connection strings and auth settings are no longer printed when environment variables are loaded.
- The realtime subscription used for scavenging resubscribes reliably after drops, and its metadata cache is rebuilt after a gap.
- Replication no longer hangs on shutdown when the writer has stopped; the metrics reporter keeps running after a failed position read.
```

- [ ] **Step 5: Build the docs**

```bash
cd docs && pnpm install --frozen-lockfile && pnpm build
```

Expected: build succeeds and `dist/deployment/authentication/index.html` exists. If `pnpm` is not available, skip and say so in the PR description.

- [ ] **Step 6: Commit**

```bash
git add docs/src CHANGELOG.md
git commit -m "docs: OAuth authentication guide and configuration reference

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: Full verification

- [ ] **Step 1: Whole solution builds**

Run: `dotnet build Kurrent.Replicator.slnx -c Release -p:UseAppHost=false -v q`
Expected: 0 errors, no new warnings in `Kurrent.Replicator.KurrentDb`.

- [ ] **Step 2: All new tests**

Run: `dotnet build test/Kurrent.Replicator.Tests -p:UseAppHost=false -v q` then `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/Kurrent.Replicator.Tests.Auth/*/*" --timeout 300s`
Expected: all PASS. Run it three times to catch flaky timing tests; fix any flake rather than retrying.

- [ ] **Step 3: Existing container tests (needs Docker)**

Run: `DOTNET_ROLL_FORWARD=Major dotnet exec test/Kurrent.Replicator.Tests/bin/Debug/net9.0/Kurrent.Replicator.Tests.dll --treenode-filter "/*/Kurrent.Replicator.Tests/*/*"`
Expected: `ValuePartitionerTests` and `ChaserCheckpointSeedingTests` PASS unchanged (they exercise basic-auth gRPC reads/writes, the reworked `Realtime`, `StreamMetaCache` and `Replicator`). If Docker is unavailable, note it for CI.

- [ ] **Step 4: Secret hygiene sweep**

```bash
git diff master --stat && git diff master -- src | grep -nE 'Log\.(Info|Warn|Error|Debug).*\b(ClientSecret|_secret|assertion|AccessToken|lease\.Value|Value\))' || echo "no suspicious log arguments"
```

Expected: `no suspicious log arguments` (review any hit manually).

- [ ] **Step 5: Record the manual end-to-end plan**

The spec's "Manual end-to-end" scenarios (OAuth sink, OAuth reader across ≥3 token lifetimes, OAuth on both sides with token-file rotation, token endpoint outage) need a licensed KurrentDB with the OAuth plugin and a real IdP. They are run before release and recorded in the PR description, including which long-lived-stream behaviour KurrentDB showed at token expiry.
