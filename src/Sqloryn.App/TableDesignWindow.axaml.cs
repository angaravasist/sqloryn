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
    }

    readonly SqlSession _session;
    readonly TableRef _table;
    readonly ObservableCollection<DesignRow> _rows = [];
    TableDesignDraft? _existing;
    string? _previewedSql;
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
        Opened += async (_, _) => await LoadAsync();
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
            var columns = await CatalogClient.ListColumnsAsync(_session, _table.Database, _table.Schema, _table.Name);
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
                    IsIdentity = c.IsIdentity
                });
            }
            _existing = new TableDesignDraft
            {
                Database = _table.Database,
                Schema = _table.Schema,
                TableName = _table.Name,
                Columns = _rows.Select(ToDefinition).ToList()
            };
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

    TableDesignDraft BuildDesired() => new()
    {
        Database = _table.Database,
        Schema = _table.Schema,
        TableName = _table.Name,
        Columns = _rows.Select(ToDefinition).ToList()
    };

    void SetPreview(string? sql, string? message)
    {
        _previewedSql = sql;
        PreviewBox.Text = sql ?? message ?? "";
        ApplyButton.IsEnabled = sql is not null && !sql.StartsWith("-- No changes.", StringComparison.Ordinal);
    }

    void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (_existing is null)
        {
            StatusText.Text = "Columns are not loaded yet.";
            return;
        }
        try
        {
            ColumnGrid.CommitEdit();
            var desired = BuildDesired();
            var sql = TableDesignSql.PreviewAlter(_existing, desired);
            SetPreview(sql, null);
            StatusText.Text = TableDesignSql.HasDestructiveChanges(_existing, desired)
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
        if (_busy || _existing is null || _previewedSql is null)
            return;
        _busy = true;
        try
        {
            ColumnGrid.CommitEdit();
            // The draft may have been edited after the preview; require a fresh preview in that case.
            string current;
            bool destructive;
            try
            {
                var desired = BuildDesired();
                current = TableDesignSql.PreviewAlter(_existing, desired);
                destructive = TableDesignSql.HasDestructiveChanges(_existing, desired);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                StatusText.Text = "Draft is invalid: " + Safe(ex.Message);
                return;
            }
            if (!string.Equals(current, _previewedSql, StringComparison.Ordinal))
            {
                SetPreview(current, null);
                StatusText.Text = "The draft changed after the previous preview. Review the refreshed DDL, then confirm again.";
                return;
            }

            var summary = destructive
                ? "This change can permanently remove data or alter column types. The statements run in order without a wrapping transaction; if one fails, earlier ones stay applied."
                : "These statements run in order without a wrapping transaction; if one fails, earlier ones stay applied.";
            var confirmed = await Dialogs.ConfirmAsync(
                this, "Confirm schema change", summary + "\n\nTarget: " + _table.Qualified, current,
                "Apply to server", destructive);
            if (!confirmed)
            {
                StatusText.Text = "Apply cancelled. Nothing was executed.";
                return;
            }

            StatusText.Text = "Applying…";
            var result = await _session.ExecuteAsync(current);
            Applied = true;
            if (result.Completion == QueryCompletionKind.Succeeded)
            {
                StatusText.Text = "Applied. Reloading columns from the server…";
                _busy = false;
                await LoadAsync();
                StatusText.Text = "Applied successfully. " + StatusText.Text;
            }
            else
            {
                StatusText.Text =
                    "Apply did not fully succeed (" + QueryOutcomeFormatter.DescribeStatus(result) +
                    "). Some statements may already have been applied; reload to see the current table.";
                PreviewBox.Text = current + "\n" + QueryOutcomeFormatter.FormatMessages(result)
                    .Split('\n').Select(l => "-- " + l.TrimEnd('\r')).Aggregate("", (a, b) => a + b + "\n");
                ApplyButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Apply failed: " + Safe(ex.Message) +
                              " Reload to see the current table; earlier statements may have been applied.";
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
