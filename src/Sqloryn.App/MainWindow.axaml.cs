using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sqloryn.Core;

namespace Sqloryn;

public partial class MainWindow : Window
{
    readonly ObservableCollection<QueryTab> _tabs = [];
    readonly ObservableCollection<TreeNode> _treeRoots = [];
    QueryTab? _active;
    bool _closeApproved;
    bool _closePromptInFlight;
    long _treeLoadGeneration;

    sealed class TreeNode(string title, string kind, string? database = null, string? schema = null, string? objectName = null)
    {
        public string Title { get; } = title;
        public string Kind { get; } = kind;
        public string? Database { get; } = database;
        public string? Schema { get; } = schema;
        public string? ObjectName { get; } = objectName;
        public ObservableCollection<TreeNode> Children { get; } = [];
        public bool Loaded { get; set; }
        public bool Loading { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        ObjectTree.ItemsSource = _treeRoots;
        ObjectTree.ItemTemplate = new FuncTreeDataTemplate<TreeNode>(
            _ => true,
            (node, _) => new TextBlock { Text = node.Title },
            node => node.Children);
        Editor.LostFocus += (_, _) => CaptureSelection();
        Editor.PointerReleased += (_, _) => CaptureSelection();
        Editor.KeyUp += (_, _) => CaptureSelection();
        Closing += OnWindowClosing;
        AddTab();
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
            return;
        e.Cancel = true;
        if (_closePromptInFlight)
            return;
        _closePromptInFlight = true;
        _ = CompleteNativeCloseAsync();
    }

    async Task CompleteNativeCloseAsync()
    {
        try
        {
            if (await ConfirmQuitAsync(disposeOnSuccess: true))
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closePromptInFlight = false;
        }
    }

    /// <summary>Used by App shutdown path; cancels synchronously then completes asynchronously.</summary>
    public bool TryBeginQuit(out Task<bool> completion)
    {
        if (_closeApproved)
        {
            completion = Task.FromResult(true);
            return true;
        }
        if (_closePromptInFlight)
        {
            completion = Task.FromResult(false);
            return false;
        }
        _closePromptInFlight = true;
        completion = FinishQuitFromAppAsync();
        return false;
    }

    async Task<bool> FinishQuitFromAppAsync()
    {
        try
        {
            if (await ConfirmQuitAsync(disposeOnSuccess: true))
            {
                _closeApproved = true;
                return true;
            }
            return false;
        }
        finally
        {
            _closePromptInFlight = false;
        }
    }

    void AddTab()
    {
        var tab = new QueryTab();
        _tabs.Add(tab);
        var item = new TabItem { Header = tab.Header, Tag = tab };
        QueryTabs.Items.Add(item);
        QueryTabs.SelectedItem = item;
        ActivateTab(tab);
    }

    void ActivateTab(QueryTab tab)
    {
        if (!ReferenceEquals(_active, tab))
            _active?.BumpGeneration();
        _active = tab;
        Editor.Text = tab.Document.Text;
        RefreshHeader(tab);
        UpdateStatus();
        UpdateRunMode();
        RenderResult(tab.LastResult);
        _ = LoadDatabasesAsync(tab);
    }

    bool TabOpen(QueryTab tab) => _tabs.Contains(tab);

    /// <summary>Connect/open/save/disconnect: same tab session and document; database may change.</summary>
    bool OwnerStillCurrent(QueryTab tab, OperationBinding binding)
        => TabOpen(tab)
           && binding.TabId == tab.Id
           && ReferenceEquals(tab.Session, binding.SessionKey)
           && ReferenceEquals(tab.Document, binding.Document);

    /// <summary>Metadata loads: reject after tab switch, session replace, or database identity change.</summary>
    bool BindingStillCurrent(QueryTab tab, OperationBinding binding)
        => binding.Matches(
            tab.Id,
            tab.Session,
            tab.Document,
            tab.Session.Database,
            tab.Generation,
            TabOpen(tab));

    void RefreshHeader(QueryTab tab)
    {
        foreach (var item in QueryTabs.Items)
        {
            if (item is TabItem ti && ReferenceEquals(ti.Tag, tab))
                ti.Header = tab.Header;
        }
    }

