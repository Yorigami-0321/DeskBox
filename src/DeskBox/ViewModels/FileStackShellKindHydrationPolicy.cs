namespace DeskBox.ViewModels;

internal static class FileStackShellKindHydrationPolicy
{
    public static bool RequiresFullUniverse(
        bool usesStackProjection,
        bool autoStacking) =>
        usesStackProjection && autoStacking;
}
