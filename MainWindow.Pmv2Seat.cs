using System;
using System.Diagnostics;
using System.Windows;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

// Release-time Idle edge seating (F1-A) and the scale foot-contact anchor (F1-B).
//
// The seat is a one-shot intent created only on an ordinary MouseUp of an
// admitted Drag. It is consumed by the SAME Idle visual transaction that already
// owns the unique PilotPlace/CrossPlace writer, so there is never a second
// native writer and never a late correction after Cancel/LostCapture.
public partial class MainWindow
{
    private readonly record struct PilotSeatIntent(PhysicalRect Release, PlacementReleaseKind Kind, double? SpeedPx,
        long Generation, long SourceToken, nint Hwnd, Coordinates.DpiScale Scale, long ArmedAt);
    private PilotSeatIntent? _pilotSeatIntent;
    // A release correction may only be applied by the Idle transaction that follows
    // this release. A bounded age keeps a stranded intent from firing much later.
    private const double SeatIntentLifetimeMilliseconds = 1500;
    private PlacementReleaseClassifier? _pilotSeatSamples;
    private CursorDwellTracker? _pilotSeatDwell;
    private PhysicsOwnerIdentity? _pilotSeatSampleKey;
    // Last classification outcome, mirrored for tests/diagnostics (the authoritative
    // copy is the idle-edge-seat-classified log row).
    private string? _pilotSeatLastKind;
    private bool _pilotSeatLastProvenStationary;
    private double? _pilotSeatLastStationaryMs;
    // F1-B: the pre-arrange arranged foot contact of one user scale transaction.
    private bool _pilotScaleActive, _pilotScaleGrounded;
    private PhysicalRect _pilotScaleRect;
    private Coordinates.DpiScale _pilotScaleDpi;
    private double _pilotScaleContactX;

    private void PilotSeatFact(string kind, string reason, object? detail = null) => _pilotLog?.Write(kind,
        new { owner = "PilotIdleSeat", generation = _pilotGeneration, gesture = _pilotGesture,
            source = _pilotSourceToken, hwnd = (long)PilotHwnd, reason, detail });

    private PhysicsOwnerIdentity PilotSeatIdentity => new(_pilotGeneration, _pilotGesture, _pilotSourceToken,
        (long)PilotHwnd, _pilotTopology?.State.LastKnownGood?.Version ?? 0, 0);

    private void PilotSeatResetSamples()
    {
        _pilotSeatSamples?.Reset();
        _pilotSeatDwell?.Reset();
        _pilotSeatSampleKey = null;
    }

    private void PilotSeatReset()
    {
        _pilotSeatIntent = null;
        _pilotSeatSamples = null;
        _pilotSeatDwell = null;
        _pilotSeatLastKind = null;
        _pilotSeatLastProvenStationary = false;
        _pilotSeatLastStationaryMs = null;
        PilotSeatResetSamples();
    }

    // Every real drag input event feeds both the short-window velocity estimator and
    // the long-window stationarity proof. The CURSOR is sampled, not the HWND origin:
    // a pose admission and a wall clamp both stop the window without stopping the
    // hand, so the window origin would call a real fling "stationary". This is
    // independent of the physics ReleaseVelocityEstimator (which samples the accepted
    // window RECT for the release lease).
    private void PilotSeatInputSample(PhysicalPoint cursor, long timestamp)
    {
        if (!PilotClickLive || !PetSurface.IsMouseCaptured || !cursor.IsFinite)
        { PilotSeatResetSamples(); return; }
        var key = PilotSeatIdentity;
        if (_pilotSeatSampleKey is { } previous && previous != key)
        {
            PilotSeatFact("seat-samples-reset", "owner identity changed");
            _pilotSeatSamples?.Reset();
            _pilotSeatDwell?.Reset();
        }
        _pilotSeatSampleKey = key;
        _pilotSeatSamples ??= new PlacementReleaseClassifier(Stopwatch.Frequency,
            PlacementReleaseClassifier.MaximumSpeedDipPerSecond);
        _pilotSeatDwell ??= new CursorDwellTracker(Stopwatch.Frequency,
            CursorDwellTracker.ToleranceDip * _pilotScale.X);
        if (!_pilotSeatSamples.Add(timestamp, cursor) || !_pilotSeatDwell.Add(timestamp, cursor))
        {
            PilotSeatFact("seat-samples-reset", "estimator rejected the real input sample");
            PilotSeatResetSamples();
        }
    }