    void UpdateStatus()
    {
        if (_active is null)
        {
            StatusBar.Text = "No tab.";
            ConnectionLabel.Text = "Not connected";
            return;
        }
        var session = _active.Session;
        ConnectionLabel.Text = !session.IsConnected
            ? "Not connected"
            : session.TargetUnknown
                ? $"{session.ServerDisplay} / (target unknown — revalidate)"
                : $"{session.ServerDisplay} / {session.Database}";
        StatusBar.Text =
            $"{_active.Status} · tab {_active.Id.ToString()[..8]} · preview: single batch, no GO, session-only credentials";
    }

    void UpdateRunMode()
    {
        RunModeLabel.Text = string.IsNullOrEmpty(_active?.Selection)
            ? "Run mode: document (select text to run a selection)"
            : "Run mode: selection";
    }

    async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        var binding = tab.CaptureBinding();
        try
        {
            var dialog = new ConnectWindow();
            var ok = await dialog.ShowDialog<bool>(this);
            if (!ok || dialog.Result is null)
                return;
            if (!OwnerStillCurrent(tab, binding))
            {
                StatusBar.Text = "Connect ignored: tab/session changed.";
                return;
            }
            tab.Status = "Connecting…";
            if (ReferenceEquals(_active, tab))
                UpdateStatus();
            await tab.Session.ConnectAsync(dialog.Result);
            if (!OwnerStillCurrent(tab, binding))
                return;
            tab.Status = $"Connected · SQL Server {tab.Session.ServerVersion}";
            if (ReferenceEquals(_active, tab))
            {
                UpdateStatus();
                await LoadDatabasesAsync(tab);
            }
        }
        catch (Exception ex)
        {
            if (!TabOpen(tab))
                return;
            tab.Status = "Connect failed";
            if (ReferenceEquals(_active, tab))
            {
                StatusBar.Text = SafeUi(ex.Message);
                UpdateStatus();
            }
        }
    }

