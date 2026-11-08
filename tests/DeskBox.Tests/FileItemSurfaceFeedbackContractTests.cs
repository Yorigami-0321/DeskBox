using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileItemSurfaceFeedbackContractTests
{
    [Theory]
    [InlineData("SurfaceFileIconTemplate")]
    [InlineData("SurfaceFileListTemplate")]
    [InlineData("StackPopoverFileIconTemplate")]
    [InlineData("StackPopoverFileListTemplate")]
    public void Templates_WireFeedbackBeforeLoadedAndKeepItAcrossReuse(string templateKey)
    {
        XDocument document = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement template = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == templateKey);
        XElement surface = template.Descendants(presentation + "Border").Single(element =>
            (string?)element.Attribute("Tag") == "InteractiveSurface");

        Assert.Equal("ItemSurface_Loaded", (string?)surface.Attribute("Loaded"));
        Assert.Equal("ItemSurface_Unloaded", (string?)surface.Attribute("Unloaded"));
        Assert.Equal("ItemSurface_DataContextChanged", (string?)surface.Attribute("DataContextChanged"));
        Assert.Equal("ItemSurface_PointerEntered", (string?)surface.Attribute("PointerEntered"));
        Assert.Equal("ItemSurface_PointerExited", (string?)surface.Attribute("PointerExited"));
        Assert.Equal("ItemSurface_PointerPressed", (string?)surface.Attribute("PointerPressed"));
        Assert.Equal("ItemSurface_PointerReleased", (string?)surface.Attribute("PointerReleased"));
        Assert.Equal("ItemSurface_PointerGestureEnded", (string?)surface.Attribute("PointerCaptureLost"));
    }

    [Fact]
    public void Surface_ProvidesAHitTestBackgroundBeforeFirstPaint()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement[] surfaces = document.Descendants(presentation + "Border")
            .Where(element => (string?)element.Attribute("Tag") == "InteractiveSurface")
            .ToArray();

        Assert.Equal(4, surfaces.Length);
        Assert.All(surfaces, surface =>
            Assert.Equal("Transparent", (string?)surface.Attribute("Background")));
    }

    [Fact]
    public void LoadedAndUnloaded_DoNotOwnFeedbackSubscriptionsOrOverwriteHover()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        int start = source.IndexOf("private void ItemSurface_Loaded(", StringComparison.Ordinal);
        int end = source.IndexOf("private void ItemSurface_PointerEntered(", StringComparison.Ordinal);
        string lifecycle = source[start..end];

        Assert.DoesNotContain("VisualStateChanged +=", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualStateChanged -=", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContextChanged +=", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContextChanged -=", lifecycle, StringComparison.Ordinal);
        Assert.Contains("FileItemSurface.ApplyLayoutMetrics(border)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ApplyItemSurfaceVisual(border, tile.VisualState)", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void MovementRecovery_OnlyUpdatesTheLocalVisualAndDoesNotConsumeInput()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs"));
        Assert.Contains("UIElement.PointerMovedEvent", source, StringComparison.Ordinal);
        Assert.Contains("new PointerEventHandler(ItemSurface_PointerMoved)", source, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", source, StringComparison.Ordinal);
        int start = source.IndexOf("private void ItemSurface_PointerMoved(", StringComparison.Ordinal);
        int end = source.IndexOf("private void ItemSurface_PointerPressed(", StringComparison.Ordinal);
        string moved = source[start..end];

        Assert.Contains("tile.OnPointerMoved(", moved, StringComparison.Ordinal);
        Assert.Contains("point.IsInContact", moved, StringComparison.Ordinal);
        Assert.Contains("border.ActualWidth", moved, StringComparison.Ordinal);
        Assert.Contains("border.ActualHeight", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("e.Handled", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePointer", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItems", moved, StringComparison.Ordinal);
    }
}
