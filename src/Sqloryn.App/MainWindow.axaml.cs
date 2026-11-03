using System.Collections.ObjectModel;
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
    readonly TextBox MessagesBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Avalonia.Thickness(0),
        CornerRadius = new Avalonia.CornerRadius(0),
        FontFamily = new FontFamily("Menlo, Monaco, Consolas, monospace"),
        FontSize = 12
    };
    QueryTab? _active;
    AppSettings _settings = new();
    FindReplaceWindow? _findWindow;
    string _lastError = "";
    bool _closeApproved;
    bool _closePromptInFlight;
    long _treeLoadGeneration;

    sealed class TreeNode(string title, string kind, string? database = null, string? schema = null, string? objectName = null)
    {
        public string Title { get; } = title;
        /// <summary>database, schema, tables, views, procedures (folders), table, view, procedure, column, placeholder.</summary>
        public string Kind { get; } = kind;
        public string? Database { get; } = database;
        public string? Schema { get; } = schema;
        public string? ObjectName { get; } = objectName;
        /// <summary>Extra read-only text shown in the properties panel.</summary>
        public string? Info { get; set; }
        public ObservableCollection<TreeNode> Children { get; } = [];
        public bool Loaded { get; set; }
        public bool Loading { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ObjectTree.ItemsSource = _treeRoots;
        ObjectTree.ItemTemplate = new FuncTreeDataTemplate<TreeNode>(
            _ => true,
            (node, _) => BuildNodeView(node),
            node => node.Children);
        ObjectTree.AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded);
        Editor.LostFocus += (_, _) => CaptureSelection();
        Editor.PointerReleased += (_, _) => CaptureSelection();
        Editor.KeyUp += (_, _) => CaptureSelection();
        Closing += OnWindowClosing;
        ApplySettings(_settings);
        InitShell();
        AddTab();
    }

    static AppSettings LoadSettings()
    {
        try
        {
            return AppServices.Settings.Load();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        Editor.FontSize = settings.EditorFontSize;
        AppServices.ApplyTheme(settings);
    }

    static Control BuildNodeView(TreeNode node)
    {
        var text = new TextBlock { Text = node.Title, VerticalAlignment = VerticalAlignment.Center };
        switch (node.Kind)
        {
            case "database":
                text.FontWeight = FontWeight.SemiBold;
                break;
            case "tables" or "views" or "procedures":
                text.Classes.Add("muted");
                break;
            case "column" or "placeholder":
                text.Classes.Add("muted");
                text.FontSize = 12;
                break;
        }
        return text;
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            SaveLayout();
            return;
        }
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

    QueryTab AddTab(QueryTab? existing = null)
    {
        var tab = existing ?? new QueryTab();
        _tabs.Add(tab);
        var item = new TabItem { Header = tab.Header, Tag = tab };
        QueryTabs.Items.Add(item);
        QueryTabs.SelectedItem = item;
        ActivateTab(tab);
        return tab;
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
            UpdateWelcome();
            return;
        }
        UpdateCommandState(_active);
        var session = _active.Session;
        ConnectionLabel.Text = !session.IsConnected
            ? "Not connected"
            : session.TargetUnknown
                ? $"{session.ServerDisplay} / (target unknown — revalidate)"
                : $"{session.ServerDisplay} / {session.Database}";
        StatusBar.Text = _active.Status;
        UpdateWelcome();
    }

    void UpdateCommandState(QueryTab tab)
    {
        var connected = tab.Session.IsConnected;
        var executing = tab.IsExecuting || tab.Session.HasActiveCommand;
        DisconnectButton.IsEnabled = connected && !executing;
        DisconnectButton.IsVisible = connected;
        RunButton.IsEnabled = connected && !executing;
        CancelButton.IsEnabled = executing;
        CancelButton.IsVisible = executing;
        // Export is only offered once there is something to export.
        var hasSets = tab.LastResult?.ResultSets.Count > 0;
        ExportCsvButton.IsEnabled = hasSets;
        ExportJsonButton.IsEnabled = hasSets;
        ExportCsvButton.IsVisible = hasSets;
        ExportJsonButton.IsVisible = hasSets;
        ExportSeparator.IsVisible = hasSets;
    }

    void ReportError(string message)
    {
        _lastError = message;
        StatusBar.Text = SafeUi(message);
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
            var profileNote = await PersistProfileAsync(dialog);
            if (!OwnerStillCurrent(tab, binding))
                return;
            tab.Status = $"Connected · SQL Server {tab.Session.ServerVersion}";
            if (ReferenceEquals(_active, tab))
            {
                UpdateStatus();
                if (profileNote is not null)
                    StatusBar.Text = profileNote;
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
                UpdateStatus();
                ReportError(ex.Message);
            }
            else
            {
                _lastError = ex.Message;
            }
        }
    }

    /// <summary>Upserts the non-secret profile and, when asked, stores the password in the Keychain. Returns a warning on failure.</summary>
    async Task<string?> PersistProfileAsync(ConnectWindow dialog)
    {
        var options = dialog.Result;
        if (options is null || !dialog.SaveProfile)
            return null;
        try
        {
            AppServices.Profiles.Upsert(new ConnectionProfile
            {
                Id = dialog.ProfileId,
                Name = dialog.ProfileName,
                Server = options.Server,
                Port = options.Port,
                Database = options.Database,
                UserId = options.UserId,
                Encrypt = true,
                TrustServerCertificate = options.TrustServerCertificate,
                ServerCertificatePath = options.ServerCertificatePath,
                RememberPassword = dialog.RememberPassword,
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            var key = ProfileSecretKeys.ForProfile(dialog.ProfileId);
            if (dialog.RememberPassword)
                await AppServices.Secrets.SetAsync(key, options.Password);
            else
                await AppServices.Secrets.DeleteAsync(key);
            return null;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return SafeUi("Connected, but the profile or password was not saved: " + ex.Message);
        }
    }

    async void OnRevalidate(object? sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        var tab = _active;
        var binding = tab.CaptureBinding();
        try
        {
            await tab.Session.RevalidateTargetAsync();
            if (!OwnerStillCurrent(tab, binding))
                return;
            tab.Status = "Target revalidated";
            if (ReferenceEquals(_active, tab))
                UpdateStatus();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_active, tab))
                ReportError(ex.Message);
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
            tab.Status = QueryOutcomeFormatter.DescribeStatus(result);
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
                _lastError = ex.Message;
                MessagesBox.Text = SafeUi(ex.Message);
                SetResultsVisible(true);
                ShowMessagesTab();
                UpdateStatus();
                StatusBar.Text = SafeUi(ex.Message);
            }
            else
            {
                _lastError = ex.Message;
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

    void OnNewTab(object? sender, RoutedEventArgs e)
    {
        _welcomeDismissed = true;
        AddTab();
    }

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
            var (text, fingerprint) = await ScriptDocument.ReadFileAsync(path);
            if (!OwnerStillCurrent(tab, binding))
            {
                if (pending is not null)
                    DocumentLifecycle.Rollback([pending]);
                return;
            }
            // Pull any intervening editor edits into the document first.
            if (ReferenceEquals(tab, _active))
                tab.Document.SetText(Editor.Text ?? "");
            if (!DocumentLifecycle.TryCommitOpenReplacement(tab.Document, path, text, versionAtReadStart, fingerprint))
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
        catch (ExternalDocumentChangedException ex)
        {
            // File changed on disk since open/last save: nothing was written, the external content is intact,
            // and the buffer stays dirty. No overwrite path is offered silently; use Save As to keep this text.
            if (ReferenceEquals(_active, tab))
                StatusBar.Text = SafeUi(ex.Message + " Your unsaved text is kept; use Save As to write it elsewhere.");
            RefreshHeader(tab);
            return false;
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
        UpdateWelcome();
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
        PropertiesBox.Text = "";
        SetPropertiesVisible(false);
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
                ReportError(ex.Message);
        }
    }

    TreeNode? SelectedNode => ObjectTree.SelectedItem as TreeNode;

    async void OnExpandSelected(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } node)
            await ExpandNodeAsync(node);
    }

    async void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: TreeNode node })
            await ExpandNodeAsync(node);
    }

    async void OnObjectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Right or Key.Enter or Key.Space))
            return;
        if (SelectedNode is not { } node)
            return;
        e.Handled = true;
        await ExpandNodeAsync(node);
    }

    async void OnObjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SelectedNode is not { } node)
            return;
        if (node.Kind is "table" or "view")
        {
            await ScriptSelectInNewTabAsync(node);
            return;
        }
        await ExpandNodeAsync(node);
    }

    void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var node = SelectedNode;
        PropertiesBox.Text = DescribeNode(node);
        SetPropertiesVisible(node is not null);
    }

    string DescribeNode(TreeNode? node)
    {
        if (node is null)
            return "Select an object to see its properties.";
        var sb = new System.Text.StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                sb.Append(label.PadRight(10)).Append(value).Append('\n');
        }
        var kind = node.Kind switch
        {
            "database" => "Database",
            "schema" => "Schema",
            "tables" => "Tables folder",
            "views" => "Views folder",
            "procedures" => "Procedures folder",
            "table" => "Table",
            "view" => "View",
            "procedure" => "Stored procedure",
            "column" => "Column",
            _ => "Item"
        };
        Line("Kind", kind);
        Line("Server", _active?.Session.ServerDisplay);
        Line("Database", node.Database);
        Line("Schema", node.Schema);
        Line(node.Kind == "column" ? "Table" : "Name", node.Kind == "column" || node.Kind is "table" or "view" or "procedure" ? node.ObjectName : null);
        if (node.Kind is "table" or "view" && node.Database is not null && node.Schema is not null && node.ObjectName is not null)
            Line("Qualified", SqlIdentity.Qualify3(node.Database, node.Schema, node.ObjectName));
        var loadedCount = node.Children.Count(c => c.Kind != "placeholder").ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (node.Kind is "table" or "view" && node.Loaded)
            Line("Columns", loadedCount);
        if (node.Kind is "tables" or "views" or "procedures" && node.Loaded)
            Line("Items", loadedCount);
        if (!string.IsNullOrEmpty(node.Info))
            sb.Append('\n').Append(node.Info).Append('\n');
        return sb.ToString().TrimEnd();
    }

    async void OnScriptSelect(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } node || _active is null)
            return;
        if (node.Kind is not ("table" or "view") || node.Database is null || node.Schema is null || node.ObjectName is null)
        {
            StatusBar.Text = "Select a table or view to script a quoted SELECT into a new tab.";
            return;
        }
        await ScriptSelectInNewTabAsync(node);
    }

    bool _scriptTabInFlight;

    /// <summary>
    /// Opens a NEW tab holding only the generated SELECT. The originating tab's document, dirty state and
    /// selection are not touched. The new tab gets its own session, bound to the object's database.
    /// </summary>
    async Task ScriptSelectInNewTabAsync(TreeNode node)
    {
        if (_active is null || _scriptTabInFlight)
            return;
        var source = _active;
        var sourceBinding = source.CaptureBinding();
        var session = source.Session;
        ScriptFromObjectPlan plan;
        try
        {
            plan = ScriptFromObjectPlan.Create(session.Database, session.TargetUnknown, node.Database, node.Schema, node.ObjectName);
        }
        catch (ArgumentException)
        {
            StatusBar.Text = "Incomplete object identity; cannot script.";
            return;
        }
        var options = session.IsConnected ? session.CreateSiblingOptions(plan.TargetDatabase) : null;
        if (options is null)
        {
            StatusBar.Text = "Connect first to script an object into a new tab.";
            return;
        }

        _scriptTabInFlight = true;
        var created = new QueryTab();
        var adopted = false;
        try
        {
            StatusBar.Text = "Opening script tab…";
            await created.Session.ConnectAsync(options);
            // The sibling session must target the originating database before any SQL can run in it.
            if (!string.Equals(created.Session.Database, plan.TargetDatabase, StringComparison.OrdinalIgnoreCase))
                await created.Session.ChangeDatabaseAsync(plan.TargetDatabase);
            if (!string.Equals(created.Session.Database, plan.TargetDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("New tab could not be bound to the originating database.");
            if (_closeApproved || !OwnerStillCurrent(source, sourceBinding))
            {
                StatusBar.Text = "Script tab discarded: source tab/session changed.";
                return;
            }
            // Preserve the source editor text before the editor swaps documents.
            if (ReferenceEquals(_active, source))
                source.Document.SetText(Editor.Text ?? "");
            created.Document.DiscardChangesTo(plan.Sql);
            created.Status = $"Connected · SQL Server {created.Session.ServerVersion}";
            AddTab(created);
            adopted = true;
            StatusBar.Text = "Scripted " + SqlIdentity.Qualify3(plan.TargetDatabase, node.Schema!, node.ObjectName!) + " into a new tab.";
        }
        catch (Exception ex)
        {
            ReportError("Script tab failed: " + ex.Message);
        }
        finally
        {
            _scriptTabInFlight = false;
            if (!adopted)
                await created.DisposeAsync();
        }
    }

    /// <summary>
    /// Fetches children off the UI-thread-bound tree, then rejects the result if the tab, session,
    /// database or generation changed while the query was in flight.
    /// </summary>
    async Task LoadChildrenAsync(TreeNode node, QueryTab tab, OperationBinding binding, Func<Task<(List<TreeNode> Children, string? TruncationNote)>> fetch)
    {
        if (node.Loading)
            return;
        node.Loading = true;
        node.Children.Clear();
        node.Children.Add(new TreeNode("Loading…", "placeholder", node.Database, node.Schema, node.ObjectName));
        List<TreeNode> children;
        string? truncationNote;
        try
        {
            (children, truncationNote) = await fetch();
        }
        catch
        {
            ResetNode(node);
            throw;
        }
        finally
        {
            node.Loading = false;
        }

        if (!BindingStillCurrent(tab, binding) || !ReferenceEquals(_active, tab))
        {
            ResetNode(node);
            StatusBar.Text = "Object explorer load discarded (tab/session/database changed).";
            return;
        }
        node.Children.Clear();
        foreach (var child in children)
            node.Children.Add(child);
        node.Info = truncationNote;
        if (truncationNote is not null)
        {
            // Never present a capped catalog read as the complete list.
            node.Children.Add(new TreeNode("… (truncated)", "placeholder", node.Database, node.Schema, node.ObjectName) { Info = truncationNote });
            StatusBar.Text = "Object explorer: " + truncationNote;
        }
        node.Loaded = true;
        if (ReferenceEquals(SelectedNode, node))
            PropertiesBox.Text = DescribeNode(node);
    }

    static void ResetNode(TreeNode node)
    {
        node.Children.Clear();
        node.Children.Add(new TreeNode("…", "placeholder", node.Database, node.Schema, node.ObjectName));
        node.Loaded = false;
    }

    static TreeNode WithPlaceholder(TreeNode node)
    {
        node.Children.Add(new TreeNode("…", "placeholder", node.Database, node.Schema, node.ObjectName));
        return node;
    }

    async Task ExpandNodeAsync(TreeNode node)
    {
        if (_active is null || node.Loaded || node.Loading || node.Kind is "placeholder" or "column" or "procedure")
            return;
        var tab = _active;
        var session = tab.Session;
        var binding = tab.CaptureBinding();
        var db = node.Database;
        try
        {
            switch (node.Kind)
            {
                case "database" when db is not null:
                    await LoadChildrenAsync(node, tab, binding, async () =>
                    {
                        var schemas = await CatalogClient.ListSchemasAsync(session, db);
                        return (schemas.Items.Select(s => WithPlaceholder(new TreeNode(s, "schema", db, s))).ToList(), schemas.TruncationNote);
                    });
                    break;

                case "schema" when db is not null && node.Schema is not null:
                {
                    // Folders are static; the catalog query runs when a folder expands.
                    var schema = node.Schema;
                    await LoadChildrenAsync(node, tab, binding, () => Task.FromResult((new List<TreeNode>
                    {
                        WithPlaceholder(new TreeNode("Tables", "tables", db, schema)),
                        WithPlaceholder(new TreeNode("Views", "views", db, schema)),
                        WithPlaceholder(new TreeNode("Programmability · Procedures", "procedures", db, schema))
                    }, (string?)null)));
                    break;
                }

                case "tables" or "views" or "procedures" when db is not null && node.Schema is not null:
                {
                    var schema = node.Schema;
                    var kind = node.Kind;
                    await LoadChildrenAsync(node, tab, binding, async () =>
                    {
                        var names = kind switch
                        {
                            "tables" => await CatalogClient.ListTablesAsync(session, db, schema),
                            "views" => await CatalogClient.ListViewsAsync(session, db, schema),
                            _ => await CatalogClient.ListProceduresAsync(session, db, schema)
                        };
                        var childKind = kind switch { "tables" => "table", "views" => "view", _ => "procedure" };
                        return (names.Items.Select(n =>
                        {
                            var child = new TreeNode(n, childKind, db, schema, n);
                            return childKind == "procedure" ? child : WithPlaceholder(child);
                        }).ToList(), names.TruncationNote);
                    });
                    break;
                }

                case "table" or "view" when db is not null && node.Schema is not null && node.ObjectName is not null:
                {
                    var schema = node.Schema;
                    var table = node.ObjectName;
                    await LoadChildrenAsync(node, tab, binding, async () =>
                    {
                        var columns = await CatalogClient.ListColumnsPageAsync(session, db, schema, table);
                        return (columns.Items.Select(c => new TreeNode(
                            $"{c.Name} : {c.TypeDisplay}{(c.IsNullable ? "" : " · NOT NULL")}{(c.IsIdentity ? " · identity" : "")}",
                            "column", db, schema, table)
                        {
                            Info = $"Column   {c.Name}\nType     {c.TypeDisplay}\nNullable {(c.IsNullable ? "yes" : "no")}\nIdentity {(c.IsIdentity ? "yes" : "no")}"
                        }).ToList(), columns.TruncationNote);
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_active, tab))
                ReportError(ex.Message);
        }
    }

    /// <summary>Resolves the selected table node for design/edit actions, or reports why it cannot.</summary>
    (QueryTab Tab, TableRef Table)? RequireSelectedTable(bool tablesOnly)
    {
        if (_active is null)
            return null;
        var tab = _active;
        if (SelectedNode is not { } node || node.Database is null || node.Schema is null || node.ObjectName is null ||
            node.Kind != "table" && (tablesOnly || node.Kind != "view"))
        {
            StatusBar.Text = tablesOnly ? "Select a table in Object Explorer first." : "Select a table or view in Object Explorer first.";
            return null;
        }
        if (!tab.Session.IsConnected)
        {
            StatusBar.Text = "Connect first.";
            return null;
        }
        if (tab.IsExecuting || tab.Session.HasActiveCommand)
        {
            StatusBar.Text = "A command is running on this tab. Wait or cancel before opening this window.";
            return null;
        }
        return (tab, new TableRef(node.Database, node.Schema, node.ObjectName));
    }

    async void OnDesignTable(object? sender, RoutedEventArgs e)
    {
        if (RequireSelectedTable(tablesOnly: true) is not { } selected)
            return;
        var (tab, table) = selected;
        var node = SelectedNode;
        var window = new TableDesignWindow(tab.Session, table);
        await window.ShowDialog(this);
        if (window.Applied && node is not null && node.Loaded && ReferenceEquals(_active, tab))
        {
            ResetNode(node);
            StatusBar.Text = "Table changed; collapse and expand it to reload columns.";
        }
    }

    async void OnEditRows(object? sender, RoutedEventArgs e)
    {
        if (RequireSelectedTable(tablesOnly: true) is not { } selected)
            return;
        var (tab, table) = selected;
        await new EditRowsWindow(tab.Session, table).ShowDialog(this);
    }

    // ---- Find / Replace -------------------------------------------------------------------

    void OnFindReplace(object? sender, RoutedEventArgs e)
    {
        if (_findWindow is not null)
        {
            _findWindow.Activate();
            return;
        }
        var window = new FindReplaceWindow(Editor);
        window.TextReplaced += _ =>
        {
            if (_active is null)
                return;
            _active.Document.SetText(Editor.Text ?? "");
            RefreshHeader(_active);
        };
        window.Closed += (_, _) => _findWindow = null;
        _findWindow = window;
        window.Show(this);
    }

    // ---- Tools / Help ---------------------------------------------------------------------

    async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow();
        await dialog.ShowDialog<bool>(this);
        if (dialog.Saved is { } saved)
        {
            ApplySettings(saved);
            if (_active is not null)
                RenderResult(_active.LastResult);
            StatusBar.Text = "Settings saved.";
        }
    }

    async void OnDiagnostics(object? sender, RoutedEventArgs e)
    {
        var bundle = BuildDiagnosticsBundle();
        await new DiagnosticsWindow(bundle).ShowDialog(this);
    }

    string BuildDiagnosticsBundle()
    {
        var parts = new List<(string Title, string Text)>
        {
            ("Application", string.Join('\n',
                "SQLoryn developer preview (not a release build)",
                $"Version: {typeof(MainWindow).Assembly.GetName().Version}",
                $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
                $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
                $".NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
                $"Keychain available: {AppServices.SecretsArePersistent}",
                $"Generated (UTC): {DateTime.UtcNow:O}")),
            ("Capabilities", "GO batch separators supported. No SSMS parity claimed. No live SQL Server evidence is implied by this bundle.")
        };

        var sessions = new List<string>();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var s = tab.Session;
            sessions.Add(string.Join(" | ",
                $"tab {i + 1}",
                $"status: {tab.Status}",
                $"connected: {s.IsConnected}",
                $"server: {s.ServerDisplay ?? "-"}",
                $"database: {s.Database ?? "-"}",
                $"version: {s.ServerVersion ?? "-"}",
                $"target unknown: {s.TargetUnknown}",
                $"executing: {tab.IsExecuting}"));
        }
        parts.Add(("Sessions", string.Join('\n', sessions)));
        parts.Add(("Status line", StatusBar.Text ?? ""));
        parts.Add(("Last error (redacted)", string.IsNullOrEmpty(_lastError) ? "(none recorded this run)" : _lastError));
        if (_active?.LastResult is { } last)
        {
            parts.Add(("Last run outcome (redacted)", QueryOutcomeFormatter.FormatMessages(last)));
        }
        parts.Add(("Excluded", "SQL text, result rows, passwords, connection strings and script paths are never included."));
        return DiagnosticsRedactor.BuildSupportBundle(parts);
    }

    async void OnAbout(object? sender, RoutedEventArgs e)
        => await Dialogs.ShowMessageAsync(this, "About SQLoryn",
            "SQLoryn — database studio (developer preview)\n\n" +
            "• SQL authentication with mandatory encryption.\n" +
            "• GO batch separators are supported.\n" +
            $"• Version {typeof(MainWindow).Assembly.GetName().Version}. Build and support details are in Tools > Diagnostics Bundle.\n" +
            "• This preview does not claim parity with SQL Server Management Studio, and no live SQL Server test evidence is implied.\n" +
            "• Passwords are stored only if you choose Remember (macOS Keychain).\n\n" +
            "Not affiliated with Microsoft.");

    static string SafeUi(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "…";
    }
}
