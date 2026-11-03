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

    static readonly HashSet<string> CharTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar"
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
    EditSupport _support = EditSupport.Refuse("Not loaded.");
    bool _busy;
    // Set when a save outcome is unknown: edits stay disabled until a successful reload.
    bool _needsReload;

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

    async Task LoadAsync(string? statusPrefix = null)
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
            var insteadOf = await CatalogClient.HasInsteadOfTriggerAsync(_session, _table.Database, _table.Schema, _table.Name);
            // Bounded sequential read: oversized string/binary cells come back as RawCellOmitted placeholders.
            var data = await _session.QueryRawAsync(EditRowsSql.BuildSelectTop(_table, TopRows), null, TopRows);

            _keyColumns = key;
            _nullable = columns.ToDictionary(c => c.Name, c => c.IsNullable, StringComparer.OrdinalIgnoreCase);
            var identity = columns.Where(c => c.IsIdentity).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _columnNames = data.Columns;
            _sqlTypes = data.SqlTypes;
            var computed = columns.Where(c => c.IsComputed).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var infos = _columnNames
                .Select((n, i) => new EditColumnInfo(
                    n, _sqlTypes[i], _nullable.TryGetValue(n, out var nl) && nl, identity.Contains(n), computed.Contains(n)))
                .ToList();
            _support = EditRowsPolicy.Evaluate(key, infos, insteadOf);
            _computed = computed;

            BuildColumns(identity);
            _items.Clear();
            foreach (var row in data.Rows)
            {
                var display = new string?[row.Length];
                for (var i = 0; i < row.Length; i++)
                    display[i] = FormatValue(row[i]);
                _items.Add(new EditRowItem(row, display));
            }

            HintText.Text = _support.IsSupported
                ? $"Key: {string.Join(", ", key)}. Edit cells, then Save. All changed rows are written in one transaction: if any row changed on the server ({(_support.UsesRowVersion ? "rowversion check" : "every non-key column is compared")}), nothing is saved. Rows with an oversized cell that was not fully loaded cannot be edited. Empty text in a non-text column means NULL; use “Set cell to NULL” for text columns."
                : "Editing is disabled (read-only view): " + _support.RefusalReason;
            var omitted = data.Rows.Count(r => r.Any(RawCellOmitted.IsOmitted));
            StatusText.Text = (statusPrefix is null ? "" : statusPrefix + " ") +
                              $"Loaded {data.Rows.Count} row(s)." +
                              (data.Truncated ? (data.ByteBudgetExhausted ? " Stopped early: load size budget reached." : " More rows exist.") : "") +
                              (omitted > 0 ? $" {omitted} row(s) contain oversized or unsupported cells and are read-only." : "");
            _needsReload = false;
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

    HashSet<string> _computed = new(StringComparer.OrdinalIgnoreCase);

    void BuildColumns(HashSet<string> identity)
    {
        RowGrid.Columns.Clear();
        _columnIndex.Clear();
        for (var i = 0; i < _columnNames.Count; i++)
        {
            var name = _columnNames[i];
            var type = i < _sqlTypes.Count ? _sqlTypes[i] : "";
            var readOnly = !_support.IsSupported
                           || _keyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                           || identity.Contains(name)
                           || _computed.Contains(name)
                           || !EditRowsPolicy.IsEditableType(type);
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

    void RefreshSaveEnabled() => SaveButton.IsEnabled = !_busy && !_needsReload && _support.IsSupported && _items.Count > 0;

    static string? FormatValue(object? value) => value switch
    {
        null => null,
        RawCellOmitted omitted => omitted.Display,
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
        if (!_support.IsSupported)
            throw new InvalidOperationException(_support.RefusalReason ?? "Editing is not supported for this table.");
        // Only the columns the policy trusts take part in the optimistic predicate (rowversion, or every comparable column).
        var concurrency = new HashSet<string>(_support.ConcurrencyColumns, StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _columnNames)
        {
            if (!concurrency.Contains(name))
                excluded.Add(name);
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
                if (RawCellOmitted.IsOmitted(item.OriginalValues[i]))
                    throw new InvalidOperationException(
                        $"Row {r + 1}, column '{_columnNames[i]}' was not fully loaded; its value cannot be edited or written back.");
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
        string? reload = null;
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
                $"{pending.Count} row(s) in {_table.Qualified} will be updated in ONE transaction: all rows are saved, or none. " +
                "Every statement must still match the row you loaded and affect exactly one row, otherwise the whole set is rolled back.",
                detail,
                "Update rows");
            if (!ok)
            {
                StatusText.Text = "Save cancelled. Nothing was written.";
                return;
            }

            var result = await _session.ApplyEditsAsync(pending.Select(p => p.Command).ToList());
            switch (result.Outcome)
            {
                case EditBatchOutcome.Committed:
                    // Reload: server-side values (rowversion, defaults, triggers) may differ from what was typed.
                    reload = result.Message;
                    break;
                case EditBatchOutcome.RolledBackConflict:
                    StatusText.Text = "Edit conflict; nothing was saved. " + Safe(result.Message);
                    await Dialogs.ShowMessageAsync(this, "Edit conflict",
                        result.Message + "\n\nNo row was written. Use Reload to see current data; your edits stay in the grid until you reload.");
                    break;
                case EditBatchOutcome.OutcomeUnknown:
                    _needsReload = true;
                    _items.Clear();
                    StatusText.Text = "Save outcome unknown: " + Safe(result.Message);
                    await Dialogs.ShowMessageAsync(this, "Save outcome unknown",
                        result.Message + "\n\nEditing is disabled until the data is reloaded. Reconnect if the session was closed.");
                    break;
                default:
                    StatusText.Text = "Save failed; nothing was saved. " + Safe(result.Message);
                    break;
            }
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
        if (reload is not null)
            await LoadAsync(reload);
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
