using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

// Pilot adapter for the original RestSession/SleepSession/SleepWakeSession rules.
// GIF changes are admitted by the existing PilotVisualQueue; this owner never places a window.
public partial class MainWindow
{
    private bool _pilotRestEnabled;
    private DispatcherTimer? _pilotRestDeadline, _pilotSleepDeadline;
    private RandomIdleScheduler? _pilotRestAmbient, _pilotSleepAmbient;
    private long _pilotRestRevision, _pilotRestPlayback = -1, _pilotSleepPlayback = -1;
    private long _pilotRestGeneration, _pilotRestSourceToken;
    private nint _pilotRestHwnd;
    private string? _pilotRestAmbientPath;
    private bool _pilotRestAmbientActive, _pilotSleepAmbientActive;
    private const string PilotRestExitPath = "NecoPetMaterials/drag/scruff.gif";
    private sealed record PilotRestExitSession(long Revision, long Generation, long SourceToken,
        object? Source, nint Hwnd, long Playback, long Started);
    private PilotRestExitSession? _pilotRestExit;
    private long _pilotRestExitRevision;
    private bool _pilotRestMenuPaused;
    private bool PilotRestHasState => _restSession.IsResting || _sleepSession.IsSleeping || _sleepWakeSession != null;

    private void PilotRestFact(string kind, string reason, object? detail = null) => _pilotLog?.Write(kind,
        new { owner = "PilotRestSleep", revision = _pilotRestRevision, generation = _pilotGeneration,
            source = _pilotSourceToken, hwnd = (long)PilotHwnd, gif = _gifPlayer?.ResourcePath, reason, detail });

    private bool PilotRestOwnsSource() => _pilotRestGeneration == _pilotGeneration &&
        _pilotRestSourceToken == _pilotSourceToken && _pilotRestHwnd == PilotHwnd && PilotClickLive;

    private bool PilotRestCanEnter() => _pilotRestEnabled && _settings.Rest.Enabled && PilotClickLive && IsVisible &&
        !_contextMenuOpen && _pilotRestExit == null &&
        !_pilotTopologyHold && !PilotVisualBusy && !_pilotDrag.Active && !_crossActive &&
        !PetSurface.IsMouseCaptured && _pilotClicks?.Held != true && !_clickResolver.IsPending &&
        !_pilotClickAction && _pilotRocketTimer == null && _pilotRocketSession == null &&
        !PilotMotionBusy && _pilotAttachmentTimer == null && _pilotAttachmentTarget == null &&
        _gifPlayer?.ResourcePath == _effectiveBaseIdlePath &&
        (!_crossScreen || _crossMapped && _crossRecovery?.Active != true && CrossCheckpoint().Ready);

    private bool PilotRestBase() => PilotRestOwnsSource() && _restSession.IsResting &&
        !_contextMenuOpen && !_pilotRestAmbientActive && PilotRestUninterrupted() && _gifPlayer?.ResourcePath == RestGifPath;
    private bool PilotSleepBase() => PilotRestOwnsSource() && _sleepSession.IsSleeping &&
        !_contextMenuOpen && !_pilotSleepAmbientActive && PilotRestUninterrupted() && _gifPlayer?.ResourcePath == SleepGifPath;
    private bool PilotRestUninterrupted() => IsVisible && !_pilotTopologyHold && !_pilotDrag.Active &&
        !_crossActive && !PetSurface.IsMouseCaptured && _pilotClicks?.Held != true &&
        !_pilotClickAction && _pilotRocketTimer == null && _pilotRocketSession == null &&
        !PilotMotionBusy && _pilotAttachmentTimer == null && _pilotAttachmentTarget == null;

