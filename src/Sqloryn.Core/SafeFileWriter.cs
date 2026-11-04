using System.Text;

namespace Sqloryn.Core;

/// <summary>Replaces <paramref name="destinationPath"/> with an already fully written staged file.</summary>
public interface IStagedFileCommitter
{
    void Commit(string stagedPath, string destinationPath);
}

/// <summary>
/// Stage-then-replace writer for user-chosen documents (scripts, exports).
/// <list type="bullet">
/// <item>Contents are written as UTF-8 (no BOM) and flushed to disk in a temp file in the destination's directory,
/// so the final step is a same-volume rename.</item>
/// <item>The destination is touched only after staging succeeds. Any failure or cancellation before the rename
/// leaves the existing file byte-for-byte unchanged and removes the temp file.</item>
/// <item>Symlink policy: a destination that is a symlink is written THROUGH — the staged file replaces the final
/// link target and the link itself is preserved. Dangling links create their target.</item>
/// <item>Permission policy: an existing destination's Unix mode is copied to the staged file; new files use the
/// process default (umask-governed) mode. Ownership, ACLs, xattrs and hard links are not preserved (rename
/// semantics: a hard-linked destination is detached from its other names).</item>
/// <item>Concurrency policy: last writer wins with a complete file. This writer does no external-modification
/// detection itself; <see cref="ScriptDocument.SaveAsync"/> does it via <c>beforeCommit</c> (fingerprint check just
/// before the rename). Interleaved saves each stage under unique names, so the result is exactly one writer's full payload.</item>
/// <item>The directory must already exist; it is never created. A hard crash can leave a hidden
/// <c>.name.GUID.tmp</c> file beside the destination.</item>
/// </list>
/// </summary>
public static class SafeFileWriter
{
    public static async Task WriteUtf8Async(
        string path,
        string contents,
        CancellationToken cancellationToken = default,
        IStagedFileCommitter? committer = null,
        Func<CancellationToken, Task>? beforeCommit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);
        cancellationToken.ThrowIfCancellationRequested();

        var destination = ResolveDestination(path);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new ArgumentException("Invalid path.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Destination folder does not exist: {directory}");

        var temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            await using (var stream = new FileStream(temp, options))
            {
                var bytes = new UTF8Encoding(false).GetBytes(contents);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows() && File.Exists(destination))
            {
                try { File.SetUnixFileMode(temp, File.GetUnixFileMode(destination)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep default mode */ }
            }

            if (beforeCommit is not null)
                await beforeCommit(cancellationToken).ConfigureAwait(false);
            // Last chance to cancel; after this the replace is a single rename.
            cancellationToken.ThrowIfCancellationRequested();

            (committer ?? DefaultCommitter.Instance).Commit(temp, destination);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Full path with symlinks followed to the final target (existing or dangling).</summary>
    public static string ResolveDestination(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var current = full;
            for (var hops = 0; hops < 40; hops++)
            {
                var info = new FileInfo(current);
                if (info.LinkTarget is not { } next)
                    return current;
                current = Path.GetFullPath(next, Path.GetDirectoryName(current) ?? "/");
            }
            throw new IOException("Too many levels of symbolic links: " + path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }

    sealed class DefaultCommitter : IStagedFileCommitter
    {
        public static readonly DefaultCommitter Instance = new();

        public void Commit(string stagedPath, string destinationPath)
        {
            // Prefer Replace when the destination already exists. When it does not, Move can still
            // race with another writer that created the destination between Exists and Move —
            // fall back to Replace so concurrent saves never throw or leave a torn destination.
            if (File.Exists(destinationPath))
            {
                File.Replace(stagedPath, destinationPath, destinationBackupFileName: null);
                return;
            }
            try
            {
                File.Move(stagedPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                File.Replace(stagedPath, destinationPath, destinationBackupFileName: null);
            }
        }
    }
}
