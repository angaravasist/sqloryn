using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// Security administration (WS7-T1): lists server logins, server roles, database users and database roles, shows the
/// selected principal's explicit permissions, and previews GRANT/DENY/REVOKE. Reading and previewing never change the
/// server. Apply requires an explicit confirmation of the exact previewed SQL and runs in one owned transaction through
/// the session's owned permission-change batch. All SQL comes from Core; this window holds no SQL text.
/// No secret of any kind can be entered or shown here.
/// </summary>
public partial class SecurityWindow : Window
{
    sealed record RoleRow(string Role, string Fixed, string Member, string MemberType);

    sealed record UserRow(string Name, string TypeDescription, string Authentication, string DefaultSchema, string Login);

    readonly SqlSession _session;
    readonly SecurityCatalogReader _reader;
    readonly string? _initialDatabase;
    string? _previewedSql;
    string? _currentPrincipal;
    bool _ready;
    bool _suppressDatabaseChange;
    bool _busy;
    int _generation;
    int _permissionsGeneration;

    public SecurityWindow() : this(new SqlSession(), null)
    {
    }

    public SecurityWindow(SqlSession session, string? database)
    {
        _session = session;
        _reader = new SecurityCatalogReader(new SqlSessionSecurityRunner(session));
        _initialDatabase = database;
        InitializeComponent();
        ScopeBox.SelectedIndex = 1;
        RefreshPermissionChoices();
        ApplyScopeFields();
        _ready = true;
        StatusText.Text = "Loading security information…";
        Opened += async (_, _) => await LoadAsync();
    }

    /// <summary>True when permissions were changed at least once, or the outcome could not be confirmed.</summary>
    public bool Applied { get; private set; }

    string? SelectedDatabase => DatabaseBox.SelectedItem as string;

    // ---- Loading (read-only) ---------------------------------------------------------------

