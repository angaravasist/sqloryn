using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

public partial class SettingsWindow : Window
{
    static readonly (string Value, string Label)[] Themes =
    [
        ("System", "Follow system appearance"),
        ("Dark", "Dark"),
        ("Light", "Light")
    ];

    /// <summary>Settings that were saved, or null when the dialog was cancelled.</summary>
    public AppSettings? Saved { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        PathText.Text = AppServices.Settings.FilePath;
        ThemeBox.ItemsSource = Themes.Select(t => t.Label).ToList();
        var settings = AppServices.Settings.Load();
        FontSizeBox.Value = (decimal)settings.EditorFontSize;
        MaxRowsBox.Maximum = AppSettings.MaxResultsMaxRows;
        MaxRowsBox.Value = settings.ResultsMaxRows;
        RetentionText.Text = $"SQLoryn retains at most {AppSettings.MaxResultsMaxRows:N0} rows per result set, so this value cannot exceed that. " +
                             "It limits how many retained rows the grid draws. When a set is cut off its tab says so: " +
                             "the server's total row count is not known, and exports contain only retained rows.";
        ThemeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Value == settings.Theme));
        ConnectTimeoutBox.Value = settings.ConnectTimeoutSeconds;
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Start from the stored settings so layout preferences are preserved.
            var settings = (AppServices.Settings.Load() with
            {
                EditorFontSize = (double)(FontSizeBox.Value ?? 13m),
                ResultsMaxRows = (int)(MaxRowsBox.Value ?? AppSettings.MaxResultsMaxRows),
                ConfirmDestructive = true,
                Theme = Themes[Math.Clamp(ThemeBox.SelectedIndex, 0, Themes.Length - 1)].Value,
                ConnectTimeoutSeconds = (int)(ConnectTimeoutBox.Value ?? AppSettings.DefaultConnectTimeoutSeconds)
            }).Normalize();
            AppServices.Settings.Save(settings);
            Saved = settings;
            Close(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Could not save settings: " + ex.Message;
            ErrorText.IsVisible = true;
        }
    }
}
