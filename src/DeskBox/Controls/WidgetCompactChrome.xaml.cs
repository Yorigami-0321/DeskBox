using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Controls;

/// <summary>
/// The capsule chrome: a self-contained layer that can be created only for
/// widgets whose collapse behavior actually produces a capsule.
/// </summary>
public sealed partial class WidgetCompactChrome : UserControl
{
    public WidgetCompactChrome()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TitleGlyphProperty =
        DependencyProperty.Register(
            nameof(TitleGlyph),
            typeof(string),
            typeof(WidgetCompactChrome),
            new PropertyMetadata("\uE8A5"));

    public static readonly DependencyProperty TitleIconKindProperty =
        DependencyProperty.Register(
            nameof(TitleIconKind),
            typeof(string),
            typeof(WidgetCompactChrome),
            new PropertyMetadata(WidgetTitleIconKindNames.Default));

    public static readonly DependencyProperty TitleIconAccentColorProperty =
        DependencyProperty.Register(
            nameof(TitleIconAccentColor),
            typeof(Color),
            typeof(WidgetCompactChrome),
            new PropertyMetadata(AccentColorHelper.DefaultAccentColor));

    public static readonly DependencyProperty TitleIconCustomEmojiProperty =
        DependencyProperty.Register(
            nameof(TitleIconCustomEmoji),
            typeof(string),
            typeof(WidgetCompactChrome),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleIconCustomSourceProperty =
        DependencyProperty.Register(
            nameof(TitleIconCustomSource),
            typeof(ImageSource),
            typeof(WidgetCompactChrome),
            new PropertyMetadata(null));

    public static readonly DependencyProperty OverlayTitleProperty =
        DependencyProperty.Register(
            nameof(OverlayTitle),
            typeof(string),
            typeof(WidgetCompactChrome),
            new PropertyMetadata(string.Empty));

    public string TitleGlyph
    {
        get => (string)GetValue(TitleGlyphProperty);
        set => SetValue(TitleGlyphProperty, value);
    }

    public string TitleIconKind
    {
        get => (string)GetValue(TitleIconKindProperty);
        set => SetValue(TitleIconKindProperty, value);
    }

    public Color TitleIconAccentColor
    {
        get => (Color)GetValue(TitleIconAccentColorProperty);
        set => SetValue(TitleIconAccentColorProperty, value);
    }

    public string TitleIconCustomEmoji
    {
        get => (string)GetValue(TitleIconCustomEmojiProperty);
        set => SetValue(TitleIconCustomEmojiProperty, value);
    }

    public ImageSource? TitleIconCustomSource
    {
        get => (ImageSource?)GetValue(TitleIconCustomSourceProperty);
        set => SetValue(TitleIconCustomSourceProperty, value);
    }

    public string OverlayTitle
    {
        get => (string)GetValue(OverlayTitleProperty);
        set => SetValue(OverlayTitleProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? LayerGotFocus;
    public event EventHandler<RoutedEventArgs>? LayerLostFocus;
    public event EventHandler<PointerRoutedEventArgs>? DragPointerCaptureLost;
    public event EventHandler<PointerRoutedEventArgs>? DragPointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? DragPointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? DragPointerReleased;
    public event EventHandler<RightTappedRoutedEventArgs>? LayerRightTapped;
    public event EventHandler<SizeChangedEventArgs>? LayerSizeChanged;

    public event EventHandler<PointerRoutedEventArgs>? IdentityPointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? IdentityPointerExited;
    public event EventHandler<PointerRoutedEventArgs>? MoveRegionPointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? MoveRegionPointerExited;
    public event EventHandler<PointerRoutedEventArgs>? TextPointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? TextPointerExited;
    public event EventHandler<PointerRoutedEventArgs>? ActionRegionPointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? ActionRegionPointerExited;
    public event EventHandler<PointerRoutedEventArgs>? ReorderPointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? ReorderPointerExited;
    public event EventHandler<PointerRoutedEventArgs>? ActionHostPointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? ActionHostPointerExited;

    public event EventHandler<RoutedEventArgs>? PrimaryActionClicked;
    public event EventHandler<RoutedEventArgs>? PreviousClicked;
    public event EventHandler<RoutedEventArgs>? PlayPauseClicked;
    public event EventHandler<RoutedEventArgs>? NextClicked;
    public event EventHandler<RoutedEventArgs>? ExpandClicked;

    private void CollapsedChromeLayer_GotFocus(object sender, RoutedEventArgs e) =>
        LayerGotFocus?.Invoke(this, e);

    private void CollapsedChromeLayer_LostFocus(object sender, RoutedEventArgs e) =>
        LayerLostFocus?.Invoke(this, e);

    private void OverlayDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        DragPointerCaptureLost?.Invoke(this, e);

    private void OverlayDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        DragPointerMoved?.Invoke(this, e);

    private void OverlayDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        DragPointerPressed?.Invoke(this, e);

    private void OverlayDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        DragPointerReleased?.Invoke(this, e);

    private void CollapsedChromeLayer_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        LayerRightTapped?.Invoke(this, e);

    private void CollapsedChromeLayer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        LayerSizeChanged?.Invoke(this, e);

    private void CompactIdentityHost_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        IdentityPointerEntered?.Invoke(this, e);

    private void CompactIdentityHost_PointerExited(object sender, PointerRoutedEventArgs e) =>
        IdentityPointerExited?.Invoke(this, e);

    private void CompactMoveHitRegion_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        MoveRegionPointerEntered?.Invoke(this, e);

    private void CompactMoveHitRegion_PointerExited(object sender, PointerRoutedEventArgs e) =>
        MoveRegionPointerExited?.Invoke(this, e);

    private void CompactTextContainer_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        TextPointerEntered?.Invoke(this, e);

    private void CompactTextContainer_PointerExited(object sender, PointerRoutedEventArgs e) =>
        TextPointerExited?.Invoke(this, e);

    private void CompactActionRegionTrigger_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ActionRegionPointerEntered?.Invoke(this, e);

    private void CompactActionRegionTrigger_PointerExited(object sender, PointerRoutedEventArgs e) =>
        ActionRegionPointerExited?.Invoke(this, e);

    private void CompactReorderHandle_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ReorderPointerEntered?.Invoke(this, e);

    private void CompactReorderHandle_PointerExited(object sender, PointerRoutedEventArgs e) =>
        ReorderPointerExited?.Invoke(this, e);

    private void CompactActionHost_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ActionHostPointerEntered?.Invoke(this, e);

    private void CompactActionHost_PointerExited(object sender, PointerRoutedEventArgs e) =>
        ActionHostPointerExited?.Invoke(this, e);

    private void CompactPrimaryActionButton_Click(object sender, RoutedEventArgs e) =>
        PrimaryActionClicked?.Invoke(this, e);

    private void CompactPreviousButton_Click(object sender, RoutedEventArgs e) =>
        PreviousClicked?.Invoke(this, e);

    private void CompactPlayPauseButton_Click(object sender, RoutedEventArgs e) =>
        PlayPauseClicked?.Invoke(this, e);

    private void CompactNextButton_Click(object sender, RoutedEventArgs e) =>
        NextClicked?.Invoke(this, e);

    private void CompactExpandButton_Click(object sender, RoutedEventArgs e) =>
        ExpandClicked?.Invoke(this, e);
}
