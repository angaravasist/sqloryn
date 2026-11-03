namespace Sqloryn.Core;

/// <summary>
/// UI-independent model of the connect dialog's autofill event sequence. It owns the text the password box should show
/// and routes every event through <see cref="ProfileAutofillSession"/>, so a delayed Keychain result can only fill the
/// box when it is still current and is never treated as typed. The window mirrors <see cref="PasswordText"/> into the
/// control (with its programmatic-change guard) after each call. Call from a single (UI) thread.
/// </summary>
public sealed class ConnectAutofillController
{
    readonly ProfileAutofillSession _session;

    public ConnectAutofillController() : this(new ProfileAutofillSession())
    {
    }

    public ConnectAutofillController(ProfileAutofillSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>What the password box should currently contain.</summary>
    public string PasswordText { get; private set; } = "";

    /// <summary>True only while <see cref="PasswordText"/> is a Keychain secret the user did not type.</summary>
    public bool PasswordIsAutofilled { get; private set; }

    public long Generation => _session.Generation;

    /// <summary>SelectProfile: clears the box and starts a lookup. Returns the generation to present to <see cref="LookupCompleted"/>.</summary>
    public long SelectProfile(ConnectionProfile profile)
    {
        var generation = _session.BeginLookup(profile);
        PasswordText = "";
        PasswordIsAutofilled = false;
        return generation;
    }

    /// <summary>True when <see cref="SelectProfile"/> left a lookup outstanding (profile remembers a password).</summary>
    public bool LookupPending => _session.IsWaiting;

    /// <summary>UserTypedPassword: the text is explicit; any pending or applied secret is invalidated.</summary>
    public void UserTypedPassword(string? text)
    {
        _session.NotifyPasswordEdited();
        PasswordText = text ?? "";
        PasswordIsAutofilled = false;
    }

    /// <summary>LookupCompleted: fills the box only when the session accepts the generation. A rejected result never touches the box.</summary>
    public AutofillOutcome LookupCompleted(long generation, string? secret)
    {
        var outcome = _session.CompleteLookup(generation, secret);
        if (outcome.Status == AutofillStatus.Applied)
        {
            PasswordText = outcome.Secret ?? "";
            PasswordIsAutofilled = true;
        }
        return outcome;
    }

    /// <summary>The Keychain read for <paramref name="generation"/> threw.</summary>
    public AutofillOutcome LookupFailed(long generation, Exception error) => _session.FailLookup(generation, error);

    /// <summary>
    /// UserChangedServer (or port, login, trust, pin): invalidates any pending or applied lookup when the endpoint no
    /// longer matches. Returns true when an autofilled password was removed from the box; typed passwords stay.
    /// </summary>
    public bool UserChangedEndpoint(EndpointIdentity current)
    {
        var cleared = _session.NotifyEndpointEdited(current);
        if (cleared)
        {
            PasswordText = "";
            PasswordIsAutofilled = false;
        }
        return cleared;
    }

    /// <summary>ResolveForConnect: the password Connect may send to <paramref name="current"/>; a remembered secret is refused at a mismatched endpoint.</summary>
    public CredentialDecision ResolveForConnect(EndpointIdentity current)
    {
        var decision = _session.ResolvePassword(current, PasswordText);
        if (!decision.Allowed)
        {
            PasswordText = "";
            PasswordIsAutofilled = false;
        }
        return decision;
    }

    /// <summary>Dialog closing, save unchecked or profile deleted.</summary>
    public void Forget()
    {
        _session.Forget();
        PasswordText = "";
        PasswordIsAutofilled = false;
    }
}