    // Classifies the ordinary MouseUp of an admitted Drag, before the gesture is
    // revoked (the classification consumes the accepted samples). No state is
    // written here: the caller arms the one-shot intent after the cancellation
    // has run, so Cancel/LostCapture can never leave a late correction behind.
    private PilotSeatIntent? PilotSeatClassify(PhysicalRect release, long timestamp)
    {
        if (!PilotClickLive || !PilotFresh || _pilotTopologyHold || PilotMotionBusy || _pilotMotionTimer != null) return null;
        if (_crossScreen && (!_crossMapped || _crossRecovery?.Active == true || _crossBoundaryPreparing)) return null;
        string? veto = null;
        // The releasing Drag itself is still active here; only a different
        // higher-priority owner can veto the correction.
        if (_pilotAttachmentTarget != null || _pilotAttachmentTimer != null || _pilotAttachmentPosePending) veto = "attachment owner";
        else if (_pilotRocketSession != null || _pilotRocketTimer != null || _pilotRocketAttachment != null) veto = "rocket owner";
        else if (_pilotClickAction) veto = "click action owns the pose";
        else if (_pilotMoveActive) veto = "autonomous movement owns the pose";
        // Only the admitted Drag pose owns the pose-width correction.
        else if (!PilotDragClipAdmitted) veto = "current clip is not the admitted Drag";
        else if (release != _pilotRect) veto = "release RECT is not the trusted RECT";
        else if (!PilotNative.GetWindowRect(PilotHwnd, out var native) || native.Physical != release) veto = "native RECT is not trusted";
        else if (!PilotMapping(out var mappedScale) || mappedScale != _pilotScale) veto = "source/DPI mapping changed";
        else if (_pilotSeatSamples is null || _pilotSeatDwell is null ||
            _pilotSeatSampleKey is not { } key || key != PilotSeatIdentity) veto = "no accepted drag samples for this owner";
        if (veto != null)
        {
            PilotSeatFact("idle-edge-seat-skipped", veto, new { release, rect = _pilotRect,
                clip = _gifPlayer?.ResourcePath, seatSamples = _pilotSeatSamples?.BufferedSamples });
            return null;
        }
        var scale = _pilotScale;
        var kind = _pilotSeatSamples!.Classify(timestamp, scale.X, out var speed, out var failure);
        var diagnostic = _pilotSeatSamples.LastDiagnostic;
        // A short (100 ms) velocity window can be empty even though the hand has
        // provably rested for 120 ms. The real-input dwell proof then supplies the
        // stationary classification; it never substitutes for a moving cursor and it
        // never relaxes the 400 DIP/s threshold used for a release in motion.
        var stationaryMs = _pilotSeatDwell?.StationaryMilliseconds(timestamp);
        // A real 120 ms rest always wins over a starved short window or a starved
        // tail (both report InsufficientSamples). An ESTIMATED Throw from the
        // velocity window (failure None) is never overridden.
        var provenStationary = failure == ReleaseFailure.InsufficientSamples && stationaryMs is not null;
        if (provenStationary) kind = PlacementReleaseKind.Stationary;
        _pilotSeatLastKind = kind.ToString();
        _pilotSeatLastProvenStationary = provenStationary;
        _pilotSeatLastStationaryMs = stationaryMs;
        PilotSeatFact("idle-edge-seat-classified", provenStationary ? "real-input stationary proof" : kind.ToString(), new
        {
            release, kind = kind.ToString(), failure = failure.ToString(), speedPx = speed,
            provenStationary, stationaryMs, dwellSamples = _pilotSeatDwell?.Samples,
            dwellTolerancePx = _pilotSeatDwell?.Tolerance,
            samples = diagnostic?.BufferSamples ?? _pilotSeatSamples.BufferedSamples,
            tailSamples = _pilotSeatSamples.TailSamples, tailSpeedPx = _pilotSeatSamples.LastTailSpeedPx,
            classifierBranch = _pilotSeatSamples.LastBranch, branch = diagnostic?.Branch,
            activeSamples = diagnostic?.ActiveSamples, inlierSamples = diagnostic?.InlierSamples,
            activeSpanMs = diagnostic?.ActiveSpanMs, lastSampleAgeMs = diagnostic?.LastSampleAgeMs,
            velocitySource = diagnostic?.VelocitySource,
            thresholdDips = PlacementReleaseClassifier.MaximumSpeedDipPerSecond,
            dwellRequiredMs = CursorDwellTracker.RequiredStationaryMilliseconds
        });
        if (kind is not (PlacementReleaseKind.Stationary or PlacementReleaseKind.Slow))
        {
            PilotSeatFact("idle-edge-seat-skipped", "release is not a slow placement", new
            {
                release, kind = kind.ToString(), speedPx = speed, failure = failure.ToString(),
                samples = _pilotSeatSamples.BufferedSamples, thresholdDips = PlacementReleaseClassifier.MaximumSpeedDipPerSecond
            });
            return null;
        }
        var intent = new PilotSeatIntent(release, kind, speed, _pilotGeneration, _pilotSourceToken, PilotHwnd, scale,
            Stopwatch.GetTimestamp());
        PilotSeatFact("idle-edge-seat-intent", kind.ToString(), new
        {
            release, speedPx = speed, thresholdDips = PlacementReleaseClassifier.MaximumSpeedDipPerSecond,
            thresholdPx = PlacementReleaseClassifier.MaximumSpeedDipPerSecond * scale.X, scale
        });
        return intent;
    }

