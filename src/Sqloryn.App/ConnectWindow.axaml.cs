using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

public partial class ConnectWindow : Window
{
    ConnectionProfile? _selectedProfile;
    bool _loadingProfile;
    bool _settingPassword;
    readonly ConnectAutofillController _autofill = new();

    public ConnectionOptions? Result { get; private set; }

    /// <summary>True when the user asked for the non-secret profile to be saved after a successful connect.</summary>
    public bool SaveProfile { get; private set; }

    /// <summary>True when the password should be stored in the Keychain after a successful connect.</summary>
    public bool RememberPassword { get; private set; }

    /// <summary>Existing profile id when one was selected; otherwise a fresh id.</summary>
    public Guid ProfileId { get; private set; } = Guid.NewGuid();

    public string ProfileName { get; private set; } = "";

    /// <summary>Optional display group for the saved profile; null when blank.</summary>
    public string? ProfileGroup { get; private set; }

    public ConnectWindow()
    {
        InitializeComponent();
        ProfileBox.ItemTemplate = new FuncDataTemplate<ConnectionProfile>(
            (profile, _) => new TextBlock
            {
                Text = profile is null ? "" : $"{profile.Name}  ·  {profile.UserId}@{profile.Server}"
            });
        KeychainNote.Text = AppServices.SecretsArePersistent
            ? "Passwords are stored only in your macOS Keychain, never in the profile file."
            : "Keychain is unavailable on this OS; a remembered password lasts for this app run only.";
        try
        {
            ProfileBox.ItemsSource = AppServices.Profiles.ListOrderedByRecent().ToList();
        }
        catch (Exception ex)
        {
            ShowError("Could not read saved profiles: " + ex.Message);
        }
        UpdateTlsSummary();
    }

    void UpdateTlsSummary()
    {
        // Endpoint events can fire while InitializeComponent is still resolving named controls.
        if (TlsSummaryText is null || TrustBox is null || PinBox is null)
            return;
        TlsSummaryText.Text = TlsSummary.Describe(TrustBox.IsChecked == true, PinBox.Text);
    }

    void OnCancel(object? sender, RoutedEventArgs e)
    {
        _autofill.Forget();
        Close(false);
    }

    void OnSaveProfileChanged(object? sender, RoutedEventArgs e)
    {
        var save = SaveProfileBox.IsChecked == true;
        ProfileNameBox.IsEnabled = save;
        GroupBox.IsEnabled = save;
        RememberBox.IsEnabled = save;
        if (!save)
            RememberBox.IsChecked = false;
    }

