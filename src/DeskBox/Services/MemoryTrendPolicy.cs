namespace DeskBox.Services;

internal readonly record struct MemoryTrendSample(
    DateTimeOffset AtUtc,
    long PrivateMinusGcHeapBytes);

/// <summary>
/// Detects slow native/heap growth over a quiet window. Log-only: the
/// warning marks a long-session trend, it never restarts or prompts.
/// </summary>
internal static class MemoryTrendPolicy
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);
    internal const int MinimumSamples = 8;
    internal const long MinimumGrowthBytes = 80L * 1024 * 1024;
    internal const double MinimumGrowthRatio = 0.30;
    internal const int MaximumDips = 1;

    public static bool IsRisingTrend(IReadOnlyList<MemoryTrendSample> samples)
    {
        if (samples.Count < MinimumSamples)
        {
            return false;
        }

        MemoryTrendSample first = samples[0];
        MemoryTrendSample last = samples[^1];
        if (last.AtUtc - first.AtUtc < MinimumSpan)
        {
            return false;
        }

        long requiredGrowth = Math.Max(
            MinimumGrowthBytes,
            (long)(first.PrivateMinusGcHeapBytes * MinimumGrowthRatio));
        if (last.PrivateMinusGcHeapBytes - first.PrivateMinusGcHeapBytes <
            requiredGrowth)
        {
            return false;
        }

        int dips = 0;
        for (int index = 1; index < samples.Count; index++)
        {
            if (samples[index].PrivateMinusGcHeapBytes <
                samples[index - 1].PrivateMinusGcHeapBytes)
            {
                dips++;
                if (dips > MaximumDips)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

internal sealed class MemoryTrendTracker
{
    private readonly List<MemoryTrendSample> _samples = [];
    private DateTimeOffset? _lastWarningAtUtc;

    public bool Observe(MemoryTrendSample sample)
    {
        _samples.Add(sample);
        DateTimeOffset cutoff = sample.AtUtc - MemoryTrendPolicy.Window;
        _samples.RemoveAll(candidate => candidate.AtUtc < cutoff);

        if (_lastWarningAtUtc is { } lastWarning &&
            sample.AtUtc - lastWarning < MemoryTrendPolicy.Cooldown)
        {
            return false;
        }

        if (!MemoryTrendPolicy.IsRisingTrend(_samples))
        {
            return false;
        }

        _lastWarningAtUtc = sample.AtUtc;
        _samples.Clear();
        return true;
    }
}