    async Task LoadAsync()
    {
        if (_busy)
            return;
        _busy = true;
        var generation = ++_generation;
        try
        {
            StatusText.Text = "Loading server logins and roles…";
            var logins = await _reader.ReadServerLoginsAsync();
            if (generation != _generation)
                return;
            ShowListing(LoginsGrid, LoginsNote, logins, logins.Items, "No logins were returned. SQL Server only lists principals this login may see, so this may not be the complete list.");

            var roles = await _reader.ReadServerRoleMembersAsync();
            if (generation != _generation)
                return;
            ShowListing(ServerRolesGrid, ServerRolesNote, roles, RoleRows(roles.Items), "No server roles were returned. SQL Server only lists roles this login may see.");

            _currentPrincipal = await _reader.ReadCurrentPrincipalAsync();

            IReadOnlyList<string> databases;
            try
            {
                databases = await _session.ListDatabasesAsync();
            }
            catch (Exception ex)
            {
                var failure = SecurityFailure.From(ex, "databases", serverScope: true);
                databases = [];
                UsersNote.Text = failure.Text;
                DbRolesNote.Text = failure.Text;
            }
            if (generation != _generation)
                return;

            _suppressDatabaseChange = true;
            DatabaseBox.ItemsSource = databases;
            var initial = _initialDatabase ?? _session.Database;
            DatabaseBox.SelectedItem = initial is not null && databases.Contains(initial) ? initial : databases.FirstOrDefault();
            _suppressDatabaseChange = false;

            await LoadDatabaseListsAsync(generation);
            if (generation == _generation)
                StatusText.Text = "Loaded. Select a principal to see its explicit permissions; nothing changes until you confirm an Apply.";
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

    async Task LoadDatabaseListsAsync(int generation)
    {
        var database = SelectedDatabase;
        if (database is null)
        {
            ShowEmpty(UsersGrid, UsersNote, "No database is selected or visible to this login.");
            ShowEmpty(DbRolesGrid, DbRolesNote, "No database is selected or visible to this login.");
            return;
        }
        StatusText.Text = $"Loading users and roles of {database}…";
        var users = await _reader.ReadDatabaseUsersAsync(database);
        if (generation != _generation || database != SelectedDatabase)
            return;
        ShowListing(UsersGrid, UsersNote, users, users.Items.Select(UserRowOf).ToList(),
            "No users were returned. SQL Server only lists users this login may see in this database.",
            users.Items.Any(u => u.HasNoVisibleLogin)
                ? "Some users show no visible login: that can mean an orphaned user, or a login this principal may not see."
                : null);

        var roles = await _reader.ReadDatabaseRoleMembersAsync(database);
        if (generation != _generation || database != SelectedDatabase)
            return;
        ShowListing(DbRolesGrid, DbRolesNote, roles, RoleRows(roles.Items), "No database roles were returned. SQL Server only lists roles this login may see.");
    }

    static IReadOnlyList<RoleRow> RoleRows(IReadOnlyList<RoleMembership> items) => items
        .Select(m => new RoleRow(m.Role, m.IsFixedRole ? "yes" : "", m.Member ?? "(no members)", m.MemberType ?? ""))
        .ToList();

    static UserRow UserRowOf(DatabaseUser u) => new(
        u.Name, u.TypeDescription, u.AuthenticationType, u.DefaultSchema ?? "",
        u.IsContained ? "(contained: no login)" : u.MappedLogin ?? (u.HasNoVisibleLogin ? "(none visible)" : ""));

    /// <summary>A failed or denied read shows its reason and an empty grid; an empty success says so; both are never silent.</summary>
    static void ShowListing<T, TRow>(
        DataGrid grid, TextBlock note, SecurityListing<T> listing, IReadOnlyList<TRow> rows, string emptyText, string? extra = null)
    {
        grid.ItemsSource = rows.ToList();
        if (listing.Failure is not null)
            note.Text = listing.Failure.Text;
        else if (rows.Count == 0)
            note.Text = emptyText;
        else
            note.Text = string.Join(" ", new[] { $"{rows.Count} row(s).", listing.Note, extra }.Where(s => !string.IsNullOrEmpty(s)));
    }

    static void ShowEmpty(DataGrid grid, TextBlock note, string text)
    {
        grid.ItemsSource = new List<object>();
        note.Text = text;
    }

    async void OnRefresh(object? sender, RoutedEventArgs e) => await LoadAsync();

    async void OnDatabaseChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressDatabaseChange || _busy || SelectedDatabase is null)
            return;
        _busy = true;
        try
        {
            ClearPreview();
            ClearPermissions("Select a principal to see its explicit permissions.");
            await LoadDatabaseListsAsync(_generation);
            StatusText.Text = "Database changed. Previewed SQL was cleared.";
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

    // ---- Principal selection and current permissions ---------------------------------------------

    void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != SecurityTabs)
            return;
        var serverTab = SecurityTabs.SelectedIndex <= 1;
        if (serverTab && ScopeBox.SelectedIndex == (int)PermissionScope.Database)
            ScopeBox.SelectedIndex = (int)PermissionScope.Server;
        else if (!serverTab && ScopeBox.SelectedIndex == (int)PermissionScope.Server)
            ScopeBox.SelectedIndex = (int)PermissionScope.Database;
        ClearPermissions("Select a principal to see its explicit permissions.");
    }

