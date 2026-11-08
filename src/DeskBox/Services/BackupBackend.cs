using DeskBox.Contracts;

namespace DeskBox.Services;

internal sealed class BackupBackend(
    SettingsService settings,
    DeskBoxDataBackupService local,
    CloudBackupService cloud) : IBackupBackend
{
    public bool LocalScheduleEnabled => local.AutomaticBackupOptions.IsEnabled;
    public bool CloudScheduleEnabled => cloud.Options.IsConfigured;

    public bool RefreshOptions()
    {
        // Record value equality covers every effective field. LastSuccessUtc
        // is excluded on the cloud side: the service updates it in memory on
        // every successful upload, and a failed settings persist would then
        // make every refresh look "changed" forever.
        AutomaticBackupOptions previousLocal = local.AutomaticBackupOptions;
        CloudBackupOptions previousCloud = cloud.Options;
        local.UpdateAutomaticBackupOptions(DataBackupSettingsPolicy.GetOptions(settings.Settings));
        cloud.UpdateOptions(CloudBackupSettingsPolicy.GetOptions(settings.Settings));
        bool localChanged = previousLocal != local.AutomaticBackupOptions;
        bool cloudChanged =
            previousCloud with { LastSuccessUtc = DateTimeOffset.MinValue } !=
            cloud.Options with { LastSuccessUtc = DateTimeOffset.MinValue };
        return localChanged || cloudChanged;
    }

    public async Task PrepareManualBackupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await settings.FlushPendingSaveAsync(notifySubscribers: false))
            throw new IOException("Settings could not be saved before creating a backup.");
        cancellationToken.ThrowIfCancellationRequested();
        RefreshOptions();
    }

    public Task<LocalBackupResult> CreateSnapshotAsync(bool force, CancellationToken cancellationToken) =>
        // Snapshot copy, compression and hashing must not run on the
        // dispatcher; the runtime awaits this from the UI thread and reports
        // the completion there.
        Task.Run(
            () => local.CreateAutomaticSnapshotResultAsync(force, cancellationToken),
            cancellationToken);

    public Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        local.ExportBackupAsync(destinationDirectory, cancellationToken);

    public Task<CloudBackupRunResult> UploadAsync(bool scheduled, CancellationToken cancellationToken) =>
        scheduled ? cloud.RunScheduledIfDueAsync(cancellationToken) : cloud.RunBackupNowAsync(cancellationToken);
}