    private void PilotRestStart()
    {
        _pilotRestEnabled = PilotApplication.RestSleep;
        if (!_pilotRestEnabled) return;
        _pilotRestAmbient = new RandomIdleScheduler(_availableRestAmbientActions, PilotRestBase, PilotRestPlayAmbient);
        _pilotRestAmbient.Configure(() => (_settings.Rest.AmbientMinDelay, _settings.Rest.AmbientMaxDelay),
            () => _settings.Rest.Enabled && _settings.Rest.AmbientEnabled);
        _pilotSleepAmbient = new RandomIdleScheduler(_availableSleepAmbientActions, PilotSleepBase, PilotSleepPlayAmbient);
        _pilotSleepAmbient.Configure(() => (_settings.Sleep.AmbientMinDelay, _settings.Sleep.AmbientMaxDelay),
            () => _settings.Sleep.Enabled && _settings.Sleep.AmbientEnabled);
        PilotRestFact("rest-sleep-enabled", "process flag; original settings defaults", new {
            restSeconds = _settings.Rest.EnterAfter.TotalSeconds,
            sleepAfterRestSeconds = _settings.Sleep.EnterAfterRest.TotalSeconds,
            restAmbient = _availableRestAmbientActions, sleepAmbient = _availableSleepAmbientActions,
            audio = "shared action voice; Slumber and LieDown silent; Stretch 25 percent" });
        PilotRestUpdate();
    }