    private void PilotSeatArm(PilotSeatIntent? intent)
    {
        _pilotSeatIntent = intent;
        if (intent is { } armed)
            PilotSeatFact("idle-edge-seat-armed", "one-shot intent awaits the Idle visual transaction", new
            { armed.Release, kind = armed.Kind.ToString(), rect = _pilotRect, releaseStable = _pilotRect == armed.Release });
    }

    // Consumed by the Idle visual transaction. Never writes: the caller keeps the
    // single PilotPlace/CrossPlace commit and its full corridor postcheck.
    private PhysicalRect? PilotSeatPlacement(PhysicalRect release, PhysicalRect natural, string? path, bool eligible, PhysicalRect[] areas)
    {
        if (_pilotSeatIntent is not { } intent) return null;
        // A non-Idle transaction may run first (click/pose). Keep the bounded intent
        // for the Idle admission that belongs to the same release.
        if (path != _effectiveBaseIdlePath)
        { PilotSeatFact("idle-edge-seat-skipped", "admitted clip is not the base Idle", new { path }); return null; }
        _pilotSeatIntent = null; // one-shot for the Idle transaction
        if (!eligible)
        { PilotSeatFact("idle-edge-seat-skipped", "return/retained viewport placement is not seatable", new { release, natural }); return null; }
        if (Stopwatch.GetElapsedTime(intent.ArmedAt).TotalMilliseconds > SeatIntentLifetimeMilliseconds)
        { PilotSeatFact("idle-edge-seat-skipped", "release intent expired", new { release, ageMs = Stopwatch.GetElapsedTime(intent.ArmedAt).TotalMilliseconds }); return null; }
        if (intent.Release != release)
        { PilotSeatFact("idle-edge-seat-skipped", "release RECT is no longer trusted", new { release, intent.Release }); return null; }
        if (intent.Generation != _pilotGeneration || intent.SourceToken != _pilotSourceToken ||
            intent.Hwnd != PilotHwnd || intent.Scale != _pilotScale)
        { PilotSeatFact("idle-edge-seat-skipped", "owner/DPI identity changed", new { release }); return null; }
        if (IdleEdgeSeat.Resolve(release, natural, areas) is not { } seat)
        { PilotSeatFact("idle-edge-seat-skipped", "no provable outer-wall contact", new { release, natural, areas }); return null; }
        if (CrossScreenGeometry.Fit(seat.Target, areas) != seat.Target ||
            !CrossScreenGeometry.TranslationSafe(release, seat.Target, areas))
        {
            PilotSeatFact("idle-edge-seat-refused", "Idle footprint/corridor denied", new { release, natural, seat.Target, seat.Side, areas });
            return null;
        }
        PilotSeatFact("idle-edge-seat", "one-time legal outer-wall seating", new
        {
            kind = intent.Kind.ToString(), speedPx = intent.SpeedPx, side = seat.Side,
            release, natural, target = seat.Target, correctionPx = seat.CorrectionPx,
            tolerancePx = IdleEdgeSeat.ContactTolerancePx
        });
        return seat.Target;
    }