    async void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        var binding = tab.CaptureBinding();
        await tab.Session.DisconnectAsync();
        if (!OwnerStillCurrent(tab, binding))
            return;
        tab.Status = "Disconnected";
        tab.BumpGeneration();
        if (ReferenceEquals(_active, tab))
        {
            _treeRoots.Clear();
            UpdateStatus();
        }
    }

    async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        CaptureSelection();
        var sql = tab.BatchToRun;
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusBar.Text = "Nothing to run.";
            return;
        }
        if (!tab.Session.IsConnected)
        {
            StatusBar.Text = "Connect before running SQL.";
            return;
        }
        if (tab.Session.TargetUnknown)
        {
            StatusBar.Text =
                "Database target unknown after a prior batch. Reconnect or revalidate before running. SQL is never replayed.";
            return;
        }
        if (tab.IsExecuting || tab.Session.HasActiveCommand || !tab.TryBeginRun())
        {
            StatusBar.Text = "A command is already running on this tab. Use Cancel; Run will not queue or replay SQL.";
            return;
        }

        tab.RunCts = new CancellationTokenSource();
        var cts = tab.RunCts;
        var binding = tab.CaptureBinding();
        tab.Status = "Running…";
        if (ReferenceEquals(_active, tab))
            UpdateStatus();
        try
        {
            var result = await tab.Session.ExecuteAsync(sql, cts.Token);
            if (!OwnerStillCurrent(tab, binding))
                return;
            tab.LastResult = result;
            tab.Status = Describe(result);
            if (ReferenceEquals(_active, tab))
            {
                RenderResult(result);
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            if (!OwnerStillCurrent(tab, binding))
                return;
            tab.Status = "Run failed";
            if (ReferenceEquals(_active, tab))
            {
                MessagesBox.Text = SafeUi(ex.Message);
                StatusBar.Text = SafeUi(ex.Message);
                UpdateStatus();
            }
        }
        finally
        {
            tab.EndRun();
            if (ReferenceEquals(tab.RunCts, cts))
                tab.RunCts = null;
            if (ReferenceEquals(_active, tab))
                UpdateStatus();
        }
    }

    void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        if (!tab.IsExecuting && !tab.Session.HasActiveCommand)
        {
            StatusBar.Text = "Nothing to cancel.";
            return;
        }
        tab.Status = "Cancellation requested…";
        tab.RunCts?.Cancel();
        tab.Session.RequestCancel();
        UpdateStatus();
    }

    void OnNewTab(object? sender, RoutedEventArgs e) => AddTab();

    async void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        if (ReferenceEquals(tab, _active))
            tab.Document.SetText(Editor.Text ?? "");
        var (ok, pending) = await DocumentLifecycle.BeginOpenAsync(
            tab.Document,
            async () => MapPrompt(await ShowDirtyDialogAsync(tab.Document.Title)),
            async _ => await SaveTabAsync(tab, tab.Document.FilePath is null));
        if (!ok)
            return;
        if (pending is not null)
            DocumentLifecycle.Commit([pending]);
        await RemoveTabAsync(tab);
    }

    async Task RemoveTabAsync(QueryTab tab)
    {
        tab.BumpGeneration();
        await tab.DisposeAsync();
        _tabs.Remove(tab);
        var item = QueryTabs.Items.OfType<TabItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, tab));
        if (item is not null)
            QueryTabs.Items.Remove(item);
        if (_tabs.Count == 0)
            AddTab();
        else if (QueryTabs.SelectedItem is TabItem { Tag: QueryTab next })
            ActivateTab(next);
        else if (_tabs[0] is { } first)
        {
            QueryTabs.SelectedItem = QueryTabs.Items.OfType<TabItem>().First(i => ReferenceEquals(i.Tag, first));
            ActivateTab(first);
        }
    }

    async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        var binding = tab.CaptureBinding();
        if (ReferenceEquals(tab, _active))
            tab.Document.SetText(Editor.Text ?? "");
        // Discard/save intent is deferred until a replacement file loads successfully.
        var (ok, pending) = await DocumentLifecycle.BeginOpenAsync(
            tab.Document,
            async () => MapPrompt(await ShowDirtyDialogAsync(tab.Document.Title)),
            async _ => await SaveTabAsync(tab, tab.Document.FilePath is null));
        if (!ok)
            return; // cancelled prompt — dirty unchanged
        if (!OwnerStillCurrent(tab, binding))
        {
            if (pending is not null)
                DocumentLifecycle.Rollback([pending]);
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }]
        });
        if (files.Count == 0)
        {
            // Cancelled picker — deferred discard never modified the document.
            if (pending is not null)
                DocumentLifecycle.Rollback([pending]);
            RefreshHeader(tab);
            return;
        }
        if (!TabOpen(tab) || !ReferenceEquals(tab.Document, binding.Document))
        {
            if (pending is not null)
                DocumentLifecycle.Rollback([pending]);
            return;
        }
        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            if (pending is not null)
                DocumentLifecycle.Rollback([pending]);
            return;
        }
        // Capture version before awaiting the replacement read.
        var versionAtReadStart = tab.Document.Version;
        try
        {
            var text = await ScriptDocument.ReadFileTextAsync(path);
            if (!OwnerStillCurrent(tab, binding))
            {
                if (pending is not null)
                    DocumentLifecycle.Rollback([pending]);
                return;
            }
            // Pull any intervening editor edits into the document first.
            if (ReferenceEquals(tab, _active))
                tab.Document.SetText(Editor.Text ?? "");
            if (!DocumentLifecycle.TryCommitOpenReplacement(tab.Document, path, text, versionAtReadStart))
            {
                if (pending is not null)
                    DocumentLifecycle.Rollback([pending]);
                RefreshHeader(tab);
                if (ReferenceEquals(_active, tab))
                {
                    Editor.Text = tab.Document.Text;
                    StatusBar.Text = "Open abandoned: newer edits arrived while reading the file. Unsaved text kept.";
                }
                return;
            }
            if (ReferenceEquals(_active, tab))
                Editor.Text = tab.Document.Text;
            RefreshHeader(tab);
        }
        catch (Exception ex)
        {
            if (pending is not null)
                DocumentLifecycle.Rollback([pending]);
            // Preserve latest buffer (including edits during the failed read).
            if (ReferenceEquals(tab, _active))
                tab.Document.SetText(Editor.Text ?? "");
            RefreshHeader(tab);
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = SafeUi("Open failed; unsaved buffer preserved. " + ex.Message);
        }
    }

    async void OnSave(object? sender, RoutedEventArgs e) => await SaveTabAsync(_active, saveAs: false);
    async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveTabAsync(_active, saveAs: true);

    async Task<bool> SaveTabAsync(QueryTab? tab, bool saveAs)
    {
        if (tab is null)
            return false;
        var binding = tab.CaptureBinding();
        if (ReferenceEquals(tab, _active))
            tab.Document.SetText(Editor.Text ?? "");
        string? path = tab.Document.FilePath;
        if (saveAs || path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save SQL script",
                DefaultExtension = "sql",
                FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }]
            });
            path = file?.TryGetLocalPath();
            if (path is null)
                return false; // cancel save — keep dirty
        }
        if (!OwnerStillCurrent(tab, binding))
            return false;
        try
        {
            await tab.Document.SaveAsync(path);
            RefreshHeader(tab);
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = tab.Document.IsDirty
                    ? $"Saved {tab.Document.Title} (newer edits remain unsaved)"
                    : $"Saved {tab.Document.Title}";
            // Proceed only when the originating document is clean after save.
            return !tab.Document.IsDirty;
        }
        catch (Exception ex)
        {
            // Dirty state preserved by ScriptDocument.SaveAsync
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = SafeUi("Save failed: " + ex.Message);
            RefreshHeader(tab);
            return false;
        }
    }

    async void OnQuit(object? sender, RoutedEventArgs e)
    {
        if (_closePromptInFlight || _closeApproved)
            return;
        _closePromptInFlight = true;
        try
        {
            if (await ConfirmQuitAsync(disposeOnSuccess: true))
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closePromptInFlight = false;
        }
    }

    public Task<bool> ConfirmQuitAsync() => ConfirmQuitAsync(disposeOnSuccess: true);

    public async Task<bool> ConfirmQuitAsync(bool disposeOnSuccess)
    {
        // Sync editor into active document before prompting.
        if (_active is not null)
            _active.Document.SetText(Editor.Text ?? "");

        var docs = _tabs.Select(t => t.Document).ToList();
        var (ok, pending) = await DocumentLifecycle.ResolveAllDirtyDeferredAsync(
            docs,
            async (doc, index) =>
            {
                var tab = _tabs[index];
                if (QueryTabs.Items.OfType<TabItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, tab)) is { } item)
                    QueryTabs.SelectedItem = item;
                ActivateTab(tab);
                return MapPrompt(await ShowDirtyDialogAsync(doc.Title));
            },
            async doc =>
            {
                var tab = _tabs.First(t => ReferenceEquals(t.Document, doc));
                if (!ReferenceEquals(tab, _active))
                    ActivateTab(tab);
                return await SaveTabAsync(tab, tab.Document.FilePath is null);
            });

        if (!ok)
        {
            // Rollback pending discards — all still-open unsaved buffers stay dirty.
            foreach (var tab in _tabs)
            {
                if (ReferenceEquals(tab, _active))
                    Editor.Text = tab.Document.Text;
                RefreshHeader(tab);
            }
            return false;
        }

        DocumentLifecycle.Commit(pending);
        foreach (var tab in _tabs)
        {
            if (ReferenceEquals(tab, _active))
                Editor.Text = tab.Document.Text;
            RefreshHeader(tab);
        }

        if (!disposeOnSuccess)
            return true;
        foreach (var tab in _tabs.ToList())
            await tab.DisposeAsync();
        _tabs.Clear();
        QueryTabs.Items.Clear();
        return true;
    }

    static DocumentLifecycle.PromptChoice MapPrompt(string choice) => choice switch
    {
        "save" => DocumentLifecycle.PromptChoice.Save,
        "discard" => DocumentLifecycle.PromptChoice.Discard,
        _ => DocumentLifecycle.PromptChoice.Cancel
    };

    async Task<string> ShowDirtyDialogAsync(string title)
    {
        var tcs = new TaskCompletionSource<string>();
        var dialog = new Window
        {
            Title = "Unsaved changes",
            Width = 440,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"“{title}” has unsaved changes.",
            TextWrapping = TextWrapping.Wrap
        });
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Button Btn(string label, string result)
        {
            var button = new Button { Content = label, MinWidth = 88 };
            button.Click += (_, _) => { tcs.TrySetResult(result); dialog.Close(); };
            return button;
        }
        row.Children.Add(Btn("Save", "save"));
        row.Children.Add(Btn("Discard", "discard"));
        row.Children.Add(Btn("Cancel", "cancel"));
        panel.Children.Add(row);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult("cancel");
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_active is not null)
            _active.Document.SetText(Editor.Text ?? "");
        if (QueryTabs.SelectedItem is TabItem { Tag: QueryTab tab })
            ActivateTab(tab);
    }

    void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_active is null)
            return;
        _active.Document.SetText(Editor.Text ?? "");
        RefreshHeader(_active);
    }

    void CaptureSelection()
    {
        if (_active is null)
            return;
        var text = Editor.Text ?? "";
        var start = Math.Clamp(Math.Min(Editor.SelectionStart, Editor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(Editor.SelectionStart, Editor.SelectionEnd), 0, text.Length);
        _active.Selection = start == end ? null : text[start..end];
        UpdateRunMode();
    }

    async void OnRefreshTree(object? sender, RoutedEventArgs e)
    {
        if (_active is not null)
            await LoadDatabasesAsync(_active);
    }

    async Task LoadDatabasesAsync(QueryTab tab)
    {
        var loadId = Interlocked.Increment(ref _treeLoadGeneration);
        var binding = tab.CaptureBinding();
        if (!ReferenceEquals(_active, tab))
            return;
        _treeRoots.Clear();
        if (!tab.Session.IsConnected)
            return;
        try
        {
            var names = await tab.Session.ListDatabasesAsync();
            if (loadId != Volatile.Read(ref _treeLoadGeneration) || !ReferenceEquals(_active, tab) ||
                !ReferenceEquals(tab.Session, binding.SessionKey))
                return;
            foreach (var n in names)
            {
                var node = new TreeNode(n, "database", database: n);
                node.Children.Add(new TreeNode("…", "placeholder", database: n));
                _treeRoots.Add(node);
            }
            if (names.Count == 0)
                StatusBar.Text = "Object explorer: no accessible databases.";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = SafeUi(ex.Message);
        }
    }

    async void OnExpandSelected(object? sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is TreeNode node)
            await ExpandNodeAsync(node, insertSql: false);
    }

    async void OnObjectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Right or Key.Enter or Key.Space))
            return;
        if (ObjectTree.SelectedItem is not TreeNode node)
            return;
        e.Handled = true;
        await ExpandNodeAsync(node, insertSql: false);
    }

    async void OnObjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ObjectTree.SelectedItem is not TreeNode node)
            return;
        // Double-tap expands/loads only — does not insert SQL.
        await ExpandNodeAsync(node, insertSql: false);
    }

    async void OnScriptSelect(object? sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is not TreeNode node || _active is null)
            return;
        if (node.Kind is not ("table" or "view") || node.Database is null || node.Schema is null || node.ObjectName is null)
        {
            StatusBar.Text = "Select a table or view to insert a quoted SELECT.";
            return;
        }
        var tab = _active;
        var sql = SqlIdentity.ScriptSelectTop(node.Database, node.Schema, node.ObjectName);
        Editor.Text = (Editor.Text ?? "") + sql;
        tab.Document.SetText(Editor.Text);
        RefreshHeader(tab);
        StatusBar.Text = "Inserted " + SqlIdentity.Qualify3(node.Database, node.Schema, node.ObjectName);
    }

    async Task ExpandNodeAsync(TreeNode node, bool insertSql)
    {
        if (_active is null)
            return;
        var tab = _active;
        var binding = tab.CaptureBinding();
        var database = node.Database;
        try
        {
            if (node.Kind == "database" && !node.Loaded)
            {
                if (node.Loading)
                    return;
                node.Loading = true;
                node.Children.Clear();
                node.Children.Add(new TreeNode("Loading…", "placeholder", database));
                IReadOnlyList<string> schemas;
                try
                {
                    schemas = await tab.Session.ListSchemasAsync(node.Database!);
                }
                finally
                {
                    node.Loading = false;
                }
                if (!BindingStillCurrent(tab, binding) || !ReferenceEquals(_active, tab))
                {
                    StatusBar.Text = "Object explorer load discarded (tab/session/database changed).";
                    return;
                }
                node.Children.Clear();
                foreach (var schema in schemas)
                {
                    var sn = new TreeNode(schema, "schema", node.Database, schema);
                    sn.Children.Add(new TreeNode("…", "placeholder", node.Database, schema));
                    node.Children.Add(sn);
                }
                node.Loaded = true;
            }
            else if (node.Kind == "schema" && !node.Loaded)
            {
                if (node.Loading)
                    return;
                node.Loading = true;
                node.Children.Clear();
                node.Children.Add(new TreeNode("Loading…", "placeholder", node.Database, node.Schema));
                IReadOnlyList<(string Schema, string Name, string Type)> objects;
                try
                {
                    objects = await tab.Session.ListTablesAndViewsAsync(node.Database!, node.Schema!);
                }
                finally
                {
                    node.Loading = false;
                }
                if (!BindingStillCurrent(tab, binding) || !ReferenceEquals(_active, tab))
                {
                    StatusBar.Text = "Object explorer load discarded (tab/session/database changed).";
                    return;
                }
                node.Children.Clear();
                foreach (var (schema, name, type) in objects)
                {
                    var kind = type == "V" ? "view" : "table";
                    // Show database context so identical names across DBs stay distinguishable.
                    var tn = new TreeNode($"{node.Database}.{schema}.{name} ({kind})", kind, node.Database, schema, name);
                    tn.Children.Add(new TreeNode("…", "placeholder", node.Database, schema, name));
                    node.Children.Add(tn);
                }
                node.Loaded = true;
            }
            else if (node.Kind is "table" or "view")
            {
                if (!node.Loaded)
                {
                    if (node.Loading)
                        return;
                    node.Loading = true;
                    node.Children.Clear();
                    node.Children.Add(new TreeNode("Loading…", "placeholder", node.Database, node.Schema, node.ObjectName));
                    IReadOnlyList<(string Name, string TypeName, bool IsNullable)> columns;
                    try
                    {
                        columns = await tab.Session.ListColumnsAsync(node.Database!, node.Schema!, node.ObjectName!);
                    }
                    finally
                    {
                        node.Loading = false;
                    }
                    if (!BindingStillCurrent(tab, binding) || !ReferenceEquals(_active, tab))
                    {
                        StatusBar.Text = "Object explorer load discarded (tab/session/database changed).";
                        return;
                    }
                    node.Children.Clear();
                    foreach (var (name, type, nullable) in columns)
                        node.Children.Add(new TreeNode($"{name} : {type}{(nullable ? "?" : "")}", "column", node.Database, node.Schema, name));
                    node.Loaded = true;
                }
                if (insertSql && node.Database is not null && node.Schema is not null && node.ObjectName is not null)
                {
                    var sql = SqlIdentity.ScriptSelectTop(node.Database, node.Schema, node.ObjectName);
                    Editor.Text = (Editor.Text ?? "") + sql;
                    tab.Document.SetText(Editor.Text);
                    RefreshHeader(tab);
                }
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = SafeUi(ex.Message);
        }
    }

    void RenderResult(QueryResult? result)
    {
        if (result is null)
        {
            ResultsBox.Text = "";
            MessagesBox.Text = "";
            return;
        }
        var rendered = ResultMaterializer.RenderForDisplay(result);
        ResultsBox.Text = rendered;
        var msg = new StringBuilder();
        msg.AppendLine($"Completion: {result.Completion}");
        msg.AppendLine($"Target: {result.TargetConfidence}");
        if (result.TransactionState is int xact)
            msg.AppendLine($"XACT_STATE: {xact}");
        if (!string.IsNullOrEmpty(result.ErrorSummary))
            msg.AppendLine(result.ErrorSummary);
        foreach (var m in result.Messages)
            msg.AppendLine($"Msg {m.Number} (class {m.Class}): {m.Text}");
        if (result.PartialResults)
            msg.AppendLine("Partial or truncated output.");
        MessagesBox.Text = msg.ToString();
    }

    static string Describe(QueryResult result) => result.Completion switch
    {
        QueryCompletionKind.Succeeded => "Succeeded",
        QueryCompletionKind.Failed => "Failed",
        QueryCompletionKind.CancellationRequested => "Cancellation requested",
        QueryCompletionKind.CancelledClientEnded => "Cancelled (client ended)",
        QueryCompletionKind.CancelledServerVerified => "Cancelled (server verified)",
        QueryCompletionKind.TimedOut => "Timed out",
        QueryCompletionKind.UnknownAfterDisconnect => "Unknown after disconnect",
        _ => result.Completion.ToString()
    };

    static string SafeUi(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "…";
    }
}
