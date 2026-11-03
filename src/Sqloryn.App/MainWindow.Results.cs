using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Sqloryn.Core;

namespace Sqloryn;

public partial class MainWindow
{
    sealed record ResultRow(int Number, CellValue[] Cells);

    bool _renderingResults;

    /// <summary>Rebuilds the results area: one DataGrid tab per result set plus a Messages tab.</summary>
    void RenderResult(QueryResult? result)
    {
        _renderingResults = true;
        try
        {
            ResultTabs.Items.Clear();
            var maxRows = Math.Max(1, _settings.ResultsMaxRows);
            var shownNote = new StringBuilder();

            if (result is null)
            {
                MessagesBox.Text = "";
                ResultSummary.Text = "";
            }
            else
            {
                for (var i = 0; i < result.ResultSets.Count; i++)
                {
                    var set = result.ResultSets[i];
                    var shown = Math.Min(set.Rows.Count, maxRows);
                    if (shown < set.Rows.Count)
                        shownNote.Append($"Result {i + 1}: showing first {shown:N0} of {set.Rows.Count:N0} rows. ");
                    var suffix = set.Truncated || set.BudgetExceeded ? ", truncated" : "";
                    ResultTabs.Items.Add(new TabItem
                    {
                        Header = $"Result {i + 1} ({set.Rows.Count:N0} row{(set.Rows.Count == 1 ? "" : "s")}{suffix})",
                        Tag = i,
                        Content = BuildResultGrid(set, shown)
                    });
                }
                MessagesBox.Text = QueryOutcomeFormatter.FormatMessages(result);
                ResultSummary.Text = $"{QueryOutcomeFormatter.DescribeStatus(result)} · {result.ResultSets.Count} result set(s)" +
                                     (result.PartialResults || result.RetentionLimitReached ? " · output truncated" : "") +
                                     (shownNote.Length > 0 ? " · " + shownNote.ToString().TrimEnd() : "");
            }

            var messagesTab = new TabItem { Header = "Messages", Tag = -1, Content = MessagesBox };
            ResultTabs.Items.Add(messagesTab);

            var preferred = _active?.LastResultSetIndex ?? -1;
            var setCount = result?.ResultSets.Count ?? 0;
            // Failed or empty runs land on Messages so the outcome is the first thing visible.
            var showMessages = result is null || setCount == 0 || result.Completion != QueryCompletionKind.Succeeded
                               || preferred < 0 || preferred >= setCount;
            if (!showMessages && result is not null && result.Completion == QueryCompletionKind.Succeeded)
                ResultTabs.SelectedItem = ResultTabs.Items.OfType<TabItem>().First(t => t.Tag is int n && n == preferred);
            else
                ResultTabs.SelectedItem = messagesTab;
        }
        finally
        {
            _renderingResults = false;
        }
        if (_active is not null)
            UpdateCommandState(_active);
    }

