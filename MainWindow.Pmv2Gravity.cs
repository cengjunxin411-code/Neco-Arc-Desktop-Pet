using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private GravitySession? _pilotGravitySession;
    private bool _pilotGravityPending, _pilotGravityGrounded;
    private MonitorSnapshot _pilotGravityMonitor;
    private int _pilotGravityBudgetFrames, _pilotGravityLateFrames;
    private double _pilotGravityDroppedSeconds, _pilotGravityLastElapsed;
    private PhysicsResult? _pilotGravityLastTiming;

    private void PilotGravityFact(string kind, string reason, GravitySession? session = null, object? release = null)
    {
        // Existing End calls this before clearing the session for every exit,
        // including owner cancellation. Never add a second timer or End path.
        if (kind == "physics-gravity-yield" && session != null && _pilotGravityLastTiming is {} timing)
            PilotPhysicsFact("physics-gravity-time", "summary", new {
                failure = timing.Failure.ToString(), elapsed = _pilotGravityLastElapsed,
                substeps = timing.Substeps, droppedSeconds = timing.DroppedSeconds,
                state = session.State.Phase.ToString(), stopped = session.Stopped,
                acceptedRect = _pilotRect, budgetFrames = _pilotGravityBudgetFrames,
                lateFrames = _pilotGravityLateFrames, totalDroppedSeconds = _pilotGravityDroppedSeconds });
        PilotPhysicsFact(kind, reason, new { owner = "Gravity", hwnd = (long)PilotHwnd,
            source = _pilotSourceToken, monitor = _pilotGravityMonitor, acceptedRect = _pilotRect,
            grounded = _pilotGravityGrounded, bounceCount = session?.State.BounceCount,
            velocity = session?.State.Velocity, landingPhase = _pilotGravityLanding?.Phase.ToString(),
            state = session?.State.Phase.ToString(), stopReason = session?.StopReason, release });
    }

    private void PilotGravityTimeFact(GravitySession session, double elapsed)
    {
        if (session.LastResult is not {} step || step.Failure is not
            (PhysicsFailure.BudgetTimeDiscarded or PhysicsFailure.LateFrameDiscarded)) return;
        if (step.Failure == PhysicsFailure.BudgetTimeDiscarded) _pilotGravityBudgetFrames++;
        else _pilotGravityLateFrames++;
        _pilotGravityDroppedSeconds += step.DroppedSeconds;
        _pilotGravityLastElapsed = elapsed; _pilotGravityLastTiming = step;
        if (_pilotGravityBudgetFrames + _pilotGravityLateFrames > 3) return;
        PilotPhysicsFact("physics-gravity-time", step.Failure.ToString(), new {
            failure = step.Failure.ToString(), elapsed, substeps = step.Substeps,
            droppedSeconds = step.DroppedSeconds, state = session.State.Phase.ToString(),
            stopped = session.Stopped, acceptedRect = _pilotRect });
    }

    // Called only at Loaded completion or after the existing MouseUp cancellation.
    // The planner may reset physics; record the fresh identity AFTER it starts.
    // 19C.2 adds only a one-shot release snapshot; startup keeps zero velocity.
    private void PilotGravityScheduleStart(string reason, PhysicalRect? release = null) =>
        PilotGravityScheduleStartCore(reason, release, null);
    private void PilotGravityScheduleBallistic(string reason, PhysicalRect release, PilotPhysicsRelease? snapshot, bool throwSelected=false) =>
        PilotGravityScheduleStartCore(reason, release, snapshot,throwSelected);

    // A release snapshot is one-time: the value here is copied into this closure
    // and is never re-sampled or aged while the Attachment planner is pending.
    private void PilotGravityScheduleStartCore(string reason, PhysicalRect? release, PilotPhysicsRelease? snapshot, bool throwSelected=false)
    {
        if (PilotPhysicsEffectiveMode != PhysicsMode.Gravity) return;
        var decided = release == null || !_pilotAttachmentEnabled;
        var attached = false;
        Action? wake = null;
        var velocity = default(PhysicalVelocity);
        var velocitySource = release == null ? "startup" : "none";
        long? releaseGesture = null;
        double? releaseAgeMs = null;
        var useThrow = false;
        if (release is not null && snapshot is {} captured) {
            // Bind the value to the original gesture/generation/source/HWND and
            // topology. Any mismatch degrades to a legal zero-velocity fall.
            var current = PilotPhysicsSnapshot().Identity;
            var matches = captured.Identity.Generation == current.Generation &&
                captured.Identity.SourceToken == current.SourceToken &&
                captured.Identity.Hwnd == current.Hwnd &&
                captured.Identity.TopologyVersion == current.TopologyVersion &&
                captured.Identity.Gesture + 1 == _pilotGesture && captured.Scale == _pilotScale &&
                PilotClickLive && !_pilotDrag.Active && !_crossActive && !PetSurface.IsMouseCaptured;
            releaseGesture = captured.Identity.Gesture;
            releaseAgeMs = Stopwatch.GetElapsedTime(captured.ReleasedAt).TotalMilliseconds;
            if (matches) { velocity = captured.Velocity; velocitySource = captured.VelocitySource; useThrow=throwSelected; }
            else PilotGravityFact("physics-gravity-refused", "release snapshot identity lost; zero-velocity fallback");
        }
        if (release is {} rect) PilotAttachmentPlanRelease(true, rect, won => {
            decided = true; attached = won;
            if (wake is {} callback) Dispatcher.BeginInvoke(DispatcherPriority.Background, callback);
        }, true);
        else PilotPhysicsEnd("replace gravity startup request", false);
        _pilotGravityPending = true;
        _pilotGravityGrounded = false;
        // Planner Reset has completed. Only now install the visual owner, still
        // before any Gravity lease; rejected throw layouts fall back through the
        // same Idle transaction and the same bounded admission below.
        useThrow = useThrow && PilotGravityThrowBegin(velocity);
        if(throwSelected && !useThrow && _gifPlayer?.ResourcePath==ScruffGifPath)
            _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);
        var identity = PilotPhysicsSnapshot().Identity;
        var scale = _pilotScale;
        var began = Stopwatch.GetTimestamp();
        PilotPhysicsArm(() => {
            var current = PilotPhysicsSnapshot();
            if (PilotPhysicsEffectiveMode != PhysicsMode.Gravity || !PilotClickLive || !PilotFresh ||
                identity with { GeometryRevision = current.Identity.GeometryRevision } != current.Identity ||
                scale != _pilotScale || _pilotTopologyHold) {
                PilotPhysicsEnd("gravity request identity lost", false); return;
            }
            // Only a finally attached planner wins. A candidate lease or a
            // pending pose is still unresolved; the bounded deadline below owns it.
            if (attached) {
                PilotPhysicsEnd("attachment priority", false); return;
            }
            if (Stopwatch.GetElapsedTime(began).TotalMilliseconds > 1750) {
                PilotGravityFact("physics-gravity-refused", "bounded admission deadline");
                PilotPhysicsEnd("gravity admission deadline", false); return;
            }
            // Unresolved planner is never a miss, even after its timer disappears.
            if (!decided || _pilotAttachmentTarget != null || !current.Stable || current.VisualTransaction ||
                current.HigherPriorityOwner || current.AutonomousMovement) return;
            if(useThrow && !PilotGravityThrowOwnsClip) {
                // A rejected footprint may have already restored Idle. Release
                // this visual ownership before evaluating the original Idle gate.
                PilotGravityThrowEnd("throw visual identity/admission lost; Idle fallback");
                useThrow=false;return;
            }
            if (useThrow ? _pilotVisualSizePath!=PilotGravityThrowPath :
                _gifPlayer?.ResourcePath != _effectiveBaseIdlePath || _pilotVisualSizePath!=_effectiveBaseIdlePath) return;
            var topology = _pilotTopology!.State.LastKnownGood!;
            var monitors = topology.Monitors.Where(m => SingleMonitorDrag.Fits(_pilotRect,m.WorkArea)).ToArray();
            if (!PhysicsBounds.FromMonitors(topology.Monitors).IsValid || monitors.Length != 1 ||
                (!_crossScreen && monitors[0] != _pilotMonitor) || (_crossScreen && _crossEffectiveScale != 1)) {
                if(useThrow) {
                    PilotGravityThrowEnd("throw single monitor unavailable; Idle fallback");
                    useThrow=false;return;
                }
                PilotGravityFact("physics-gravity-refused", "no unique complete WorkArea");
                PilotPhysicsEnd("gravity single monitor unavailable", false); return;
            }
            var token = PilotPhysicsTryAcquire();
            if (token == null) {
                if(useThrow) { PilotGravityThrowEnd("throw lease refused; Idle fallback");useThrow=false;return; }
                PilotPhysicsEnd("gravity lease refused", false); return;
            }
            _pilotGravityMonitor = monitors[0];
            _pilotGravitySession = new(_pilotRect, _pilotGravityMonitor.WorkArea, velocity);
            // Eligibility identifies a release, not its initial height. A real
            // Ground impact is still required; a supported zero release has none.
            _pilotGravityFallEligible = release != null;
            _pilotGravityBudgetFrames = _pilotGravityLateFrames = 0;
            _pilotGravityDroppedSeconds = _pilotGravityLastElapsed = 0;
            _pilotGravityLastTiming = null;
            _pilotGravityPending = false;
            _pilotPhysicsRunToken = token;
            // One bounded release summary: the estimator's own source, the
            // one-time snapshot actually applied, and the final Attachment state.
            PilotGravityFact("physics-gravity-begin", reason, _pilotGravitySession, new {
                releaseVelocity = velocity, velocitySource, releaseGesture, releaseAgeMs,
                attachment = release == null ? "startup" : !_pilotAttachmentEnabled ? "disabled"
                    : _pilotAttachmentReleaseOutcome ?? (decided ? "not-attached" : "unresolved"),
                visual = useThrow ? "mouse-release-throw" : release==null ? "startup-idle" : "release-idle",
                releaseRect = release, elapsedSinceReleaseMs = releaseAgeMs });
            // Start elapsed at lease acquisition, never at Loaded or MouseUp.
            var last = Stopwatch.GetTimestamp();
            PilotPhysicsArm(() => {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now-last)/(double)Stopwatch.Frequency; last = now;
                PilotGravityAdvance(elapsed);
            }, TimeSpan.FromSeconds(1d/120));
            if (useThrow) PilotGravityThrowVoiceAdmitted();
        });
        wake = _pilotPhysicsTimerBody; // shared revision AND timer identity fence
        Dispatcher.BeginInvoke(DispatcherPriority.Background, wake!);
    }

    private void PilotGravityAdvance(double elapsed)
    {
        var session = _pilotGravitySession; var token = _pilotPhysicsRunToken;
        if (session == null) return;
        if (PilotPhysicsEffectiveMode != PhysicsMode.Gravity || !PilotPhysicsIsCurrent(token) ||
            _pilotGravityThrowVisual!=null&&!PilotGravityThrowOwnsClip ||
            _pilotGravityLanding!=null&&!PilotGravityLandingOwnsClip ||
            !_pilotTopology!.State.LastKnownGood!.Monitors.Contains(_pilotGravityMonitor)) {
            PilotPhysicsEnd("gravity lease/owner lost", false); return;
        }
        var result = session.Advance(elapsed, _pilotRect, out var target);
        var acceptedStep = result is GravityAdvance.Move or GravityAdvance.Hold || session.Grounded;
        if (result == GravityAdvance.Move) {
            var work = new[] { _pilotGravityMonitor.WorkArea };
            if (!CrossScreenGeometry.Covered(target, work) || !CrossScreenGeometry.TranslationSafe(_pilotRect,target,work) ||
                !PilotPhysicsIsCurrent(token) || !PilotPlace(target,"physics-gravity-step")) {
                PilotPhysicsEnd("gravity safe writer/corridor refused", false); return;
            }
            if (!ReferenceEquals(session,_pilotGravitySession) || !PilotPhysicsIsCurrent(token) ||
                !PilotNative.GetWindowRect(PilotHwnd,out var actual) || actual.Physical != _pilotRect ||
                !session.Accept(actual.Physical)) {
                PilotPhysicsEnd("gravity native readback/owner mismatch", false); return;
            }
        }
        // A fractional/no-move step can contact the ground at the same rounded
        // RECT. Confirm that native RECT too before publishing collision facts.
        else if (acceptedStep && session.LastResult is { Impacts.Length: > 0 } &&
            (!ReferenceEquals(session, _pilotGravitySession) || !PilotPhysicsIsCurrent(token) ||
             !PilotNative.GetWindowRect(PilotHwnd, out var held) || held.Physical != _pilotRect || held.Physical != session.Actual)) {
            PilotPhysicsEnd("gravity held native readback/owner mismatch", false); return;
        }
        if (acceptedStep) { PilotGravityTimeFact(session, elapsed); PilotGravityThrowFace(session.State.Velocity); }
        // Gravity Ground absorbs vertical speed. Air walls/ceiling retain 75%;
        // a wall during ground sliding absorbs X instead of reflecting it.
        if (acceptedStep && session.LastResult is {} step)
            foreach (var impact in step.Impacts)
                PilotPhysicsFact("physics-gravity-impact", impact.Kind switch {
                    CollisionKind.Ground => "WorkArea ground contact",
                    // A contact below the GravityWallBounceMinimumSpeed tolerance is
                    // absorbed, so the bounded class text must not claim a bounce.
                    CollisionKind.Ceiling => "WorkArea ceiling; Gravity wall contact (75% mirror or near-rest absorb), gravity continues",
                    CollisionKind.LeftWall => "WorkArea left wall; Gravity wall contact (75% mirror or near-rest absorb), gravity continues",
                    _ => "WorkArea right wall; Gravity wall contact (75% mirror or near-rest absorb), gravity continues" }, new {
                    hwnd=(long)PilotHwnd,monitor=_pilotGravityMonitor,impact,acceptedRect=_pilotRect });
        if (acceptedStep && _pilotGravityLanding == null && _pilotGravityFallEligible && session.HasGroundContact) {
            PilotGravityLandingBegin(session);
            return;
        }
        if (session.Grounded) {
            PilotActionVoiceEnd(ref _pilotThrowVoiceId, completed: true);
            _pilotGravityGrounded = true;
            PilotGravityFact("physics-gravity-settled", "stable ground; lease returned",session);
            if (_pilotGravityLanding is {} landing) PilotGravityLandingSettled(landing);
            else PilotPhysicsEnd("gravity settled",false);
        } else if (result is GravityAdvance.Stop or GravityAdvance.Invalid)
            PilotPhysicsEnd("gravity " + (session.StopReason ?? "session stopped"),false);
    }
}