    private void PilotRestStopTimer(ref DispatcherTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }
    private void PilotRestArmDeadline()
    {
        PilotRestStopTimer(ref _pilotRestDeadline);
        if (!_pilotRestEnabled || !_settings.Rest.Enabled || _restSession.TimerPaused ||
            _restSession.IsResting || _restSession.RestPending) return;
        var revision = _pilotRestRevision;
        var timer = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromTicks(Math.Max(1, _restSession.Remaining.Ticks)) };
        _pilotRestDeadline = timer;
        timer.Tick += (_, _) => {
            timer.Stop();
            if (!ReferenceEquals(timer, _pilotRestDeadline) || revision != _pilotRestRevision) return;
            _pilotRestDeadline = null;
            if (_restSession.Poll(PilotRestCanEnter())) PilotRestBegin();
            else PilotRestArmDeadline();
        };
        timer.Start();
    }
    private void PilotSleepArmDeadline()
    {
        PilotRestStopTimer(ref _pilotSleepDeadline);
        if (!_pilotRestEnabled || _contextMenuOpen || _pilotRestMenuPaused || !_settings.Sleep.Enabled ||
            !_restSession.IsResting || _sleepSession.SleepPending) return;
        var revision = _pilotRestRevision;
        var timer = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromTicks(Math.Max(1, _sleepSession.Remaining.Ticks)) };
        _pilotSleepDeadline = timer;
        timer.Tick += (_, _) => {
            timer.Stop();
            if (!ReferenceEquals(timer, _pilotSleepDeadline) || revision != _pilotRestRevision) return;
            _pilotSleepDeadline = null;
            if (_sleepSession.Poll(PilotRestBase())) PilotSleepBegin();
            else PilotSleepArmDeadline();
        };
        timer.Start();
    }

    private void PilotRestUpdate()
    {
        if (!_pilotRestEnabled || _pilotClosing || _pilotStopped) return;
        if (_pilotRestExit is { } exit) {
            if (!PilotRestExitOwns(exit)) PilotRestCancel("Rest exit owner/playback invalidated", false);
            return;
        }
        if (PilotRestHasState && (!PilotRestOwnsSource() || !PilotRestUninterrupted() && _pilotClicks?.Held != true)) {
            PilotRestCancel("owner/source or higher priority changed");
            return;
        }
        if (_pilotRestMenuPaused) return; // Source/safety checks above still run while the menu is open.
        if (_sleepWakeSession != null) return;
        if (_sleepSession.IsSleeping) { _pilotSleepAmbient?.Update(); return; }
        if (_restSession.IsResting) {
            if (_settings.Sleep.Enabled && _pilotSleepDeadline == null && !_sleepSession.SleepPending)
                PilotSleepArmDeadline();
            if (_settings.Sleep.Enabled && _sleepSession.SleepPending && PilotRestBase()) PilotSleepBegin();
            else _pilotRestAmbient?.Update();
            return;
        }
        if (!PilotRestCanEnter()) return;
        if (_restSession.TimerPaused) { _restSession.ResumeAtBaseIdle(); _pilotRestRevision++; PilotRestArmDeadline(); }
        if (_restSession.RestPending && _restSession.Poll(true)) PilotRestBegin();
    }

    private void PilotRestApplySettings(bool previousRestEnabled, bool previousSleepEnabled)
    {
        if (!_pilotRestEnabled) return;
        if (previousRestEnabled && !_settings.Rest.Enabled && !PilotRestHasState)
        {
            PilotRestStopTimer(ref _pilotRestDeadline);
            _restSession.UserInteraction();
            _pilotRestRevision++;
        }
        if (previousSleepEnabled && !_settings.Sleep.Enabled)
        {
            PilotRestStopTimer(ref _pilotSleepDeadline);
            _sleepSession.Poll(false); // Clear an already pending entry without ending an active pose.
            _pilotRestRevision++;
        }
        _pilotRestAmbient?.Update();
        _pilotSleepAmbient?.Update();
        PilotRestUpdate();
    }

    private void PilotRestBegin()
    {
        if (!PilotRestCanEnter()) return;
        if (!_restAvailable || _gifPlayer?.TrySwitchTo(RestGifPath) != true) {
            _restSession.RetryAfterFailure(); _pilotRestRevision++; PilotRestArmDeadline();
            PilotRestFact("rest-refused", "Rest GIF unavailable"); return;
        }
        _restSession.BeginRest(); _pilotRestRevision++;
        PilotActionVoiceSilence();
        _pilotRestGeneration = _pilotGeneration; _pilotRestSourceToken = _pilotSourceToken; _pilotRestHwnd = PilotHwnd;
        PilotRestStopTimer(ref _pilotRestDeadline);
        _sleepSession.BeginRest(); PilotSleepArmDeadline();
        _pilotRestAmbient?.Update(); _gifPlayer.Resume();
        var revision = _pilotRestRevision;
        PilotActionVoiceStart(ref _pilotRestVoiceId, RestGifPath, _gifPlayer.PlaybackGeneration,
            () => revision == _pilotRestRevision && PilotRestOwnsSource() && _restSession.IsResting);
        PilotRestFact("rest-enter", "normal base Idle inactivity threshold");
    }

    private void PilotSleepBegin(GifPlaybackCompletedEventArgs? completedAmbient = null)
    {
        var completedRestAmbient = completedAmbient != null && _sleepSession.SleepPending &&
            PilotRestOwnsSource() && _restSession.IsResting && PilotRestUninterrupted() &&
            _gifPlayer?.IsComplete == true &&
            completedAmbient.Generation == _gifPlayer.PlaybackGeneration &&
            completedAmbient.ResourcePath == _gifPlayer.ResourcePath &&
            Array.Exists(_availableRestAmbientActions, path => path == completedAmbient.ResourcePath);
        if ((!PilotRestBase() && !completedRestAmbient) || !_settings.Sleep.Enabled) return;
        PilotRestStopTimer(ref _pilotSleepDeadline); _pilotRestAmbient?.Reset();
        _restSession.InterruptRest(); _sleepSession.BeginSleep(); _pilotRestRevision++;
        if (!_sleepAvailable || _gifPlayer?.TrySwitchTo(SleepGifPath) != true) {
            PilotRestCancel("Sleep GIF unavailable"); return;
        }
        _pilotSleepAmbient?.Update(); _gifPlayer.Resume();
        PilotActionVoiceSilence();
        PilotRestFact("sleep-enter", "Rest deadline completed");
    }

    private void PilotRestPlayAmbient(string path)
    {
        if (!PilotRestBase() || !_settings.Rest.Enabled || !_settings.Rest.AmbientEnabled ||
            !Array.Exists(_availableRestAmbientActions, p => p == path)) return;
        _pilotRestAmbient?.Reset();
        var loops = RestAmbientActions.LoopsFor(path, _restAmbientRandom, _settings.Rest);
        if (_gifPlayer?.TrySwitchToLoops(path, loops) != true) { _pilotRestAmbient?.Update(); return; }
        _pilotRestAmbientActive = true; _pilotRestAmbientPath = path;
        _pilotRestPlayback = _gifPlayer.PlaybackGeneration; _gifPlayer.Resume();
        PilotRestFact("rest-ambient-enter", "original Rest catalog", new { loops, playback = _pilotRestPlayback });
    }
    private void PilotSleepPlayAmbient(string path)
    {
        if (!PilotSleepBase() || !_settings.Sleep.Enabled || !_settings.Sleep.AmbientEnabled || !SleepAmbientActions.Contains(path)) return;
        _pilotSleepAmbient?.Reset();
        if (_gifPlayer?.TrySwitchToLoops(path, 1) != true) { _pilotSleepAmbient?.Update(); return; }
        _pilotSleepAmbientActive = true; _pilotSleepPlayback = _gifPlayer.PlaybackGeneration; _gifPlayer.Resume();
        // This successful finite session is the sole lottery point, even if voice
        // is disabled. Frame callbacks, settings and restoration never draw again.
        if (_pilotStretchVoiceRandom.Next(4) == 0) {
            var playback = _pilotSleepPlayback;
            PilotActionVoiceStart(ref _pilotStretchVoiceId, path, playback,
                () => PilotRestOwnsSource() && _sleepSession.IsSleeping &&
                    _pilotSleepAmbientActive && playback == _pilotSleepPlayback);
        }
        PilotRestFact("sleep-ambient-enter", "original Sleep catalog", new { playback = _pilotSleepPlayback });
    }

    // Returns true when the accepted MouseUp is consumed by Rest or Sleep/Wake.
    private bool PilotRestAcceptClick()
    {
        if (!_pilotRestEnabled) return false;
        if (_pilotRestExit != null) return true;
        if (!PilotRestHasState) {
            _restSession.UserInteraction(); _pilotRestRevision++; PilotRestStopTimer(ref _pilotRestDeadline);
            return false;
        }
        if (_sleepWakeSession is {} wake) {
            wake.RegisterClick(); PilotRestFact("wake-click", "Startle escalation recorded"); return true;
        }
        if (_sleepSession.IsSleeping) {
            // Wake is a forced end of the Sleep action, including any Stretch
            // tail already released normally into Slumber.
            PilotActionVoiceSilence();
            PilotRestStopTimer(ref _pilotSleepDeadline); _pilotSleepAmbient?.Reset();
            _pilotSleepAmbientActive = false; _sleepSession.Cancel();
            _restSession.UserInteraction(); _pilotRestRevision++;
            if (!_startleAvailable || _gifPlayer?.TrySwitchToLoops(SleepWakeSession.StartlePath, 1) != true) {
                PilotRestCancel("Wake GIF unavailable");
                _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!); _gifPlayer?.Resume();
                PilotRestFact("wake-refused", "Startle GIF unavailable; Normal Idle restored");
                return true;
            }
            _sleepWakeSession = new SleepWakeSession(_gifPlayer.PlaybackGeneration);
            var wakeSession = _sleepWakeSession;
            PilotActionVoiceStart(ref _pilotWakeVoiceId, SleepWakeSession.StartlePath, wakeSession.PlaybackGeneration,
                () => PilotRestOwnsSource() && ReferenceEquals(wakeSession, _sleepWakeSession) &&
                    wakeSession.Stage == SleepWakeStage.Startle);
            _gifPlayer.Resume(); PilotRestFact("wake-enter", "accepted Sleep click", new { playback = _sleepWakeSession.PlaybackGeneration });
            return true;
        }
        PilotRestExitBegin();
        return true;
    }

    private bool PilotRestExitOwns(PilotRestExitSession exit) =>
        ReferenceEquals(exit, _pilotRestExit) && exit.Revision == _pilotRestExitRevision &&
        exit.Generation == _pilotGeneration && exit.SourceToken == _pilotSourceToken &&
        ReferenceEquals(exit.Source, _pilotSource) && exit.Hwnd == PilotHwnd && PilotClickLive &&
        _gifPlayer?.PlaybackGeneration == exit.Playback && _gifPlayer.ResourcePath == PilotRestExitPath;

    private void PilotRestExitBegin()
    {
        PilotRestCancel("accepted Rest click; finite exit", false);
        if (_gifPlayer?.TryCacheClip(PilotRestExitPath) != true ||
            !_gifPlayer.TrySwitchToLoops(PilotRestExitPath, 5)) {
            PilotRestFact("rest-exit-refused", "scruff finite playback unavailable; direct Idle fallback");
            _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!); _gifPlayer?.Resume();
            PilotRestUpdate(); PilotRandomUpdate();
            return;
        }
        _pilotRestExit = new PilotRestExitSession(_pilotRestExitRevision, _pilotGeneration, _pilotSourceToken,
            _pilotSource, PilotHwnd, _gifPlayer.PlaybackGeneration, Stopwatch.GetTimestamp());
        _gifPlayer.Resume();
        var exit = _pilotRestExit;
        PilotActionVoiceStart(ref _pilotRestExitVoiceId, PilotRestExitPath, exit.Playback,
            () => PilotRestExitOwns(exit));
        PilotRestFact("rest-exit-enter", "five complete scruff loops", new { loops = 5, playback = _pilotRestExit.Playback });
    }

    // Only Rest/Sleep lifetimes are preserved. Wake retains its former cancellation.
    private void PilotRestMenuOpen()
    {
        if (!_pilotRestEnabled) return;
        if (_pilotRestExit != null) return;
        if (_sleepWakeSession == null && (_restSession.IsResting || _sleepSession.IsSleeping)) {
            _pilotRestMenuPaused = true;
            _sleepSession.Pause();
            PilotRestStopTimer(ref _pilotSleepDeadline);
            _pilotRestAmbient?.Update(); _pilotSleepAmbient?.Update();
            PilotRestFact("rest-menu-pause", "menu dwell excluded from Sleep clock", new { remainingMs = _sleepSession.Remaining.TotalMilliseconds });
            return;
        }
        PilotRestCancel("Menu.Open");
    }

    private void PilotRestMenuClose()
    {
        if (!_pilotRestMenuPaused) return;
        _pilotRestMenuPaused = false;
        _sleepSession.Resume();
        PilotRestFact("rest-menu-resume", "continue remaining Sleep deadline", new { remainingMs = _sleepSession.Remaining.TotalMilliseconds });
    }

    private void PilotRestCompleted(object? sender, EventArgs e)
    {
        if (_pilotRestExit is { } exit) {
            if (!PilotRestExitOwns(exit) || !ReferenceEquals(sender, _gifPlayer) ||
                e is not GifPlaybackCompletedEventArgs done || _gifPlayer?.IsComplete != true ||
                done.Generation != exit.Playback || done.ResourcePath != PilotRestExitPath) return;
            PilotActionVoiceEnd(ref _pilotRestExitVoiceId, true);
            _pilotRestExit = null; _pilotRestExitRevision++; _pilotRestRevision++;
            _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!); _gifPlayer.Resume();
            PilotRestFact("rest-exit-complete", "Normal Idle restored", new {
                playback = exit.Playback, elapsedMs = Stopwatch.GetElapsedTime(exit.Started).TotalMilliseconds });
            PilotRestUpdate(); PilotRandomUpdate();
            return;
        }
        if (!_pilotRestEnabled || !PilotRestHasState || !PilotRestOwnsSource() ||
            !ReferenceEquals(sender, _gifPlayer) || e is not GifPlaybackCompletedEventArgs completed ||
            _gifPlayer?.IsComplete != true || completed.Generation != _gifPlayer.PlaybackGeneration ||
            completed.ResourcePath != _gifPlayer.ResourcePath) return;
        if (_pilotRestAmbientActive && completed.Generation == _pilotRestPlayback && completed.ResourcePath == _pilotRestAmbientPath) {
            _pilotRestAmbientActive = false; _pilotRestAmbientPath = null; _pilotRestPlayback = -1;
            if (_sleepSession.Poll(!_contextMenuOpen)) { PilotSleepBegin(completed); return; }
            if (_gifPlayer.TrySwitchTo(RestGifPath)) { _gifPlayer.Resume(); _pilotRestAmbient?.Update(); PilotRestFact("rest-ambient-complete", "Rest loop restored"); }
            else PilotRestCancel("Rest loop unavailable after ambient");
            return;
        }
        if (_pilotSleepAmbientActive && completed.Generation == _pilotSleepPlayback && completed.ResourcePath == SleepAmbientActions.StretchPath) {
            PilotActionVoiceEnd(ref _pilotStretchVoiceId, true);
            _pilotSleepAmbientActive = false; _pilotSleepPlayback = -1;
            if (_gifPlayer.TrySwitchTo(SleepGifPath)) { _gifPlayer.Resume(); _pilotSleepAmbient?.Update(); PilotRestFact("sleep-ambient-complete", "Sleep loop restored"); }
            else PilotRestCancel("Sleep loop unavailable after ambient");
            return;
        }
        var wake = _sleepWakeSession;
        if (wake == null || wake.PlaybackGeneration != completed.Generation || wake.ResourcePath != completed.ResourcePath) return;
        if (wake.Stage == SleepWakeStage.Startle && wake.EscalateToGrouchy && _grouchyAvailable &&
            _gifPlayer.TrySwitchToLoops(SleepWakeSession.GrouchyPath, _settings.Sleep.GrouchyLoops)) {
            wake.BeginGrouchy(_gifPlayer.PlaybackGeneration); _gifPlayer.Resume();
            PilotActionVoiceStart(ref _pilotWakeVoiceId, SleepWakeSession.GrouchyPath, wake.PlaybackGeneration,
                () => PilotRestOwnsSource() && ReferenceEquals(wake, _sleepWakeSession) &&
                    wake.Stage == SleepWakeStage.Grouchy);
            PilotRestFact("wake-grouchy-enter", "later Startle click", new { loops = _settings.Sleep.GrouchyLoops });
            return;
        }
        PilotActionVoiceEnd(ref _pilotWakeVoiceId, true);
        PilotRestCancel("Wake completion");
        PilotRestFact("wake-complete", "Normal Idle restored");
    }

    private void PilotRestCancel(string reason, bool restore = true)
    {
        if (!_pilotRestEnabled) return;
        var active = PilotRestHasState || _pilotRestExit != null || _pilotRestAmbientActive || _pilotSleepAmbientActive;
        PilotActionVoiceEnd(ref _pilotRestVoiceId, false);
        PilotActionVoiceEnd(ref _pilotRestExitVoiceId, false);
        PilotActionVoiceEnd(ref _pilotStretchVoiceId, false);
        PilotActionVoiceEnd(ref _pilotWakeVoiceId, false);
        // Rest/Sleep/exit also cut released tails (notably Stretch in Slumber).
        // Wake uses its explicit End above: cancellation stops it, normal
        // completion keeps its tail while retaining the original Idle restoration.
        if (active && _sleepWakeSession == null) PilotActionVoiceSilence();
        _pilotRestExit = null;
        _pilotRestExitRevision++;
        _pilotRestMenuPaused = false;
        _pilotRestRevision++;
        PilotRestStopTimer(ref _pilotRestDeadline); PilotRestStopTimer(ref _pilotSleepDeadline);
        _pilotRestAmbient?.Reset(); _pilotSleepAmbient?.Reset();
        _pilotRestAmbientActive = _pilotSleepAmbientActive = false;
        _pilotRestAmbientPath = null; _pilotRestPlayback = _pilotSleepPlayback = -1;
        _sleepWakeSession = null; _sleepSession.Cancel(); _restSession.UserInteraction();
        if (active) {
            PilotRestFact("rest-sleep-cancel", reason);
            if (restore && !_pilotClosing && !_pilotStopped && _effectiveBaseIdlePath != null) {
                PilotVisualHoldGrabFrame();
                _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath); _gifPlayer?.Resume();
            }
        }
    }

    private void PilotRestClose()
    {
        PilotRestCancel("Close", false);
        _pilotRestAmbient?.Dispose(); _pilotSleepAmbient?.Dispose();
        _pilotRestAmbient = _pilotSleepAmbient = null;
    }
}
