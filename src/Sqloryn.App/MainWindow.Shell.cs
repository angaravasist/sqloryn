using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Shell concerns: one command table exported as the native macOS menu bar (⌘ shortcuts) or an in-window
/// menu elsewhere (Ctrl), plus the welcome panel, collapsible sidebar and on-demand properties/results panes.
/// Everything here is verified by build and manual inspection only; it has no automated UI tests.
/// </summary>
public partial class MainWindow
{
    const double CompactWidth = 900;
    const double DefaultResultsHeight = 260;

    static readonly bool IsMac = OperatingSystem.IsMacOS();
    /// <summary>⌘ on macOS, Ctrl elsewhere.</summary>
    static readonly KeyModifiers Primary = IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    bool _sidebarVisible = true;
    bool _sidebarAutoCollapsed;
    double _sidebarWidth = AppSettings.DefaultSidebarWidth;
    double _resultsHeight;
    bool _welcomeDismissed;

    sealed record Cmd(string Header, Action Invoke, Key Key = Key.None, KeyModifiers Mods = KeyModifiers.None,
        Key DisplayKey = Key.None);

    void InitShell()
    {
        _sidebarVisible = _settings.SidebarVisible;
        _sidebarWidth = _settings.SidebarWidth;
        _resultsHeight = _settings.ResultsHeight;
        BuildMenus();
        ApplySidebar();
        AddHandler(KeyDownEvent, OnShellKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SizeChanged += OnShellSizeChanged;
        Opened += (_, _) => FitToWorkingArea();
        _explorer.StateChanged += UpdateExplorerHints;
        UpdateExplorerHints();
    }

    // ---- Menus ----------------------------------------------------------------------------

    IReadOnlyList<(string Title, Cmd?[] Items)> CommandGroups()
    {
        Cmd C(string header, Action invoke, Key key = Key.None, KeyModifiers mods = KeyModifiers.None, Key display = Key.None)
            => new(header, invoke, key, mods, display);
        var e = new RoutedEventArgs();

        var file = new List<Cmd?>
        {
            C("_New Query", () => OnNewTab(this, e), Key.N, Primary),
            C("_Open…", () => OnOpen(this, e), Key.O, Primary),
            C("_Save", () => OnSave(this, e), Key.S, Primary),
            C("Save _As…", () => OnSaveAs(this, e), Key.S, Primary | KeyModifiers.Shift),
            C("_Close Tab", () => OnCloseTab(this, e), Key.W, Primary),
            null,
            C("Export Results as _CSV…", () => OnExportCsv(this, e)),
            C("Export Results as CSV (Spreadsheet-_safe)…", () => OnExportCsvSafe(this, e)),
            C("Export Results as _JSON…", () => OnExportJson(this, e))
        };
        // macOS supplies Quit (⌘Q) in the application menu; it routes through the unsaved-changes prompt.
        if (!IsMac)
        {
            file.Add(null);
            file.Add(C("_Quit", () => OnQuit(this, e), Key.Q, Primary));
        }

        var tools = new List<Cmd?>();
        if (!IsMac)
            tools.Add(C("_Settings…", () => OnSettings(this, e), Key.OemComma, Primary));
        tools.Add(C("_Diagnostics Bundle…", () => OnDiagnostics(this, e)));

        var groups = new List<(string, Cmd?[])>
        {
            ("_File", file.ToArray()),
            ("_Edit",
            [
                C("_Find / Replace…", () => OnFindReplace(this, e), Key.F, Primary),
                // Ctrl+G on every platform (⌃G on Mac): Cmd+G is conventionally "Find Next".
                C("Go to _Line…", () => OnGoToLine(this, e), Key.G, KeyModifiers.Control)
            ]),
            ("_View", [IsMac
                ? C("Toggle _Sidebar", () => OnToggleSidebar(this, e), Key.S, KeyModifiers.Meta | KeyModifiers.Control)
                : C("Toggle _Sidebar", () => OnToggleSidebar(this, e), Key.B, KeyModifiers.Control)]),
            ("_Connection",
            [
                C("_Connect…", () => OnConnect(this, e), Key.C, Primary | KeyModifiers.Shift),
                C("_Disconnect", () => OnDisconnect(this, e)),
                null,
                C("_Revalidate Target", () => OnRevalidate(this, e))
            ]),
            ("_Query",
            [
                // F5 is handled for every platform in OnShellKeyDown; the menu advertises the platform shortcut.
                IsMac
                    ? C("_Run", () => OnRun(this, e), Key.Enter, KeyModifiers.Meta)
                    : C("_Run", () => OnRun(this, e), Key.Enter, KeyModifiers.Control, display: Key.F5),
                IsMac
                    ? C("_Cancel", () => OnCancel(this, e), Key.OemPeriod, KeyModifiers.Meta)
                    : C("_Cancel", () => OnCancel(this, e), display: Key.Escape),
                null,
                // Compiles under SET SHOWPLAN_XML; the SQL is never executed.
                C("Display _Estimated Plan", () => OnDisplayEstimatedPlan(this, e), Key.L, Primary)
            ]),
            ("_Object",
            [
                C("_Expand Selected", () => OnExpandSelected(this, e), Key.E, Primary),
                C("Script _SELECT in New Tab (Table/View)", () => OnScriptSelect(this, e), Key.E, Primary | KeyModifiers.Shift),
                null,
                C("_New Table…", () => OnNewTable(this, e)),
                C("_Design Table…", () => OnDesignTable(this, e)),
                C("Edit Top _200 Rows…", () => OnEditRows(this, e))
            ]),
            ("_Tools", tools.ToArray())
        };
        if (!IsMac)
            groups.Add(("_Help", [C("_About SQLoryn", () => OnAbout(this, e))]));
        return groups;
    }

    void BuildMenus()
    {
        var groups = CommandGroups();
        if (IsMac)
        {
            WindowMenu.IsVisible = false;
            var bar = new NativeMenu();
            foreach (var (title, items) in groups)
            {
                var top = new NativeMenuItem(Plain(title)) { Menu = new NativeMenu() };
                foreach (var cmd in items)
                    top.Menu.Add(cmd is null ? new NativeMenuItemSeparator() : ToNative(cmd));
                bar.Add(top);
            }
            NativeMenu.SetMenu(this, bar);

            // Application menu: the title comes from Application.Name ("SQLoryn", set in App.axaml).
            if (Application.Current is { } app)
            {
                var e = new RoutedEventArgs();
                var appMenu = new NativeMenu();
                appMenu.Add(ToNative(new Cmd("About SQLoryn", () => OnAbout(this, e))));
                appMenu.Add(new NativeMenuItemSeparator());
                appMenu.Add(ToNative(new Cmd("Settings…", () => OnSettings(this, e), Key.OemComma, KeyModifiers.Meta)));
                NativeMenu.SetMenu(app, appMenu);
            }
            return;
        }

        foreach (var (title, items) in groups)
        {
            var top = new MenuItem { Header = title };
            foreach (var cmd in items)
            {
                if (cmd is null)
                {
                    top.Items.Add(new Separator());
                    continue;
                }
                var item = new MenuItem { Header = cmd.Header };
                if (cmd.Key != Key.None)
                    item.HotKey = new KeyGesture(cmd.Key, cmd.Mods);
                if (cmd.DisplayKey != Key.None)
                    item.InputGesture = new KeyGesture(cmd.DisplayKey);
                var invoke = cmd.Invoke;
                item.Click += (_, _) => invoke();
                top.Items.Add(item);
            }
            WindowMenu.Items.Add(top);
        }
    }

    static NativeMenuItem ToNative(Cmd cmd)
    {
        var item = new NativeMenuItem(Plain(cmd.Header));
        if (cmd.Key != Key.None)
            item.Gesture = new KeyGesture(cmd.Key, cmd.Mods);
        var invoke = cmd.Invoke;
        item.Click += (_, _) => invoke();
        return item;
    }

    static string Plain(string header) => header.Replace("_", "");

    /// <summary>F5 runs on every platform; Escape cancels only while a command is running.</summary>
    void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            OnRun(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && _active is { } tab && (tab.IsExecuting || tab.Session.HasActiveCommand))
        {
            e.Handled = true;
            OnCancel(this, new RoutedEventArgs());
        }
    }

