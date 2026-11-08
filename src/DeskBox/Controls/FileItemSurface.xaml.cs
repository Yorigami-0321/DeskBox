using System.Runtime.CompilerServices;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public enum FileItemSurfaceMode
{
    Icon,
    List
}

/// <summary>
/// Behavior for one realized inline file tile. The tile visual tree lives in
/// the item templates; this state carries the interaction bits that used to
/// live inside the FileItemSurface UserControl.
/// </summary>
internal sealed class FileItemTileState
{
    private static readonly ConditionalWeakTable<Border, FileItemTileState> States = new();

    /// <summary>Edge slack so the zone stays clickable on thin rows.</summary>
    private const double PrimaryContentZoneInflation = 4;

    private FileItemPointerFeedback _pointerFeedback;
    private FileTransferPathState _transferState = FileTransferPathState.None;
    private string _transferStatusText = string.Empty;
    private bool _isOpening;
    private string _openingStatusText = string.Empty;
    private bool _partsCached;
    private bool _pointerInside;
    private Grid? _slot;
    private Grid? _iconHost;
    private TextBlock? _nameText;
    private TextBlock? _detailText;
    private FrameworkElement? _listTextHost;
    private Image? _iconImage;
    private Viewbox? _fallbackIcon;
    private StackPanel? _contentStack;
    private Border? _activityBadge;
    private ContentControl? _activityBadgeRing;
    private ToolTip? _toolTip;
    private TextBlock? _toolTipStatus;
    private TextBlock? _toolTipPath;
    private bool _toolTipAttached;

    private FileItemTileState(Border border)
    {
        Border = border;
    }

    internal static FileItemTileState For(Border border) =>
        States.GetValue(border, static value => new FileItemTileState(value));

    public Border Border { get; }

    public FileItemSurfaceMode Mode => FileItemSurface.GetMode(Border);

    public bool IsListMode => Mode == FileItemSurfaceMode.List;

    public WidgetItem? DataContext => Border.DataContext as WidgetItem;

    public WidgetViewModel? LayoutContext =>
        FileItemSurface.GetLayoutContext(Border);

    public FileItemSurfaceVisualState VisualState { get; set; }

    internal bool PointerMovedHandlerAttached { get; set; }

    public bool IsTransferActive => _transferState.IsActive;

    public bool IsOpening => _isOpening;

    public bool IsActivityActive => IsTransferActive || IsOpening;

    public string ActivityStatusText =>
        IsTransferActive ? _transferStatusText : _openingStatusText;

    public TextBlock? ItemNameText
    {
        get
        {
            EnsureParts();
            return _nameText;
        }
    }

    public FrameworkElement? IconHitTestElement
    {
        get
        {
            EnsureParts();
            return _iconHost;
        }
    }

    internal bool IsPointWithinListPrimaryContent(Windows.Foundation.Point point)
    {
        EnsureParts();
        if (_iconHost is not { } iconHost ||
            _listTextHost is not { } textHost)
        {
            return true;
        }

        return FileItemSelectionGeometry.BuildPrimaryContentZone(
                BoundsRelativeToRoot(iconHost),
                BoundsRelativeToRoot(textHost),
                PrimaryContentZoneInflation)
            .Contains(point);

        Windows.Foundation.Rect BoundsRelativeToRoot(FrameworkElement element) =>
            element.TransformToVisual(Border)
                .TransformBounds(new Windows.Foundation.Rect(
                    0,
                    0,
                    element.ActualWidth,
                    element.ActualHeight));
    }

    internal FileItemSurfaceVisualState OnOpenDispatched() =>
        _pointerFeedback.OnOpenDispatched();

    internal FileItemSurfaceVisualState OnPointerEntered()
    {
        _pointerInside = true;
        return _pointerFeedback.OnPointerEntered();
    }

    internal FileItemSurfaceVisualState OnPointerExited()
    {
        _pointerInside = false;
        return FileItemSurfaceVisualState.Normal;
    }

    internal FileItemSurfaceVisualState OnPointerPressed() =>
        _pointerFeedback.OnPointerPressed();

    internal FileItemSurfaceVisualState OnPointerMoved(
        bool isInContact,
        double x,
        double y) =>
        _pointerFeedback.OnPointerMoved(VisualState, isInContact, x, y);

    internal void RecordPointerPosition(double x, double y) =>
        _pointerFeedback.RecordPointerPosition(x, y);

    internal FileItemSurfaceVisualState OnPointerReleased(bool inside) =>
        _pointerFeedback.OnPointerReleased(inside);

