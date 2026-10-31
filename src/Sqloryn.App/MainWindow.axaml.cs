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

    sealed class TreeNode(string title, string kind, string? database = null, string? schema = null, string? objectName = null)
    {
        public string Title { get; } = title;
        public string Kind { get; } = kind;
        public string? Database { get; } = database;
        public string? Schema { get; } = schema;
        public string? ObjectName { get; } = objectName;
        public ObservableCollection<TreeNode> Children { get; } = [];
        public bool Loaded { get; set; }
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
        AddTab();
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
        _active = tab;
        Editor.Text = tab.Document.Text;
        RefreshHeader(tab);
        UpdateStatus();
        UpdateRunMode();
        RenderResult(tab.LastResult);
        // OE follows the active tab's session; never retarget another tab's connection.
        _ = LoadDatabasesAsync();
    }

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
        ConnectionLabel.Text = session.IsConnected
            ? $"{session.ServerDisplay} / {session.Database}"
            : "Not connected";
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
        try
        {
            var dialog = new ConnectWindow();
            var ok = await dialog.ShowDialog<bool>(this);
            if (!ok || dialog.Result is null || _active is null)
                return;
            _active.Status = "Connecting…";
            UpdateStatus();
            await _active.Session.ConnectAsync(dialog.Result);
            _active.Status = $"Connected · SQL Server {_active.Session.ServerVersion}";
            UpdateStatus();
            await LoadDatabasesAsync();
        }
        catch (Exception ex)
        {
            if (_active is not null)
                _active.Status = "Connect failed";
            StatusBar.Text = SafeUi(ex.Message);
            UpdateStatus();
        }
    }

    async void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        await _active.Session.DisconnectAsync();
        _active.Status = "Disconnected";
        _treeRoots.Clear();
        UpdateStatus();
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

        tab.RunCts?.Cancel();
        tab.RunCts = new CancellationTokenSource();
        var cts = tab.RunCts;
        var tabId = tab.Id;
        tab.Status = "Running…";
        UpdateStatus();
        try
        {
            var result = await tab.Session.ExecuteAsync(sql, cts.Token);
            if (!_tabs.Contains(tab))
                return; // tab closed while running — drop late result
            tab.LastResult = result;
            tab.Status = Describe(result);
            if (_active?.Id == tabId)
            {
                RenderResult(result);
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            if (!_tabs.Contains(tab))
                return;
            tab.Status = "Run failed";
            if (_active?.Id == tabId)
            {
                MessagesBox.Text = SafeUi(ex.Message);
                StatusBar.Text = SafeUi(ex.Message);
                UpdateStatus();
            }
        }
    }

    void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        _active.RunCts?.Cancel();
        _active.Session.RequestCancel();
        _active.Status = "Cancel requested…";
        UpdateStatus();
    }

    void OnNewTab(object? sender, RoutedEventArgs e) => AddTab();

    async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        if (!await EnsureDiscardOrSaveAsync(_active))
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }]
        });
        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;
        await _active.Document.LoadFileAsync(path);
        Editor.Text = _active.Document.Text;
        RefreshHeader(_active);
    }

    async void OnSave(object? sender, RoutedEventArgs e) => await SaveActiveAsync(saveAs: false);
    async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveActiveAsync(saveAs: true);

    async Task<bool> SaveActiveAsync(bool saveAs)
    {
        if (_active is null)
            return false;
        _active.Document.SetText(Editor.Text ?? "");
        string? path = _active.Document.FilePath;
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
                return false;
        }
        await _active.Document.SaveAsync(path);
        RefreshHeader(_active);
        StatusBar.Text = $"Saved {_active.Document.Title}";
        return true;
    }

    async void OnQuit(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmQuitAsync())
            Close();
    }

    public async Task<bool> ConfirmQuitAsync()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (QueryTabs.Items.OfType<TabItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, tab)) is { } item)
                QueryTabs.SelectedItem = item;
            ActivateTab(tab);
            if (!await EnsureDiscardOrSaveAsync(tab))
                return false;
        }
        foreach (var tab in _tabs.ToList())
            await tab.DisposeAsync();
        _tabs.Clear();
        QueryTabs.Items.Clear();
        return true;
    }

    async Task<bool> EnsureDiscardOrSaveAsync(QueryTab tab)
    {
        if (ReferenceEquals(tab, _active))
            tab.Document.SetText(Editor.Text ?? "");
        if (!tab.Document.IsDirty)
            return true;

        var choice = await ShowDirtyDialogAsync(tab.Document.Title);
        switch (choice)
        {
            case "save":
                if (!ReferenceEquals(tab, _active))
                    ActivateTab(tab);
                return await SaveActiveAsync(tab.Document.FilePath is null);
            case "discard":
                var text = ReferenceEquals(tab, _active) ? Editor.Text ?? "" : tab.Document.Text;
                tab.Document.DiscardChangesTo(text);
                RefreshHeader(tab);
                return true;
            default:
                return false;
        }
    }

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

    async void OnRefreshTree(object? sender, RoutedEventArgs e) => await LoadDatabasesAsync();

    async Task LoadDatabasesAsync()
    {
        _treeRoots.Clear();
        if (_active?.Session.IsConnected != true)
            return;
        var session = _active.Session;
        try
        {
            var names = await session.ListDatabasesAsync();
            if (!ReferenceEquals(_active?.Session, session))
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
            if (ReferenceEquals(_active?.Session, session))
                StatusBar.Text = SafeUi(ex.Message);
        }
    }

    async void OnObjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ObjectTree.SelectedItem is not TreeNode node || _active is null)
            return;
        try
        {
            if (node.Kind == "database" && !node.Loaded)
            {
                node.Children.Clear();
                foreach (var schema in await _active.Session.ListSchemasAsync(node.Database!))
                {
                    var sn = new TreeNode(schema, "schema", node.Database, schema);
                    sn.Children.Add(new TreeNode("…", "placeholder", node.Database, schema));
                    node.Children.Add(sn);
                }
                node.Loaded = true;
            }
            else if (node.Kind == "schema" && !node.Loaded)
            {
                node.Children.Clear();
                foreach (var (schema, name, type) in await _active.Session.ListTablesAndViewsAsync(node.Database!, node.Schema!))
                {
                    var kind = type == "V" ? "view" : "table";
                    var tn = new TreeNode($"{name} ({kind})", kind, node.Database, schema, name);
                    tn.Children.Add(new TreeNode("…", "placeholder", node.Database, schema, name));
                    node.Children.Add(tn);
                }
                node.Loaded = true;
            }
            else if (node.Kind is "table" or "view")
            {
                if (!node.Loaded)
                {
                    node.Children.Clear();
                    foreach (var (name, type, nullable) in await _active.Session.ListColumnsAsync(node.Database!, node.Schema!, node.ObjectName!))
                        node.Children.Add(new TreeNode($"{name} : {type}{(nullable ? "?" : "")}", "column", node.Database, node.Schema, name));
                    node.Loaded = true;
                }
                var sql = "SELECT TOP (100) * FROM " + SqlIdentity.Qualify(node.Schema, node.ObjectName!) + ";\n";
                Editor.Text = (Editor.Text ?? "") + sql;
                _active.Document.SetText(Editor.Text);
                RefreshHeader(_active);
            }
        }
        catch (Exception ex)
        {
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
        var lines = new List<string>
        {
            $"Completion: {result.Completion}",
            $"Server: {result.Server}  Database: {result.Database}",
            $"Partial: {result.PartialResults}"
        };
        if (!string.IsNullOrEmpty(result.ErrorSummary))
            lines.Add(result.ErrorSummary);
        foreach (var msg in result.Messages)
            lines.Add($"Msg {msg.Number} (class {msg.Class}): {msg.Text}");
        if (result.PartialResults)
            lines.Add("Partial or truncated output.");
        MessagesBox.Text = string.Join(Environment.NewLine, lines);

        if (result.ResultSets.Count == 0)
        {
            ResultsBox.Text = "(no result sets)";
            return;
        }
        var sb = new StringBuilder();
        for (var i = 0; i < result.ResultSets.Count; i++)
        {
            var set = result.ResultSets[i];
            if (i > 0)
                sb.AppendLine();
            sb.AppendLine($"--- result set {i + 1} ---");
            sb.AppendLine(string.Join('\t', set.Columns));
            foreach (var row in set.Rows)
                sb.AppendLine(string.Join('\t', row.Select(c => c.IsNull ? "NULL" : c.Display.Replace('\t', ' ').Replace('\n', ' '))));
            if (set.Truncated)
            {
                sb.AppendLine($"… truncated at {QueryLimits.MaxRowsPerSet} rows");
                MessagesBox.Text += $"{Environment.NewLine}Result set {i + 1} truncated at {QueryLimits.MaxRowsPerSet} rows.";
            }
        }
        ResultsBox.Text = sb.ToString();
    }

    static string Describe(QueryResult result) => result.Completion switch
    {
        QueryCompletionKind.Succeeded => "Succeeded",
        QueryCompletionKind.Failed => "Failed",
        QueryCompletionKind.CancelledConfirmed => "Cancelled",
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