    void ShowMessagesTab()
    {
        if (ResultTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag is -1) is { } tab)
            ResultTabs.SelectedItem = tab;
    }

    void OnResultTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_renderingResults || _active is null)
            return;
        if (ResultTabs.SelectedItem is TabItem { Tag: int index })
            _active.LastResultSetIndex = index;
    }

    static Control BuildResultGrid(ResultSet set, int shownRows)
    {
        var rows = new List<ResultRow>(shownRows);
        for (var r = 0; r < shownRows; r++)
        {
            var row = set.Rows[r];
            var cells = new CellValue[set.Columns.Count];
            for (var c = 0; c < cells.Length; c++)
                cells[c] = c < row.Count ? row[c] : CellValue.Null;
            rows.Add(new ResultRow(r + 1, cells));
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            HeadersVisibility = DataGridHeadersVisibility.All,
            ItemsSource = rows
        };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "#",
            IsReadOnly = true,
            CanUserSort = false,
            Width = new DataGridLength(56),
            CellTemplate = new FuncDataTemplate<ResultRow>((row, _) =>
            {
                var tb = new TextBlock
                {
                    Text = row?.Number.ToString(CultureInfo.InvariantCulture) ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(8, 0)
                };
                tb.Classes.Add("muted");
                return tb;
            })
        });

        for (var i = 0; i < set.Columns.Count; i++)
        {
            var index = i;
            var name = set.Columns[i];
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = string.IsNullOrEmpty(name) ? "(no column name)" : name,
                IsReadOnly = true,
                CanUserSort = false,
                MinWidth = 64,
                MaxWidth = 640,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                ClipboardContentBinding = new Binding($"Cells[{index}].Display"),
                CellTemplate = new FuncDataTemplate<ResultRow>((row, _) => BuildCell(row, index))
            });
        }
        return grid;
    }

    static Control BuildCell(ResultRow? row, int index)
    {
        var cell = row is not null && index < row.Cells.Length ? row.Cells[index] : CellValue.Null;
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(8, 0),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        if (cell.IsNull)
        {
            // NULL is distinct from the empty string and from the text "NULL".
            text.Text = "NULL";
            text.Classes.Add("null");
        }
        else
        {
            text.Text = cell.Display.Length == 0 ? "" : cell.Display.Replace("\r\n", " ⏎ ").Replace('\n', '⏎').Replace('\r', '⏎');
            if (cell.Display.Length == 0)
                ToolTip.SetTip(text, "empty string");
            else if (cell.Truncated)
                ToolTip.SetTip(text, "Value truncated for display");
        }
        return text;
    }

    // ---- Export ---------------------------------------------------------------------------

    void OnExportCsv(object? sender, RoutedEventArgs e) => _ = ExportAsync(ResultExportFormat.Csv, spreadsheetSafe: false);
    void OnExportCsvSafe(object? sender, RoutedEventArgs e) => _ = ExportAsync(ResultExportFormat.Csv, spreadsheetSafe: true);
    void OnExportJson(object? sender, RoutedEventArgs e) => _ = ExportAsync(ResultExportFormat.Json, spreadsheetSafe: false);

    CancellationTokenSource? _exportCts;

    /// <summary>Result-set indexes to export: the selected result tab, or every result set when Messages is selected.</summary>
    List<int> ExportTargets(QueryResult result)
    {
        if (ResultTabs.SelectedItem is TabItem { Tag: int index and >= 0 } && index < result.ResultSets.Count)
            return [index];
        return Enumerable.Range(0, result.ResultSets.Count).ToList();
    }

    /// <summary>
    /// One export = one user-chosen file. No sibling names are derived, so no other file can be overwritten.
    /// CSV holds a single result set (select its tab); JSON holds all sets for the scope in one document.
    /// The write is staged beside the destination and replaced only if the active result is still the one exported.
    /// </summary>
    async Task ExportAsync(ResultExportFormat format, bool spreadsheetSafe)
    {
        if (_active?.LastResult is not { } result || result.ResultSets.Count == 0)
        {
            StatusBar.Text = "Nothing to export: run a query that returns a result set first.";
            return;
        }
        var tab = _active;
        var targets = ExportTargets(result);
        var json = format == ResultExportFormat.Json;
        if (!json && targets.Count != 1)
        {
            StatusBar.Text = "CSV holds one result set per file: select a result tab, then export again (or export JSON for all sets).";
            return;
        }

        var extension = json ? "json" : "csv";
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _exportCts, cts);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { /* previous export already finished */ }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = json ? "Export results as JSON" : "Export results as CSV",
                SuggestedFileName = "results." + extension,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    json
                        ? new FilePickerFileType("JSON") { Patterns = ["*.json"] }
                        : new FilePickerFileType("CSV") { Patterns = ["*.csv"] }
                ]
            });
            var path = file?.TryGetLocalPath();
            if (path is null)
                return;

            bool Stale() => !ReferenceEquals(_active, tab) || !ReferenceEquals(tab.LastResult, result);
            if (Stale())
            {
                StatusBar.Text = "Export cancelled: the active result changed while choosing a file.";
                return;
            }

            var views = targets.Select(i => ResultSetView.FromResultSet(result.ResultSets[i])).ToList();
            var outcome = await ResultExportWriter.ExportAsync(
                path,
                format,
                views,
                new ResultExportOptions { SpreadsheetSafeCsv = spreadsheetSafe },
                cts.Token,
                beforeCommit: ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (Stale())
                        throw new OperationCanceledException("The active result changed before the file was replaced.");
                    return Task.CompletedTask;
                });

            StatusBar.Text = $"Exported {outcome.ResultSetCount} result set(s) to {outcome.Path}" +
                             (outcome.Incomplete ? " — WARNING: retained rows were truncated, so the export is incomplete." : "") +
                             (spreadsheetSafe ? " (spreadsheet-safe: formula-like text cells were prefixed with ')" : "") +
                             (json ? " (JSON: columns by ordinal with SQL types; values are strings except NULL, int and bit.)" : "");
        }
        catch (OperationCanceledException)
        {
            StatusBar.Text = "Export cancelled: no file was changed.";
        }
        catch (Exception ex)
        {
            ReportError("Export failed (existing files were left unchanged): " + ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _exportCts, null, cts);
            cts.Dispose();
        }
    }
}
