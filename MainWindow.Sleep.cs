using System;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private const string SleepGifPath = "NecoPetMaterials/sleep/slumber.gif";
    private SleepSession _sleepSession = null!;
    private SleepWakeSession? _sleepWakeSession;
    private readonly DispatcherTimer _sleepTimer = new();
    private RandomIdleScheduler _sleepAmbientScheduler = null!;
    private string[] _availableSleepAmbientActions = Array.Empty<string>();
    private bool _sleepAvailable, _startleAvailable, _grouchyAvailable;
    private bool _sleepAmbientActive;
    private long _sleepAmbientPlaybackGeneration = -1;
    private bool HasSleepOwnership => _sleepSession.IsSleeping || _sleepWakeSession != null;
    private bool IsSleepBaseIdle => IsVisible && _sleepSession.IsSleeping && !_sleepAmbientActive &&
        !_isDragging && !_leftPressed && !_contextMenuOpen && !_landingActive &&
        _attachmentMode == AttachmentMode.None && !(_animationTimer?.IsEnabled ?? false) &&
        _gifPlayer?.ResourcePath == SleepGifPath;

    private void InitializeSleep()
    {
        _sleepAvailable = _gifPlayer?.TryCacheClip(SleepGifPath) == true;
        _startleAvailable = _gifPlayer?.TryCacheClip(SleepWakeSession.StartlePath) == true;
        _grouchyAvailable = _gifPlayer?.TryCacheClip(SleepWakeSession.GrouchyPath) == true;
        _availableSleepAmbientActions = SleepAmbientActions.Available(_gifPlayer);
        _sleepAmbientScheduler = new RandomIdleScheduler(_availableSleepAmbientActions,
            () => IsSleepBaseIdle, PlaySleepAmbientAction);
        _sleepAmbientScheduler.Configure(() => (_settings.Sleep.AmbientMinDelay, _settings.Sleep.AmbientMaxDelay),
            () => _settings.Sleep.AmbientEnabled);
        _sleepTimer.Tick += OnSleepDeadline;
        IsVisibleChanged += (_, _) =>
        {
#if PMV2_PILOT
            if (_pilotLog != null) return;
#endif
            if (IsVisible) return;
            var active = HasSleepOwnership;
            InterruptSleep();
            if (active) RestoreIdleGif();
            _gifPlayer?.Pause();
        };
    }

    private void StartSleepDeadline()
    {
        _sleepSession.BeginRest();
        ArmSleepDeadline();
    }
    private void ArmSleepDeadline()
    {
        _sleepTimer.Stop();
        if (!_settings.Sleep.Enabled || !IsVisible || !_restSession.IsResting || _sleepSession.SleepPending) return;
        _sleepTimer.Interval = TimeSpan.FromTicks(Math.Max(1, _sleepSession.Remaining.Ticks));
        _sleepTimer.Start();
    }
    private void OnSleepDeadline(object? sender, EventArgs e)
    {
        _sleepTimer.Stop();
        if (!IsVisible || !_restSession.IsResting) return;
        if (_sleepSession.Poll(IsRestBaseIdle)) BeginSleep();
        else ArmSleepDeadline(); // Pending waits for the current action, never polls at high frequency.
    }

    private void BeginSleep()
    {
        if (!_settings.Sleep.Enabled || !IsVisible || !_restSession.IsResting || _restAmbientActive || _leftPressed || _isDragging ||
            _contextMenuOpen || _attachmentMode != AttachmentMode.None) return;
        InterruptRest(); // Clears Rest ownership and its obsolete callbacks before the switch.
        _restTimer.Stop();
        _randomIdleScheduler.Reset();
        CancelClickBubble();
        _bubbleWindow?.Hide();
        _sleepSession.BeginSleep();
        if (!_sleepAvailable || _gifPlayer?.TrySwitchTo(SleepGifPath) != true)
        {
            InterruptSleep();
            RestoreIdleGif(); // Fresh Normal inactivity, no invisible Sleep or retry loop.
            return;
        }
        ApplyPetLayout();
        _gifPlayer.Resume();
        _sleepAmbientScheduler.Update();
    }

    private void CancelSleepAmbient()
    {
        _sleepAmbientScheduler.Reset();
        _sleepAmbientActive = false;
        _sleepAmbientPlaybackGeneration = -1;
    }
    private void InterruptSleep()
    {
        if (_sleepWakeSession != null && _leftPressed) _suppressClickOnRelease = true;
        _sleepTimer.Stop();
        CancelSleepAmbient();
        _sleepSession.Cancel();
        _sleepWakeSession = null;
    }

    private void PlaySleepAmbientAction(string path)
    {
        if (!_settings.Sleep.AmbientEnabled || !IsSleepBaseIdle || !SleepAmbientActions.Contains(path) ||
            !Array.Exists(_availableSleepAmbientActions, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        _sleepAmbientScheduler.Reset();
        if (_gifPlayer?.TrySwitchToLoops(path, 1) != true)
        {
            _sleepAmbientScheduler.Update();
            return;
        }
        _sleepAmbientActive = true;
        _sleepAmbientPlaybackGeneration = _gifPlayer.PlaybackGeneration;
        ApplyPetLayout();
        _gifPlayer.Resume();
    }
    private bool IsCurrentSleepCompletion(object? sender, EventArgs e, string path, long generation) =>
        IsVisible && !_isDragging && !_contextMenuOpen && !_landingActive && _attachmentMode == AttachmentMode.None &&
        ReferenceEquals(sender, _gifPlayer) && _gifPlayer?.IsComplete == true &&
        e is GifPlaybackCompletedEventArgs completed && completed.Generation == generation &&
        completed.Generation == _gifPlayer.PlaybackGeneration && completed.ResourcePath == path && _gifPlayer.ResourcePath == path;

    private void CompleteSleepPlayback(object? sender, EventArgs e)
    {
        if (_sleepSession.IsSleeping && _sleepAmbientActive)
        {
            if (!IsCurrentSleepCompletion(sender, e, SleepAmbientActions.StretchPath, _sleepAmbientPlaybackGeneration)) return;
            CancelSleepAmbient();
            if (!_sleepAvailable || !_gifPlayer!.TrySwitchTo(SleepGifPath))
            {
                InterruptSleep(); RestoreIdleGif(); return;
            }
            ApplyPetLayout();
            _gifPlayer.Resume();
            _sleepAmbientScheduler.Update();
            return;
        }
        var session = _sleepWakeSession;
        if (session == null || !IsCurrentSleepCompletion(sender, e, session.ResourcePath, session.PlaybackGeneration)) return;
        if (session.Stage == SleepWakeStage.Startle && session.EscalateToGrouchy && _grouchyAvailable &&
            _gifPlayer!.TrySwitchToLoops(SleepWakeSession.GrouchyPath, _settings.Sleep.GrouchyLoops))
        {
            session.BeginGrouchy(_gifPlayer.PlaybackGeneration);
            ApplyPetLayout();
            _gifPlayer.Resume();
            return;
        }
        InterruptSleep();
        RestoreIdleGif();
    }

    // Valid MouseUp only: first Click wakes, later Startle Clicks set one escalation flag.
    private bool RegisterSleepClick()
    {
        if (!HasSleepOwnership || !IsVisible || _contextMenuOpen || _isDragging) return false;
        if (_sleepWakeSession != null)
        {
            _sleepWakeSession.RegisterClick();
            return true;
        }
        RegisterRestInteraction(); // Cancels Sleep + Rest and pauses Normal inactivity.
        _restSession.UserInteraction(); // Exclude the whole finite Wake sequence from inactivity.
        _restTimer.Stop();
        if (!_startleAvailable || _gifPlayer?.TrySwitchToLoops(SleepWakeSession.StartlePath, 1) != true)
        {
            RestoreIdleGif();
            return true;
        }
        _sleepWakeSession = new SleepWakeSession(_gifPlayer.PlaybackGeneration);
        ApplyPetLayout();
        _gifPlayer.Resume();
        return true;
    }
}
