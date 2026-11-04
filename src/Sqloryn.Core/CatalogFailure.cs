using Microsoft.Data.SqlClient;

namespace Sqloryn.Core;

public enum CatalogFailureKind
{
    /// <summary>The server refused the read for permission reasons.</summary>
    Denied,
    /// <summary>Any other failure.</summary>
    Error,
    /// <summary>The read was cancelled before it finished (for example a newer expansion started).</summary>
    Cancelled
}

/// <summary>
/// Maps a failed Object Explorer catalog read to explicit, single-line, redacted node text so a failed or denied
/// folder is never rendered like an empty one.
/// </summary>
public sealed record CatalogFailure(CatalogFailureKind Kind, int Number, string Text)
{
    const int MaxMessageChars = 200;

    // 229/230 object/column permission, 262 database permission, 297/300 server-state permission,
    // 916 principal cannot access the database under the current security context.
    static readonly HashSet<int> DeniedNumbers = [229, 230, 262, 297, 300, 916];

    public static CatalogFailure From(Exception ex, string what)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var label = Clean(what, 80);
        if (ex is OperationCanceledException)
            return new CatalogFailure(CatalogFailureKind.Cancelled, 0, $"Cancelled: {label} did not finish loading. Collapse and expand to retry.");
        var number = ex is SqlException sql ? sql.Number
            : ex is ProviderErrorException provider ? provider.Number
            : 0;
        if (DeniedNumbers.Contains(number))
            return new CatalogFailure(CatalogFailureKind.Denied, number,
                $"Denied: this login cannot read {label} (SQL error {number}). Ask an administrator for VIEW DEFINITION on the object or database.");
        var detail = Clean(ex.Message, MaxMessageChars);
        var code = number == 0 ? "" : $" (SQL error {number})";
        return new CatalogFailure(CatalogFailureKind.Error, number, $"Error: could not load {label}{code}: {detail}");
    }

    static string Clean(string? value, int max)
    {
        var line = SchemaObjectCatalog.OneLine(DiagnosticsRedactor.Redact(value ?? ""));
        return line.Length <= max ? line : line[..max] + "…";
    }
}
