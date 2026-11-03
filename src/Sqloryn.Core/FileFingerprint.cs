using System.Security.Cryptography;

namespace Sqloryn.Core;

/// <summary>
/// Identity of a file's on-disk bytes: length plus SHA-256. Content based (not mtime based) so an external edit
/// within the filesystem timestamp granularity, or one that restores the old mtime, is still detected.
/// </summary>
public sealed record FileFingerprint(long Length, string Sha256Hex)
{
    public static FileFingerprint FromBytes(ReadOnlySpan<byte> bytes)
        => new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));

    /// <summary>Fingerprint of the file now at <paramref name="path"/>, or null when it does not exist.</summary>
    public static FileFingerprint? TryCapture(string path)
    {
        try
        {
            return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>Last known on-disk state of the document's file. <c>Fingerprint == null</c> means "known not to exist".</summary>
public sealed record TrackedDiskState(string ResolvedPath, FileFingerprint? Fingerprint);
