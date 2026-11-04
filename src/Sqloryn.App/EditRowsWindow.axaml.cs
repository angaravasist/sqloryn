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
        /// <summary>A row added in this window and not yet on the server: it has no baseline and is written with INSERT.</summary>
        public bool IsNew { get; init; }
    }

    readonly SqlSession _session;
    readonly TableRef _table;
    readonly ObservableCollection<EditRowItem> _items = [];
    // Loaded rows the user removed from the grid; they are DELETEd (after confirmation) on Save and restored by Reload.
    readonly List<EditRowItem> _deleted = [];
    readonly Dictionary<DataGridColumn, int> _columnIndex = [];
    IReadOnlyList<string> _columnNames = [];
    IReadOnlyList<string> _sqlTypes = [];
    IReadOnlyList<string> _keyColumns = [];
    Dictionary<string, bool> _nullable = new(StringComparer.OrdinalIgnoreCase);
    EditSupport _support = EditSupport.Refuse("Not loaded.");
    EditOperationSupport _insertSupport = EditOperationSupport.Refuse("Not loaded.");
    EditOperationSupport _deleteSupport = EditOperationSupport.Refuse("Not loaded.");
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
            _insertSupport = EditRowsPolicy.EvaluateInsert(_support, infos);
            _deleteSupport = EditRowsPolicy.EvaluateDelete(_support);
            _computed = computed;

            BuildColumns(identity);
            _items.Clear();
            _deleted.Clear();
            foreach (var row in data.Rows)
            {
                var display = new string?[row.Length];
                for (var i = 0; i < row.Length; i++)
                    display[i] = FormatValue(row[i]);
                _items.Add(new EditRowItem(row, display));
            }

            HintText.Text = _support.IsSupported
                ? $"Key: {string.Join(", ", key)}. Edit cells, Add row or Delete row, then Save. All changes are written in one transaction: if any row changed on the server ({(_support.UsesRowVersion ? "rowversion check" : "every non-key column is compared")}), nothing is saved. Rows with an oversized cell that was not fully loaded cannot be edited. Empty text in a non-text column means NULL; use “Set cell to NULL” for text columns. On a new row, a blank cell is left out of the INSERT so the server default (or NULL) applies; identity, computed and rowversion columns are always server-generated." +
                  (_insertSupport.IsSupported ? "" : " Add row is unavailable: " + _insertSupport.RefusalReason)
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
            // Key columns are typed on new rows (unless identity) but never changed on loaded rows; BuildPending enforces that.
            var keyLocked = _keyColumns.Contains(name, StringComparer.OrdinalIgnoreCase) && !_insertSupport.IsSupported;
            var readOnly = !_support.IsSupported
                           || keyLocked
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

    void RefreshSaveEnabled()
    {
        var idle = !_busy && !_needsReload;
        SaveButton.IsEnabled = idle && _support.IsSupported && (_items.Count > 0 || _deleted.Count > 0);
        AddButton.IsEnabled = idle && _insertSupport.IsSupported;
        DeleteButton.IsEnabled = idle && _deleteSupport.IsSupported && _items.Count > 0;
    }

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

    enum EditKind { Delete, Update, Insert }

    sealed record PendingEdit(EditKind Kind, EditCommand Command, string Description);

    (HashSet<string> Excluded, HashSet<string> Characters) ConcurrencySets()
    {
        // Only the columns the policy trusts take part in the optimistic predicate (rowversion, or every comparable column).
        var concurrency = new HashSet<string>(_support.ConcurrencyColumns, StringComparer.OrdinalIgnoreCase);
        var characterColumns = new HashSet<string>(_support.CharacterConcurrencyColumns ?? [], StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _columnNames)
        {
            if (!concurrency.Contains(name))
                excluded.Add(name);
        }
        return (excluded, characterColumns);
    }

    string Describe(string heading, EditCommand command)
    {
        var description = new StringBuilder();
        description.Append(heading).Append('\n');
        description.Append(command.CommandText.TrimEnd()).Append('\n');
        foreach (var p in command.Parameters)
            description.Append("--   ").Append(p.Name).Append(" = ").Append(p.Value is null ? "NULL" : FormatValue(p.Value)).Append('\n');
        return description.ToString();
    }

    RowKey KeyOf(EditRowItem item) => new(_keyColumns, _keyColumns.Select(k => item.OriginalValues[IndexOf(k)]).ToList());

    Dictionary<string, object?> OriginalsOf(EditRowItem item)
    {
        var originals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _columnNames.Count; i++)
            originals[_columnNames[i]] = item.OriginalValues[i];
        return originals;
    }

    /// <summary>Builds deletes, then updates, then inserts (so a key freed by a delete can be reused by an insert).</summary>
    List<PendingEdit> BuildPending()
    {
        var table = _table;
        var pending = new List<PendingEdit>();
        if (!_support.IsSupported)
            throw new InvalidOperationException(_support.RefusalReason ?? "Editing is not supported for this table.");
        var (excluded, characterColumns) = ConcurrencySets();

        if (_deleted.Count > 0 && !_deleteSupport.IsSupported)
            throw new InvalidOperationException(_deleteSupport.RefusalReason ?? "Deleting rows is not supported for this table.");
        foreach (var item in _deleted)
        {
            var key = KeyOf(item);
            var command = EditRowsSql.BuildDelete(table, key, OriginalsOf(item), excluded, characterColumns);
            pending.Add(new PendingEdit(EditKind.Delete, command,
                Describe("-- DELETE row, key " + KeyText(key), command)));
        }

        for (var r = 0; r < _items.Count; r++)
        {
            var item = _items[r];
            if (item.IsNew)
                continue;
            var changed = new Dictionary<int, object?>();
            for (var i = 0; i < _columnNames.Count; i++)
            {
                if (string.Equals(item.Cells[i], item.OriginalDisplay[i], StringComparison.Ordinal))
                    continue;
                if (_keyColumns.Contains(_columnNames[i], StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Row {r + 1}: key column '{_columnNames[i]}' cannot be changed on an existing row. Delete the row and add a new one instead.");
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

            var key = KeyOf(item);
            var newValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (index, value) in changed)
                newValues[_columnNames[index]] = value;

            var command = EditRowsSql.BuildUpdate(table, key, newValues, OriginalsOf(item), excluded, characterColumns);
            pending.Add(new PendingEdit(EditKind.Update, command, Describe($"-- UPDATE row {r + 1}, key " + KeyText(key), command)));
        }

        var writable = new HashSet<string>(_insertSupport.WritableColumns, StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < _items.Count; r++)
        {
            var item = _items[r];
            if (!item.IsNew)
                continue;
            if (!_insertSupport.IsSupported)
                throw new InvalidOperationException(_insertSupport.RefusalReason ?? "Inserting rows is not supported for this table.");
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _columnNames.Count; i++)
            {
                var text = item.Cells[i];
                var supplied = text is not null && (CharTypes.Contains(_sqlTypes[i]) || text.Trim().Length > 0);
                if (!supplied)
                    continue; // blank: the server default (or NULL) applies
                if (!writable.Contains(_columnNames[i]))
                    throw new InvalidOperationException($"New row {r + 1}: column '{_columnNames[i]}' is generated by the server and cannot be set.");
                values[_columnNames[i]] = Parse(i, text);
            }
            var command = EditRowsSql.BuildInsert(table, values);
            pending.Add(new PendingEdit(EditKind.Insert, command, Describe($"-- INSERT new row {r + 1}", command)));
        }
        return pending;
    }

    string KeyText(RowKey key) => string.Join(", ", key.Columns.Select((k, n) => $"{k}={FormatValue(key.Values[n])}"));

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
            List<PendingEdit> pending;
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
            var deletes = pending.Count(p => p.Kind == EditKind.Delete);
            var updates = pending.Count(p => p.Kind == EditKind.Update);
            var inserts = pending.Count(p => p.Kind == EditKind.Insert);
            var summary =
                $"In {_table.Qualified}: {deletes} row(s) will be DELETED, {updates} updated and {inserts} inserted, in ONE transaction: all changes are saved, or none. " +
                "Every update and delete must still match the row you loaded and affect exactly one row, otherwise the whole set is rolled back.";
            if (deletes > 0)
                summary += "\n\nDELETE permanently removes rows. Foreign keys with ON DELETE CASCADE (or triggers) can remove or change further rows that are not listed here.";
            var ok = await Dialogs.ConfirmAsync(
                this,
                deletes > 0 ? "Confirm row changes (includes DELETE)" : "Confirm row changes",
                summary,
                detail,
                deletes > 0 ? "Delete and save" : "Save rows",
                destructive: deletes > 0);
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
                    _deleted.Clear();
                    break;
                case EditBatchOutcome.RolledBackConflict:
                    StatusText.Text = "Edit conflict; nothing was saved. " + Safe(result.Message);
                    await Dialogs.ShowMessageAsync(this, "Edit conflict",
                        result.Message + "\n\nNo row was written. Use Reload to see current data; your edits stay in the grid until you reload.");
                    break;
                case EditBatchOutcome.OutcomeUnknown:
                    _needsReload = true;
                    _items.Clear();
                    _deleted.Clear();
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

    void OnAddRow(object? sender, RoutedEventArgs e)
    {
        if (_busy || _needsReload)
            return;
        if (!_insertSupport.IsSupported)
        {
            StatusText.Text = "Add row is unavailable: " + _insertSupport.RefusalReason;
            return;
        }
        RowGrid.CommitEdit();
        var item = new EditRowItem(new object?[_columnNames.Count], new string?[_columnNames.Count]) { IsNew = true };
        _items.Add(item);
        RowGrid.SelectedItem = item;
        RowGrid.ScrollIntoView(item, null);
        StatusText.Text = "New row added (not saved). Fill in the cells, leave blanks for server defaults, then Save changes.";
        RefreshSaveEnabled();
    }

    void OnDeleteRow(object? sender, RoutedEventArgs e)
    {
        if (_busy || _needsReload)
            return;
        if (!_deleteSupport.IsSupported)
        {
            StatusText.Text = "Delete is unavailable: " + _deleteSupport.RefusalReason;
            return;
        }
        if (RowGrid.SelectedItem is not EditRowItem item)
        {
            StatusText.Text = "Select a row first.";
            return;
        }
        RowGrid.CommitEdit();
        _items.Remove(item);
        if (item.IsNew)
        {
            StatusText.Text = "New row discarded.";
        }
        else
        {
            _deleted.Add(item);
            StatusText.Text = $"Row marked for deletion ({_deleted.Count} pending). Nothing is deleted until you confirm in Save changes; Reload discards this.";
        }
        RefreshSaveEnabled();
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
