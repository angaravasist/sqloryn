namespace Sqloryn.Core;

/// <summary>
/// Everything that decides where a SQL-authentication password is sent: host, port, login and TLS trust.
/// Database name is deliberately excluded because it does not change the network destination or the credential.
/// </summary>
public readonly record struct EndpointIdentity(
    string Server,
    int Port,
    string UserId,
    bool TrustServerCertificate,
    string? ServerCertificatePath)
{
    public static EndpointIdentity From(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new(profile.Server, profile.Port, profile.UserId, profile.TrustServerCertificate, profile.ServerCertificatePath);
    }

    public static EndpointIdentity From(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.Server, options.Port, options.UserId, options.TrustServerCertificate, options.ServerCertificatePath);
    }

    /// <summary>Host compares case-insensitively; login and pin path are exact after trimming; a blank pin equals no pin.</summary>
    public bool Matches(EndpointIdentity other)
        => string.Equals(Server?.Trim(), other.Server?.Trim(), StringComparison.OrdinalIgnoreCase)
           && Port == other.Port
           && string.Equals(UserId?.Trim(), other.UserId?.Trim(), StringComparison.Ordinal)
           && TrustServerCertificate == other.TrustServerCertificate
           && string.Equals(NormalisePin(ServerCertificatePath), NormalisePin(other.ServerCertificatePath), StringComparison.Ordinal);

    static string? NormalisePin(string? pin) => string.IsNullOrWhiteSpace(pin) ? null : pin.Trim();
}

public enum AutofillStatus
{
    /// <summary>The profile does not remember a password; nothing was requested.</summary>
    NotRequested,
    /// <summary>A secret was found and is now bound to the profile's endpoint identity.</summary>
    Applied,
    /// <summary>The lookup completed but the user edited the form or switched profile first; the result was discarded.</summary>
    Stale,
    /// <summary>The store has no secret for the profile.</summary>
    NotFound,
    /// <summary>The store refused or failed (for example Keychain access denied).</summary>
    Failed
}

public sealed record AutofillOutcome(AutofillStatus Status, string? Secret = null, string? Error = null);

public sealed record CredentialDecision(bool Allowed, string Password, bool UsesRememberedSecret, string? Reason)
{
    public static CredentialDecision Explicit(string password) => new(true, password, false, null);
    public static CredentialDecision Remembered(string password) => new(true, password, true, null);
    public static CredentialDecision Refused(string reason) => new(false, "", false, reason);
}

/// <summary>
/// UI-independent policy for a remembered (Keychain) password in the connect dialog. A looked-up secret is bound to the
/// endpoint identity of the profile it came from and to an edit generation. Any identity or trust edit, profile switch,
/// typed password or forget advances the generation, so late lookup results are discarded and an autofilled secret is
/// never offered to a different endpoint. Call from a single (UI) thread.
/// </summary>
public sealed class ProfileAutofillSession
{
    public const string ChangedEndpointMessage =
        "Server, port, login or certificate settings changed. Re-enter the password for this endpoint.";

    long _generation;
    EndpointIdentity _bound;
    bool _hasBound;
    string? _secret;
    // Secret text that was revoked; if it is still in the password box and the user did not type it, it must not be used.
    string? _withheld;

    public long Generation => Volatile.Read(ref _generation);

    /// <summary>Starts a profile selection: cancels any pending lookup, drops any applied secret and, when the profile remembers a password, looks it up.</summary>
    public async Task<AutofillOutcome> BeginProfileAsync(ConnectionProfile profile, ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(store);
        var generation = Revoke();
        _bound = EndpointIdentity.From(profile);
        _hasBound = true;
        if (!profile.RememberPassword)
            return new AutofillOutcome(AutofillStatus.NotRequested);

        string? secret;
        try
        {
            secret = await store.GetAsync(ProfileSecretKeys.ForProfile(profile.Id)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return generation != Generation
                ? new AutofillOutcome(AutofillStatus.Stale)
                : new AutofillOutcome(AutofillStatus.Failed, Error: "Could not read the Keychain: " + ex.Message);
        }

        if (generation != Generation)
            return new AutofillOutcome(AutofillStatus.Stale);
        if (string.IsNullOrEmpty(secret))
            return new AutofillOutcome(AutofillStatus.NotFound,
                Error: "No password found in the Keychain for this profile. Enter it to continue.");
        _secret = secret;
        return new AutofillOutcome(AutofillStatus.Applied, secret);
    }

    /// <summary>
    /// Call when server, port, login or trust fields change. Returns true when an applied secret was dropped, so the
    /// caller must clear the password box.
    /// </summary>
    public bool NotifyEndpointEdited(EndpointIdentity current)
    {
        if (!_hasBound || current.Matches(_bound))
            return false;
        var hadSecret = _secret is not null;
        Revoke();
        return hadSecret;
    }

    /// <summary>The user typed or replaced the password: it is now explicit, and any pending lookup must not overwrite it.</summary>
    public void NotifyPasswordEdited()
    {
        Interlocked.Increment(ref _generation);
        _secret = null;
        _withheld = null;
    }

    /// <summary>Drops any applied secret and cancels pending lookups (profile deleted, save unchecked, dialog closing).</summary>
    public void Forget()
    {
        Revoke();
        _hasBound = false;
    }

    /// <summary>Decides which password Connect may send to <paramref name="current"/>.</summary>
    public CredentialDecision ResolvePassword(EndpointIdentity current, string? passwordFieldText)
    {
        var text = passwordFieldText ?? "";
        if (_secret is not null && string.Equals(text, _secret, StringComparison.Ordinal))
            return _hasBound && current.Matches(_bound)
                ? CredentialDecision.Remembered(_secret)
                : CredentialDecision.Refused(ChangedEndpointMessage);
        if (_withheld is not null && string.Equals(text, _withheld, StringComparison.Ordinal))
            return CredentialDecision.Refused(ChangedEndpointMessage);
        return CredentialDecision.Explicit(text);
    }

    long Revoke()
    {
        if (_secret is not null)
            _withheld = _secret;
        _secret = null;
        return Interlocked.Increment(ref _generation);
    }
}
