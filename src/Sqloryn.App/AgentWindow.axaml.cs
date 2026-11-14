using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

/// <summary>
/// SQL Server Agent jobs, schedules and history. Reads go through <see cref="AgentCatalog"/>; Run and Delete are built
/// by <see cref="AgentCommand"/>, previewed in a confirmation dialog and sent only through <see cref="AgentActions"/>,
/// which re-checks the capability gate. No SQL text lives in this window.
/// </summary>
public partial class AgentWindow : Window
{
    sealed record JobRow(AgentJob Job)
    {
        public string Name => Job.Name;
        public string Enabled => Job.Enabled ? "yes" : "no";
        public string LastStatus => Job.LastStatus.ToString();
        public string LastRun => AgentTime.Format(Job.LastRun);
        public string NextRun => AgentTime.Format(Job.NextRun);
    }

    sealed record ScheduleRow(AgentSchedule Schedule)
    {
        public string Name => Schedule.Name;
        public string Enabled => Schedule.Enabled ? "yes" : "no";
        public string Description => Schedule.Description;
        public string NextRun => AgentTime.Format(Schedule.NextRun);
    }

    sealed record HistoryRow(AgentHistoryEntry Entry)
    {
        public string When => AgentTime.Format(Entry.RunTime);
        public string Step => Entry.StepId == 0 ? "(job outcome)" : $"{Entry.StepId}. {Entry.StepName}";
        public string Status => Entry.Status.ToString();
        public string Duration => AgentTime.FormatDuration(Entry.Duration);
    }

    readonly SqlSession _session;
    readonly AgentSessionRunner _runner;
    readonly CancellationTokenSource _cts = new();
    AgentGate _gate = new(AgentAvailability.Unknown, "Checking SQL Server Agent availability…");
    AgentJob? _selected;
    int _jobsGeneration;
    int _detailGeneration;
    bool _busy;

    public AgentWindow() : this(new SqlSession())
    {
    }

    public AgentWindow(SqlSession session)
    {
        _session = session;
        _runner = new AgentSessionRunner(session);
        InitializeComponent();
        GateText.Text = _gate.Message;
        Closing += (_, _) => _cts.Cancel();
        Opened += async (_, _) => await LoadJobsAsync();
    }

    void UpdateButtons()
    {
        var hasJob = _selected is not null;
        RunButton.IsEnabled = hasJob && _gate.CanChange && !_busy;
        DeleteButton.IsEnabled = hasJob && _gate.CanChange && !_busy;
        RefreshButton.IsEnabled = !_busy && _gate.CanRead;
        BusyBar.IsVisible = _busy;
    }

    void ClearDetail()
    {
        SchedulesGrid.ItemsSource = null;
        HistoryGrid.ItemsSource = null;
        MessageBox.Text = "";
    }

