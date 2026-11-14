namespace Sqloryn.Core;

/// <summary>
/// Four session SET options. This is not the SSMS Query Options dialog and does not change timeouts.
/// QUOTED_IDENTIFIER is parse-time for a batch, so it applies to later batches on this session.
/// </summary>
public readonly record struct QueryAnsiSettings(bool AnsiNulls, bool AnsiPadding, bool QuotedIdentifier, bool ArithAbort)
{
    public const string ScopeNote =
        "These SET statements apply to this tab's session until it disconnects. " +
        "QUOTED_IDENTIFIER takes effect for later batches on this session. " +
        "This is not the full SSMS Query Options dialog. Connect timeout and command timeout are unchanged.";

    public string ToSessionScript()
    {
        static string OnOff(bool on) => on ? "ON" : "OFF";
        return
            "SET ANSI_NULLS " + OnOff(AnsiNulls) + ";\n" +
            "SET ANSI_PADDING " + OnOff(AnsiPadding) + ";\n" +
            "SET QUOTED_IDENTIFIER " + OnOff(QuotedIdentifier) + ";\n" +
            "SET ARITHABORT " + OnOff(ArithAbort) + ";\n" +
            "PRINT N'Session ANSI settings applied for this tab.';\n" +
            "SELECT SESSIONPROPERTY(N'ANSI_NULLS') AS ANSI_NULLS,\n" +
            "       SESSIONPROPERTY(N'ANSI_PADDING') AS ANSI_PADDING,\n" +
            "       SESSIONPROPERTY(N'QUOTED_IDENTIFIER') AS QUOTED_IDENTIFIER,\n" +
            "       SESSIONPROPERTY(N'ARITHABORT') AS ARITHABORT;\n";
    }
}
