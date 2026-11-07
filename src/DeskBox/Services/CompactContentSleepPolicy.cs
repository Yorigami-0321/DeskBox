namespace DeskBox.Services;

/// <summary>
/// Decides when a capsule-collapsed content window may release its content
/// tree. The shell keeps the window chrome and the last compact presentation,
/// so the visible capsule survives; the existing wake factory rebuilds the
/// content on the next expand or hover.
/// </summary>
internal static class CompactContentSleepPolicy
{
    /// <summary>
    /// Continuous collapsed time before the content tree is released. Long
    /// enough that quick collapse/expand cycles never rebuild, short enough
    /// that a capsule resting on the desktop reclaims its tree.
    /// </summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    public static bool ShouldSleep(
        bool isCollapsed,
        bool isClosing,
        bool contentBusy,
        bool hasWakeFactory,
        bool hasContent,
        bool alreadySleeping)
    {
        return isCollapsed &&
            !isClosing &&
            !contentBusy &&
            hasWakeFactory &&
            hasContent &&
            !alreadySleeping;
    }
}
