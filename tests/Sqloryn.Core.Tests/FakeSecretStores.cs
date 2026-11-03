using Sqloryn.Core;

/// <summary>Secret store whose GetAsync completes only when the test releases or fails it.</summary>
sealed class DelayedSecretStore : ISecretStore
{
    readonly object _gate = new();
    readonly Dictionary<string, Queue<TaskCompletionSource<string?>>> _pending = new(StringComparer.Ordinal);

    public bool ThrowOnGet { get; init; }

    public Task SetAsync(string key, string secret) => Task.CompletedTask;

    public Task DeleteAsync(string key) => Task.CompletedTask;

    public Task<string?> GetAsync(string key)
    {
        if (ThrowOnGet)
            throw new InvalidOperationException("Keychain read failed (OSStatus -128).");
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var queue))
                _pending[key] = queue = new Queue<TaskCompletionSource<string?>>();
            queue.Enqueue(tcs);
        }
        return tcs.Task;
    }

    public void Release(string key, string? secret) => Take(key).SetResult(secret);

    public void Fail(string key, Exception error) => Take(key).SetException(error);

    TaskCompletionSource<string?> Take(string key)
    {
        lock (_gate)
            return _pending[key].Dequeue();
    }
}
