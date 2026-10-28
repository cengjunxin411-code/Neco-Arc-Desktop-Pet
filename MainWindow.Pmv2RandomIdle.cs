using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

// Pilot owns the presentation; the original scheduler owns delay and no-repeat selection.
// This adapter never writes a window position or changes the Rest inactivity clock.
public partial class MainWindow
{
    private bool _pilotRandomEnabled, _pilotRandomActive;
    private RandomIdleScheduler? _pilotRandomScheduler;
    private string[] _pilotRandomAvailable = Array.Empty<string>();
    private string? _pilotRandomPath;
    private long _pilotRandomRevision, _pilotRandomPlayback, _pilotRandomGeneration, _pilotRandomSourceToken;
    private object? _pilotRandomSource;
    private nint _pilotRandomHwnd;

    private void PilotRandomFact(string kind, string reason, object? detail = null) => _pilotLog?.Write(kind,
        new { owner = "PilotNormalRandomIdle", revision = _pilotRandomRevision,
            generation = _pilotGeneration, source = _pilotSourceToken, hwnd = (long)PilotHwnd,
            gif = _gifPlayer?.ResourcePath, reason, detail });

    private bool PilotRandomOwnsSource() => _pilotRandomGeneration == _pilotGeneration &&
        _pilotRandomSourceToken == _pilotSourceToken && ReferenceEquals(_pilotRandomSource, _pilotSource) &&
        _pilotRandomHwnd == PilotHwnd && PilotClickLive;

    // D2: both physical modes and protected throw/landing visuals outrank daily behavior.
    private bool PilotRandomGravityBusy => PilotPhysicsBusy;

    // Keep Down/Menu outside the base predicate so their temporary UI block retains
    // the original scheduler deadline. Drag, clicks and other owners reset it.
    private bool PilotRandomBase() => (_pilotRandomEnabled || _pilotMoveEnabled) && PilotClickLive && IsVisible &&
        !PilotRandomGravityBusy && !_pilotTopologyHold && !PilotVisualBusy && !_pilotRandomActive && !_pilotMoveActive &&
        !_restSession.IsResting && !_sleepSession.IsSleeping && _sleepWakeSession == null && _pilotRestExit == null &&
        !_pilotClickAction && !_clickResolver.IsPending && _pilotRocketTimer == null &&
        _pilotRocketSession == null && _pilotMotionTimer == null &&
        _pilotMotionState == PilotMotionState.Idle &&
        _pilotAttachmentTimer == null && _pilotAttachmentTarget == null &&
        !_pilotDrag.Dragging && !_crossDragging &&
        _gifPlayer?.ResourcePath == _effectiveBaseIdlePath;

    private bool PilotRandomCanStart() => !PilotRandomGravityBusy && PilotRandomBase() && !_contextMenuOpen &&
        _pilotClicks?.Held != true && !_pilotDrag.Active && !_crossActive &&
        !PetSurface.IsMouseCaptured &&
        (!_pilotRestEnabled || !_restSession.RestPending &&
            (_restSession.TimerPaused || _restSession.Remaining > TimeSpan.Zero)) &&
        (!_crossScreen || _crossMapped && _crossRecovery?.Active != true && CrossCheckpoint().Ready) &&
        PilotMapping(out var scale) && scale == _pilotScale;

    private bool PilotRandomCanContinue() => PilotRandomOwnsSource() && IsVisible &&
        !_pilotTopologyHold && !_contextMenuOpen && !_restSession.IsResting &&
        !_sleepSession.IsSleeping && _sleepWakeSession == null && !_pilotClickAction &&
        _pilotRocketTimer == null && _pilotRocketSession == null && !PilotMotionBusy &&
        _pilotMotionState == PilotMotionState.Idle && _pilotAttachmentTimer == null &&
        _pilotAttachmentTarget == null && _pilotClicks?.Held != true &&
        !_pilotDrag.Active && !_crossActive && !PetSurface.IsMouseCaptured &&
        _gifPlayer?.PlaybackGeneration == _pilotRandomPlayback &&
        _gifPlayer.ResourcePath == _pilotRandomPath;

    private void PilotRandomStart()
    {
        if (!_pilotRandomEnabled && !_pilotMoveEnabled) return;
        _pilotRandomAvailable = (_pilotRandomEnabled ? RandomIdleActions.Available(_gifPlayer) : Array.Empty<string>())
            .Where(path => _gifPlayer!.PilotVisibleUnionFor(path) is { IsEmpty: false }).ToArray();
        var clock = Stopwatch.StartNew();
        _pilotRandomScheduler = new RandomIdleScheduler(_pilotRandomAvailable,
            PilotRandomCanStart, PilotRandomPlay, new Random(), () => clock.Elapsed,
            PilotMoveCanChoose, PilotMoveSelect, PilotRandomBase);
        _pilotRandomScheduler.Configure(
            () => (_settings.Normal.RandomIdleMinDelay, _settings.Normal.RandomIdleMaxDelay),
            () => _pilotRandomEnabled && _settings.Normal.RandomIdleEnabled,
            () => _settings.Movement.SelectionWeight);
        IsVisibleChanged += PilotRandomVisibleChanged;
        PilotRandomFact("random-idle-enabled", "process flag; original scheduler and catalog",
            new { candidates = _pilotRandomAvailable, minSeconds = _settings.Normal.RandomIdleMinDelay.TotalSeconds,
                maxSeconds = _settings.Normal.RandomIdleMaxDelay.TotalSeconds,
                durationSeconds = RandomIdleActions.DefaultAmbientDuration.TotalSeconds,
                movementEnabled = _pilotMoveEnabled, movementShare = _settings.Movement.SelectionWeight });
        PilotRandomUpdate();
    }

