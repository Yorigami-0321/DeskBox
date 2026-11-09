using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

/// <summary>
/// Runtime-health diagnostics, batch 49 form: the snapshot computation and
/// the resync flow (App-level external state recovery) stay on the shell;
/// the localized summary/detail lines are pushed into the backup settings
/// editor (<see cref="Features.Backup.BackupSettingsViewModel"/>) which
/// owns the compatibility-diagnostics section's XAML binding surface.
/// </summary>
public partial class SettingsViewModel
{
    public void RefreshRuntimeDiagnostics()
    {
        AppRuntimeHealthSnapshot? snapshot = _appActions.GetRuntimeHealthSnapshot();
        if (snapshot is null)
        {
            _backupSettings.SetRuntimeHealth(
                _localizationService.T("Settings.RuntimeHealth.Unavailable"),
                string.Empty);
            return;
        }

        string summaryText =
            snapshot.OfflineFolderCount > 0 ||
            snapshot.DegradedFolderCount > 0 ||
            snapshot.AccessDeniedFolderCount > 0
                ? _localizationService.T("Settings.RuntimeHealth.Summary.Degraded")
                : _localizationService.T("Settings.RuntimeHealth.Summary.Healthy");

        string lastLifecycle = snapshot.LastLifecycleEventAt is { } lifecycleAt
            ? lifecycleAt.ToLocalTime().ToString("g")
            : _localizationService.T("Settings.RuntimeHealth.Never");

        string detailText = _localizationService.Format(
            "Settings.RuntimeHealth.Detail",
            snapshot.LifecycleEventCount,
            lastLifecycle,
            snapshot.OfflineFolderCount,
            snapshot.DegradedFolderCount,
            snapshot.AccessDeniedFolderCount,
            string.IsNullOrWhiteSpace(snapshot.LastLifecycleReason)
                ? _localizationService.T("Settings.RuntimeHealth.Never")
                : snapshot.LastLifecycleReason);

        _backupSettings.SetRuntimeHealth(summaryText, detailText);
    }

    public async Task ResyncRuntimeStateAsync()
    {
        if (!_backupSettings.CanResyncRuntime)
        {
            return;
        }

        _backupSettings.SetRuntimeResyncBusy(true);
        try
        {
            await _appActions.ForceExternalStateRecoveryAsync();

            RefreshRuntimeDiagnostics();
        }
        finally
        {
            _backupSettings.SetRuntimeResyncBusy(false);
        }
    }
}
