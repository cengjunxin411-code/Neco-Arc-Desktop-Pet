using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

// Pilot-only adapter. The original session decides direction, distance and speed;
// this owner converts its DIPs to the current monitor's physical coordinate space.
public partial class MainWindow
{
    private bool _pilotMoveEnabled, _pilotMoveActive;
    private long _pilotMoveRevision, _pilotMoveGeneration, _pilotMoveGesture, _pilotMoveSourceToken, _pilotMovePlayback;
    private object? _pilotMoveSource;
    private nint _pilotMoveHwnd;
    private string? _pilotMovePath, _pilotMoveMonitorId;
    private PhysicalRect _pilotMoveWorkArea;
    private NecoArcDesktopPet.Coordinates.DpiScale _pilotMoveScale;
    private MovementDefinition? _pilotMoveDefinition;
    private AutonomousMovementSession? _pilotMoveSession;
    private DispatcherTimer? _pilotMoveTimer;
    private readonly Stopwatch _pilotMoveClock = new();
    private readonly List<double> _pilotMoveAcceptedIntervals = new();
    private long _pilotMoveLastAcceptedTick;
    private int _pilotMoveAcceptedSteps;

    private sealed class PilotMoveChoice(Random source) : Random
    {
        public int InitialDirection { get; private set; }
        public double Fraction { get; private set; }
        public override int Next(int maxValue)
        {
            var value = source.Next(maxValue);
            if (maxValue == 2) InitialDirection = value;
            return value;
        }
        public override double NextDouble() => Fraction = source.NextDouble();
    }
    private sealed class PilotMoveReplay(int direction, double fraction) : Random
    {
        public override int Next(int maxValue) => maxValue == 2 ? direction : 0;
        public override double NextDouble() => fraction;
    }

    private void PilotMoveFact(string kind, string reason, object? detail = null) => _pilotLog?.Write(kind,
        new { owner = "PilotAutonomousMovement", revision = _pilotMoveRevision,
            generation = _pilotGeneration, gesture = _pilotGesture, source = _pilotSourceToken,
            hwnd = (long)PilotHwnd, gif = _gifPlayer?.ResourcePath, reason, detail });

    private bool PilotMoveCanChoose() => !PilotPhysicsBusy && _pilotMoveEnabled && !_pilotMoveActive &&
        MovementDefinitions.CanChoose(_availableMovements, _settings.Movement) &&
        _availableMovements.Any(item => _gifPlayer?.PilotVisibleUnionFor(item.ResourcePath) is { IsEmpty: false });

    private bool PilotMoveOwns() => _pilotMoveActive && _pilotMoveGeneration == _pilotGeneration &&
        _pilotMoveGesture == _pilotGesture && _pilotMoveSourceToken == _pilotSourceToken &&
        ReferenceEquals(_pilotMoveSource, _pilotSource) && _pilotMoveHwnd == PilotHwnd && PilotClickLive &&
        _gifPlayer?.PlaybackGeneration == _pilotMovePlayback && _gifPlayer.ResourcePath == _pilotMovePath;

    private bool PilotMoveCanContinue() => PilotMoveContinueRejection() == null;