    // ---- Sidebar and layout ---------------------------------------------------------------

    void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        CaptureSidebarWidth();
        _sidebarAutoCollapsed = false;
        _sidebarVisible = !_sidebarVisible;
        ApplySidebar();
    }

    void CaptureSidebarWidth()
    {
        var col = BodyGrid.ColumnDefinitions[0];
        if (_sidebarVisible && Sidebar.Bounds.Width >= AppSettings.MinSidebarWidth)
            _sidebarWidth = Math.Clamp(Sidebar.Bounds.Width, AppSettings.MinSidebarWidth, AppSettings.MaxSidebarWidth);
        else if (_sidebarVisible && col.Width.IsAbsolute)
            _sidebarWidth = Math.Clamp(col.Width.Value, AppSettings.MinSidebarWidth, AppSettings.MaxSidebarWidth);
    }

    void ApplySidebar()
    {
        var col = BodyGrid.ColumnDefinitions[0];
        if (_sidebarVisible)
        {
            col.MinWidth = AppSettings.MinSidebarWidth;
            col.MaxWidth = AppSettings.MaxSidebarWidth;
            col.Width = new GridLength(_sidebarWidth);
        }
        else
        {
            col.MinWidth = 0;
            col.MaxWidth = double.PositiveInfinity;
            col.Width = new GridLength(0);
        }
        Sidebar.IsVisible = _sidebarVisible;
        SidebarSplitter.IsVisible = _sidebarVisible;
    }

    /// <summary>Compact windows collapse the sidebar automatically and bring it back when there is room.</summary>
    void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < CompactWidth && _sidebarVisible)
        {
            CaptureSidebarWidth();
            _sidebarAutoCollapsed = true;
            _sidebarVisible = false;
            ApplySidebar();
        }
        else if (e.NewSize.Width >= CompactWidth && _sidebarAutoCollapsed && !_sidebarVisible)
        {
            _sidebarAutoCollapsed = false;
            _sidebarVisible = true;
            ApplySidebar();
        }
    }

    /// <summary>Never open larger than the usable area of the display the window lands on.</summary>
    void FitToWorkingArea()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
            return;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var maxW = screen.WorkingArea.Width / scale * 0.94;
        var maxH = screen.WorkingArea.Height / scale * 0.94;
        if (Width > maxW)
            Width = Math.Max(MinWidth, maxW);
        if (Height > maxH)
            Height = Math.Max(MinHeight, maxH);
    }

    void SaveLayout()
    {
        try
        {
            CaptureSidebarWidth();
            if (ResultsPane.IsVisible && ResultsPane.Bounds.Height >= 90)
                _resultsHeight = ResultsPane.Bounds.Height;
            var next = (_settings with
            {
                SidebarWidth = _sidebarWidth,
                // An automatic compact collapse is not a user choice.
                SidebarVisible = _sidebarVisible || _sidebarAutoCollapsed,
                ResultsHeight = _resultsHeight
            }).Normalize();
            if (next != _settings)
            {
                _settings = next;
                AppServices.Settings.Save(next);
            }
        }
        catch (Exception)
        {
            // Layout is a convenience; never block closing on it.
        }
    }

    // ---- On-demand panes ------------------------------------------------------------------

    void SetResultsVisible(bool visible)
    {
        if (ResultsPane.IsVisible == visible)
            return;
        var row = Workspace.RowDefinitions[3];
        if (visible)
        {
            var height = _resultsHeight >= 90
                ? _resultsHeight
                : Workspace.Bounds.Height > 0
                    ? Math.Clamp(Workspace.Bounds.Height * 0.4, 120, 400)
                    : DefaultResultsHeight;
            row.Height = new GridLength(height);
        }
        else
        {
            if (ResultsPane.Bounds.Height >= 90)
                _resultsHeight = ResultsPane.Bounds.Height;
            row.Height = GridLength.Auto;
        }
        ResultsPane.IsVisible = visible;
        ResultsSplitter.IsVisible = visible;
    }

    void SetPropertiesVisible(bool visible)
    {
        PropertiesHeader.IsVisible = visible;
        PropertiesBox.IsVisible = visible;
    }

    /// <summary>Not-connected hint, or the filter's empty state when an active filter hides every loaded node.</summary>
    void UpdateExplorerHints()
    {
        ExplorerHint.IsVisible = !_explorer.HasRoots;
        var noMatches = _explorer.FilterActive && _explorer.HasRoots && _explorer.FilterMatchCount == 0;
        ExplorerFilterEmpty.IsVisible = noMatches;
        if (noMatches)
            ExplorerFilterEmpty.Text = ExplorerFilter.EmptyStateText(_explorer.FilterQuery ?? "");
        ExplorerFilterClear.IsVisible = _explorer.FilterActive;
    }

    // ---- Welcome --------------------------------------------------------------------------

    void OnWelcomeNewQuery(object? sender, RoutedEventArgs e)
    {
        _welcomeDismissed = true;
        UpdateWelcome();
        Editor.TextArea.Focus();
    }

    /// <summary>True while nothing is connected and every tab is an untouched, unsaved blank document.</summary>
    void UpdateWelcome()
    {
        var pristine = !_welcomeDismissed && _tabs.All(t =>
            !t.Session.IsConnected && t.Document.FilePath is null && string.IsNullOrEmpty(t.Document.Text));
        WelcomePanel.IsVisible = pristine;
    }
}