    internal void ResetPointerForLoaded()
    {
        ResetPointerFeedback();
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    internal void ResetForReuse()
    {
        ResetPointerFeedback();
        SetVisualState(FileItemSurfaceVisualState.Normal);
        SetOpeningState(false, string.Empty);
        SetTransferState(FileTransferPathState.None, string.Empty);
    }

    internal void ResetPointerFeedback()
    {
        _pointerInside = false;
        _pointerFeedback.ResetForReuse();
    }

    internal void SetVisualState(FileItemSurfaceVisualState state) =>
        VisualState = state;

    internal void ClearPointerFeedbackAfterOpen() =>
        SetVisualState(OnOpenDispatched());

    internal void SetTransferState(
        FileTransferPathState state,
        string? statusText)
    {
        string normalizedStatus = statusText ?? string.Empty;
        if (_transferState == state &&
            string.Equals(
                _transferStatusText,
                normalizedStatus,
                StringComparison.Ordinal))
        {
            return;
        }

        _transferState = state;
        _transferStatusText = normalizedStatus;
        UpdateActivityPresentation();
    }

    internal void SetOpeningState(bool isOpening, string? statusText)
    {
        string normalizedStatus = statusText ?? string.Empty;
        if (_isOpening == isOpening &&
            string.Equals(
                _openingStatusText,
                normalizedStatus,
                StringComparison.Ordinal))
        {
            return;
        }

        _isOpening = isOpening;
        _openingStatusText = normalizedStatus;
        UpdateActivityPresentation();
    }

    internal void ApplyLayoutMetrics()
    {
        EnsureParts();
        WidgetViewModel? layout = LayoutContext;
        if (Mode == FileItemSurfaceMode.Icon)
        {
            if (_slot is not null && layout is not null)
            {
                _slot.Width = layout.IconTileWidth;
                _slot.Height = layout.IconTileHeight;
                _slot.Margin = layout.IconTileMargin;
            }

            Border.Margin = new Thickness(1, 0, 1, 0);
            if (layout is null)
            {
                RefreshToolTip();
                return;
            }

            Border.Padding = layout.IconTilePadding;
            if (_contentStack is not null)
            {
                _contentStack.Spacing = layout.IconContentSpacing;
            }

            SetIconSize(layout.IconImageSize);
            if (_nameText is not null)
            {
                _nameText.MaxWidth = layout.IconLabelMaxWidth;
                _nameText.FontSize = layout.IconLabelFontSize;
                _nameText.MaxLines = layout.IconLabelMaxLines;
                _nameText.Visibility = layout.IconLabelVisibility;
            }
        }
        else if (layout is not null)
        {
            Border.Padding = layout.ListItemPadding;
            Thickness margin = layout.ListItemMargin;
            if (FileItemSurface.GetUseStackChildIndent(Border) &&
                DataContext is { IsStackChild: true })
            {
                margin = new Thickness(
                    margin.Left + 18,
                    margin.Top,
                    margin.Right,
                    margin.Bottom);
            }

            Border.Margin = margin;
            SetIconSize(layout.ListIconSize);
            if (_iconHost is not null)
            {
                _iconHost.Width = layout.ListIconSize;
            }

            if (_nameText is not null)
            {
                _nameText.FontSize = layout.ListLabelFontSize;
            }

            if (_detailText is not null)
            {
                _detailText.FontSize = layout.ListItemDetailFontSize;
                _detailText.Visibility = layout.ListItemDetailVisibility;
            }
        }

        RefreshToolTip();
    }

    internal void RefreshToolTip()
    {
        bool hasStatus = !string.IsNullOrWhiteSpace(ActivityStatusText);
        bool hasPath = LayoutContext?.ShowFileItemPathTooltips == true;
        if (!hasStatus && !hasPath)
        {
            if (_toolTipAttached)
            {
                ToolTipService.SetToolTip(Border, null);
                _toolTipAttached = false;
            }

            return;
        }

        // Keep the first frame free of one ToolTip + two TextBlocks per tile.
        // Create and attach only once a pointer actually enters; the tooltip
        // service still sees it before the hover delay elapses.
        if (!_pointerInside)
        {
            return;
        }

        EnsureToolTip();
        if (_toolTipStatus is not null)
        {
            _toolTipStatus.Text = ActivityStatusText;
            _toolTipStatus.Visibility = hasStatus
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_toolTipPath is not null)
        {
            _toolTipPath.Text = DataContext?.FullPath ?? string.Empty;
            _toolTipPath.Visibility = hasPath
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!_toolTipAttached && _toolTip is not null)
        {
            ToolTipService.SetToolTip(Border, _toolTip);
            _toolTipAttached = true;
        }
    }

    internal void DetachToolTip()
    {
        if (_toolTipAttached)
        {
            ToolTipService.SetToolTip(Border, null);
            _toolTipAttached = false;
        }
    }

    private void UpdateActivityPresentation()
    {
        EnsureParts();
        AutomationProperties.SetItemStatus(Border, ActivityStatusText);
        bool active = IsActivityActive;
        if (_activityBadge is not null)
        {
            _activityBadge.Visibility = active
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (active)
        {
            if (_activityBadgeRing is not null &&
                _activityBadgeRing.Content is null)
            {
                _activityBadgeRing.Content = new ProgressRing
                {
                    Width = 13,
                    Height = 13,
                    IsActive = true
                };
            }
        }
        else if (_activityBadgeRing?.Content is not null)
        {
            _activityBadgeRing.Content = null;
        }

        RefreshToolTip();
    }

    private void EnsureToolTip()
    {
        if (_toolTip is not null)
        {
            return;
        }

        _toolTipStatus = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Visibility = Visibility.Collapsed
        };
        _toolTipPath = new TextBlock
        {
            MaxWidth = 360,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(_toolTipStatus);
        content.Children.Add(_toolTipPath);
        _toolTip = new ToolTip { Content = content };
    }

    private void SetIconSize(double size)
    {
        if (_iconImage is not null)
        {
            _iconImage.Width = size;
            _iconImage.Height = size;
        }

        if (_fallbackIcon is not null)
        {
            _fallbackIcon.Width = size;
            _fallbackIcon.Height = size;
        }
    }

    private void EnsureParts()
    {
        if (_partsCached)
        {
            return;
        }

        _slot = FindAncestorByTag(Border, "ItemSlot") as Grid;
        CacheParts(Border);
        // Attached presentation properties are assigned while the template is
        // still being inflated. Keep re-scanning until the content exists;
        // afterwards the parts are stable for the container's lifetime.
        _partsCached = _nameText is not null;
    }

    private void CacheParts(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element &&
                element.Tag is string tag)
            {
                switch (tag)
                {
                    case "ItemIconHost":
                        _iconHost ??= element as Grid;
                        break;
                    case "ItemNameText":
                        _nameText ??= element as TextBlock;
                        break;
                    case "ListItemTextHost":
                        _listTextHost ??= element;
                        break;
                    case "ListItemDetailText":
                        _detailText ??= element as TextBlock;
                        break;
                    case "ItemIconImage":
                        _iconImage ??= element as Image;
                        break;
                    case "ItemFallbackIcon":
                        _fallbackIcon ??= element as Viewbox;
                        break;
                    case "ItemContentStack":
                        _contentStack ??= element as StackPanel;
                        break;
                    case "ActivityBadge":
                        _activityBadge ??= element as Border;
                        break;
                    case "ActivityBadgeRing":
                        _activityBadgeRing ??= element as ContentControl;
                        break;
                }
            }

            CacheParts(child);
        }
    }

