using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

public partial class SettingsWindow : Window
{
    static readonly (string Value, string Label)[] Themes =
    [
        ("System", "Studio default (dark slate)"),
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
        LineNumbersBox.IsChecked = settings.ShowLineNumbers;
        MaxRowsBox.Value = settings.ResultsMaxRows;
        ThemeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Value == settings.Theme));
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new AppSettings
            {
                EditorFontSize = (double)(FontSizeBox.Value ?? 13m),
                ShowLineNumbers = LineNumbersBox.IsChecked == true,
                ResultsMaxRows = (int)(MaxRowsBox.Value ?? 10_000m),
                ConfirmDestructive = true,
                Theme = Themes[Math.Clamp(ThemeBox.SelectedIndex, 0, Themes.Length - 1)].Value
            }.Normalize();
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