    // ---- F1-B: arranged foot-contact anchor for one user scale transaction ----

    private bool PilotGroundContact()
    {
        if (!PilotClickLive || _pilotTopologyHold || _crossBoundaryPreparing || _crossRecovery?.Active == true) return false;
        if (_pilotDrag.Active || _crossActive || PetSurface.IsMouseCaptured) return false;
        if (_pilotMotionState is PilotMotionState.Falling or PilotMotionState.Landing || PilotMotionBusy || _pilotMotionTimer != null) return false;
        if (_pilotPhysicsSession != null || _pilotGravitySession != null || _pilotGravityPending || _pilotGravityLanding != null) return false;
        if (_pilotAttachmentTarget != null || _pilotAttachmentTimer != null || _pilotAttachmentPosePending) return false;
        if (_pilotRocketSession != null || _pilotRocketTimer != null || _pilotRocketAttachment != null) return false;
        if (_pilotMoveActive || _pilotMoveTimer != null) return false;
        var rect = _pilotRect;
        if (_pilotTopology?.State.LastKnownGood is not { } topology) return false;
        // Reliable ground contact is the arranged visual bottom edge standing on a
        // verified WorkArea bottom, with no dynamic owner above it.
        return topology.Monitors.Any(m =>
            Math.Abs(rect.Bottom - m.WorkArea.Bottom) < 1e-6 && SingleMonitorDrag.Fits(rect, m.WorkArea));
    }

    private double PilotVisualFootCenterX => (_pilotVisualVisibleBounds.Left + _pilotVisualVisibleBounds.Right) / 2;

    private void PilotScaleBegin()
    {
        _pilotScaleActive = false;
        if (!_pilotReady || _pilotClosing || _pilotStopped || !PilotFresh || !_pilotVisualCalibrated) return;
        if (_pilotTopologyHold || !PilotMapping(out var scale) || scale != _pilotScale) return;
        if (!PilotNative.GetWindowRect(PilotHwnd, out var native) || native.Physical != _pilotRect) return;
        if (_crossScreen && (!_crossMapped || _crossRecovery?.Active == true)) return;
        _pilotScaleDpi = scale;
        _pilotScaleRect = _pilotRect;
        // Horizontal foot-contact point: the centre of the cropped visible union.
        // Vertical contact is the union bottom edge, which the viewport fits to.
        // Rounded once: the anchor is a physical screen coordinate, and a rounded
        // anchor makes the re-derivation idempotent instead of accumulating the
        // layout's half-pixel remainder over long wheel sequences.
        _pilotScaleContactX = Math.Round(_pilotRect.Left + PilotVisualFootCenterX * scale.X);
        _pilotScaleGrounded = PilotGroundContact();
        _pilotScaleActive = true;
        PilotSeatFact("scale-contact-anchor", "arranged foot contact captured before layout", new
        {
            rect = _pilotRect, contactX = _pilotScaleContactX, anchorLocal = _pilotVisualAnchorLocal,
            visibleBounds = _pilotVisualVisibleBounds,
            grounded = _pilotScaleGrounded, scale, clip = _gifPlayer?.ResourcePath
        });
    }

    private void PilotScaleEnd()
    {
        if (_pilotScaleActive)
            PilotSeatFact("scale-contact-anchored", "wheel/menu scale kept the arranged foot contact", new
            {
                before = _pilotScaleRect, after = _pilotRect, grounded = _pilotScaleGrounded,
                contactX = _pilotScaleContactX, moved = new PhysicalVector(_pilotRect.Left - _pilotScaleRect.Left,
                    _pilotRect.Bottom - _pilotScaleRect.Bottom)
            });
        _pilotScaleActive = false;
        _pilotScaleGrounded = false;
    }
}