    // Same short-circuit order and observations as the original boolean guard.
    // Return the first rejected checkpoint without re-querying HWND for logging.
    private string? PilotMoveContinueRejection()
    {
        if (PilotPhysicsBusy) return "physics owner busy";
        if (!PilotMoveOwns()) return "movement owner changed";
        if (!IsVisible) return "window hidden";
        if (_pilotTopologyHold) return "topology hold";
        if (_contextMenuOpen) return "context menu open";
        if (_restSession.IsResting || _sleepSession.IsSleeping || _sleepWakeSession != null) return "Rest/Sleep owner active";
        if (_pilotRandomActive || _pilotClickAction || _clickResolver.IsPending) return "ambient/click owner active";
        if (_pilotClicks?.Held == true || _pilotDrag.Active || _crossActive) return "input gesture active";
        if (PetSurface.IsMouseCaptured) return "mouse captured";
        if (_pilotRocketTimer != null || _pilotRocketSession != null) return "Rocket owner active";
        if (_pilotMotionTimer != null || _pilotMotionState != PilotMotionState.Idle) return "motion owner active";
        if (_pilotAttachmentTimer != null || _pilotAttachmentTarget != null) return "Attachment owner active";
        if (_pilotPlacementDepth != 0 || _pilotHookDepth != 0 || _crossLayoutDepth != 0) return "placement/hook/layout stack busy";
        if (!PilotMapping(out var scale)) return "DPI mapping unavailable";
        if (scale != _pilotMoveScale) return "DPI scale changed";
        if (!PilotFresh) return "topology not fresh";
        var monitors = _pilotTopology!.State.LastKnownGood!.Monitors;
        var monitor = monitors.FirstOrDefault(m => m.Id == _pilotMoveMonitorId);
        var center = new PhysicalPoint((_pilotRect.Left + _pilotRect.Right) / 2,
            (_pilotRect.Top + _pilotRect.Bottom) / 2);
        var centerMonitor = monitors.FirstOrDefault(m => m.Bounds.ContainsClosed(center));
        if (!monitor.IsValid) return "movement monitor invalid";
        if (monitor.WorkArea != _pilotMoveWorkArea) return "movement WorkArea changed";
        if (centerMonitor.Id != monitor.Id) return "movement center monitor changed";
        if (!SingleMonitorDrag.Fits(_pilotRect, monitor.WorkArea)) return "full movement RECT outside WorkArea";
        if (_crossScreen)
        {
            if (!_crossMapped) return "Cross mapping unavailable";
            if (_crossRecovery?.Active == true) return "Cross recovery active";
            if (_crossBoundaryPreparing) return "Cross boundary preparing";
            if (!CrossCheckpoint().Ready) return "Cross checkpoint not ready";
        }
        return _pilotMoveSession == null || !PilotVisualBusy ? null : "movement visual busy";
    }

    private Rect PilotMoveLogicalWorkArea() => new(
        _pilotMoveWorkArea.Left / _pilotMoveScale.X,
        _pilotMoveWorkArea.Top / _pilotMoveScale.Y,
        _pilotMoveWorkArea.Width / _pilotMoveScale.X,
        _pilotMoveWorkArea.Height / _pilotMoveScale.Y);

    private AutonomousMovementSession? PilotMovePlan(Random random)
    {
        if (_pilotMoveDefinition == null || !PilotMoveCanContinue() || PilotVisualBusy ||
            !PilotNative.GetWindowRect(PilotHwnd, out var native) || native.Physical != _pilotRect)
            return null;
        return AutonomousMovementSession.TryCreate(_pilotRect.Left / _pilotMoveScale.X,
            _pilotRect.Width / _pilotMoveScale.X, PilotMoveLogicalWorkArea(), random, _pilotMoveDefinition);
    }

    private void PilotMoveSelect()
    {
        if (!PilotRandomCanStart() || !PilotMoveCanChoose() || _gifPlayer == null) return;
        var center = new PhysicalPoint((_pilotRect.Left + _pilotRect.Right) / 2,
            (_pilotRect.Top + _pilotRect.Bottom) / 2);
        var monitor = _pilotTopology!.State.LastKnownGood!.Monitors.FirstOrDefault(m => m.Bounds.ContainsClosed(center));
        if (!monitor.IsValid || !SingleMonitorDrag.Fits(_pilotRect, monitor.WorkArea)) return;
        var candidates = _availableMovements.Where(item =>
            !_gifPlayer.PilotVisibleUnionFor(item.ResourcePath).IsEmpty).ToArray();
        while (MovementDefinitions.ChooseConfigured(candidates, _movementRandom, _settings.Movement) is { } definition)
        {
            if (!_gifPlayer.TrySwitchTo(definition.ResourcePath))
            {
                _availableMovements = Array.FindAll(_availableMovements, item => item.Type != definition.Type);
                _movementAvailable = _availableMovements.Length > 0;
                candidates = Array.FindAll(candidates, item => item.Type != definition.Type);
                PilotMoveFact("auto-move-resource-refused", "cached movement GIF switch failed", new { definition.ResourcePath });
                continue;
            }
            _pilotMoveActive = true;
            _pilotMoveRevision++;
            _pilotMoveGeneration = _pilotGeneration; _pilotMoveGesture = _pilotGesture;
            _pilotMoveSourceToken = _pilotSourceToken; _pilotMoveSource = _pilotSource; _pilotMoveHwnd = PilotHwnd;
            _pilotMovePlayback = _gifPlayer.PlaybackGeneration; _pilotMovePath = definition.ResourcePath;
            _pilotMoveMonitorId = monitor.Id; _pilotMoveWorkArea = monitor.WorkArea;
            _pilotMoveScale = _pilotScale; _pilotMoveDefinition = definition;
            _pilotMoveSession = null; _pilotMoveAcceptedSteps = 0;
            _pilotMoveAcceptedIntervals.Clear(); _pilotMoveLastAcceptedTick = 0;
            var revision = _pilotMoveRevision;
            // ClipChanged already queued the bounded visual footprint transition.
            // No position timer is armed until that independent callback accepts it.
            _pilotVisualCompleted = () => PilotMoveAdmit(revision);
            PilotMoveFact("auto-move-select", "original shared scheduler selected weighted movement",
                new { definition.Type, definition.ResourcePath, definition.Speed,
                    definition.MinDistance, definition.MaxDistance, monitor = monitor.Id,
                    workArea = monitor.WorkArea, scale = _pilotMoveScale });
            return;
        }
        PilotRandomUpdate();
    }

