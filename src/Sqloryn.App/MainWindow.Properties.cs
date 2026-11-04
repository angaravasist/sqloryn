using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

public partial class MainWindow
{
    /// <summary>Opens a read-only properties sheet for the selected object. Catalog reads do not write.</summary>
    async void OnObjectProperties(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } node || _active is null)
        {
            StatusBar.Text = "Select an object in Object Explorer to read its properties. Nothing is changed.";
            return;
        }
        var tab = _active;
        var binding = tab.CaptureBinding();
        StatusBar.Text = "Reading catalog properties…";
        string text;
        try
        {
            text = await _explorer.LoadPropertiesAsync(node, tab);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = "Properties cancelled. Nothing was changed.";
            return;
        }
        catch (Exception ex)
        {
            text = CatalogFailure.From(ex, "properties of " + (node.CatalogName ?? node.ObjectName ?? node.Title)).Text
                + "\nNothing was changed.";
        }
        if (!BindingStillCurrent(tab, binding) || !ReferenceEquals(SelectedNode, node))
        {
            StatusBar.Text = "Properties discarded because the selection changed. Nothing was changed.";
            return;
        }
        StatusBar.Text = "Properties are read-only. Nothing was changed.";
        await new ObjectPropertiesWindow(node.Title, text).ShowDialog(this);
    }
}
