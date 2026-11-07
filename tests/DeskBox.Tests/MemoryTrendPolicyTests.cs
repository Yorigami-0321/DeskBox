using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MemoryTrendPolicyTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static List<MemoryTrendSample> Series(
        int count,
        Func<int, long> bytes,
        TimeSpan? step = null)
    {
        TimeSpan interval = step ?? TimeSpan.FromSeconds(30);
        var samples = new List<MemoryTrendSample>(count);
        for (int index = 0; index < count; index++)
        {
            samples.Add(new MemoryTrendSample(
                Start + interval * index,
                bytes(index)));
        }

        return samples;
    }

    [Fact]
    public void RisingTrend_OverTenMinutesAndEightyMegabytes_Warns()
    {
        List<MemoryTrendSample> samples = Series(
            21,
            index => 150L * 1024 * 1024 + index * 5L * 1024 * 1024);

        Assert.True(MemoryTrendPolicy.IsRisingTrend(samples));
    }

    [Fact]
    public void FlatOrOscillatingSeries_DoesNotWarn()
    {
        List<MemoryTrendSample> flat = Series(
            21,
            _ => 150L * 1024 * 1024);
        List<MemoryTrendSample> oscillating = Series(
            21,
            index => 150L * 1024 * 1024 + (index % 2 == 0 ? 0 : 40L * 1024 * 1024));

        Assert.False(MemoryTrendPolicy.IsRisingTrend(flat));
        Assert.False(MemoryTrendPolicy.IsRisingTrend(oscillating));
    }

    [Fact]
    public void LargeButShortRise_DoesNotWarn()
    {
        List<MemoryTrendSample> samples = Series(
            6,
            index => 150L * 1024 * 1024 + index * 40L * 1024 * 1024);

        Assert.False(MemoryTrendPolicy.IsRisingTrend(samples));
    }

    [Fact]
    public void OneNoiseDip_IsTolerated_TwoDipsAreNot()
    {
        List<MemoryTrendSample> oneDip = Series(
            21,
            index => 150L * 1024 * 1024 + index * 5L * 1024 * 1024 -
                (index == 10 ? 8L * 1024 * 1024 : 0));
        List<MemoryTrendSample> twoDips = Series(
            21,
            index => 150L * 1024 * 1024 + index * 5L * 1024 * 1024 -
                (index == 10 || index == 12 ? 8L * 1024 * 1024 : 0));

        Assert.True(MemoryTrendPolicy.IsRisingTrend(oneDip));
        Assert.False(MemoryTrendPolicy.IsRisingTrend(twoDips));
    }

    [Fact]
    public void Tracker_WarnsOncePerCooldown()
    {
        var tracker = new MemoryTrendTracker();
        List<MemoryTrendSample> samples = Series(
            21,
            index => 150L * 1024 * 1024 + index * 5L * 1024 * 1024);

        bool first = false;
        foreach (MemoryTrendSample sample in samples)
        {
            first |= tracker.Observe(sample);
        }

        Assert.True(first);

        // A second rising window inside the cooldown stays silent.
        var later = new List<MemoryTrendSample>();
        DateTimeOffset laterStart = Start.AddHours(1);
        for (int index = 0; index < 21; index++)
        {
            later.Add(new MemoryTrendSample(
                laterStart + TimeSpan.FromSeconds(30) * index,
                150L * 1024 * 1024 + index * 5L * 1024 * 1024));
        }

        Assert.DoesNotContain(later, sample => tracker.Observe(sample));

        // Past the cooldown it can warn again.
        DateTimeOffset past = Start.AddHours(7);
        var afterCooldown = new List<MemoryTrendSample>();
        for (int index = 0; index < 21; index++)
        {
            afterCooldown.Add(new MemoryTrendSample(
                past + TimeSpan.FromSeconds(30) * index,
                150L * 1024 * 1024 + index * 5L * 1024 * 1024));
        }

        Assert.Contains(afterCooldown, sample => tracker.Observe(sample));
    }
}
