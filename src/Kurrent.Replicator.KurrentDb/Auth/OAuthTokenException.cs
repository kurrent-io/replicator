namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>
/// A token could not be obtained, or the token source refuses to hand one out (quarantine, probe in progress).
/// Messages never contain tokens, secrets, assertions or provider-supplied free text.
/// </summary>
public sealed class OAuthTokenException(string message, Exception? inner = null) : Exception(message, inner);