    private static FrameworkElement? FindAncestorByTag(
        DependencyObject source,
        string tag)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(source);
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                string.Equals(element.Tag as string, tag, StringComparison.Ordinal))
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

/// <summary>
/// Static helpers for the inline file tile. The visual tree is owned by the
/// item templates; this type resolves the interactive border, per-tile state,
/// and attached presentation values.
/// </summary>
public static class FileItemSurface
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(FileItemSurfaceMode),
            typeof(FileItemSurface),
            new PropertyMetadata(
                FileItemSurfaceMode.Icon,
                OnPresentationPropertyChanged));

    public static readonly DependencyProperty LayoutContextProperty =
        DependencyProperty.RegisterAttached(
            "LayoutContext",
            typeof(WidgetViewModel),
            typeof(FileItemSurface),
            new PropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty UseStackChildIndentProperty =
        DependencyProperty.RegisterAttached(
            "UseStackChildIndent",
            typeof(bool),
            typeof(FileItemSurface),
            new PropertyMetadata(false, OnPresentationPropertyChanged));

    public static FileItemSurfaceMode GetMode(DependencyObject element) =>
        (FileItemSurfaceMode)element.GetValue(ModeProperty);

    public static void SetMode(
        DependencyObject element,
        FileItemSurfaceMode value) =>
        element.SetValue(ModeProperty, value);

    public static WidgetViewModel? GetLayoutContext(DependencyObject element) =>
        (WidgetViewModel?)element.GetValue(LayoutContextProperty);

    public static void SetLayoutContext(
        DependencyObject element,
        WidgetViewModel? value) =>
        element.SetValue(LayoutContextProperty, value);

    public static bool GetUseStackChildIndent(DependencyObject element) =>
        (bool)element.GetValue(UseStackChildIndentProperty);

    public static void SetUseStackChildIndent(
        DependencyObject element,
        bool value) =>
        element.SetValue(UseStackChildIndentProperty, value);

    public static Border? TryGetInteractiveBorder(object? source) =>
        source is Border border && IsInteractiveSurface(border) ? border : null;

    public static Border? FindInteractiveBorder(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Border border && IsInteractiveSurface(border))
            {
                return border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    internal static FileItemTileState? FindOwner(DependencyObject source) =>
        FindInteractiveBorder(source) is { } border
            ? FileItemTileState.For(border)
            : null;

    public static bool IsInteractiveSurface(Border border) =>
        string.Equals(
            border.Tag as string,
            "InteractiveSurface",
            StringComparison.Ordinal);

    internal static void ApplyLayoutMetrics(Border border) =>
        FileItemTileState.For(border).ApplyLayoutMetrics();

    private static void OnPresentationPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is Border border && IsInteractiveSurface(border))
        {
            FileItemTileState.For(border).ApplyLayoutMetrics();
        }
    }
}
