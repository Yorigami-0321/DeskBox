using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetVisualInteractionPolicyTests
{
    [Theory]
    [InlineData(WidgetFeedbackSeverity.Info, 1800)]
    [InlineData(WidgetFeedbackSeverity.Success, 1800)]
    [InlineData(WidgetFeedbackSeverity.Warning, 3000)]
    [InlineData(WidgetFeedbackSeverity.Error, 4500)]
    public void FeedbackDuration_FollowsSeverity(
        WidgetFeedbackSeverity severity,
        int expectedMilliseconds)
    {
        var request = new WidgetFeedbackRequest("message", severity);

        Assert.Equal(
            expectedMilliseconds,
            request.DisplayDuration.TotalMilliseconds);
    }

    [Fact]
    public void FeedbackWithAction_UsesFiveSecondWindow()
    {
        var request = new WidgetFeedbackRequest(
            "deleted",
            WidgetFeedbackSeverity.Info,
            "delete",
            "Undo",
            () => Task.CompletedTask);

        Assert.Equal(5000, request.DisplayDuration.TotalMilliseconds);
    }

    [Fact]
    public void FeedbackWithDismiss_UsesFiveSecondWindow()
    {
        var request = new WidgetFeedbackRequest(
            "copied",
            WidgetFeedbackSeverity.Info,
            "drag-out-result-tip",
            DismissAction: () => Task.CompletedTask);

        Assert.Equal(5000, request.DisplayDuration.TotalMilliseconds);
    }

    [Fact]
    public void CompactProfile_ReducedMotionIsImmediate()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Slow", 220, false);

        Assert.False(profile.IsAnimated);
        Assert.Equal(0, profile.DurationMilliseconds);
        Assert.Equal((1d, 0d), profile.GetOpacity(true, 0.5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactProfile_NeverShowsCompactAndExpandedIdentityTogether(
        bool collapsing)
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        for (int step = 0; step <= 100; step++)
        {
            (double compact, double expanded) =
                profile.GetOpacity(collapsing, step / 100d);

            Assert.False(
                compact > 0.001 && expanded > 0.001,
                $"Both identity layers were visible at step {step}.");
        }
    }

    [Fact]
    public void CompactProfile_ExpansionKeepsLiveContentVisible()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        for (int step = 0; step <= 100; step++)
        {
            Assert.Equal(
                1,
                profile.GetLiveContentOpacity(
                    collapsing: false,
                    progress: step / 100d));
        }
    }

    [Fact]
    public void CompactProfile_CollapseKeepsLiveContentAndRevealsCapsuleEarly()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        Assert.Equal(1, profile.GetLiveContentOpacity(collapsing: true, progress: 0));
        Assert.Equal(1, profile.GetCompactSurfaceOpacity(collapsing: true, progress: 0));
        Assert.Equal(1, profile.GetCompactSurfaceOpacity(collapsing: true, progress: 1));
        Assert.Equal(0, profile.GetCompactTextOpacity(collapsing: true, progress: 0.68));
        Assert.True(profile.GetCompactIdentityOpacity(collapsing: true, progress: 0.68) > 0);
        Assert.True(profile.GetCompactTextOpacity(collapsing: true, progress: 0.8) > 0);
    }

    [Fact]
    public void CompactProfile_CollapseNeverOverlapsLiveAndCompactText()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        for (int step = 0; step <= 100; step++)
        {
            double progress = step / 100d;
            double live = profile.GetLiveContentOpacity(collapsing: true, progress);
            double compactText = profile.GetCompactTextOpacity(collapsing: true, progress);

            Assert.False(
                live > 0.01 && compactText > 0.01,
                $"Live and compact text overlap at step {step}.");
        }
    }
}
