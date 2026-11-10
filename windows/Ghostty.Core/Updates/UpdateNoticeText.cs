namespace Ghostty.Core.Updates;

/// <summary>
/// Product copy for the update-ready notice. Lives in Core (no XAML, no
/// WinUI) so tests pin the exact wording: title <c>Update ready</c>,
/// accept action <c>Close Wintty and update</c> warning that updating
/// closes terminals, and no subscription or Pro language.
/// </summary>
internal static class UpdateNoticeText
{
    /// <summary>Notice headline.</summary>
    public const string Title = "Update ready";

    /// <summary>Primary action label. Names what it does (close + update)
    /// so the approval it records is unambiguous.</summary>
    public const string AcceptLabel = "Close Wintty and update";

    /// <summary>
    /// Notice body for a downloaded <paramref name="version"/>. Warns that
    /// updating closes every window and with them every terminal session,
    /// so the user picks the moment.
    /// </summary>
    public static string Body(string version) =>
        $"Wintty {version} is downloaded and ready to install. " +
        "Updating closes all windows, which ends every open terminal session. " +
        "Choose this when nothing is running that you need.";
}
