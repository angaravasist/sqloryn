using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Sqloryn;

public partial class DiagnosticsWindow : Window
{
    string _bundle = "";

    public DiagnosticsWindow()
    {
        InitializeComponent();
    }

    /// <param name="redactedBundle">Output of DiagnosticsRedactor.BuildSupportBundle; shown and saved verbatim.</param>
    public DiagnosticsWindow(string redactedBundle) : this()
    {
        _bundle = redactedBundle;
        BundleBox.Text = redactedBundle;
    }

    async void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save diagnostics bundle",
                SuggestedFileName = $"sqloryn-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }]
            });
            var path = file?.TryGetLocalPath();
            if (path is null)
                return;
            await File.WriteAllTextAsync(path, _bundle, new UTF8Encoding(false));
            StatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard unavailable.";
            return;
        }
        await clipboard.SetTextAsync(_bundle);
        StatusText.Text = "Copied.";
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
