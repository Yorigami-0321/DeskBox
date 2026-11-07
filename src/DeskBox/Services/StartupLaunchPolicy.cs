namespace DeskBox.Services;

internal static class StartupLaunchPolicy
{
    private const string StartupArgument = "--startup";
    private const string LazyRestoreArgument = "--lazy-restore";

    public static bool IsStartupLaunch(
        IEnumerable<string> processArguments,
        string? activationArguments = null,
        bool isStartupTaskActivation = false)
    {
        return isStartupTaskActivation ||
               processArguments.Any(IsStartupArgument) ||
               ParseActivationArguments(activationArguments).Any(IsStartupArgument);
    }

    /// <summary>
    /// The idle process recycle relaunches without any visible widget, so the
    /// restored windows would only burn the memory the recycle just released.
    /// This flag defers window creation to the first explicit reveal.
    /// </summary>
    public static bool IsLazyRestoreLaunch(IEnumerable<string> processArguments)
    {
        return processArguments.Any(argument => string.Equals(
            argument.Trim().Trim('"'),
            LazyRestoreArgument,
            StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ParseActivationArguments(string? arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? []
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsStartupArgument(string argument)
    {
        return string.Equals(
            argument.Trim().Trim('"'),
            StartupArgument,
            StringComparison.OrdinalIgnoreCase);
    }
}
