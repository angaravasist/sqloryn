using System.Collections.ObjectModel;
using System.ComponentModel;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// One Object Explorer tree node. <see cref="Children"/> is the full loaded list; <see cref="Shown"/> is what the
/// TreeView binds to (the same list unless a filter hides some children). Mutate through <see cref="ExplorerController"/>.
/// </summary>
sealed class ExplorerNode(string title, string kind, string? database = null, string? schema = null, string? objectName = null)
    : INotifyPropertyChanged
{
    static readonly HashSet<string> FolderKinds = BuildFolderKinds();

    bool _isExpanded;

    public string Title { get; } = title;

    /// <summary>
    /// Folders: types (database); tables, views, keys, indexes, procedures, functions, sequences, synonyms (schema);
    /// columns, foreignkeys, constraints, triggers (table/view). Items: database, schema, table, view, key, index,
    /// procedure, function, sequence, synonym, usertype, typesystem, column, foreignkey, constraint, trigger, placeholder.
    /// </summary>
    public string Kind { get; } = kind;
    public string? Database { get; } = database;
    public string? Schema { get; } = schema;
    public string? ObjectName { get; } = objectName;

    /// <summary>Extra read-only text shown in the properties panel (also searched by the filter for notes).</summary>
    public string? Info { get; set; }

    /// <summary>Catalog object name when <see cref="ObjectName"/> is the parent table (keys, constraints, triggers).</summary>
    public string? CatalogName { get; init; }

    /// <summary>Precomputed child label, such as a user-defined type's system type. Not SQL.</summary>
    public string? Detail { get; init; }

    public List<ExplorerNode> Children { get; } = [];
    public ObservableCollection<ExplorerNode> Shown { get; } = [];
    public bool Loaded { get; set; }
    public bool Loading { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsFolder => FolderKinds.Contains(Kind);

    /// <summary>Leaf kinds never load children.</summary>
    public bool IsLeaf => Kind is "placeholder" or "column" or "procedure" or "function" or "key" or "index"
        or "foreignkey" or "constraint" or "trigger" or "sequence" or "synonym" or "typesystem";

    static HashSet<string> BuildFolderKinds()
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in ObjectExplorerService.DatabaseFolders)
            kinds.Add(f.FolderKind);
        foreach (var f in ObjectExplorerService.SchemaFolders)
            kinds.Add(f.FolderKind);
        foreach (var f in ObjectExplorerService.TableFolders("table"))
            kinds.Add(f.FolderKind);
        return kinds;
    }
}
