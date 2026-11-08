using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileItemSurfaceLayoutContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void InteractionShells_RemainEagerAndKeepTheirHitTestAndPointerContract()
    {
        XDocument document = LoadSurface();
        XElement[] templates = GetItemTemplates(document).ToArray();
        Assert.Equal(4, templates.Length);

        foreach (XElement template in templates)
        {
            XElement border = template.Descendants(Presentation + "Border")
                .Single(element =>
                    (string?)element.Attribute("Tag") == "InteractiveSurface");
            Assert.Equal("Transparent", (string?)border.Attribute("Background"));
            Assert.Equal("2", (string?)border.Attribute("CornerRadius"));
            Assert.Equal("True", (string?)border.Attribute("AllowDrop"));
            Assert.Null(border.Attribute("DataContext"));

            foreach (string eventName in new[]
            {
                "Loaded", "Unloaded", "DataContextChanged",
                "PointerEntered", "PointerExited",
                "PointerPressed", "PointerReleased", "PointerCaptureLost",
                "DragLeave", "DragOver", "Drop"
            })
            {
                Assert.StartsWith(
                    "ItemSurface_",
                    (string?)border.Attribute(eventName),
                    StringComparison.Ordinal);
            }

            // The former UserControl wrapper is gone: the interaction shell is
            // part of the compiled item template.
            Assert.DoesNotContain(
                template.Descendants(),
                element => element.Name.LocalName == "FileItemSurface");
        }
    }

    [Fact]
    public void Presentations_KeepFileIdentityOnCompiledBindings()
    {
        XDocument document = LoadSurface();
        foreach (XElement template in GetItemTemplates(document))
        {
            Assert.Equal("models:WidgetItem", (string?)template.Attribute(Xaml + "DataType"));

            XElement image = template.Descendants(Presentation + "Image").Single();
            Assert.Equal("{x:Bind Icon, Mode=OneWay}", (string?)image.Attribute("Source"));
            Assert.Equal(
                "{x:Bind IconVisibility, Mode=OneWay}",
                (string?)image.Attribute("Visibility"));

            XElement name = template.Descendants(Presentation + "TextBlock").Single(element =>
                (string?)element.Attribute("Tag") == "ItemNameText");
            Assert.Equal("{x:Bind Name, Mode=OneWay}", (string?)name.Attribute("Text"));

            // The presentation must never replace the inherited WidgetItem
            // DataContext with a layout wrapper or owner.
            Assert.DoesNotContain(
                template.DescendantsAndSelf(),
                element => element.Attribute("DataContext") is not null);
        }

        string state = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));
        Assert.Contains(
            "_nameText.MaxWidth = layout.IconLabelMaxWidth;",
            state,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nameText.Visibility = layout.IconLabelVisibility;",
            state,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailText.Visibility = layout.ListItemDetailVisibility;",
            state,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SurfaceFileIconTemplate")]
    [InlineData("SurfaceFileListTemplate")]
    public void Presentations_DoNotIntroduceInputHandlersAndKeepActivityBadgeNonInteractive(
        string templateKey)
    {
        XElement template = GetTemplate(templateKey);
        XElement border = template.Descendants(Presentation + "Border")
            .Single(element =>
                (string?)element.Attribute("Tag") == "InteractiveSurface");
        string[] inputAttributes =
        [
            "AllowDrop", "DragOver", "DragEnter", "DragLeave", "Drop", "DragStarting",
            "PointerPressed", "PointerReleased", "PointerMoved", "Tapped", "DoubleTapped"
        ];

        // Only the interaction shell owns input; the inline presentation below
        // it stays passive.
        XElement content = border.Elements().Single();
        Assert.DoesNotContain(content.DescendantsAndSelf().Attributes(), attribute =>
            inputAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal));

        XElement badge = border.Descendants(Presentation + "Border").Single(element =>
            (string?)element.Attribute("Tag") == "ActivityBadge");
        Assert.Equal("False", (string?)badge.Attribute("IsHitTestVisible"));
        Assert.Equal("Collapsed", (string?)badge.Attribute("Visibility"));
        XElement ring = badge.Element(Presentation + "ContentControl")!;
        Assert.Equal("ActivityBadgeRing", (string?)ring.Attribute("Tag"));
    }

    private static IEnumerable<XElement> GetItemTemplates(XDocument document) =>
        document.Descendants(Presentation + "DataTemplate")
            .Where(element => (string?)element.Attribute(Xaml + "Key") is
                "SurfaceFileIconTemplate" or
                "SurfaceFileListTemplate" or
                "StackPopoverFileIconTemplate" or
                "StackPopoverFileListTemplate");

    private static XElement GetTemplate(string key) => LoadSurface()
        .Descendants(Presentation + "DataTemplate")
        .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XDocument LoadSurface() => XDocument.Load(TestPaths.FromRepository(
        "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
}
