using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Flat-file import wizard sheet. Choosing a file, changing options and generating the SQL preview only read the file
/// (and catalog metadata); nothing is written until the user confirms the previewed plan. All SQL comes from Core.
/// </summary>
public partial class ImportCsvWindow : Window
{
    sealed class MapRow
    {
        public int SourceIndex { get; init; }
        public bool Include { get; set; }
        public string Source { get; init; } = "";
        public string Guess { get; init; } = "";
        public string Target { get; set; } = "";
        public string TypeText { get; set; } = nameof(CsvSqlType.NVarChar);
        public bool IsKey { get; set; }
        public int? Length { get; init; }
        public int? Scale { get; init; }
    }

    readonly SqlSession _session;
    readonly string _database;
    readonly string _schema;
    List<MapRow> _rows = [];
    string? _path;
    CsvImportPreviewResult? _preview;
    CsvImportOptions? _previewOptions;
    CsvImportPlan? _previewedPlan;
    CsvImportSource? _previewedSource;
    bool _destinationBlocked;
    bool _running;
    int _generation;
    CancellationTokenSource? _cts;

    public ImportCsvWindow() : this(new SqlSession(), "master", "dbo", null)
    {
    }

    public ImportCsvWindow(SqlSession session, string database, string schema, string? table)
    {
        _session = session;
        _database = database;
        _schema = schema;
        InitializeComponent();
        DestinationPrefix.Text = SqlIdentity.Qualify(database, schema) + ".";
        TableBox.Text = table ?? "";
        ModeBox.SelectedIndex = string.IsNullOrEmpty(table) ? 1 : 0;
        MapGrid.ItemsSource = _rows;
        Closing += OnWindowClosing;
        StatusText.Text = "Choose a file to preview. Nothing is read from or written to the database until you map and confirm.";
    }

    /// <summary>True when an import committed, or its outcome could not be confirmed (the caller should refresh Object Explorer).</summary>
    public bool Applied { get; private set; }

    CsvImportMode Mode => ModeBox.SelectedIndex switch
    {
        1 => CsvImportMode.Create,
        2 => CsvImportMode.Replace,
        _ => CsvImportMode.Append
    };

    CsvImportOptions BuildOptions() => new()
    {
        Delimiter = DelimiterBox.SelectedIndex switch { 1 => ';', 2 => '\t', 3 => '|', _ => ',' },
        HasHeader = HeaderCheck.IsChecked == true,
        Encoding = EncodingBox.SelectedIndex switch
        {
            1 => Encoding.Unicode,
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.Latin1,
            4 => Encoding.ASCII,
            _ => null
        }
    };