    private void PilotMoveAdmit(long revision)
    {
        if (revision != _pilotMoveRevision || !PilotMoveCanContinue() || PilotVisualBusy)
        { if (revision == _pilotMoveRevision) PilotMoveCancel("footprint admission or owner rejected"); return; }
        var choice = new PilotMoveChoice(_movementRandom);
        var first = PilotMovePlan(choice);
        if (first == null) { PilotMoveCancel("original movement plan has no safe single-monitor space"); return; }
        var mirror = first.IsMirrored;
        var transform = (ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0];
        transform.ScaleX = mirror ? -1 : 1;
        if (!mirror) { PilotMoveStart(first); return; }
        // The mirror changes the transformed alpha union's local anchor. Admit it
        // through the same visual checkpoint before any location update.
        PilotVisualQueue();
        _pilotVisualCompleted = () => {
            if (revision != _pilotMoveRevision || !PilotMoveCanContinue() || PilotVisualBusy)
            { if (revision == _pilotMoveRevision) PilotMoveCancel("mirrored footprint admission rejected"); return; }
            var replay = new PilotMoveReplay(first.Direction < 0 ? 0 : 1, choice.Fraction);
            var final = PilotMovePlan(replay);
            if (final == null || final.Direction != first.Direction || final.IsMirrored != mirror)
            { PilotMoveCancel("mirrored direction or footprint changed during admission"); return; }
            PilotMoveStart(final);
        };
    }

    private void PilotMoveStart(AutonomousMovementSession session)
    {
        if (!PilotMoveCanContinue() || PilotVisualBusy) { PilotMoveCancel("start checkpoint rejected"); return; }
        _pilotMoveGesture = _pilotGesture;
        _pilotMoveSession = session;
        _pilotMoveClock.Restart();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _pilotMoveTimer = timer;
        var revision = _pilotMoveRevision;
        timer.Tick += (_, _) => {
            if (revision != _pilotMoveRevision || !ReferenceEquals(timer, _pilotMoveTimer)) return;
            var elapsed = _pilotMoveClock.Elapsed.TotalSeconds;
            _pilotMoveClock.Restart();
            PilotMoveAdvance(elapsed);
        };
        timer.Start();
        _gifPlayer?.Resume();
        PilotMoveFact("auto-move-start", "real GIF and HWND footprint admitted",
            new { session.Direction, session.IsMirrored, session.X, session.TargetX,
                session.Definition.Speed, rect = _pilotRect, workArea = _pilotMoveWorkArea });
        // Revision is the movement's one-shot latch, never a global voice ActionId.
        // Consume even a disabled/missing voice so retries cannot replay this session.
        if (_pilotMoveVoiceRevision != revision)
        {
            _pilotMoveVoiceRevision = revision;
            if (_pilotMoveEnabled && _settings.Movement.Enabled && _pilotMovePath != null)
                PilotActionVoiceStart(ref _pilotMoveVoiceId, _pilotMovePath, _pilotMovePlayback,
                    () => revision == _pilotMoveRevision && _pilotMoveSession != null &&
                        _pilotMoveEnabled && _settings.Movement.Enabled && PilotMoveCanContinue());
        }
    }

