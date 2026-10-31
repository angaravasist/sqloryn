using Sqloryn.Core;

namespace Sqloryn;

sealed class QueryTab : IAsyncDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public SqlSession Session { get; } = new();
    public ScriptDocument Document { get; } = new();
    public string Header => Document.IsDirty ? Document.Title + " *" : Document.Title;
    public string Status { get; set; } = "Disconnected";
    public QueryResult? LastResult { get; set; }
    public string? Selection { get; set; }
    public CancellationTokenSource? RunCts { get; set; }

    public string BatchToRun => ScriptDocument.ResolveBatch(Document.Text, Selection);

    public async ValueTask DisposeAsync()
    {
        RunCts?.Cancel();
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
