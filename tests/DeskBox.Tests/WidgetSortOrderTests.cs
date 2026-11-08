using System.Collections.ObjectModel;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WidgetSortOrderTests
{
    [Fact]
    public void ApplySortedOrder_ReordersTheCollectionAndReportsMoves()
    {
        WidgetItem a = Item("a");
        WidgetItem b = Item("b");
        WidgetItem c = Item("c");
        var items = new ObservableCollection<WidgetItem> { c, a, b };

        int moves = WidgetViewModel.ApplySortedOrder(items, [a, b, c]);

        Assert.Equal(["a", "b", "c"], items.Select(item => item.Name));
        Assert.True(moves > 0);
    }

    [Fact]
    public void ApplySortedOrder_LeavesAnAlreadySortedCollectionUntouched()
    {
        WidgetItem a = Item("a");
        WidgetItem b = Item("b");
        WidgetItem c = Item("c");
        var items = new ObservableCollection<WidgetItem> { a, b, c };

        int moves = WidgetViewModel.ApplySortedOrder(items, [a, b, c]);

        Assert.Equal(0, moves);
        Assert.Same(a, items[0]);
        Assert.Same(b, items[1]);
        Assert.Same(c, items[2]);
    }

    [Fact]
    public void ApplySortedOrder_HandlesReverseOrder()
    {
        WidgetItem[] original = [Item("a"), Item("b"), Item("c"), Item("d"), Item("e")];
        var items = new ObservableCollection<WidgetItem>(original.Reverse());

        int moves = WidgetViewModel.ApplySortedOrder(items, original);

        Assert.Equal(["a", "b", "c", "d", "e"], items.Select(item => item.Name));
        Assert.Equal(original.Length - 1, moves);
    }

    [Fact]
    public void ApplySortedOrder_KeepsSubsequentIndexLookupsCorrect()
    {
        WidgetItem a = Item("a");
        WidgetItem b = Item("b");
        WidgetItem c = Item("c");
        WidgetItem d = Item("d");
        var items = new ObservableCollection<WidgetItem> { d, b, a, c };

        WidgetViewModel.ApplySortedOrder(items, [a, b, c, d]);

        Assert.Equal(["a", "b", "c", "d"], items.Select(item => item.Name));
    }

    private static WidgetItem Item(string name) => new()
    {
        Name = name,
        Path = $@"C:\sort-test\{name}.txt"
    };
}