    async void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ConnectionProfile profile)
            return;
        _selectedProfile = profile;
        long generation;
        _loadingProfile = true;
        try
        {
            ServerBox.Text = profile.Server;
            PortBox.Text = profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            DatabaseBox.Text = profile.Database;
            UserBox.Text = profile.UserId;
            TrustBox.IsChecked = profile.TrustServerCertificate;
            PinBox.Text = profile.ServerCertificatePath ?? "";
            SaveProfileBox.IsChecked = true;
            ProfileNameBox.Text = profile.Name;
            GroupBox.Text = profile.Group ?? "";
            RememberBox.IsChecked = profile.RememberPassword;
            // SelectProfile -> BeginLookup(generation); the box starts empty.
            generation = _autofill.SelectProfile(profile);
            SetPasswordProgrammatically(_autofill.PasswordText);
            ErrorText.IsVisible = false;
        }
        finally
        {
            _loadingProfile = false;
        }

        if (!_autofill.LookupPending)
            return;

        // The controller binds any looked-up secret to this profile's endpoint identity and to the captured
        // generation. A typed password, endpoint edit or another profile selection invalidates it, in which case the
        // result is discarded and the password box is not touched.
        AutofillOutcome outcome;
        try
        {
            var secret = await AppServices.Secrets.GetAsync(ProfileSecretKeys.ForProfile(profile.Id));
            outcome = _autofill.LookupCompleted(generation, secret);
        }
        catch (Exception ex)
        {
            outcome = _autofill.LookupFailed(generation, ex);
        }
        switch (outcome.Status)
        {
            case AutofillStatus.Applied:
                SetPasswordProgrammatically(_autofill.PasswordText);
                break;
            case AutofillStatus.NotFound:
            case AutofillStatus.Failed:
                ShowError(outcome.Error ?? "Could not read the Keychain.");
                break;
        }
    }

    EndpointIdentity CurrentEndpoint() => new(
        ServerBox.Text?.Trim() ?? "",
        int.TryParse(PortBox.Text, out var port) ? port : -1,
        UserBox.Text?.Trim() ?? "",
        TrustBox.IsChecked == true,
        string.IsNullOrWhiteSpace(PinBox.Text) ? null : PinBox.Text.Trim());

    void SetPasswordProgrammatically(string value)
    {
        _settingPassword = true;
        try
        {
            PasswordBox.Text = value;
        }
        finally
        {
            _settingPassword = false;
        }
    }

    void OnEndpointTextChanged(object? sender, TextChangedEventArgs e) => OnEndpointEdited();

    void OnEndpointCheckChanged(object? sender, RoutedEventArgs e) => OnEndpointEdited();

    void OnEndpointEdited()
    {
        UpdateTlsSummary();
        if (_loadingProfile)
            return;
        if (_autofill.UserChangedEndpoint(CurrentEndpoint()))
        {
            SetPasswordProgrammatically(_autofill.PasswordText);
            ShowError(ProfileAutofillSession.ChangedEndpointMessage);
        }
    }

    void OnPasswordTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingProfile || _settingPassword)
            return;
        _autofill.UserTypedPassword(PasswordBox.Text);
    }

    void OnConnect(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_loadingProfile)
                return;
            if (!int.TryParse(PortBox.Text, out var port))
                throw new ArgumentException("Port must be an integer.");
            // Defence in depth: any box text the controller did not set itself is user input, never autofill.
            if (!string.Equals(PasswordBox.Text ?? "", _autofill.PasswordText, StringComparison.Ordinal))
                _autofill.UserTypedPassword(PasswordBox.Text);
            var credential = _autofill.ResolveForConnect(CurrentEndpoint());
            if (!credential.Allowed)
            {
                SetPasswordProgrammatically(_autofill.PasswordText);
                throw new ArgumentException(credential.Reason ?? ProfileAutofillSession.ChangedEndpointMessage);
            }
            var options = new ConnectionOptions
            {
                Server = ServerBox.Text?.Trim() ?? "",
                Port = port,
                Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? "master" : DatabaseBox.Text.Trim(),
                UserId = UserBox.Text?.Trim() ?? "",
                Password = credential.Password,
                Encrypt = true,
                TrustServerCertificate = TrustBox.IsChecked == true,
                ServerCertificatePath = string.IsNullOrWhiteSpace(PinBox.Text) ? null : PinBox.Text.Trim()
            };
            options.Validate();

            SaveProfile = SaveProfileBox.IsChecked == true;
            RememberPassword = SaveProfile && RememberBox.IsChecked == true;
            ProfileId = _selectedProfile?.Id ?? Guid.NewGuid();
            ProfileName = string.IsNullOrWhiteSpace(ProfileNameBox.Text)
                ? $"{options.UserId}@{options.DisplayTarget}/{options.Database}"
                : ProfileNameBox.Text.Trim();
            if (ProfileName.Length > 128)
                throw new ArgumentException("Profile name must be 128 characters or fewer.");
            ProfileGroup = SaveProfile && !string.IsNullOrWhiteSpace(GroupBox.Text) ? GroupBox.Text.Trim() : null;
            if (ProfileGroup is { Length: > 128 } || (ProfileGroup?.Any(char.IsControl) ?? false))
                throw new ArgumentException("Profile group must be 128 characters or fewer with no control characters.");

            Result = options;
            _autofill.Forget();
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