    async void OnPrincipalSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not DataGrid grid || grid.SelectedItem is null)
            return;
        string? name;
        bool server;
        switch (grid.SelectedItem)
        {
            case ServerLogin login:
                name = login.Name;
                server = true;
                break;
            case RoleRow role when ReferenceEquals(grid, ServerRolesGrid):
                name = role.Role;
                server = true;
                break;
            case RoleRow role:
                name = role.Role;
                server = false;
                break;
            case UserRow user:
                name = user.Name;
                server = false;
                break;
            default:
                return;
        }
        PrincipalBox.Text = name;
        await LoadPermissionsAsync(name, server);
    }

    async Task LoadPermissionsAsync(string principal, bool server)
    {
        var generation = ++_permissionsGeneration;
        var database = SelectedDatabase;
        if (!server && database is null)
        {
            ClearPermissions("Select a database first.");
            return;
        }
        PermissionsNote.Text = $"Loading permissions of {principal}…";
        var listing = server
            ? await _reader.ReadServerPermissionsAsync(principal)
            : await _reader.ReadDatabasePermissionsAsync(database!, principal);
        if (generation != _permissionsGeneration)
            return;
        PermissionsGrid.ItemsSource = listing.Items.ToList();
        PermissionsNote.Text = listing.Failure is not null
            ? listing.Failure.Text
            : listing.Items.Count == 0
                ? $"No explicit {(server ? "server" : "database")} permissions for {principal} are visible. Permissions inherited from roles are not shown here."
                : string.Join(" ", new[] { $"{listing.Items.Count} explicit permission entries for {principal}. Inherited role permissions are not included.", listing.Note }
                    .Where(s => !string.IsNullOrEmpty(s)));
    }

    void ClearPermissions(string note)
    {
        _permissionsGeneration++;
        PermissionsGrid.ItemsSource = new List<object>();
        PermissionsNote.Text = note;
    }

    // ---- Proposed change: form, preview, confirmed apply ----------------------------------------

    void RefreshPermissionChoices()
    {
        var scope = (PermissionScope)Math.Max(0, ScopeBox.SelectedIndex);
        var previous = PermissionBox.SelectedItem as string;
        var choices = PermissionChangePreview.AllowedPermissions(scope);
        PermissionBox.ItemsSource = choices;
        PermissionBox.SelectedItem = previous is not null && choices.Contains(previous) ? previous : choices.FirstOrDefault();
    }

    void ApplyScopeFields()
    {
        var scope = (PermissionScope)Math.Max(0, ScopeBox.SelectedIndex);
        SchemaBox.IsEnabled = scope is PermissionScope.Schema or PermissionScope.Object;
        ObjectBox.IsEnabled = scope == PermissionScope.Object;
        ColumnsBox.IsEnabled = scope == PermissionScope.Object;
    }

    void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        RefreshPermissionChoices();
        ApplyScopeFields();
        ClearPreview();
    }

    void OnComboFormChanged(object? sender, SelectionChangedEventArgs e) => FormChanged();

    void OnTextFormChanged(object? sender, TextChangedEventArgs e) => FormChanged();

    void OnCheckFormChanged(object? sender, RoutedEventArgs e) => FormChanged();

    void FormChanged()
    {
        if (!_ready)
            return;
        if (_previewedSql is not null)
            StatusText.Text = "The draft changed. Preview SQL again before applying.";
        ClearPreview();
    }

    void ClearPreview()
    {
        _previewedSql = null;
        PreviewBox.Text = "";
        WarningsText.Text = "";
        WarningsText.IsVisible = false;
        ApplyButton.IsEnabled = false;
    }

    PermissionChange BuildChange()
    {
        var scope = (PermissionScope)ScopeBox.SelectedIndex;
        IReadOnlyList<string>? columns = null;
        var columnText = (ColumnsBox.Text ?? "").Trim();
        if (scope == PermissionScope.Object && columnText.Length > 0)
            columns = columnText.Split(',', StringSplitOptions.TrimEntries).ToList();
        return new PermissionChange(
            ActionBox.SelectedIndex switch { 1 => PermissionAction.Deny, 2 => PermissionAction.Revoke, _ => PermissionAction.Grant },
            scope,
            PermissionBox.SelectedItem as string ?? "",
            (PrincipalBox.Text ?? "").Trim(),
            scope == PermissionScope.Server ? null : SelectedDatabase,
            string.IsNullOrWhiteSpace(SchemaBox.Text) ? null : SchemaBox.Text.Trim(),
            string.IsNullOrWhiteSpace(ObjectBox.Text) ? null : ObjectBox.Text.Trim(),
            columns,
            GrantOptionCheck.IsChecked == true,
            CascadeCheck.IsChecked == true);
    }

    /// <summary>Builds the preview from the current form. Throws ArgumentException for an invalid draft.</summary>
    PermissionChangePreview BuildPreview() => PermissionChangePreview.Create([BuildChange()], _currentPrincipal);

    void ShowPreview(PermissionChangePreview preview)
    {
        _previewedSql = preview.ExecutableSql;
        PreviewBox.Text = preview.ExecutableSql;
        WarningsText.Text = string.Join("\n", preview.Warnings.Select(w => "• " + w));
        WarningsText.IsVisible = WarningsText.Text.Length > 0;
        ApplyButton.IsEnabled = true;
    }

    void OnPreview(object? sender, RoutedEventArgs e)
    {
        ClearPreview();
        try
        {
            var preview = BuildPreview();
            ShowPreview(preview);
            StatusText.Text = "Preview ready. Permissions on the server are unchanged until you confirm Apply.";
        }
        catch (ArgumentException ex)
        {
            PreviewBox.Text = "-- Preview failed: " + Safe(ex.Message);
            StatusText.Text = "Preview failed: " + Safe(ex.Message);
        }
    }

    async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_busy || _previewedSql is null)
            return;
        _busy = true;
        try
        {
            // The form may have been edited after the preview; require a fresh preview in that case.
            PermissionChangePreview current;
            try
            {
                current = BuildPreview();
            }
            catch (ArgumentException ex)
            {
                ClearPreview();
                StatusText.Text = "Draft is invalid: " + Safe(ex.Message);
                return;
            }
            if (!string.Equals(current.ExecutableSql, _previewedSql, StringComparison.Ordinal))
            {
                ShowPreview(current);
                StatusText.Text = "The draft changed after the previous preview. Review the refreshed SQL, then confirm again.";
                return;
            }

            var summary = "This runs the SQL below in ONE transaction; if any statement fails everything is rolled back. " +
                "The server decides whether this login may change these permissions and refuses otherwise. " +
                "Principal: " + (PrincipalBox.Text ?? "").Trim() + ".";
            if (current.Warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", current.Warnings.Select(w => "• " + w));
            var destructive = current.Warnings.Count > 0;
            var confirmed = await Dialogs.ConfirmAsync(
                this, "Confirm permission change", summary, current.ExecutableSql, "Apply to server", destructive);
            if (!confirmed)
            {
                StatusText.Text = "Apply cancelled. Nothing was executed.";
                return;
            }

            StatusText.Text = "Applying…";
            var result = await _session.ApplyPermissionChangeAsync(current);
            switch (result.Outcome)
            {
                case PermissionApplyOutcome.Committed:
                    Applied = true;
                    ClearPreview();
                    StatusText.Text = "Applied. " + Safe(result.Message) + " Reloading permissions…";
                    _busy = false;
                    await ReloadSelectedPermissionsAsync();
                    break;
                case PermissionApplyOutcome.OutcomeUnknown:
                    Applied = true;
                    ClearPreview();
                    StatusText.Text = "Apply outcome unknown: " + Safe(result.Message);
                    await Dialogs.ShowMessageAsync(this, "Apply outcome unknown",
                        result.Message + "\n\nThe session may have been disconnected. Reconnect, then reload permissions before changing them again.");
                    break;
                case PermissionApplyOutcome.RefusedDenied:
                    StatusText.Text = "Refused: " + Safe(result.Message);
                    PreviewBox.Text = current.ExecutableSql + "\n-- Refused by the server. Nothing was changed.";
                    ApplyButton.IsEnabled = false;
                    break;
                default:
                    StatusText.Text = "Nothing was changed. " + Safe(result.Message);
                    ApplyButton.IsEnabled = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Apply failed: " + Safe(ex.Message) + " Reload to see the current permissions.";
        }
        finally
        {
            _busy = false;
        }
    }

    async Task ReloadSelectedPermissionsAsync()
    {
        var principal = (PrincipalBox.Text ?? "").Trim();
        if (principal.Length == 0)
            return;
        var server = ScopeBox.SelectedIndex == (int)PermissionScope.Server;
        await LoadPermissionsAsync(principal, server);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    static string Safe(string message)
    {
        var oneLine = SchemaObjectCatalog.OneLine(DiagnosticsRedactor.Redact(message));
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }
}