    private void PilotMoveAdvance(double elapsedSeconds)
    {
        var session = _pilotMoveSession;
        if (session == null) return;
        var rejection = PilotMoveContinueRejection();
        if (rejection != null) { PilotMoveCancel(rejection); return; }
        if (!PilotNative.GetWindowRect(PilotHwnd, out var native))
        { PilotMoveCancel("movement native RECT unreadable"); return; }
        if (native.Physical != _pilotRect)
        { PilotMoveCancel("movement native RECT differs from trusted RECT"); return; }
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        // A delayed Dispatcher callback cannot produce a catch-up teleport.
        if (elapsedSeconds > 0.25) { PilotMoveCancel("late movement tick; no catch-up jump"); return; }
        session.Advance(elapsedSeconds, _pilotMoveWorkArea.Left / _pilotMoveScale.X,
            (_pilotMoveWorkArea.Right - _pilotRect.Width) / _pilotMoveScale.X);
        var x = Math.Round(session.X * _pilotMoveScale.X);
        var target = new PhysicalRect(x, _pilotRect.Top, x + _pilotRect.Width, _pilotRect.Bottom);
        if (!SingleMonitorDrag.Fits(target, _pilotMoveWorkArea) ||
            !CrossScreenGeometry.TranslationSafe(_pilotRect, target, new[] { _pilotMoveWorkArea }))
        { PilotMoveCancel("single-monitor full RECT sweep rejected"); return; }
        if (target != _pilotRect)
        {
            if (!PilotPlace(target, "auto-move-step") || !PilotMoveCanContinue() ||
                !PilotNative.GetWindowRect(PilotHwnd, out var post) || post.Physical != _pilotRect)
            { PilotMoveCancel("existing writer or postcheck rejected"); return; }
            var tick = Stopwatch.GetTimestamp();
            if (_pilotMoveLastAcceptedTick != 0)
                _pilotMoveAcceptedIntervals.Add(Stopwatch.GetElapsedTime(_pilotMoveLastAcceptedTick, tick).TotalMilliseconds);
            _pilotMoveLastAcceptedTick = tick;
            _pilotMoveAcceptedSteps++;
        }
        if (session.IsComplete) PilotMoveEnd("natural target reached", completed: true);
    }

    private void PilotMoveCancel(string reason) => PilotMoveEnd(reason, completed: false);

    private void PilotMoveEnd(string reason, bool completed)
    {
        if (!_pilotMoveActive) return;
        var path = _pilotMovePath;
        PilotActionVoiceEnd(ref _pilotMoveVoiceId, completed);
        var intervals = _pilotMoveAcceptedIntervals.Order().ToArray();
        double? Percentile(double fraction) => intervals.Length == 0 ? null :
            intervals[(int)Math.Ceiling((intervals.Length - 1) * fraction)];
        var steps = _pilotMoveAcceptedSteps;
        var sessionEstablished = _pilotMoveSession != null;
        _pilotMoveActive = false; _pilotMoveRevision++;
        _pilotMoveTimer?.Stop(); _pilotMoveTimer = null; _pilotMoveClock.Reset();
        _pilotMoveSession = null; _pilotMoveDefinition = null;
        _pilotMovePath = null; _pilotMoveMonitorId = null;
        PilotMoveFact("auto-move-end", reason,
            new { acceptedPositions = steps, intervalP50Milliseconds = Percentile(.5),
                intervalP90Milliseconds = Percentile(.9), rect = _pilotRect,
                cadenceIsNotRenderedFps = true, sessionEstablished, playback = _pilotMovePlayback,
                selectedOwner = new { generation = _pilotMoveGeneration, gesture = _pilotMoveGesture,
                    source = _pilotMoveSourceToken, hwnd = (long)_pilotMoveHwnd },
                sameSource = ReferenceEquals(_pilotMoveSource, _pilotSource),
                crossCheckpoint = _crossScreen ? _crossLastCheckpoint : null });
        var transform = (ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0];
        transform.ScaleX = 1;
        if (!_pilotClosing && !_pilotStopped && _gifPlayer?.ResourcePath == path && _effectiveBaseIdlePath != null)
        {
            PilotVisualCancelPending();
            if (!_gifPlayer!.TrySwitchTo(_effectiveBaseIdlePath)) PilotStop("Movement cancellation could not restore base Idle.");
            else { _gifPlayer.Resume(); PilotRestUpdate(); PilotRandomUpdate(); }
        }
    }

    private void PilotMoveClose(string reason)
    {
        PilotMoveCancel(reason);
        _pilotMoveTimer?.Stop(); _pilotMoveTimer = null;
    }

    private void PilotMoveVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) PilotMoveCancel("hidden");
    }
}
