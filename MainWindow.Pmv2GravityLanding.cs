#if PMV2_PILOT
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private const string PilotGravityFallPath = "NecoPetMaterials/special/fall_down.gif";
    private enum PilotGravityLandingPhase { VisualPending, Sliding, WaitingForAnimation }
    private sealed class PilotGravityLandingOwner(PhysicsOwnerIdentity identity, DpiScale scale,
        object? source, string path, long playback, MonitorSnapshot monitor, double vx)
    {
        internal readonly PhysicsOwnerIdentity Identity = identity;
        internal readonly DpiScale Scale = scale;
        internal readonly object? Source = source;
        internal readonly MonitorSnapshot Monitor = monitor;
        internal readonly double VelocityX = vx;
        internal readonly long Began = Stopwatch.GetTimestamp();
        internal string Path = path;
        internal long Playback = playback;
        internal bool SlidingSettled, AnimationCompleted;
        internal PilotGravityLandingPhase Phase;
    }
    private PilotGravityLandingOwner? _pilotGravityLanding;
    private bool _pilotGravityFallEligible;

    private bool PilotGravityLandingIdentity(PilotGravityLandingOwner owner) =>
        ReferenceEquals(owner, _pilotGravityLanding) && PilotPhysicsEffectiveMode == PhysicsMode.Gravity &&
        PilotClickLive && PilotFresh && !_pilotTopologyHold &&
        owner.Identity.Generation == _pilotGeneration && owner.Identity.Gesture == _pilotGesture &&
        owner.Identity.SourceToken == _pilotSourceToken && owner.Identity.Hwnd == (long)PilotHwnd &&
        owner.Identity.TopologyVersion == _pilotTopology?.State.LastKnownGood?.Version &&
        owner.Scale == _pilotScale && ReferenceEquals(owner.Source, _pilotSource) &&
        _pilotTopology!.State.LastKnownGood!.Monitors.Contains(owner.Monitor);

    private bool PilotGravityLandingOwnsClip => _pilotGravityLanding is {} owner &&
        PilotGravityLandingIdentity(owner) && _gifPlayer?.ResourcePath == owner.Path &&
        _gifPlayer.PlaybackGeneration == owner.Playback;

    private void PilotGravityLandingBegin(GravitySession air)
    {
        var velocity = air.State.Velocity.X;
        var monitor = _pilotGravityMonitor;
        PilotGravityFact("physics-gravity-first-ground", "F1; accepted first Ground; zero vertical rebound", air);
        PilotActionVoiceEnd(ref _pilotThrowVoiceId, completed: true);
        // End only the old lease/timer. The next clip must never synchronously
        // Yield a live session or first visit Idle between throw and fall.
        PilotPhysicsEndCore("first Ground; airborne lease returned", false, true);
        _pilotGravityThrowVisual = null;
        _pilotGravityFallEligible = false;
        _pilotGravityGrounded = true;
        var owner = new PilotGravityLandingOwner(PilotPhysicsSnapshot().Identity, _pilotScale,
            _pilotSource, PilotGravityFallPath, _gifPlayer!.PlaybackGeneration + 1, monitor, velocity);
        _pilotGravityLanding = owner;
        _pilotGravityPending = true;
        if (!_gifPlayer.TrySwitchTo(PilotGravityFallPath, loop: false)) {
            PilotPhysicsFact("physics-gravity-landing-fallback", "fall resource unavailable; legal Idle slide");
            owner.AnimationCompleted = true;
            owner.Path = _effectiveBaseIdlePath!;
            owner.Playback = _gifPlayer.PlaybackGeneration + 1;
            ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
            if (!_gifPlayer.TrySwitchTo(owner.Path)) { PilotPhysicsEnd("landing Idle unavailable", false); return; }
        }
        PilotPhysicsArm(() => PilotGravityLandingPoll(owner));
    }

    private void PilotGravityLandingPoll(PilotGravityLandingOwner owner)
    {
        if (!PilotGravityLandingIdentity(owner)) { PilotPhysicsEnd("landing identity lost", false); return; }
        if (Stopwatch.GetElapsedTime(owner.Began).TotalSeconds > 12) {
            PilotPhysicsEnd("landing 12s limit", false); return;
        }
        if (owner.Phase == PilotGravityLandingPhase.WaitingForAnimation) {
            if (!PilotGravityLandingOwnsClip) { PilotPhysicsEnd("landing animation owner lost", false); return; }
            PilotGravityLandingFinish(owner); return;
        }
        if (Stopwatch.GetElapsedTime(owner.Began).TotalMilliseconds > 1750) {
            PilotPhysicsEnd("landing bounded visual admission deadline", false); return;
        }
        var current = PilotPhysicsSnapshot();
        if (!current.Stable || current.VisualTransaction || current.HigherPriorityOwner || current.AutonomousMovement) return;
        if (!PilotGravityLandingOwnsClip) {
            // The existing VisualQueue may reject fall and admit Idle. Only that
            // same-identity, fully postchecked fallback can continue the slide.
            if (_gifPlayer?.ResourcePath != _effectiveBaseIdlePath || _pilotVisualSizePath != _effectiveBaseIdlePath) {
                PilotPhysicsEnd("landing visual admission lost", false); return;
            }
            owner.Path = _effectiveBaseIdlePath!; owner.Playback = _gifPlayer!.PlaybackGeneration;
            owner.AnimationCompleted = true;
            ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
            PilotPhysicsFact("physics-gravity-landing-fallback", "visual refusal; admitted Idle slide");
        }
        if (_pilotVisualSizePath != owner.Path) return;
        var topology = _pilotTopology!.State.LastKnownGood!;
        var monitors = topology.Monitors.Where(m => Pilot.SingleMonitorDrag.Fits(_pilotRect, m.WorkArea)).ToArray();
        if (!PhysicsBounds.FromMonitors(topology.Monitors).IsValid || monitors.Length != 1 || monitors[0] != owner.Monitor ||
            (!_crossScreen && monitors[0] != _pilotMonitor) || (_crossScreen && _crossEffectiveScale != 1) ||
            _pilotRect.Bottom != owner.Monitor.WorkArea.Bottom) {
            PilotPhysicsEnd("landing no unique complete floor WorkArea", false); return;
        }
        var token = PilotPhysicsTryAcquire();
        if (token == null) { PilotPhysicsEnd("landing slide lease refused", false); return; }
        _pilotGravityMonitor = owner.Monitor;
        _pilotGravitySession = new(_pilotRect, owner.Monitor.WorkArea, new(owner.VelocityX, 0));
        _pilotPhysicsRunToken = token;
        _pilotGravityPending = false;
        owner.Phase = PilotGravityLandingPhase.Sliding;
        PilotGravityFact("physics-gravity-slide-begin", "postchecked viewport; independent ground sliding lease", _pilotGravitySession);
        var last = Stopwatch.GetTimestamp();
        PilotPhysicsArm(() => {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - last) / (double)Stopwatch.Frequency; last = now;
            PilotGravityAdvance(elapsed);
        }, TimeSpan.FromSeconds(1d / 120));
        PilotGravityLandingVoiceAdmitted();
    }

    private void PilotGravityLandingSettled(PilotGravityLandingOwner owner)
    {
        PilotPhysicsEndCore("ground slide settled; F2", false, true);
        owner.SlidingSettled = true;
        owner.Phase = PilotGravityLandingPhase.WaitingForAnimation;
        PilotPhysicsFact("physics-gravity-slide-settled", "F2; lease returned; animation may continue");
        PilotPhysicsArm(() => PilotGravityLandingPoll(owner));
        PilotGravityLandingFinish(owner);
    }

    private void PilotGravityLandingCompleted(object? sender, EventArgs args)
    {
        if (_pilotGravityLanding is not {} owner || !PilotGravityLandingOwnsClip ||
            !ReferenceEquals(sender, _gifPlayer) || args is not GifPlaybackCompletedEventArgs completed ||
            completed.ResourcePath != owner.Path || completed.Generation != owner.Playback || !_gifPlayer!.IsComplete) return;
        owner.AnimationCompleted = true;
        PilotPhysicsFact("physics-gravity-fall-completed", "F3; final frame held until F2");
        PilotGravityLandingFinish(owner);
    }

    private void PilotGravityLandingFinish(PilotGravityLandingOwner owner)
    {
        if (!PilotGravityLandingIdentity(owner) || !owner.SlidingSettled || !owner.AnimationCompleted) return;
        PilotPhysicsFact("physics-gravity-landing-idle", "F2 && F3; single Idle recovery");
        PilotActionVoiceEnd(ref _pilotGravityLandVoiceId, completed: true);
        PilotPhysicsEnd("landing complete", false);
    }

    private void PilotGravityLandingEnd(string reason)
    {
        PilotActionVoiceEnd(ref _pilotGravityLandVoiceId, completed: false);
        var owner = _pilotGravityLanding;
        _pilotGravityLanding = null; // before any synchronous clip event
        _pilotGravityFallEligible = false;
        if (owner == null) return;
        var owns = _gifPlayer?.ResourcePath == owner.Path && _gifPlayer.PlaybackGeneration == owner.Playback;
        if (!owns && _gifPlayer?.ResourcePath != _effectiveBaseIdlePath) return;
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
        if (_pilotClosing || _pilotStopped) { _gifPlayer?.Pause(); return; }
        PilotPhysicsFact("physics-gravity-landing-end", reason);
        if (owns && owner.Path != _effectiveBaseIdlePath &&
            (_effectiveBaseIdlePath == null || !_gifPlayer!.TrySwitchTo(_effectiveBaseIdlePath)))
            PilotStop("Gravity landing could not restore idle.");
    }
}
#endif
