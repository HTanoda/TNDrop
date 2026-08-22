namespace TNDrop.UI;

/// <summary>
/// The one pure decision behind the header's "?" help button (v1.3.1): given whether
/// README.html was found next to the running exe, what reason code (if any) explains a refusal
/// to open it.
///
/// <para>Deliberately free of File.Exists/Process.Start -- the caller
/// (<see cref="ShelfWindow.OnHelpButtonClick"/>) resolves the path, does the I/O, and hands the
/// boolean in here, so the branch is unit-testable the same way <see cref="StackGestures"/>'s
/// pure rules are.</para>
/// </summary>
public static class HelpLauncher
{
    /// <summary>File name of the bundled help doc, at the publish output root (see
    /// TNDrop.App.csproj's Content item for assets/README.html) -- never a full path, so nothing
    /// here needs the no-paths-in-logs guard the caller applies to its own log line.</summary>
    public const string ReadmeFileName = "README.html";

    /// <summary>
    /// Reason code for the Warn line the caller logs when the README could not be found, or null
    /// when it exists and the caller should go on to actually start it.
    /// <para>"start-error" -- a <see cref="System.Diagnostics.Process.Start"/> failure after the
    /// file was found to exist (no default browser associated, shell launch refused) -- is
    /// reported directly by the caller's own catch block instead of through this method: only the
    /// caller's try/catch actually observes that failure, and folding an exception message in here
    /// would break the "pure decision, no I/O" split this type exists for.</para>
    /// </summary>
    public static string? MissingReasonCode(bool readmeExists) => readmeExists ? null : "not-found";
}
