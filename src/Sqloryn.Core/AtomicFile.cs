using System.Text.Json;

namespace Sqloryn.Core;

static class AtomicFile
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLoryn");

    public static void Write(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new ArgumentException("Invalid path.", nameof(path));
        EnsureDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    static void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
