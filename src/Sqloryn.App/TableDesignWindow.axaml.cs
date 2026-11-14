using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;
using ColumnDefinition = Sqloryn.Core.ColumnDefinition;

namespace Sqloryn;

public partial class TableDesignWindow : Window
{
    static readonly HashSet<string> LengthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "binary", "varbinary"
    };

    sealed class DesignRow
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "int";
        public string MaxLengthText { get; set; } = "";
        public bool IsNullable { get; set; } = true;
        public bool IsIdentity { get; set; }
        public bool IsPrimaryKey { get; set; }
    }

    readonly SqlSession _session;
    TableRef _table;
    bool _isNew;
    readonly ObservableCollection<DesignRow> _rows = [];
    TableDesignDraft? _existing;
    string? _previewedSql;
    // Catalog fingerprint captured when the design was loaded; Apply refuses if the server's table no longer matches it.
    string? _baselineFingerprint;
    bool _busy;

    public TableDesignWindow() : this(new SqlSession(), new TableRef("master", "dbo", "table"))
    {
    }

    public TableDesignWindow(SqlSession session, TableRef table)
    {
        _session = session;
        _table = table;
        InitializeComponent();
        TitleText.Text = "Design " + table.Qualified;
        ColumnGrid.ItemsSource = _rows;
        ApplyMode();
        Opened += async (_, _) => await LoadAsync();
    }

    /// <summary>New-table mode: starts from an empty draft in <paramref name="database"/>.<paramref name="schema"/>. Nothing is created until a previewed DDL is confirmed.</summary>
    public TableDesignWindow(SqlSession session, string database, string schema)
    {
        _session = session;
        _isNew = true;
        _table = new TableRef(database, schema, "");
        InitializeComponent();
        ColumnGrid.ItemsSource = _rows;
        _rows.Add(new DesignRow { Name = "Id", DataType = "int", IsNullable = false, IsIdentity = true, IsPrimaryKey = true });
        _existing = null;
        _baselineFingerprint = TableDesignFingerprint.Absent;
        ApplyMode();
        SetPreview(null, null);
        StatusText.Text = "New table. Name it, define columns, tick Primary key for the key columns, then Preview DDL.";
    }

    void ApplyMode()
    {
        TableNamePanel.IsVisible = _isNew;
        // The key column is the last grid column; the key of an existing table is shown, not edited (no ALTER is generated).
        ColumnGrid.Columns[^1].IsReadOnly = !_isNew;
        ReloadButton.IsVisible = !_isNew;
        ApplyButton.Content = _isNew ? "Confirm Create…" : "Confirm Apply…";
        TitleText.Text = _isNew
            ? $"New table in {SqlIdentity.QuoteIdentifier(_table.Database)}.{SqlIdentity.QuoteIdentifier(_table.Schema)}"
            : "Design " + _table.Qualified;
    }

    /// <summary>True when DDL was applied at least once (the caller may want to refresh Object Explorer).</summary>
    public bool Applied { get; private set; }

    async Task LoadAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            StatusText.Text = "Loading columns…";
            // Fingerprint first: if the catalog changes while loading, the baseline is older than the columns and Apply refuses (safe side).
            var fingerprint = await _session.ReadTableFingerprintAsync(_table);
            var columns = await CatalogClient.ListColumnsAsync(_session, _table.Database, _table.Schema, _table.Name);
            var keyColumns = await CatalogClient.ListPrimaryKeyAsync(_session, _table.Database, _table.Schema, _table.Name);
            var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
            _rows.Clear();
            foreach (var c in columns)
            {
                var isLength = LengthTypes.Contains(c.TypeName);
                _rows.Add(new DesignRow
                {
                    Name = c.Name,
                    DataType = DataTypeText(c),
                    MaxLengthText = isLength ? c.MaxLength.ToString(CultureInfo.InvariantCulture) : "",
                    IsNullable = c.IsNullable,
                    IsIdentity = c.IsIdentity,
                    IsPrimaryKey = keySet.Contains(c.Name)
                });
            }
            _existing = new TableDesignDraft
            {
                Database = _table.Database,
                Schema = _table.Schema,
                TableName = _table.Name,
                Columns = _rows.Select(ToDefinition).ToList(),
                PrimaryKeyColumns = keyColumns
            };
            _baselineFingerprint = fingerprint;
            SetPreview(null, null);
            StatusText.Text = columns.Count == 0
                ? "No columns were returned. The object may not be a table you can see."
                : $"Loaded {columns.Count} column(s) from the server.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + Safe(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    static string DataTypeText(CatalogColumn c) => c.TypeName.ToLowerInvariant() switch
    {
        "decimal" or "numeric" => $"{c.TypeName}({c.Precision},{c.Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{c.TypeName}({c.Scale})",
        _ => c.TypeName
    };

    static ColumnDefinition ToDefinition(DesignRow row)
    {
        int? maxLength = null;
        var text = row.MaxLengthText.Trim();
        if (text.Length > 0)
        {
            if (text.Equals("max", StringComparison.OrdinalIgnoreCase))
                maxLength = -1;
            else if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                maxLength = n;
            else
                throw new ArgumentException($"Length for column '{row.Name}' must be a number, -1 or MAX.");
        }
        return new ColumnDefinition
        {
            Name = row.Name.Trim(),
            DataType = row.DataType.Trim(),
            IsNullable = row.IsNullable,
            IsIdentity = row.IsIdentity,
            MaxLength = maxLength
        };
    }

    TableDesignDraft BuildDesired()
    {
        // Key columns are forced NOT NULL; the generated DDL shown in the preview says so explicitly.
        var definitions = _rows
            .Select(r => ToDefinition(r) with { IsNullable = r.IsNullable && !r.IsPrimaryKey })
            .ToList();
        return new TableDesignDraft
        {
            Database = _table.Database,
            Schema = _table.Schema,
            TableName = _isNew ? (TableNameBox.Text ?? "").Trim() : _table.Name,
            Columns = definitions,
            PrimaryKeyColumns = _rows.Where(r => r.IsPrimaryKey).Select(r => r.Name.Trim()).ToList()
        };
    }

    /// <summary>Builds the preview for the current mode. Throws ArgumentException/NotSupportedException for invalid drafts.</summary>
    (TableDesignPreview Preview, TableDesignDraft Desired, bool Destructive) BuildPreview()
    {
        var desired = BuildDesired();
        if (_isNew)
            return (TableDesignSql.PreviewCreate(desired), desired, false);
        return (TableDesignSql.PreviewAlter(_existing!, desired), desired, TableDesignSql.HasDestructiveChanges(_existing!, desired));
    }

    // Only TableDesignPreview.ExecutableSql is ever stored for Apply. Warnings are display-only
    // and are rendered in WarningsText, never inside the SQL text.
    void SetPreview(TableDesignPreview? preview, string? message)
    {
        _previewedSql = preview is { HasChanges: true } ? preview.ExecutableSql : null;
        PreviewBox.Text = preview is null
            ? message ?? ""
            : preview.HasChanges ? preview.ExecutableSql : "-- No changes.";
        WarningsText.Text = preview is { Warnings.Count: > 0 }
            ? string.Join("\n", preview.Warnings.Select(w => "• " + w))
            : "";
        WarningsText.IsVisible = WarningsText.Text.Length > 0;
        ApplyButton.IsEnabled = _previewedSql is not null;
    }

    void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (!_isNew && _existing is null)
        {
            StatusText.Text = "Columns are not loaded yet.";
            return;
        }
        try
        {
            ColumnGrid.CommitEdit();
            var (preview, _, destructive) = BuildPreview();
            SetPreview(preview, null);
            StatusText.Text = destructive
                ? "Preview contains DESTRUCTIVE changes (dropped columns or altered types). Review carefully."
                : "Preview ready. Nothing has been executed.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SetPreview(null, "-- Preview failed: " + Safe(ex.Message));
            StatusText.Text = "Preview failed: " + Safe(ex.Message);
        }
    }

    async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_busy || (!_isNew && _existing is null) || _previewedSql is null)
            return;
        _busy = true;
        try
        {
            ColumnGrid.CommitEdit();
            // The draft may have been edited after the preview; require a fresh preview in that case.
            TableDesignPreview currentPreview;
            TableDesignDraft currentDesired;
            bool destructive;
            try
            {
                (currentPreview, currentDesired, destructive) = BuildPreview();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                StatusText.Text = "Draft is invalid: " + Safe(ex.Message);
                return;
            }
            var current = currentPreview.ExecutableSql;
            if (!currentPreview.HasChanges || !string.Equals(current, _previewedSql, StringComparison.Ordinal))
            {
                SetPreview(currentPreview, null);
                StatusText.Text = "The draft changed after the previous preview. Review the refreshed DDL, then confirm again.";
                return;
            }

            if (_baselineFingerprint is null)
            {
                StatusText.Text = "The table definition was not loaded; reload before applying.";
                return;
            }
            var summary = _isNew
                ? "This statement creates a new table in one transaction; if it fails nothing is created."
                : destructive
                ? "This change can permanently remove data or alter column types. All statements run in ONE transaction: if any fails, every change is rolled back. The table definition is re-checked first and the change is refused if someone else altered the table since it was loaded."
                : "All statements run in ONE transaction: if any fails, every change is rolled back. The table definition is re-checked first and the change is refused if someone else altered the table since it was loaded.";
            if (currentPreview.Warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", currentPreview.Warnings.Select(w => "• " + w));
            var target = _isNew
                ? SqlIdentity.Qualify3(currentDesired.Database, currentDesired.Schema, currentDesired.TableName)
                : _table.Qualified;
            var confirmed = await Dialogs.ConfirmAsync(
                this, _isNew ? "Confirm create table" : "Confirm schema change", summary + "\n\nTarget: " + target, current,
                _isNew ? "Create on server" : "Apply to server", destructive);
            if (!confirmed)
            {
                StatusText.Text = "Apply cancelled. Nothing was executed.";
                return;
            }

            StatusText.Text = "Applying…";
            var applyTable = _isNew ? new TableRef(currentDesired.Database, currentDesired.Schema, currentDesired.TableName) : _table;
            var result = await _session.ApplyTableDesignAsync(applyTable, currentPreview.Statements, _baselineFingerprint);
            switch (result.Outcome)
            {
                case TableDesignApplyOutcome.Committed:
                    Applied = true;
                    if (_isNew)
                    {
                        // From now on this window edits the table that now exists on the server.
                        _table = applyTable;
                        _isNew = false;
                        ApplyMode();
                    }
                    StatusText.Text = "Applied. Reloading columns from the server…";
                    _busy = false;
                    await LoadAsync();
                    StatusText.Text = "Applied successfully. " + StatusText.Text;
                    break;
                case TableDesignApplyOutcome.OutcomeUnknown:
                    Applied = true;
                    _baselineFingerprint = null;
                    ApplyButton.IsEnabled = false;
                    StatusText.Text = "Apply outcome unknown: " + Safe(result.Message);
                    await Dialogs.ShowMessageAsync(this, "Apply outcome unknown",
                        result.Message + "\n\nThe session may have been disconnected. Reconnect, then reload the table before designing again.");
                    break;
                case TableDesignApplyOutcome.RefusedStale:
                    _baselineFingerprint = null;
                    ApplyButton.IsEnabled = false;
                    StatusText.Text = "Refused: " + Safe(result.Message);
                    await Dialogs.ShowMessageAsync(this, "Table changed on the server",
                        result.Message + (_isNew ? "" : "\n\nUse Reload to load the current definition; your draft is discarded on reload."));
                    break;
                default:
                    StatusText.Text = "Nothing was changed. " + Safe(result.Message);
                    PreviewBox.Text = current + "\n" + result.Message.Split('\n').Select(l => "-- " + l.TrimEnd('\r'))
                        .Aggregate("", (a, b) => a + b + "\n");
                    ApplyButton.IsEnabled = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Apply failed: " + Safe(ex.Message) + " Reload to see the current table.";
        }
        finally
        {
            _busy = false;
        }
    }

    void OnAddColumn(object? sender, RoutedEventArgs e)
    {
        var row = new DesignRow { Name = $"NewColumn{_rows.Count + 1}", DataType = "int", IsNullable = true };
        _rows.Add(row);
        ColumnGrid.SelectedItem = row;
        SetPreview(null, null);
    }

    void OnRemoveColumn(object? sender, RoutedEventArgs e)
    {
        if (ColumnGrid.SelectedItem is DesignRow row)
        {
            _rows.Remove(row);
            SetPreview(null, null);
            StatusText.Text = "Column removed from the draft. Preview to see the DROP COLUMN statement.";
        }
    }

    async void OnReload(object? sender, RoutedEventArgs e) => await LoadAsync();

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    static string Safe(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
