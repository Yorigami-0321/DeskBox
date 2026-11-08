using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class JumpListServiceTests
{
    [Theory]
    [InlineData("--toggle-widgets", true)]
    [InlineData("--TOGGLE-WIDGETS", true)]
    [InlineData("\"--toggle-widgets\"", true)]
    [InlineData("--open-settings", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsToggleWidgetsActivation_RecognizesOnlyTheToggleArgument(
        string? argument,
        bool expected)
    {
        Assert.Equal(
            expected,
            JumpListService.IsToggleWidgetsActivation(argument));
    }
}
