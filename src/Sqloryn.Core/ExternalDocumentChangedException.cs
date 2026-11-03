namespace Sqloryn.Core;

/// <summary>Save refused: the on-disk file changed since this document last loaded or saved it.</summary>
public sealed class ExternalDocumentChangedException(string path)
    : IOException($"'{System.IO.Path.GetFileName(path)}' was changed on disk by another program since it was opened or last saved. Save was refused so the external changes are not overwritten.")
{
    public string DocumentPath { get; } = path;
}
