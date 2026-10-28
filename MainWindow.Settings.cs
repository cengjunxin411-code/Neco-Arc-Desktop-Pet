using System;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private readonly PetSettingsStore? _settingsStore;
    private readonly string? _settingsStartupMessage;
    internal string? SettingsPersistenceStatus { get; private set; }
    internal bool SettingsSavePending { get; private set; }
    internal bool SettingsPersistenceAvailable => _settingsStore != null;
    internal Func<string, string?>? WorkbenchUnavailableReason { get; private set; }
    // Runtime ownership is independent of the optional capability hints in UI.
    internal bool Pmv2OwnsSettings =>
#if PMV2_PILOT
        _pilotLog != null;
#else
        false;
#endif

    internal PetSettingsSaveResult SaveSettingsSnapshot()
    {
        Dispatcher.VerifyAccess();
#if PMV2_PILOT
        var heightForSave = _pilotLog != null ? _pilotHeightForSave : null;
        var result = _settingsStore?.Save(GetSettingsSnapshot(), Topmost, heightForSave,
            _pilotLog != null ? _pilotPhysicsMode : null)
#else
        var result = _settingsStore?.Save(GetSettingsSnapshot(), Topmost)
#endif
            ?? new(false, "已应用于本次运行；尚未持久化。重启后会恢复默认值。");
        SettingsPersistenceStatus = result.Message + (result.DiagnosticLogPath == null ? "" : "\n诊断日志：" + result.DiagnosticLogPath);
        SettingsSavePending = _settingsStore != null && !result.Success;
        _workbenchWindow?.RefreshPersistenceStatus();
        if (result.DiagnosticReport != null)
            App.ShowPersistenceReport(result.Message + "\n诊断日志写入失败，请 Ctrl+A、Ctrl+C 复制报告。\n\n"
                + result.DiagnosticReport, _workbenchWindow ?? (System.Windows.Window)this);
        return result;
    }

    private void ShowSettingsNotice(string text, TimeSpan duration)
    {
        if (_bubbleWindow == null) return;
        PositionBubble();
        _bubbleWindow.ShowMessage(text, duration);
        PositionBubble();
    }

    public PetSettings GetSettingsSnapshot()
    {
        Dispatcher.VerifyAccess();
        return _settings.DeepClone();
    }

    // Edit a snapshot, Validate, then Apply on the window Dispatcher.
    // Active playback/session durations are retained. New draws/sessions read new values.
    public void ApplySettings(PetSettings workingCopy)
    {
        Dispatcher.VerifyAccess();
        var previousDiagnostics = _settings.Diagnostics;
#if PMV2_PILOT
        var previousRandomEnabled = _settings.Normal.RandomIdleEnabled;
        var previousRestEnabled = _settings.Rest.Enabled;
        var previousSleepEnabled = _settings.Sleep.Enabled;
#endif
        _settings.ApplyFrom(workingCopy);
        if (previousDiagnostics != _settings.Diagnostics)
        {
            StopClickDiagnostics();
            InitializeClickDiagnostics();
        }
        // Pilot owners read the new snapshot at their own decision points.
        // The ordinary schedulers must remain parked in the Pilot process.
        if (Pmv2OwnsSettings)
        {
#if PMV2_PILOT
            PilotApplySettings(previousRandomEnabled, previousRestEnabled, previousSleepEnabled);
#endif
            return;
        }
        _randomIdleScheduler.Update();
        _restAmbientScheduler.Update();
        _sleepAmbientScheduler.Update();
        ArmRestDeadline();
        ArmSleepDeadline();
    }
}
