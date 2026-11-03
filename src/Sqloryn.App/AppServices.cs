using Avalonia;
using Avalonia.Styling;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>Process-wide stores. Secrets use the macOS Keychain when available and are otherwise memory-only.</summary>
static class AppServices
{
    static readonly Lazy<ConnectionProfileStore> ProfilesLazy = new(() => new ConnectionProfileStore());
    static readonly Lazy<AppSettingsStore> SettingsLazy = new(() => new AppSettingsStore());
    static readonly Lazy<ISecretStore> SecretsLazy = new(() =>
        MacKeychainSecretStore.IsSupported ? new MacKeychainSecretStore() : new MemorySecretStore());

    public static ConnectionProfileStore Profiles => ProfilesLazy.Value;
    public static AppSettingsStore Settings => SettingsLazy.Value;
    public static ISecretStore Secrets => SecretsLazy.Value;
    public static bool SecretsArePersistent => MacKeychainSecretStore.IsSupported;

    public static void ApplyTheme(AppSettings settings)
    {
        if (Application.Current is null)
            return;
        Application.Current.RequestedThemeVariant = settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            // "System" keeps the studio default (dark slate) until the user picks otherwise.
            _ => ThemeVariant.Dark
        };
    }
}