    private void PilotRandomVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) PilotRandomCancel("hidden");
        PilotRandomUpdate();
    }

    private void PilotRandomUpdate()
    {
        if (_pilotRandomScheduler == null) return;
        if (_pilotClosing || _pilotStopped) { _pilotRandomScheduler.Reset(); return; }
        if (_pilotMoveActive) { if (!PilotMoveCanContinue()) PilotMoveCancel("owner/source or higher priority changed"); return; }
        if (_pilotRandomActive)
        {
            if (!PilotRandomCanContinue()) PilotRandomCancel("owner/source or higher priority changed",
                _gifPlayer?.ResourcePath == _pilotRandomPath);
            return;
        }
        _pilotRandomScheduler.Update();
    }

    private void PilotRandomPlay(string path)
    {
        if (!_pilotRandomEnabled || !_settings.Normal.RandomIdleEnabled ||
            !PilotRandomCanStart() || !RandomIdleActions.Contains(path) ||
            !Array.Exists(_pilotRandomAvailable, candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)))
            return;
        PilotRandomFact("random-idle-select", "original no-repeat scheduler", new { path });
        if (_gifPlayer?.TrySwitchToForDuration(path, RandomIdleActions.DurationFor(path)) != true)
        {
            PilotRandomFact("random-idle-refused", "cached GIF switch failed", new { path });
            PilotRandomUpdate();
            return;
        }
        _pilotRandomActive = true;
        _pilotRandomPath = path;
        _pilotRandomPlayback = _gifPlayer.PlaybackGeneration;
        _pilotRandomGeneration = _pilotGeneration;
        _pilotRandomSourceToken = _pilotSourceToken;
        _pilotRandomSource = _pilotSource;
        _pilotRandomHwnd = PilotHwnd;
        _pilotRandomRevision++;
        _gifPlayer.Resume();
        PilotRandomFact("random-idle-start", "original finite duration", new {
            path, playback = _pilotRandomPlayback,
            minimumSeconds = RandomIdleActions.DurationFor(path).TotalSeconds });
        var voiceRevision = _pilotRandomRevision;
        PilotActionVoiceStart(ref _pilotRandomVoiceId, path, _pilotRandomPlayback,
            () => _pilotRandomActive && voiceRevision == _pilotRandomRevision && PilotRandomCanContinue());
    }

    private void PilotRandomCompleted(object? sender, EventArgs e)
    {
        if (!_pilotRandomActive || !PilotRandomCanContinue() ||
            !ReferenceEquals(sender, _gifPlayer) || e is not GifPlaybackCompletedEventArgs completed ||
            _gifPlayer?.IsComplete != true || completed.Generation != _pilotRandomPlayback ||
            completed.ResourcePath != _pilotRandomPath) return;
        var path = _pilotRandomPath;
        PilotActionVoiceEnd(ref _pilotRandomVoiceId, true);
        _pilotRandomActive = false; _pilotRandomPath = null; _pilotRandomRevision++;
        PilotRandomFact("random-idle-complete", "real GifFramePlayer.Completed", new { path, playback = completed.Generation });
        if (_effectiveBaseIdlePath == null || !_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath))
        { PilotStop("Random Idle could not restore base Idle."); return; }
        _gifPlayer.Resume();
        PilotRandomFact("random-idle-return", "base Idle restored");
        PilotRestUpdate();
        PilotRandomUpdate();
    }

    private void PilotRandomReset() => _pilotRandomScheduler?.Reset();

    private void PilotRandomApplySettings(bool previousEnabled)
    {
        // Keep an unchanged armed deadline; changed enable starts a new draw.
        if (previousEnabled != _settings.Normal.RandomIdleEnabled) PilotRandomReset();
        PilotRandomUpdate();
    }

    private void PilotRandomCancel(string reason, bool restore = true)
    {
        if (!_pilotRandomActive) return;
        var path = _pilotRandomPath;
        PilotActionVoiceEnd(ref _pilotRandomVoiceId, false);
        _pilotRandomActive = false; _pilotRandomPath = null; _pilotRandomRevision++;
        PilotRandomReset();
        PilotRandomFact("random-idle-cancel", reason, new { path });
        if (restore && !_pilotClosing && !_pilotStopped && _effectiveBaseIdlePath != null &&
            _gifPlayer?.ResourcePath == path)
        {
            if (!_gifPlayer!.TrySwitchTo(_effectiveBaseIdlePath)) PilotStop("Random Idle cancellation could not restore base Idle.");
            else { _gifPlayer.Resume(); PilotRandomFact("random-idle-return", reason); }
        }
    }

    private void PilotRandomClose(string reason)
    {
        PilotRandomCancel(reason, false);
        _pilotRandomScheduler?.Dispose(); _pilotRandomScheduler = null;
        IsVisibleChanged -= PilotRandomVisibleChanged;
    }
}
