using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class FileStackShellKindHydrationPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void RequiresFullUniverse_OnlyWhenProjectionAndAutoStackingAreOn(
        bool usesStackProjection,
        bool autoStacking,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileStackShellKindHydrationPolicy.RequiresFullUniverse(
                usesStackProjection,
                autoStacking));
    }
}
