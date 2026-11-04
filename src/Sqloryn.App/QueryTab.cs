using Sqloryn.Core;

namespace Sqloryn;

sealed class QueryTab : IAsyncDisposable
{
    long _generation;
    int _runFlag;

    public Guid Id { get; } = Guid.NewGuid();
    public SqlSession Session { get; } = new();
    public ScriptDocument Document { get; } = new();
    public string Header => Document.IsDirty ? Document.Title + " *" : Document.Title;
    public string Status { get; set; } = "Disconnected";
    public QueryResult? LastResult { get; set; }
    /// <summary>Index of the result tab last shown for this query tab; -1 means the Messages tab.</summary>
    public int LastResultSetIndex { get; set; }
    public string? Selection { get; set; }
    /// <summary>0-based offset of <see cref="Selection"/> in the document text when it was captured.</summary>
    public int SelectionStart { get; set; }
    public CancellationTokenSource? RunCts { get; set; }
    /// <summary>When the current Execute started, so status can show client elapsed time before the command ends.</summary>
    public DateTimeOffset? RunStartedUtc { get; set; }
    public bool IsExecuting => RunGuard.IsEntered(_runFlag);
    public long Generation => Interlocked.Read(ref _generation);

    public string BatchToRun => ScriptDocument.ResolveBatch(Document.Text, Selection);

    /// <summary>
    /// Where <see cref="BatchToRun"/> starts in the document, so rejections report document lines.
    /// Null when the whole document runs (or the captured selection no longer matches the text).
    /// </summary>
    public ScriptExecutionOrigin? BatchOrigin
    {
        get
        {
            if (string.IsNullOrEmpty(Selection))
                return null;
            var text = Document.Text;
            if (SelectionStart < 0 || SelectionStart > text.Length ||
                !text.AsSpan(SelectionStart).StartsWith(Selection, StringComparison.Ordinal))
                return null;
            return ScriptExecutionOrigin.FromDocument(text, SelectionStart);
        }
    }

    public OperationBinding CaptureBinding()
        => new(Id, Session, Document, Session.Database, Generation);

    public long BumpGeneration() => Interlocked.Increment(ref _generation);

    public bool TryBeginRun() => RunGuard.TryEnter(ref _runFlag);

    public void EndRun() => RunGuard.Exit(ref _runFlag);

    public async ValueTask DisposeAsync()
    {
        BumpGeneration();
        RunCts?.Cancel();
        EndRun();
        await Session.DisposeAsync().ConfigureAwait(false);
    }
}
