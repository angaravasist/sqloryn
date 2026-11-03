using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

public partial class EditRowsWindow : Window
{
    const int TopRows = 200;

    static readonly HashSet<string> EditableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "char", "varchar", "nchar", "nvarchar", "date", "datetime", "smalldatetime", "uniqueidentifier"
    };

    static readonly HashSet<string> CharTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar"
    };

    // Types that cannot be compared with '=' or that the provider would round-trip lossily as parameters.
    static readonly HashSet<string> NotConcurrencyComparable = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "ntext", "image", "xml", "geography", "geometry", "hierarchyid", "sql_variant",
        "datetime2", "time", "datetimeoffset", "timestamp"
    };

    sealed class EditRowItem(object?[] originalValues, string?[] display)
    {
        public object?[] OriginalValues { get; set; } = originalValues;
        public string?[] OriginalDisplay { get; set; } = (string?[])display.Clone();
        public ObservableCollection<string?> Cells { get; } = new(display);
    }

    readonly SqlSession _session;
    readonly TableRef _table;
    readonly ObservableCollection<EditRowItem> _items = [];
    readonly Dictionary<DataGridColumn, int> _columnIndex = [];
    IReadOnlyList<string> _columnNames = [];
    IReadOnlyList<string> _sqlTypes = [];
    IReadOnlyList<string> _keyColumns = [];
    Dictionary<string, bool> _nullable = new(StringComparer.OrdinalIgnoreCase);
    bool _busy;

    public EditRowsWindow() : this(new SqlSession(), new TableRef("master", "dbo", "table"))
    {
    }

    public EditRowsWindow(SqlSession session, TableRef table)
    {
        _session = session;
        _table = table;
        InitializeComponent();
        TitleText.Text = $"Edit top {TopRows} rows — {table.Qualified}";
        RowGrid.ItemsSource = _items;
        Opened += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (_busy)
            return;
        _busy = true;
        SaveButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Loading…";
            var key = await CatalogClient.ListPrimaryKeyAsync(_session, _table.Database, _table.Schema, _table.Name);
            var columns = await CatalogClient.ListColumnsAsync(_session, _table.Database, _table.Schema, _table.Name);
            var data = await _session.QueryRawAsync(EditRowsSql.BuildSelectTop(_table, TopRows), null, TopRows);

            _keyColumns = key;
            _nullable = columns.ToDictionary(c => c.Name, c => c.IsNullable, StringComparer.OrdinalIgnoreCase);
            var identity = columns.Where(c => c.IsIdentity).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _columnNames = data.Columns;
            _sqlTypes = data.SqlTypes;

            BuildColumns(identity);
            _items.Clear();
            foreach (var row in data.Rows)
            {
                var display = new string?[row.Length];
                for (var i = 0; i < row.Length; i++)
                    display[i] = FormatValue(row[i]);
                _items.Add(new EditRowItem(row, display));
            }

            var editable = key.Count > 0;
            HintText.Text = editable
                ? $"Key: {string.Join(", ", key)}. Edit cells, then Save. Each changed row is updated with an optimistic check: if the row changed on the server, the update is refused. Empty text in a non-text column means NULL; use “Set cell to NULL” for text columns."
                : "This table has no primary key, so rows cannot be identified safely. Editing is disabled (read-only view).";
            StatusText.Text = $"Loaded {data.Rows.Count} row(s)." + (data.Truncated ? " More rows exist." : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + Safe(ex.Message);
        }
        finally
        {
            _busy = false;
        }
        RefreshSaveEnabled();
    }

    void BuildColumns(HashSet<string> identity)
    {
        RowGrid.Columns.Clear();
        _columnIndex.Clear();
        for (var i = 0; i < _columnNames.Count; i++)
        {
            var name = _columnNames[i];
            var type = i < _sqlTypes.Count ? _sqlTypes[i] : "";
            var readOnly = _keyColumns.Count == 0
                           || _keyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                           || identity.Contains(name)
                           || !EditableTypes.Contains(type);
            var column = new DataGridTextColumn
            {
                Header = name,
                Binding = new Binding($"Cells[{i}]") { Mode = BindingMode.TwoWay },
                IsReadOnly = readOnly
            };
            column.Header = BuildHeader(name, readOnly ? $"{type} (read-only)" : type);
            _columnIndex[column] = i;
            RowGrid.Columns.Add(column);
        }
    }

    static Control BuildHeader(string name, string tip)
    {
        var header = new TextBlock { Text = name };
        ToolTip.SetTip(header, tip);
        return header;
    }

    void RefreshSaveEnabled() => SaveButton.IsEnabled = !_busy && _keyColumns.Count > 0 && _items.Count > 0;

    static string? FormatValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    object? Parse(int ordinal, string? text)
    {
        var name = _columnNames[ordinal];
        var type = _sqlTypes[ordinal];
        var nullable = _nullable.TryGetValue(name, out var n) && n;
        if (text is null)
        {
            if (!nullable)
                throw new FormatException($"Column '{name}' does not allow NULL.");
            return null;
        }

        if (CharTypes.Contains(type))
            return text;

        var t = text.Trim();
        if (t.Length == 0)
        {
            if (!nullable)
                throw new FormatException($"Column '{name}' does not allow NULL; enter a value.");
            return null;
        }

        var inv = CultureInfo.InvariantCulture;
        try
        {
            return type.ToLowerInvariant() switch
            {
                "bit" => t is "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                    : t is "0" || t.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                    : throw new FormatException("expected 0/1/true/false"),
                "tinyint" => byte.Parse(t, NumberStyles.None, inv),
                "smallint" => short.Parse(t, NumberStyles.AllowLeadingSign, inv),
                "int" => int.Parse(t, NumberStyles.AllowLeadingSign, inv),
                "bigint" => long.Parse(t, NumberStyles.AllowLeadingSign, inv),
                "decimal" or "numeric" or "money" or "smallmoney" => decimal.Parse(t, NumberStyles.Number, inv),
                "float" => double.Parse(t, NumberStyles.Float, inv),
                "real" => float.Parse(t, NumberStyles.Float, inv),
                "date" or "datetime" or "smalldatetime" => DateTime.Parse(t, inv, DateTimeStyles.None),
                "uniqueidentifier" => Guid.Parse(t),
                _ => throw new FormatException("unsupported type")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException($"Value for '{name}' is not a valid {type}.");
        }
    }

    sealed record PendingUpdate(EditRowItem Item, EditCommand Command, Dictionary<int, object?> NewValues, string Description);

    List<PendingUpdate> BuildPending()
    {
        var table = _table;
        var pending = new List<PendingUpdate>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _columnNames.Count; i++)
        {
            var type = _sqlTypes[i];
            var comparable = !NotConcurrencyComparable.Contains(type) &&
                             (EditableTypes.Contains(type) || type is "binary" or "varbinary");
            if (!comparable)
                excluded.Add(_columnNames[i]);
        }

        for (var r = 0; r < _items.Count; r++)
        {
            var item = _items[r];
            var changed = new Dictionary<int, object?>();
            for (var i = 0; i < _columnNames.Count; i++)
            {
                if (string.Equals(item.Cells[i], item.OriginalDisplay[i], StringComparison.Ordinal))
                    continue;
                var column = RowGrid.Columns.FirstOrDefault(c => _columnIndex.TryGetValue(c, out var idx) && idx == i);
                if (column is null || column.IsReadOnly)
                    continue;
                changed[i] = Parse(i, item.Cells[i]);
            }
            if (changed.Count == 0)
                continue;

            var keyValues = _keyColumns
                .Select(k => item.OriginalValues[IndexOf(k)])
                .ToList();
            var key = new RowKey(_keyColumns, keyValues);
            var newValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (index, value) in changed)
                newValues[_columnNames[index]] = value;
            var originals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _columnNames.Count; i++)
                originals[_columnNames[i]] = item.OriginalValues[i];

            var command = EditRowsSql.BuildUpdate(table, key, newValues, originals, excluded);
            var description = new StringBuilder();
            description.Append("-- row ").Append(r + 1).Append(", key ")
                .Append(string.Join(", ", _keyColumns.Select((k, n) => $"{k}={FormatValue(keyValues[n])}"))).Append('\n');
            description.Append(command.CommandText.TrimEnd()).Append('\n');
            foreach (var p in command.Parameters)
                description.Append("--   ").Append(p.Name).Append(" = ").Append(p.Value is null ? "NULL" : FormatValue(p.Value)).Append('\n');
            pending.Add(new PendingUpdate(item, command, changed, description.ToString()));
        }
        return pending;
    }

    int IndexOf(string column)
    {
        for (var i = 0; i < _columnNames.Count; i++)
        {
            if (string.Equals(_columnNames[i], column, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new InvalidOperationException($"Key column {column} is missing from the result.");
    }

    async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _busy = true;
        SaveButton.IsEnabled = false;
        try
        {
            RowGrid.CommitEdit();
            List<PendingUpdate> pending;
            try
            {
                pending = BuildPending();
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
                StatusText.Text = "Cannot save: " + Safe(ex.Message);
                return;
            }
            if (pending.Count == 0)
            {
                StatusText.Text = "No changes to save.";
                return;
            }

            var detail = string.Join("\n", pending.Select(p => p.Description));
            var ok = await Dialogs.ConfirmAsync(
                this,
                "Confirm row updates",
                $"{pending.Count} row(s) in {_table.Qualified} will be updated, one statement per row, without a wrapping transaction. " +
                "Each statement only succeeds if the row still matches what you loaded.",
                detail,
                "Update rows");
            if (!ok)
            {
                StatusText.Text = "Save cancelled. Nothing was written.";
                return;
            }

            var saved = 0;
            foreach (var update in pending)
            {
                try
                {
                    var rows = await _session.QueryRawAsync(
                        update.Command.CommandText, update.Command.Parameters, 1);
                    var affected = rows.Rows.Count > 0 && rows.Rows[0].Length > 0
                        ? Convert.ToInt32(rows.Rows[0][0], CultureInfo.InvariantCulture)
                        : -1;
                    if (affected < 0)
                        throw new InvalidOperationException("The server did not report an affected row count.");
                    EditRowsSql.EnsureSingleRowAffected(affected);
                }
                catch (EditRowsConflictException ex)
                {
                    StatusText.Text = $"Conflict after {saved} of {pending.Count} row(s) saved: {ex.Message}";
                    await Dialogs.ShowMessageAsync(this, "Edit conflict",
                        ex.Message + $"\n\n{saved} of {pending.Count} row(s) were written before this one. Use Reload to see current data; your remaining edits are still in the grid until you reload.");
                    return;
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Save stopped after {saved} of {pending.Count} row(s): {Safe(ex.Message)}";
                    return;
                }

                // Re-baseline this row so a later retry does not re-apply or falsely conflict.
                var values = (object?[])update.Item.OriginalValues.Clone();
                foreach (var (index, value) in update.NewValues)
                    values[index] = value;
                update.Item.OriginalValues = values;
                update.Item.OriginalDisplay = update.Item.Cells.ToArray();
                saved++;
            }
            StatusText.Text = $"Saved {saved} row(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + Safe(ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshSaveEnabled();
        }
    }

    void OnSetNull(object? sender, RoutedEventArgs e)
    {
        if (RowGrid.SelectedItem is not EditRowItem item || RowGrid.CurrentColumn is not { } column ||
            !_columnIndex.TryGetValue(column, out var index))
        {
            StatusText.Text = "Select a cell first.";
            return;
        }
        if (column.IsReadOnly)
        {
            StatusText.Text = "That column is read-only.";
            return;
        }
        if (!_nullable.TryGetValue(_columnNames[index], out var nullable) || !nullable)
        {
            StatusText.Text = $"Column '{_columnNames[index]}' does not allow NULL.";
            return;
        }
        item.Cells[index] = null;
    }

    async void OnReload(object? sender, RoutedEventArgs e) => await LoadAsync();

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    static string Safe(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