    // ---- File + preview (read-only) --------------------------------------------------------

    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a delimited text file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Delimited text") { Patterns = ["*.csv", "*.tsv", "*.txt", "*.psv"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            StatusText.Text = "Only files on this Mac can be imported.";
            return;
        }
        _path = path;
        PathBox.Text = path;
        ReloadButton.IsEnabled = true;
        await LoadPreviewAsync();
    }

    async void OnReloadPreview(object? sender, RoutedEventArgs e) => await LoadPreviewAsync();

    async Task LoadPreviewAsync()
    {
        if (_path is null || _running)
            return;
        var path = _path;
        var options = BuildOptions();
        var generation = ++_generation;
        Invalidate();
        StatusText.Text = "Reading preview…";
        IssuesText.IsVisible = false;
        try
        {
            var preview = await Task.Run(() => CsvImportPreview.Parse(path, options));
            if (generation != _generation)
                return;
            _preview = preview;
            _previewOptions = options;
            ShowPreview(preview);
            ShowIssues(preview.Issues);
            MapButton.IsEnabled = true;
            await BuildMappingAsync();
            if (generation != _generation)
                return;
            PreviewSqlButton.IsEnabled = _preview is { HasErrors: false } && _rows.Count > 0;
            StatusText.Text = preview.HasErrors
                ? "The file has errors (see above). Fix the file or the options; nothing can be imported yet."
                : $"Preview: {preview.Columns.Count} column(s), {preview.RowsScanned:N0}{(preview.ScanTruncated ? "+" : "")} row(s) scanned. Review the mapping, then Preview SQL.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation != _generation)
                return;
            _preview = null;
            PreviewSqlButton.IsEnabled = false;
            PreviewGrid.Columns.Clear();
            PreviewGrid.ItemsSource = null;
            StatusText.Text = "Could not read the file: " + Safe(ex.Message);
        }
    }

    void ShowPreview(CsvImportPreviewResult preview)
    {
        PreviewGrid.Columns.Clear();
        for (var i = 0; i < preview.Columns.Count; i++)
        {
            var c = preview.Columns[i];
            PreviewGrid.Columns.Add(new DataGridTextColumn
            {
                Header = c.Name,
                Binding = new Binding($"[{i}]"),
                IsReadOnly = true,
                MinWidth = 60,
                MaxWidth = 360
            });
        }
        PreviewGrid.ItemsSource = preview.SampleRows.Select(r => r.Select(Display).ToArray()).ToList();
    }

    static string Display(string cell)
    {
        var text = cell.Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\n', '⏎').Replace('\r', '⏎').Replace('\t', '→');
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    void ShowIssues(IReadOnlyList<CsvImportIssue> issues)
    {
        if (issues.Count == 0)
        {
            IssuesText.IsVisible = false;
            return;
        }
        var lines = issues.Take(8).Select(i =>
            $"{(i.Severity == CsvIssueSeverity.Error ? "Error" : "Warning")}{(i.Line is { } l ? $" (line {l})" : "")}: {Safe(i.Message)}");
        var text = string.Join("\n", lines);
        if (issues.Count > 8)
            text += $"\n… and {issues.Count - 8} more.";
        IssuesText.Text = text;
        IssuesText.IsVisible = true;
    }

    // ---- Destination + mapping (catalog reads only) ------------------------------------------

    void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _preview is null)
            return;
        _ = RemapAsync();
    }

    void OnDestinationChanged(object? sender, RoutedEventArgs e)
    {
        if (_preview is null || _running)
            return;
        _ = RemapAsync();
    }

    void OnMapColumns(object? sender, RoutedEventArgs e) => _ = RemapAsync();

    async Task RemapAsync()
    {
        if (_running || _preview is null)
            return;
        var generation = _generation;
        Invalidate();
        await BuildMappingAsync();
        if (generation != _generation)
            return;
        PreviewSqlButton.IsEnabled = _preview is { HasErrors: false } && _rows.Count > 0 && !_destinationBlocked;
    }

    async Task BuildMappingAsync()
    {
        var preview = _preview;
        if (preview is null)
            return;
        var table = (TableBox.Text ?? "").Trim();
        _destinationBlocked = false;
        var rows = new List<MapRow>();
        var notes = new List<string>();

        IReadOnlyList<CatalogColumn> existing = [];
        IReadOnlyList<string> key = [];
        var generation = _generation;
        if (table.Length > 0 && _session.IsConnected)
        {
            try
            {
                existing = await CatalogClient.ListColumnsAsync(_session, _database, _schema, table);
                if (existing.Count > 0 && Mode != CsvImportMode.Create)
                    key = await CatalogClient.ListPrimaryKeyAsync(_session, _database, _schema, table);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not read the destination table: " + Safe(ex.Message);
                _destinationBlocked = true;
            }
        }
        if (generation != _generation)
            return;

        if (table.Length == 0)
        {
            _destinationBlocked = true;
            notes.Add("Enter a destination table name.");
        }
        else if (Mode == CsvImportMode.Create && existing.Count > 0)
        {
            _destinationBlocked = true;
            notes.Add($"{SqlIdentity.Qualify(_schema, table)} already exists. Choose another name, or Append / Replace.");
        }
        else if (Mode != CsvImportMode.Create && existing.Count == 0 && !_destinationBlocked)
        {
            _destinationBlocked = true;
            notes.Add($"{SqlIdentity.Qualify(_schema, table)} was not found or has no visible columns. Choose Create to make it.");
        }

        var keySet = new HashSet<string>(key, StringComparer.OrdinalIgnoreCase);
        foreach (var c in preview.Columns)
        {
            if (Mode == CsvImportMode.Create)
            {
                rows.Add(new MapRow
                {
                    SourceIndex = c.Index,
                    Include = true,
                    Source = c.Name,
                    Guess = c.GuessedType.ToString(),
                    Target = c.Name,
                    TypeText = c.GuessedType.ToString(),
                    Length = c.GuessedType == CsvSqlType.NVarChar ? CsvImportPlan.SuggestTextLength(c.MaxLength, preview.ScanTruncated) : null,
                    Scale = c.GuessedType == CsvSqlType.Decimal ? c.MaxScale : null
                });
                continue;
            }

            var match = existing.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            var usable = match is { IsIdentity: false, IsComputed: false };
            if (match is { IsIdentity: true } or { IsComputed: true })
                notes.Add($"'{match.Name}' is an identity or computed column and was left unmapped.");
            rows.Add(new MapRow
            {
                SourceIndex = c.Index,
                Include = usable,
                Source = c.Name,
                Guess = c.GuessedType.ToString(),
                Target = usable ? match!.Name : "",
                TypeText = usable ? CsvSqlTypes.FromSqlTypeName(match!.TypeName).ToString() : c.GuessedType.ToString(),
                IsKey = usable && keySet.Contains(match!.Name)
            });
        }

        _rows = rows;
        MapGrid.ItemsSource = _rows;
        if (notes.Count > 0 && !preview.HasErrors)
            StatusText.Text = string.Join(" ", notes.Distinct().Take(3));
    }

    void OnMappingEdited(object? sender, DataGridCellEditEndedEventArgs e) => Invalidate();

    /// <summary>Any change after the SQL preview makes it stale: the user must preview again before confirming.</summary>
    void Invalidate()
    {
        _previewedPlan = null;
        _previewedSource = null;
        ConfirmButton.IsEnabled = false;
        SqlBox.Text = "";
    }

    // ---- SQL preview (no database access) ---------------------------------------------------

    CsvImportPlan BuildPlan()
    {
        var table = (TableBox.Text ?? "").Trim();
        var mappings = new List<CsvColumnMapping>();
        var keys = new List<string>();
        foreach (var r in _rows.Where(r => r.Include))
        {
            if (!Enum.TryParse<CsvSqlType>(r.TypeText?.Trim(), ignoreCase: true, out var type) || !Enum.IsDefined(type))
                throw new ArgumentException($"'{r.TypeText}' is not a supported type for '{r.Source}'. Use Int, BigInt, Decimal, Bit, DateTime2, UniqueIdentifier or NVarChar.");
            var target = (r.Target ?? "").Trim();
            mappings.Add(new CsvColumnMapping(r.SourceIndex, target, type,
                type == CsvSqlType.NVarChar ? r.Length : null,
                type == CsvSqlType.Decimal ? r.Scale ?? 10 : null));
            if (r.IsKey)
                keys.Add(target);
        }
        return new CsvImportPlan(
            new TableRef(_database, _schema, table), Mode, mappings, keys, EmptyNullCheck.IsChecked == true);
    }

    void OnPreviewSql(object? sender, RoutedEventArgs e)
    {
        if (_preview is null || _path is null || _previewOptions is null)
            return;
        MapGrid.CommitEdit();
        Invalidate();
        if (_preview.HasErrors)
        {
            StatusText.Text = "The file has errors; fix them before previewing the import.";
            return;
        }
        if (_destinationBlocked)
        {
            StatusText.Text = "The destination is not usable yet. Check the table name and mode, then Map columns.";
            return;
        }

        try
        {
            var plan = BuildPlan();
            var errors = plan.Validate(_preview.Columns.Count).Where(i => i.Severity == CsvIssueSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                ShowIssues(errors);
                StatusText.Text = "The mapping has problems (see above).";
                return;
            }

            ShowIssues(_preview.Issues);
            SqlBox.Text = plan.GeneratePreviewSql();
            _previewedPlan = plan;
            _previewedSource = CsvImportSource.FromFile(_path, _previewOptions);
            ConfirmButton.IsEnabled = true;
            StatusText.Text = plan.IsDestructive
                ? "SQL preview ready. This mode DELETES every existing row first. Nothing has been executed."
                : "SQL preview ready. Nothing has been executed.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = "Preview failed: " + Safe(ex.Message);
        }
    }

    // ---- Confirm + apply (the only write path) -------------------------------------------------

    async void OnConfirmImport(object? sender, RoutedEventArgs e)
    {
        if (_running || _previewedPlan is not { } plan || _previewedSource is not { } source || _preview is null)
            return;

        var rows = _preview.ScanTruncated
            ? $"more than {_preview.RowsScanned:N0} rows (the whole file is validated first)"
            : $"{_preview.RowsScanned:N0} row(s)";
        var summary = plan.Mode switch
        {
            CsvImportMode.Create => $"Creates {plan.Target.Qualified} and inserts {rows} from {Path.GetFileName(_path)}.",
            CsvImportMode.Replace => $"DELETES every existing row in {plan.Target.Qualified}, then inserts {rows} from {Path.GetFileName(_path)}.",
            _ => $"Appends {rows} from {Path.GetFileName(_path)} to {plan.Target.Qualified}."
        };
        summary += "\n\nThe whole file is checked first. Everything then runs in ONE transaction: any error, duplicate key or cancel rolls the import back and leaves the destination unchanged.";
        var confirmed = await Dialogs.ConfirmAsync(
            this, plan.IsDestructive ? "Confirm replace import" : "Confirm import", summary, plan.GeneratePreviewSql(),
            plan.IsDestructive ? "Delete and import" : "Import", plan.IsDestructive);
        if (!confirmed)
        {
            StatusText.Text = "Import cancelled. Nothing was written.";
            return;
        }
        if (!ReferenceEquals(plan, _previewedPlan))
        {
            StatusText.Text = "The plan changed while confirming. Preview the SQL again.";
            return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<long>(n => StatusText.Text = $"Sent {n:N0} row(s); nothing is final until the import commits…");
        StatusText.Text = "Checking the whole file, then importing…";
        IssuesText.IsVisible = false;
        try
        {
            var result = await _session.ApplyCsvImportAsync(plan, source, progress, _cts.Token);
            switch (result.Outcome)
            {
                case CsvImportOutcome.Committed:
                    Applied = true;
                    StatusText.Text = result.Message;
                    Invalidate();
                    break;
                case CsvImportOutcome.OutcomeUnknown:
                    Applied = true;
                    StatusText.Text = result.Message;
                    Invalidate();
                    break;
                default:
                    StatusText.Text = result.Message;
                    ShowIssues(result.Issues);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            StatusText.Text = "Import did not run: " + Safe(ex.Message) + " Nothing was written.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    void OnCancelImport(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling… the transaction will be rolled back.";
    }

    void SetRunning(bool running)
    {
        _running = running;
        ImportProgress.IsVisible = running;
        CancelImportButton.IsVisible = running;
        BrowseButton.IsEnabled = !running;
        ReloadButton.IsEnabled = !running && _path is not null;
        MapButton.IsEnabled = !running && _preview is not null;
        PreviewSqlButton.IsEnabled = !running && _preview is { HasErrors: false } && _rows.Count > 0 && !_destinationBlocked;
        ConfirmButton.IsEnabled = !running && _previewedPlan is not null;
        CloseButton.IsEnabled = !running;
        MapGrid.IsEnabled = !running;
        ModeBox.IsEnabled = !running;
        TableBox.IsEnabled = !running;
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_running)
            return;
        e.Cancel = true;
        _cts?.Cancel();
        StatusText.Text = "Cancelling the import before closing…";
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    static string Safe(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
