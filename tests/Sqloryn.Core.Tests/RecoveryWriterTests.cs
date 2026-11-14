using Sqloryn.Core;

/// <summary>
/// CAP-046/CAP-064 follow-up: debounced, serialised recovery writes off the UI thread, and opt-in privacy
/// settings with retention. No SQL Server connection.
/// </summary>
static class RecoveryWriterTests
{
    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"FAIL {name}");
    }

    static void Pass(string name) => Console.WriteLine($"PASS {name}");

    static RecoveryTabState Tab(string id, string text, bool dirty = true) => new(id, id + ".sql", null, text, dirty);

    public static async Task RunAsync()
    {
        var dir = Directory.CreateTempSubdirectory("sqloryn-recovery-writer");
        try
        {
            await DebounceAndQuitFlush(dir.FullName);
            await RapidEditsKeepNewest(dir.FullName);
            await SaveCloseDiscardMultipleTabs(dir.FullName);
            await DisableClearRetentionUnreadable(dir.FullName);
            History(dir.FullName);
        }
        finally
        {
            dir.Delete(true);
        }
        Settings();
        Wiring();
        Pass("recovery-writer");
    }

    static async Task DebounceAndQuitFlush(string root)
    {
        var store = new DocumentRecoveryStore(Path.Combine(root, "debounce"));
        using var writer = new RecoveryWriter(store, TimeSpan.FromMinutes(5), _ => { }, enabled: true);
        writer.Schedule([Tab("a", "SELECT 1")]);
        Check(!File.Exists(store.FilePath), "recovery-schedule-does-not-write-synchronously");
        await writer.FlushAsync();
        var read = store.Read();
        Check(read.Count == 1 && read[0].Text == "SELECT 1", "recovery-quit-flush-writes-queued-snapshot");
    }

    static async Task RapidEditsKeepNewest(string root)
    {
        var store = new DocumentRecoveryStore(Path.Combine(root, "rapid"));
        using var writer = new RecoveryWriter(store, TimeSpan.FromMilliseconds(1), _ => { }, enabled: true);
        var flushes = new List<Task>();
        for (var i = 0; i < 300; i++)
        {
            // Alternate dirty/clean so a stale write would be visible as the wrong text or a resurrected copy.
            writer.Schedule([Tab("a", "edit " + i, dirty: i % 3 != 0), Tab("b", "other " + i)]);
            if (i % 7 == 0)
                flushes.Add(writer.FlushAsync());
        }
        writer.Schedule([Tab("a", "final"), Tab("b", "other final", dirty: false)]);
        await Task.WhenAll(flushes);
        await writer.FlushAsync();
        await Task.Delay(50); // let any timer callback that was already due run; it must find nothing newer to write
        var read = store.Read();
        Check(read.Count == 1 && read[0].Id == "a" && read[0].Text == "final", "recovery-rapid-edits-newest-wins");
    }

    static async Task SaveCloseDiscardMultipleTabs(string root)
    {
        var store = new DocumentRecoveryStore(Path.Combine(root, "lifecycle"));
        using var writer = new RecoveryWriter(store, TimeSpan.FromMinutes(5), _ => { }, enabled: true);
        writer.Schedule([Tab("a", "A1"), Tab("b", "B1"), Tab("c", "C1")]);
        await writer.FlushAsync();
        Check(store.Read().Count == 3, "recovery-multiple-tabs-stored");

        // Save: tab a is now clean, so its copy goes; b and c stay.
        writer.Schedule([Tab("a", "A1", dirty: false), Tab("b", "B2"), Tab("c", "C1")]);
        await writer.FlushAsync();
        var afterSave = store.Read();
        Check(afterSave.Count == 2 && afterSave.All(d => d.Id != "a") && afterSave.Any(d => d.Text == "B2"), "recovery-save-drops-copy");

        // Close b with Discard: it is no longer open; the removed id must drop its copy.
        writer.Schedule([Tab("a", "A1", dirty: false), Tab("c", "C1")], ["b"]);
        // A later snapshot without the removed id must not lose the removal (ids accumulate until written).
        writer.Schedule([Tab("a", "A1", dirty: false), Tab("c", "C2")]);
        await writer.FlushAsync();
        var afterDiscard = store.Read();
        Check(afterDiscard.Count == 1 && afterDiscard[0].Id == "c" && afterDiscard[0].Text == "C2", "recovery-close-discard-drops-copy");

        // Startup Discard of an old session's copy while an open tab's edit is queued: old copy must not return.
        store.Write([DocumentRecovery.Capture("old", "old.sql", null, "OLD", DateTimeOffset.UtcNow)]);
        writer.Schedule([Tab("new", "NEW")]);
        await writer.ClearAsync();
        await writer.FlushAsync();
        var afterClear = store.Read();
        Check(afterClear.Count == 1 && afterClear[0].Id == "new", "recovery-discard-not-resurrected-by-queued-write");
    }

    static async Task DisableClearRetentionUnreadable(string root)
    {
        var store = new DocumentRecoveryStore(Path.Combine(root, "privacy"));
        var reports = new List<string>();
        using var writer = new RecoveryWriter(store, TimeSpan.FromMinutes(5), m => { lock (reports) reports.Add(m); }, enabled: false);
        writer.Schedule([Tab("a", "secret-ish")]);
        await writer.FlushAsync();
        Check(!File.Exists(store.FilePath), "recovery-off-by-default-writes-nothing");

        await writer.SetEnabledAsync(true);
        writer.Schedule([Tab("a", "A")]);
        writer.Schedule([Tab("a", "A2")]);
        await writer.SetEnabledAsync(false);
        await writer.FlushAsync();
        Check(!File.Exists(store.FilePath), "recovery-turning-off-drops-queue-and-copies");

        await writer.SetEnabledAsync(true);
        writer.RetentionDays = 7;
        var old = DocumentRecovery.Capture("stale", "stale.sql", null, "STALE", DateTimeOffset.UtcNow.AddDays(-8));
        var recent = DocumentRecovery.Capture("recent", "recent.sql", null, "RECENT", DateTimeOffset.UtcNow.AddDays(-1));
        store.Write([old, recent]);
        writer.Schedule([Tab("a", "A")]);
        await writer.FlushAsync();
        var kept = store.Read().Select(d => d.Id).ToHashSet();
        Check(!kept.Contains("stale") && kept.Contains("recent") && kept.Contains("a"), "recovery-retention-drops-expired-copies");
        writer.RetentionDays = 100_000;
        Check(writer.RetentionDays == AppSettings.MaxPrivateDataRetentionDays, "recovery-retention-clamped");

        File.WriteAllText(store.FilePath, "{not-json");
        writer.Schedule([Tab("a", "A3")]);
        await writer.FlushAsync();
        Check(File.ReadAllText(store.FilePath) == "{not-json", "recovery-unreadable-copy-left-in-place");
        lock (reports)
            Check(reports.Any(r => r.Contains("could not be read", StringComparison.Ordinal)), "recovery-unreadable-copy-reported");
        Check(!store.TryRead(out _), "recovery-tryread-reports-unreadable");
    }

    static void History(string root)
    {
        var store = new ExecutionHistoryStore(Path.Combine(root, "history")) { RetentionDays = 7 };
        store.Append(ExecutionHistory.FromBatch("SELECT 'old'", QueryCompletionKind.Succeeded, 1, DateTimeOffset.UtcNow.AddDays(-8)));
        store.Append(ExecutionHistory.FromBatch("SELECT 'new'", QueryCompletionKind.Succeeded, 1, DateTimeOffset.UtcNow));
        var read = store.Read();
        Check(read.Count == 1 && read[0].Sql == "SELECT 'new'", "history-retention-drops-expired");
        Check(!File.ReadAllText(store.FilePath).Contains("'old'", StringComparison.Ordinal), "history-expired-removed-from-disk");
        store.Clear();
        Check(!File.Exists(store.FilePath), "history-clear");
    }

    static void Settings()
    {
        var defaults = new AppSettings();
        Check(!defaults.KeepUnsavedCopies && !defaults.KeepExecutionHistory, "privacy-storage-is-opt-in");
        Check(defaults.PrivateDataRetentionDays == AppSettings.DefaultPrivateDataRetentionDays, "privacy-retention-default");
        Check(new AppSettings { PrivateDataRetentionDays = 0 }.Normalize().PrivateDataRetentionDays == AppSettings.MinPrivateDataRetentionDays
              && new AppSettings { PrivateDataRetentionDays = 9999 }.Normalize().PrivateDataRetentionDays == AppSettings.MaxPrivateDataRetentionDays,
            "privacy-retention-normalized");
    }

    static void Wiring()
    {
        var app = Path.Combine(FindRepoRoot(), "src", "Sqloryn.App");
        var window = File.ReadAllText(Path.Combine(app, "MainWindow.axaml.cs"));
        var recovery = File.ReadAllText(Path.Combine(app, "MainWindow.Recovery.cs"));
        var session = File.ReadAllText(Path.Combine(app, "MainWindow.Session.cs"));
        var settings = File.ReadAllText(Path.Combine(app, "SettingsWindow.axaml")) + File.ReadAllText(Path.Combine(app, "SettingsWindow.axaml.cs"));
        Check(!recovery.Contains("AppServices.Recovery.Write", StringComparison.Ordinal)
              && !recovery.Contains("AppServices.Recovery.Clear", StringComparison.Ordinal)
              && !window.Contains("AppServices.Recovery.", StringComparison.Ordinal), "recovery-ui-never-writes-store-directly");
        Check(window.Contains("_recoveryWriter.FlushAsync()", StringComparison.Ordinal), "recovery-quit-flushes");
        Check(window.Contains("PersistRecovery(tab.Id.ToString(\"N\"))", StringComparison.Ordinal), "recovery-close-tab-drops-copy");
        Check(session.Contains("KeepExecutionHistory", StringComparison.Ordinal), "history-respects-setting");
        Check(settings.Contains("KeepUnsavedBox", StringComparison.Ordinal) && settings.Contains("KeepHistoryBox", StringComparison.Ordinal)
              && settings.Contains("RetentionDaysBox", StringComparison.Ordinal) && settings.Contains("Dialogs.ConfirmAsync", StringComparison.Ordinal),
            "privacy-settings-controls");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLoryn.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
