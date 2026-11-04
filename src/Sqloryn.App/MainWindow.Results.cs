using System.Globalization;
using System.Text;
using Avalonia.Automation;
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
                        shownNote.Append($"Result {i + 1}: displaying first {shown:N0} of {set.Rows.Count:N0} retained rows (Settings > Max rows displayed). ");
                    // "Retained" is what the engine kept (at most QueryLimits.MaxRowsPerSet per set); when it stopped
                    // early the server's total is unknown, so say so instead of presenting the count as complete.
                    var kept = set.Truncated || set.BudgetExceeded
                        ? $"first {set.Rows.Count:N0} rows kept, more exist"
                        : $"{set.Rows.Count:N0} row{(set.Rows.Count == 1 ? "" : "s")}";
                    ResultTabs.Items.Add(new TabItem
                    {
                        Header = $"Result {i + 1} ({kept})",
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
        SetResultsVisible(result is not null);
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

    /// <summary>
    /// One result set as a grid. Header clicks cycle ascending, descending and original order over the
    /// rows retained in memory (Core <see cref="ResultSetView.SortedRowIndexes"/>; NULLs last). The filter box
    /// calls <see cref="ResultSetView.FilterContains"/> on those same rows. Neither path re-queries the server,
    /// and export still writes the full retained result. A banner states the retained-row scope.
    /// </summary>
    static Control BuildResultGrid(ResultSet set, int shownRows)
    {
        ResultRow MakeRow(int index)
        {
            var row = set.Rows[index];
            var cells = new CellValue[set.Columns.Count];
            for (var c = 0; c < cells.Length; c++)
                cells[c] = c < row.Count ? row[c] : CellValue.Null;
            return new ResultRow(index + 1, cells);
        }

        List<ResultRow> Project(IEnumerable<int> order) => order.Take(shownRows).Select(MakeRow).ToList();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            HeadersVisibility = DataGridHeadersVisibility.All,
            ItemsSource = Project(Enumerable.Range(0, shownRows))
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

        var headerNames = new string[set.Columns.Count];
        var dataColumns = new DataGridTemplateColumn[set.Columns.Count];
        for (var i = 0; i < set.Columns.Count; i++)
        {
            var index = i;
            var name = set.Columns[i];
            headerNames[i] = string.IsNullOrEmpty(name) ? "(no column name)" : name;
            dataColumns[i] = new DataGridTemplateColumn
            {
                Header = headerNames[i],
                IsReadOnly = true,
                CanUserSort = true,
                MinWidth = 64,
                MaxWidth = 640,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                ClipboardContentBinding = new Binding($"Cells[{index}].Display"),
                CellTemplate = new FuncDataTemplate<ResultRow>((row, _) => BuildCell(row, index))
            };
            grid.Columns.Add(dataColumns[i]);
        }

        var banner = new TextBlock
        {
            IsVisible = false,
            Margin = new Avalonia.Thickness(8, 4),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        banner.Classes.Add("muted");

        ResultSetView? view = null;
        var sortColumn = -1;
        var descending = false;
        string? filterText = null;

        var filterBox = new TextBox
        {
            PlaceholderText = "Filter retained rows",
            Margin = new Avalonia.Thickness(8, 4, 8, 0)
        };
        AutomationProperties.SetName(filterBox, "Filter retained rows");
        ToolTip.SetTip(filterBox,
            "Narrows rows already kept in memory. Does not re-query the server. NULL cells do not match the text NULL. Export still writes the full retained result.");

        void ApplyProjection()
        {
            view ??= ResultSetView.FromResultSet(set);
            IReadOnlyList<int> sourceIndexes;
            ResultSetView scoped;
            if (string.IsNullOrEmpty(filterText))
            {
                sourceIndexes = Enumerable.Range(0, view.Rows.Count).ToList();
                scoped = view;
            }
            else
            {
                sourceIndexes = view.FilterRowIndexes(filterText, null);
                scoped = view.FilterContains(filterText);
            }

            var ordered = sourceIndexes;
            string? scope = string.IsNullOrEmpty(filterText) && sortColumn < 0 ? null : scoped.ScopeLabel;
            if (sortColumn >= 0)
            {
                var local = scoped.SortedRowIndexes(sortColumn, descending);
                ordered = local.Select(i => sourceIndexes[i]).ToList();
                scope = scoped.SortByColumn(sortColumn, descending).ScopeLabel ?? scope;
            }

            grid.ItemsSource = Project(ordered);
            for (var c = 0; c < dataColumns.Length; c++)
                dataColumns[c].Header = c == sortColumn ? headerNames[c] + (descending ? " ▼" : " ▲") : headerNames[c];

            if (string.IsNullOrEmpty(filterText) && sortColumn < 0)
            {
                banner.IsVisible = false;
                return;
            }

            var bits = new List<string>();
            if (!string.IsNullOrEmpty(filterText))
            {
                var visible = ShownFilter(filterText);
                if (sourceIndexes.Count == 0)
                    bits.Add($"No retained rows contain \"{visible}\". NULL cells are not matched as the text NULL.");
                else if (sourceIndexes.Count > shownRows)
                    bits.Add($"Filter matches {sourceIndexes.Count:N0} retained rows; displaying the first {shownRows:N0} (Settings > rows shown).");
                else
                    bits.Add($"Filter matches {sourceIndexes.Count:N0} retained row{(sourceIndexes.Count == 1 ? "" : "s")}.");
            }
            if (sortColumn >= 0)
                bits.Add($"Sorted by {headerNames[sortColumn]} ({(descending ? "descending" : "ascending")}, NULL last).");
            if (!string.IsNullOrEmpty(scope))
                bits.Add(scope.TrimEnd('.') + ".");
            bits.Add("Display only. Does not re-query the server. Export still writes the full retained result.");
            banner.Text = string.Join(" ", bits);
            banner.IsVisible = true;
        }

        grid.Sorting += (_, e) =>
        {
            e.Handled = true; // Core sorts retained rows; the DataGrid's own view sort is not used.
            var column = Array.IndexOf(dataColumns, e.Column as DataGridTemplateColumn);
            if (column < 0)
                return;
            if (sortColumn != column) { sortColumn = column; descending = false; }
            else if (!descending) descending = true;
            else sortColumn = -1;
            ApplyProjection();
        };
        filterBox.TextChanged += (_, _) =>
        {
            filterText = string.IsNullOrEmpty(filterBox.Text) ? null : filterBox.Text;
            ApplyProjection();
        };
        ApplyProjection();

        var host = new DockPanel();
        DockPanel.SetDock(filterBox, Dock.Top);
        DockPanel.SetDock(banner, Dock.Top);
        host.Children.Add(filterBox);
        host.Children.Add(banner);
        host.Children.Add(grid);
        return host;
    }

    static string ShownFilter(string text)
    {
        var one = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return one.Length <= 80 ? one : one[..80] + "…";
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
