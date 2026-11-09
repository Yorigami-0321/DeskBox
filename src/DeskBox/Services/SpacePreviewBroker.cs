namespace DeskBox.Services;

internal enum SpacePreviewBackend
{
    None,
    QuickLook,
    PowerToysPeek
}

/// <summary>
/// Pure arbitration rules for the space-preview backends.
/// </summary>
internal static class SpacePreviewArbitration
{
    /// <summary>
    /// QuickLook wins whenever it is running: it has the richest
    /// semantics (explicit switch/close pipe messages, server-side
    /// "ignore switch while closed"), and it is the order the Files app
    /// settled on (QuickLook &gt; Seer &gt; Peek), so users get the same
    /// previewer Files would pick. Neither app is ever started.
    /// </summary>
    internal static SpacePreviewBackend Resolve(
        bool quickLookAvailable,
        bool peekAvailable) =>
        quickLookAvailable
            ? SpacePreviewBackend.QuickLook
            : peekAvailable
                ? SpacePreviewBackend.PowerToysPeek
                : SpacePreviewBackend.None;

    /// <summary>
    /// A live preview session keeps the backend it opened with even when
    /// the other one appears mid-session: switching would strand the
    /// first backend's preview window open with nobody left to close it.
    /// </summary>
    internal static SpacePreviewBackend ResolveSwitchBackend(
        SpacePreviewBackend? stickyBackend,
        bool quickLookAvailable,
        bool peekAvailable) =>
        stickyBackend is { } sticky and not SpacePreviewBackend.None
            ? sticky
            : Resolve(quickLookAvailable, peekAvailable);
}

/// <summary>
/// Arbitrates the space-preview backends behind the QuickLook-era call
/// sites (WidgetManager session machine, surface fallback, transient
/// popup). Each broker instance owns its own stickiness: the
/// WidgetManager broker tracks the widget preview session, the popup
/// broker is toggle-only, so a popup toggle while a widget
/// preview is open behaves exactly as it did with the bare QuickLook
/// service (same backend, toggle semantics).
/// All members are UI-thread confined, like every other preview call
/// site.
/// </summary>
public sealed class SpacePreviewBroker
{
    private readonly QuickLookPreviewService _quickLook;
    private readonly PowerToysPeekPreviewService _peek;

    // Backend the currently open preview session used; null between
    // sessions.
    private SpacePreviewBackend? _activeBackend;

    public SpacePreviewBroker()
        : this(new QuickLookPreviewService(), new PowerToysPeekPreviewService())
    {
    }

    internal SpacePreviewBroker(
        QuickLookPreviewService quickLook,
        PowerToysPeekPreviewService peek)
    {
        _quickLook = quickLook;
        _peek = peek;
    }

    public bool CanPreview(string? path) =>
        QuickLookPreviewService.IsPreviewablePath(path) &&
        (_quickLook.CanPreview(path) || _peek.CanPreview(path));

    public async Task<bool> TryToggleAsync(string path)
    {
        SpacePreviewBackend backend = ResolveFresh(path);
        if (backend is SpacePreviewBackend.None)
        {
            return false;
        }

        bool sent = await SendToggleAsync(backend, path);
        _activeBackend = sent ? backend : null;
        return sent;
    }

    public async Task<bool> TrySwitchAsync(string path)
    {
        SpacePreviewBackend backend = SpacePreviewArbitration.ResolveSwitchBackend(
            _activeBackend,
            _quickLook.CanPreview(path),
            _peek.CanPreview(path));
        if (backend is SpacePreviewBackend.None)
        {
            return false;
        }

        bool sent = await SendSwitchAsync(backend, path);
        if (!sent)
        {
            _activeBackend = null;
        }

        return sent;
    }

    /// <summary>
    /// Closes the preview the session opened. QuickLook has an explicit
    /// close message; Peek re-sends the path the session last showed,
    /// which toggles its open preview closed.
    /// </summary>
    public async Task<bool> TryCloseAsync(string? currentPath)
    {
        if (_activeBackend is not { } backend)
        {
            return false;
        }

        try
        {
            return backend == SpacePreviewBackend.QuickLook
                ? await _quickLook.TryCloseAsync()
                : await _peek.TryCloseAsync(currentPath);
        }
        finally
        {
            _activeBackend = null;
        }
    }

    private SpacePreviewBackend ResolveFresh(string path) =>
        SpacePreviewArbitration.Resolve(
            _quickLook.CanPreview(path),
            _peek.CanPreview(path));

    private Task<bool> SendToggleAsync(SpacePreviewBackend backend, string path) =>
        backend == SpacePreviewBackend.QuickLook
            ? _quickLook.TryToggleAsync(path)
            : _peek.TryToggleAsync(path);

    private Task<bool> SendSwitchAsync(SpacePreviewBackend backend, string path) =>
        backend == SpacePreviewBackend.QuickLook
            ? _quickLook.TrySwitchAsync(path)
            : _peek.TrySwitchAsync(path);
}