    async Task LoadJobsAsync()
    {
        var generation = ++_jobsGeneration;
        _detailGeneration++;
        _busy = true;
        UpdateButtons();
        StatusText.Text = "Reading Agent jobs…";
        try
        {
            if (!_session.IsConnected)
            {
                _gate = new AgentGate(AgentAvailability.Unavailable, "Not connected. Connect to a SQL Server first.");
            }
            else
            {
                var capabilities = _session.Capabilities.Probed
                    ? _session.Capabilities
                    : await _session.ProbeCapabilitiesAsync(_cts.Token);
                _gate = AgentCatalog.Gate(capabilities);
            }
            if (generation != _jobsGeneration)
                return;
            GateText.Text = _gate.Message;
            if (!_gate.CanRead)
            {
                // Unavailable is a different state from "no jobs": nothing is listed and nothing can be changed.
                JobsGrid.ItemsSource = null;
                ClearDetail();
                StatusText.Text = "SQL Server Agent is unavailable on this connection, so there is nothing to list.";
                return;
            }

            var listing = await AgentCatalog.ListJobsAsync(_runner, _cts.Token);
            if (generation != _jobsGeneration)
                return;
            JobsGrid.ItemsSource = listing.Items.Select(j => new JobRow(j)).ToList();
            _selected = null;
            ClearDetail();
            DetailHeader.Text = "Select a job to see its schedules and history.";
            if (listing.Failure is { } failure)
                StatusText.Text = failure.Text;
            else if (listing.Items.Count == 0)
                StatusText.Text = "The server reports no Agent jobs visible to this login.";
            else
                StatusText.Text = $"{listing.Items.Count} job(s)" + (listing.Truncated ? $" (stopped at the first {AgentCatalog.MaxJobs}; more exist)." : ".");
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        finally
        {
            if (generation == _jobsGeneration)
            {
                _busy = false;
                UpdateButtons();
            }
        }
    }

    async Task LoadDetailAsync(AgentJob job)
    {
        var generation = ++_detailGeneration;
        ClearDetail();
        DetailHeader.Text = $"Schedules and history — {job.Name}";
        StatusText.Text = "Reading schedules and history…";
        try
        {
            var schedules = await AgentCatalog.ListSchedulesAsync(_runner, job.JobId, _cts.Token);
            var history = await AgentCatalog.ListHistoryAsync(_runner, job.JobId, _cts.Token);
            if (generation != _detailGeneration || !ReferenceEquals(_selected, job))
                return;
            SchedulesGrid.ItemsSource = schedules.Items.Select(s => new ScheduleRow(s)).ToList();
            HistoryGrid.ItemsSource = history.Items.Select(h => new HistoryRow(h)).ToList();
            var notes = new List<string>();
            if (schedules.Failure is { } sf)
                notes.Add(sf.Text);
            if (history.Failure is { } hf)
                notes.Add(hf.Text);
            if (history.Failure is null && history.Items.Count == 0)
                notes.Add("No history rows (the job may never have run, or history was cleared).");
            if (history.Truncated)
                notes.Add($"History shows the newest {AgentCatalog.MaxHistory} rows only.");
            StatusText.Text = notes.Count == 0 ? $"{history.Items.Count} history row(s)." : string.Join(" ", notes);
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }

    async void OnRefresh(object? sender, RoutedEventArgs e) => await LoadJobsAsync();

    async void OnJobSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selected = (JobsGrid.SelectedItem as JobRow)?.Job;
        UpdateButtons();
        if (_selected is { } job)
            await LoadDetailAsync(job);
        else
            ClearDetail();
    }

    void OnHistorySelected(object? sender, SelectionChangedEventArgs e)
        => MessageBox.Text = (HistoryGrid.SelectedItem as HistoryRow)?.Entry.Message ?? "";

    async void OnRunJob(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } job || _busy)
            return;
        if (!_gate.CanChange)
        {
            StatusText.Text = "Running jobs is disabled: " + _gate.Message;
            return;
        }
        var command = AgentCommand.StartJob(job.JobId, job.Name);
        var confirmed = await Dialogs.ConfirmAsync(
            this, "Run Agent job", command.Preview, "Statement sent: " + command.Sql + "\nParameter @job_id = " + command.JobId, "Run Job");
        if (!confirmed)
        {
            StatusText.Text = "Run cancelled. Nothing was sent to the server.";
            return;
        }
        _busy = true;
        UpdateButtons();
        StatusText.Text = "Sending the start request…";
        AgentActionResult result;
        try
        {
            result = await AgentActions.ExecuteAsync(_runner, _gate, command, confirmed: true, _cts.Token);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
        await FinishActionAsync(result, job);
    }

    async void OnDeleteJob(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } job || _busy)
            return;
        if (!_gate.CanChange)
        {
            StatusText.Text = "Deleting jobs is disabled: " + _gate.Message;
            return;
        }
        var command = AgentCommand.DeleteJob(job.JobId, job.Name);
        var confirmed = await Dialogs.ConfirmAsync(
            this, "Delete Agent job", command.Preview, "Statement sent: " + command.Sql + "\nParameter @job_id = " + command.JobId, "Delete Job",
            destructive: true);
        if (!confirmed)
        {
            StatusText.Text = "Delete cancelled. Nothing was sent to the server.";
            return;
        }
        _busy = true;
        UpdateButtons();
        StatusText.Text = "Sending the delete request…";
        AgentActionResult result;
        try
        {
            result = await AgentActions.ExecuteAsync(_runner, _gate, command, confirmed: true, _cts.Token);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
        await FinishActionAsync(result, job);
    }

    /// <summary>Shows the action result, then re-reads the server so what is displayed is what the server holds, not what the client assumed.</summary>
    async Task FinishActionAsync(AgentActionResult result, AgentJob job)
    {
        if (_cts.IsCancellationRequested)
            return;
        ActionText.Text = result.Message;
        await LoadJobsAsync();
        if (result.Outcome is AgentActionOutcome.Done or AgentActionOutcome.OutcomeUnknown)
        {
            var row = (JobsGrid.ItemsSource as IEnumerable<JobRow>)?.FirstOrDefault(r => r.Job.JobId == job.JobId);
            if (row is not null)
                JobsGrid.SelectedItem = row;
        }
        if (result.Outcome is not AgentActionOutcome.Done)
            await Dialogs.ShowMessageAsync(this, "Agent action", result.Message);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
