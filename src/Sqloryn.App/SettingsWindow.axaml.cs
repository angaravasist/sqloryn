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
        ThemeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Value == settings.Theme));
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
                Theme = Themes[Math.Clamp(ThemeBox.SelectedIndex, 0, Themes.Length - 1)].Value
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
