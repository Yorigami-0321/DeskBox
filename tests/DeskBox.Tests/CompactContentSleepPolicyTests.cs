using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class CompactContentSleepPolicyTests
{
    [Fact]
    public void Delay_IsBounded()
    {
        Assert.InRange(
            CompactContentSleepPolicy.Delay,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void ShouldSleep_RequiresCollapsedIdleWakableContent()
    {
        Assert.True(CompactContentSleepPolicy.ShouldSleep(
            isCollapsed: true,
            isClosing: false,
            contentBusy: false,
            hasWakeFactory: true,
            hasContent: true,
            alreadySleeping: false));
    }

    [Theory]
    [InlineData(false, false, false, true, true, false)] // expanded
    [InlineData(true, true, false, true, true, false)]  // closing
    [InlineData(true, false, true, true, true, false)]  // busy
    [InlineData(true, false, false, false, true, false)] // no wake factory
    [InlineData(true, false, false, true, false, false)] // no content
    [InlineData(true, false, false, true, true, true)]   // already asleep
    public void ShouldSleep_RejectsAnyBrokenPrecondition(
        bool isCollapsed,
        bool isClosing,
        bool contentBusy,
        bool hasWakeFactory,
        bool hasContent,
        bool alreadySleeping)
    {
        Assert.False(CompactContentSleepPolicy.ShouldSleep(
            isCollapsed,
            isClosing,
            contentBusy,
            hasWakeFactory,
            hasContent,
            alreadySleeping));
    }
}
