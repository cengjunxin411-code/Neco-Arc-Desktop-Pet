using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.Pilot.Physics;
using NecoArcDesktopPet.Settings;

namespace NecoArcDesktopPet;

// Pilot-only admission plus bounded 19B.2 planar release consumer.
public partial class MainWindow
{
    private bool _pilotPhysicsEnabled;
    private PhysicsMode _pilotPhysicsMode;
    private PhysicsOwnershipGate? _pilotPhysicsGate;

    internal PhysicsMode PilotPhysicsPreference => _pilotPhysicsMode;
    internal bool PilotPhysicsExperimentalEnabled => _pilotPhysicsEnabled;
    internal PhysicsMode PilotPhysicsEffectiveMode => _pilotPhysicsEnabled && Enum.IsDefined(_pilotPhysicsMode)
        ? _pilotPhysicsMode : PhysicsMode.Off;

    // Field-only observation: Owns/IsCurrent would Observe and can revoke a lease.
    internal bool PilotPhysicsBusy => _pilotPhysicsPending != null || _pilotPhysicsActiveRelease != null ||
        _pilotPhysicsSession != null || _pilotGravitySession != null || _pilotGravityPending ||
        _pilotGravityLanding != null || _pilotGravityThrowVisual != null || _pilotPlanarThrowVisual != null ||
        _pilotPhysicsTimer != null || _pilotPhysicsRunToken != null || _pilotPhysicsGate?.CurrentToken != null;

    // UI-thread query only; no layout, checkpoint, Gate.Observe or owner cleanup.
    internal string? PilotPhysicsChangeUnavailableReason(PhysicsMode requested)
    {
        if (!Enum.IsDefined(requested)) return "物理模式无效。";
        if (_pilotClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return "Pilot 正在关闭，无法切换物理模式。";
        if (_pilotStopped) return "Pilot 已安全停止，无法切换物理模式。";
        if (!_pilotReady || _pilotSource is null or { IsDisposed: true })
            return "Pilot 尚未就绪，无法切换物理模式。";
        if (requested == _pilotPhysicsMode) return null;
        // Even Off must not change the release semantics of an active gesture.
        if (_pilotDrag.Active || _crossActive || PetSurface.IsMouseCaptured ||
            _pilotClicks?.Held == true || _clickResolver.IsPending)
            return "正在拖拽、捕获鼠标或保持点击，请结束当前手势后再切换。";
        if (_pilotPlacementDepth != 0 || _pilotHookDepth != 0 || _crossLayoutDepth != 0)
            return "正在处理窗口位置或布局，请稍后再切换物理模式。";
        // Off only cancels through the existing Reset/visual admission path.
        // It may interrupt a physics pending/throw/landing owner, never schedule motion.
        if (requested == PhysicsMode.Off) return null;
        if (PilotVisualBusy || _pilotDragVisualPendingAdmit || _pilotSizeVerifyTimer != null || _pilotWheelPendingHeight != null)
            return "视觉或尺寸事务尚未结束，请稍后再切换物理模式。";
        if (PilotPhysicsBusy) return "物理运动或甩抛、落地交接尚未结束，请稍后再切换物理模式。";
        if (_pilotTopologyHold || _pilotTopologyChange != null || _crossRecovery?.Active == true ||
            _crossBoundaryPreparing || _crossBoundary != null || _crossMove != null || _seamPlan != null || _seamDpiReanchor)
            return "屏幕拓扑或跨屏恢复尚未结束，请稍后再切换物理模式。";
        if (_pilotAttachmentTimer != null || _pilotAttachmentTarget != null || _pilotAttachmentPosePending ||
            _pilotAttachmentHandoff != null || _pilotAttachmentRefuse != null)
            return "依附或依附交接尚未结束，请稍后再切换物理模式。";
        if (PilotMotionBusy || _pilotMotionTimer != null || _pilotFallPending != null ||
            _pilotRocketTimer != null || _pilotRocketSession != null || _pilotRocketAttachment != null ||
            _pilotReleaseFloatTimer != null || _pilotClickAction || _pilotMoveActive || _pilotMoveTimer != null ||
            _pilotRandomActive || PilotRestHasState || _contextMenuOpen || PetSurface.ContextMenu?.IsOpen == true)
            return "当前动作尚未结束，请回到空闲状态后再切换物理模式。";
        return null;
    }

    // Runtime-only Apply boundary. Caller owns the one subsequent snapshot save.
    internal void PilotPhysicsApplyPreference(PhysicsMode mode)
    {
        Dispatcher.VerifyAccess();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (PilotPhysicsChangeUnavailableReason(mode) is {} reason) throw new InvalidOperationException(reason);
        if (mode == _pilotPhysicsMode) return;
        PilotPhysicsRestoreMode(mode);
    }

    internal void PilotPhysicsRestoreMode(PhysicsMode mode)
    {
        Dispatcher.VerifyAccess();
        PilotPhysicsReset();
        _pilotPhysicsMode = Enum.IsDefined(mode) ? mode : PhysicsMode.Off;
        _pilotPhysicsGate?.Configure(_pilotPhysicsEnabled, _pilotPhysicsMode);
    }

    // Compatibility boundary: runtime validation followed by exactly one save.
    internal PetSettingsSaveResult PilotPhysicsSetMode(PhysicsMode mode)
    {
        PilotPhysicsApplyPreference(mode);
        return SaveSettingsSnapshot();
    }

    private void PilotPhysicsYield()
    {
        _pilotPhysicsGate?.Yield();
        // A running footprint change ends this conservative single-track session.
        // The initial release handoff may wait for its own idle footprint instead.
        if (_pilotPhysicsSession != null || _pilotGravitySession != null) PilotPhysicsEnd("visual footprint yielded", false);
    }
    private void PilotPhysicsReset()
    {
        PilotPhysicsEnd("owner reset", false);
        _pilotGravityGrounded = false;
        PilotPhysicsResetSamples();
        _pilotPhysicsGate?.Reset();
    }

    private PhysicsOwnershipSnapshot PilotPhysicsSnapshot()
    {
        var identity = new PhysicsOwnerIdentity(_pilotGeneration, _pilotGesture, _pilotSourceToken,
            (long)PilotHwnd, _pilotTopology?.State.LastKnownGood?.Version ?? 0, _pilotVisualRevision);
        var visual = PilotVisualBusy;
        // Reuse the current actual owners, including planning timers before they
        // have a session. A completed early placement does not end G's guard.
        var higher = _pilotDrag.Active || _crossActive || (PetSurface.IsMouseCaptured && _pilotPhysicsHold == null) ||
            _pilotClicks?.Held == true || _clickResolver.IsPending || _pilotClickAction ||
            _pilotRocketTimer != null || _pilotRocketSession != null || _pilotRocketAttachment != null ||
            PilotMotionBusy || _pilotMotionTimer != null || _pilotFallPending != null ||
            _pilotAttachmentTimer != null || _pilotAttachmentTarget != null || _pilotAttachmentPosePending ||
            _pilotReleaseFloatTimer != null || _pilotDragVisualPendingAdmit ||
            // A queued user wheel intent has no layout ownership. Its delayed
            // admission rejects while busy; observing it must not revoke physics.
            _pilotSizeVerifyTimer != null ||
            _contextMenuOpen || PetSurface.ContextMenu?.IsOpen == true || PilotRestHasState || _pilotRandomActive;
        var stable = PilotClickLive && IsVisible && !_pilotTopologyHold &&
            _pilotPlacementDepth == 0 && _pilotHookDepth == 0 && _crossLayoutDepth == 0 &&
            (!_crossScreen || _crossInitialized && _crossMapped && _crossRecovery?.Active != true &&
                !_crossBoundaryPreparing && _crossBoundary == null && _crossMove == null && _seamPlan == null) &&
            PilotMapping(out var scale) && scale == _pilotScale;
        // A visual transaction temporarily owns layout; it will do its own full
        // postcheck. Never UpdateLayout, repair, place or bless geometry here.
        if (stable && !visual)
            stable = IsMeasureValid && IsArrangeValid && PetSurface.IsMeasureValid && PetSurface.IsArrangeValid &&
                PetImage.IsMeasureValid && PetImage.IsArrangeValid &&
                PilotNative.GetWindowRect(PilotHwnd, out var actual) && actual.Physical == _pilotRect &&
                double.IsFinite(ActualWidth) && double.IsFinite(ActualHeight) &&
                Math.Abs(ActualWidth * _pilotScale.X - _pilotRect.Width) <= 2 &&
                Math.Abs(ActualHeight * _pilotScale.Y - _pilotRect.Height) <= 2 &&
                (_crossScreen ? CrossCheckpoint().Ready && CrossScreenGeometry.Covered(_pilotRect, CrossWork)
                    : SingleMonitorDrag.Fits(_pilotRect, _pilotMonitor.WorkArea));
        return new(identity, stable, visual, higher, _pilotMoveActive || _pilotMoveTimer != null);
    }

    private PhysicsRunToken? PilotPhysicsTryAcquire()
    {
        Dispatcher.VerifyAccess();
        PilotCancelWheelPending(); // old user intent cannot cross into a physics lease
        if (PilotPhysicsEffectiveMode == PhysicsMode.Off) { PilotPhysicsReset(); return null; }
        _pilotPhysicsGate ??= new PhysicsOwnershipGate();
        _pilotPhysicsGate.Configure(_pilotPhysicsEnabled, _pilotPhysicsMode);
        return _pilotPhysicsGate.TryAcquire(PilotPhysicsSnapshot());
    }

    private bool PilotPhysicsIsCurrent(PhysicsRunToken? token)
    {
        Dispatcher.VerifyAccess();
        if (PilotPhysicsEffectiveMode == PhysicsMode.Off) { PilotPhysicsReset(); return false; }
        return _pilotPhysicsGate?.IsCurrent(token, PilotPhysicsSnapshot()) == true;
    }

    // Off leaves the gate unallocated; autonomous movement observes this same lease.
    private bool PilotPhysicsOwns => _pilotPhysicsGate?.CurrentToken is {} token && PilotPhysicsIsCurrent(token);

    private ReleaseVelocityEstimator? _pilotPhysicsSamples;
    private PhysicalVector _pilotPhysicsSampleOrigin;
    private (PhysicsOwnerIdentity Identity, Coordinates.DpiScale Scale, double Width, double Height, PhysicalVector Grab)? _pilotPhysicsSampleKey;
    private DispatcherTimer? _pilotPhysicsTimer;
    private EventHandler? _pilotPhysicsTimerHandler;
    private Action? _pilotPhysicsTimerBody;
    private PlanarInertiaSession? _pilotPhysicsSession;
    private PhysicsRunToken? _pilotPhysicsRunToken;
    private PilotPhysicsRelease? _pilotPhysicsPending;
    private PilotPhysicsRelease? _pilotPhysicsActiveRelease;
    private bool _pilotPhysicsFirstAccepted;
    private long _pilotPhysicsRevision;
    // Diagnostic identities deliberately survive sample/owner Reset within the
    // same gesture. Only accepted summaries are deduplicated; a later refusal
    // must retain its own release timestamp and diagnostic snapshot.
    private (long Gesture,long Generation,long Source,long Hwnd)? _pilotPhysicsSummaryKey, _pilotPhysicsRefusalKey;
    private (long Gesture,long Generation,long Source,long Hwnd) PilotPhysicsDiagnosticKey =>
        (_pilotGesture,_pilotGeneration,_pilotSourceToken,(long)PilotHwnd);
    // 19C.2: VelocitySource is the estimator's own full/tail/none attribution.
    private sealed record PilotPhysicsRelease(PhysicsOwnerIdentity Identity, Coordinates.DpiScale Scale,
        PhysicalVelocity Velocity, MonitorSnapshot Monitor, long ReleasedAt, string VelocitySource = "none");

    private void PilotPhysicsFact(string kind, string reason, object? detail = null) =>
        _pilotLog?.Write(kind, new { reason, revision = _pilotPhysicsRevision, gesture=_pilotGesture,
            generation=_pilotGeneration,ticks=Stopwatch.GetTimestamp(),detail });

    private void PilotPhysicsStage(PilotPhysicsRelease release,string stage) => PilotPhysicsFact(
        "physics-handoff-stage",stage,new{releaseTick=release.ReleasedAt,
            releaseGesture=release.Identity.Gesture,elapsedMs=Stopwatch.GetElapsedTime(release.ReleasedAt).TotalMilliseconds,
            frequency=Stopwatch.Frequency,rect=_pilotRect});

    private void PilotPhysicsResetSamples(string reason="owner reset")
    {
        if(_pilotPhysicsSampleKey!=null)PilotPhysicsFact("physics-samples-reset",reason);
        _pilotPhysicsSamples?.Reset();
        _pilotPhysicsSampleKey = null;
        _pilotPhysicsSampleOrigin = default;
    }

    // An admitted pose moves/resizes HWND without moving the mouse. Continue the
    // same physical track in its original coordinate origin: no synthetic sample,
    // velocity impulse or erased real movement. Identity changes still reset.
    private void PilotPhysicsRebaseDragSamples(PhysicalRect old, PhysicalRect placed)
    {
        var identity=PilotPhysicsSnapshot().Identity with { GeometryRevision=_pilotDragVisualRevision };
        if(_pilotPhysicsSampleKey is {} key && key.Scale==_pilotScale && key.Width==old.Width && key.Height==old.Height &&
            key.Identity==(identity with { GeometryRevision=key.Identity.GeometryRevision }))
        {
            _pilotPhysicsSampleOrigin=new(_pilotPhysicsSampleOrigin.X+old.Left-placed.Left,
                _pilotPhysicsSampleOrigin.Y+old.Top-placed.Top);
            _pilotPhysicsSampleKey=(identity,_pilotScale,placed.Width,placed.Height,
                _crossScreen?_crossGrab:default);
        }
        else PilotPhysicsResetSamples("drag visual owner changed");
    }

    // Called only after the existing writer AND drag owner accepted the RECT.
    // Caller supplies monotonic ticks; this same production boundary is testable.
    private void PilotPhysicsAcceptedSample(PhysicalPoint cursor, long timestamp)
    {
        if (!PilotPhysicsReleaseMode) return;
        // Same short-circuit order and reset as R1; observe even before a key
        // exists. Never query a checkpoint (which can update recovery state).
        // The admitted drag pose is whichever clip this gesture selected (Gravity:
        // scruff, otherwise dragged); its own playback generation is the fence.
        var pendingVisual=_pilotDragVisualPendingAdmit && _pilotDragVisualPrevious is {} snapshot &&
            snapshot.Path==_pilotVisualSizePath && snapshot.PlaybackPath==snapshot.Path &&
            PilotDragClipAdmitted && _gifPlayer!.PlaybackGeneration==snapshot.PlaybackGeneration+1;
        if (!PilotPhysicsDragReady(allowPendingVisual:pendingVisual)) {
            PilotPhysicsSampleRefused("not-ready");
            PilotPhysicsResetSamples("accepted sample owner/native/cursor rejected"); return;
        }
        if (!cursor.IsFinite) {
            PilotPhysicsSampleRefused("invalid-cursor");
            PilotPhysicsResetSamples("accepted sample owner/native/cursor rejected"); return;
        }
        if (!PilotNative.GetWindowRect(PilotHwnd, out var native) || native.Physical != _pilotRect) {
            PilotPhysicsSampleRefused("invalid-native");
            PilotPhysicsResetSamples("accepted sample owner/native/cursor rejected"); return;
        }
        var key = (Identity:PilotPhysicsSnapshot().Identity with { GeometryRevision = _pilotDragVisualRevision }, Scale:_pilotScale,
            Width:_pilotRect.Width, Height:_pilotRect.Height, Grab:_crossScreen ? _crossGrab : default(PhysicalVector));
        // Ensure may queue a pose before this accepted move is sampled. Until
        // admission, HWND still has the verified old geometry/grab. Keep that
        // track's key; successful admission alone translates its origin/key.
        if(_pilotDragVisualPendingAdmit && _pilotPhysicsSampleKey is {} previous &&
            previous.Scale==key.Scale && previous.Width==key.Width && previous.Height==key.Height && previous.Grab==key.Grab &&
            previous.Identity==(key.Identity with { GeometryRevision=previous.Identity.GeometryRevision }) &&
            previous.Identity.GeometryRevision+1==key.Identity.GeometryRevision)
            key=previous;
        if (_pilotPhysicsSampleKey != key) PilotPhysicsResetSamples("sample identity/geometry/grab changed");
        _pilotPhysicsSampleKey = key;
        _pilotPhysicsSamples ??= new ReleaseVelocityEstimator(Stopwatch.Frequency);
        if (!_pilotPhysicsSamples.Add(timestamp, new(_pilotRect.Left+_pilotPhysicsSampleOrigin.X,
            _pilotRect.Top+_pilotPhysicsSampleOrigin.Y))) {
            PilotPhysicsSampleRefused("add-failed");
            PilotPhysicsResetSamples("estimator Add rejected timestamp/point");
        }
    }

    private void PilotPhysicsSampleRefused(string reason)
    {
        if(_pilotLog==null || _pilotPhysicsRefusalKey==PilotPhysicsDiagnosticKey)return;
        _pilotPhysicsRefusalKey=PilotPhysicsDiagnosticKey;
        var mapped=PilotMapping(out var scale); // read-only matrix/native query
        _pilotLog.Write("physics-sample-refused",new{reason,gesture=_pilotGesture,generation=_pilotGeneration,
            source=_pilotSourceToken,hwnd=(long)PilotHwnd,cross=_crossScreen,ticks=Stopwatch.GetTimestamp(),
            live=PilotClickLive,captured=PetSurface.IsMouseCaptured,fresh=PilotFresh,
            pendingAdmit=_pilotDragVisualPendingAdmit,mapped,
            scaleOk=mapped && scale==_pilotScale && (!_crossScreen || _crossEffectiveScale==1),
            topologyHold=_pilotTopologyHold,dragActive=_crossScreen ? _crossActive : _pilotDrag.Active,
            dragging=_crossScreen ? _crossDragging : _pilotDrag.Dragging,
            crossActive=_crossScreen ? (bool?)_crossActive : null,
            crossDragging=_crossScreen ? (bool?)_crossDragging : null,
            crossMapped=_crossScreen ? (bool?)_crossMapped : null,
            recovery=_crossScreen ? (bool?)(_crossRecovery?.Active==true) : null,
            boundary=_crossScreen ? (bool?)(_crossBoundaryPreparing || _crossBoundary!=null) : null,
            seamPlan=_crossScreen ? (bool?)(_seamPlan!=null) : null,
            dpiReanchor=_crossScreen ? (bool?)_seamDpiReanchor : null});
    }

    // Gravity and PlanarInertia share the one trusted accepted-sample owner.
    private bool PilotPhysicsReleaseMode => PilotPhysicsEffectiveMode is PhysicsMode.PlanarInertia or PhysicsMode.Gravity;
    private bool PilotPhysicsDragReady(bool allowPendingVisual=false) => PilotPhysicsReleaseMode &&
        PilotClickLive && PetSurface.IsMouseCaptured && PilotFresh && !_pilotTopologyHold &&
        (allowPendingVisual || !_pilotDragVisualPendingAdmit) && PilotMapping(out var scale) && scale == _pilotScale &&
        (_crossScreen ? _crossActive && _crossDragging && _crossMapped && _crossEffectiveScale == 1 &&
            _crossRecovery?.Active != true && !_crossBoundaryPreparing && _crossBoundary == null &&
            _seamPlan == null && !_seamDpiReanchor : _pilotDrag.Active && _pilotDrag.Dragging);

    // Value captured before CancelGesture's mandatory Reset. Never retain its lease.
    private PilotPhysicsRelease? PilotPhysicsCaptureRelease(bool dragged, long timestamp)
    {
        if(!dragged||!PilotPhysicsReleaseMode)return null;
        bool? nativeTrusted=null,identityMatches=null,geometryMatches=null,grabMatches=null,uniqueWorkArea=null;
        var dragReady=PilotPhysicsDragReady();
        void Summary(string stage,string reason,ReleaseEstimate? estimate=null,ReleaseDiagnostic? diagnostic=null) {
            double? speed=estimate is {Reason:ReleaseFailure.None} e ? Math.Sqrt(e.Velocity.X*e.Velocity.X+e.Velocity.Y*e.Velocity.Y) : null;
            var accepted=estimate is {Reason:ReleaseFailure.None} && speed>4;
            if(_pilotLog==null || accepted && _pilotPhysicsSummaryKey==PilotPhysicsDiagnosticKey)return;
            if(accepted)_pilotPhysicsSummaryKey=PilotPhysicsDiagnosticKey;
            _pilotLog.Write("physics-release-samples",new{gesture=_pilotGesture,generation=_pilotGeneration,
                source=_pilotSourceToken,hwnd=(long)PilotHwnd,cross=_crossScreen,mode=PilotPhysicsEffectiveMode.ToString(),
                flag=_pilotPhysicsEnabled,ticks=timestamp,frequency=Stopwatch.Frequency,
                releaseType=estimate is {} gentle && (gentle.Reason==ReleaseFailure.Stationary || speed<=4) ? "gentle" : "drag",stage,
                estimated=estimate!=null,reason,diagnosticBranch=diagnostic?.Branch,
                velocitySource=diagnostic?.VelocitySource ?? "none",
                fallbackAttempted=diagnostic?.FallbackAttempted ?? false,
                fallbackFailureBranch=diagnostic?.FallbackFailureBranch,
                releaseFailure=estimate?.Reason.ToString(),deadZone=speed is {} magnitude ? (bool?)(magnitude<=4) : null,
                speed=speed is {} v && double.IsFinite(v) ? speed : null,
                qualification=new{dragReady,sampleKeyPresent=_pilotPhysicsSampleKey!=null,
                    estimatorPresent=_pilotPhysicsSamples!=null,
                    pendingMove=_crossScreen && _crossMove?.Status==DispatcherOperationStatus.Pending,
                    nativeTrusted,identityMatches,geometryMatches,grabMatches,uniqueWorkArea},
                bufferSamples=diagnostic?.BufferSamples ?? _pilotPhysicsSamples?.BufferedSamples,
                acceptedPlacementId=_crossScreen ? _crossAcceptedId : null,diagnostic});
        }
        if (!dragged || !dragReady || _pilotPhysicsSampleKey is not {} key ||
            _pilotPhysicsSamples == null || (_crossScreen && _crossMove?.Status == DispatcherOperationStatus.Pending) ||
            !(nativeTrusted=PilotNative.GetWindowRect(PilotHwnd, out var native) && native.Physical == _pilotRect).Value ||
            !(identityMatches=key.Identity == (PilotPhysicsSnapshot().Identity with { GeometryRevision = _pilotDragVisualRevision })).Value ||
            !(geometryMatches=key.Scale == _pilotScale && key.Width == _pilotRect.Width && key.Height == _pilotRect.Height).Value ||
            !(grabMatches=!_crossScreen || key.Grab == _crossGrab).Value)
        { Summary("qualification","qualification guard rejected");PilotPhysicsFact("physics-release-refused","qualification guard rejected");PilotPhysicsResetSamples("release qualification rejected"); return null; }
        var topology = _pilotTopology!.State.LastKnownGood!;
        var monitors = topology.Monitors.Where(m => SingleMonitorDrag.Fits(_pilotRect, m.WorkArea)).ToArray();
        if (!(uniqueWorkArea=PhysicsBounds.FromMonitors(topology.Monitors).IsValid && monitors.Length == 1 &&
            (_crossScreen || monitors[0] == _pilotMonitor)).Value)
        { Summary("qualification","no unique full single-monitor track");PilotPhysicsFact("physics-release-refused", "no unique full single-monitor track"); return null; }
        var estimate = _pilotPhysicsSamples.Estimate(timestamp);
        Summary("estimate",estimate.Reason.ToString(),estimate,_pilotPhysicsSamples.LastDiagnostic);
        var v = estimate.Velocity;
        if (estimate.Reason != ReleaseFailure.None || Math.Sqrt(v.X*v.X+v.Y*v.Y) <= 4)
        { PilotPhysicsFact("physics-release-refused", estimate.Reason.ToString()); return null; }
        var release=new PilotPhysicsRelease(key.Identity, key.Scale, v, monitors[0],timestamp,
            _pilotPhysicsSamples.LastDiagnostic?.VelocitySource ?? "none");
        PilotPhysicsStage(release,"release-classified");
        return release;
    }

    // Attachment always plans first. No velocity is passed into that owner.
    // While the outcome is undecided, hold still; failure restores the old float.
    private void PilotPhysicsReleaseOrFloat(PilotPhysicsRelease? release, PhysicalRect rect, bool throwSelected=false)
    {
        if (PilotPhysicsEffectiveMode == PhysicsMode.Gravity)
        { PilotGravityScheduleBallistic("drag release; one-time release snapshot",rect,release,throwSelected); return; }
        if (release == null) { PilotReleaseFloatBegin(); PilotAttachmentRelease(true, rect); return; }
        // Completion is a fact of this planner, not an inference from timer==null.
        var decided=!_pilotAttachmentEnabled;var attached=false;var queued=false;
        Action? handoff=null;Action? currentCallback=null;
        void Wake(){
            if(queued||currentCallback==null)return;
            queued=true;var callback=currentCallback;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{queued=false;callback();}));
        }
        PilotAttachmentPlanRelease(true,rect,won=>{
            decided=true;attached=won;
            PilotPhysicsStage(release,won?"planner-bound":"planner-miss");Wake();
        }); // intentionally resets all old physics first
        PilotPhysicsStage(release,_pilotAttachmentEnabled?"planner-started":"attachment-disabled");
        if (!PilotPhysicsReleaseIdentity(release, release.Identity.Gesture + 1))
        {
            if(throwSelected && _gifPlayer?.ResourcePath==ScruffGifPath)_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);
            PilotReleaseFloatBegin(); PilotPhysicsFact("physics-release-refused", "cancel/recovery changed release identity"); return;
        }
        _pilotPhysicsPending = release;
        // Planner Reset has finished. Install the visual owner before any lease;
        // a missing resource keeps the existing Idle handoff and float semantics.
        var useThrow=throwSelected && !attached && PilotPlanarThrowBegin(release.Velocity);
        if(throwSelected && !useThrow && _gifPlayer?.ResourcePath==ScruffGifPath)
            _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);
        var gesture = _pilotGesture;
        handoff=() => {
            if (!PilotPhysicsReleaseIdentity(release, gesture)) { PilotPhysicsEnd("handoff identity lost", true); return; }
            if (attached||_pilotAttachmentTarget != null) { PilotPhysicsEnd("attachment priority", false); return; }
            if (Stopwatch.GetElapsedTime(release.ReleasedAt).TotalMilliseconds > 500)
            { PilotPhysicsEnd("bounded handoff deadline", true); return; }
            if (!decided||_pilotAttachmentTimer != null || PilotVisualBusy) return;
            if(useThrow && !PilotPlanarThrowOwnsClip) {
                PilotPlanarThrowEnd("throw visual identity/admission lost; Idle fallback");
                useThrow=false;return;
            }
            if (!SingleMonitorDrag.Fits(_pilotRect, release.Monitor.WorkArea) ||
                (useThrow ? _pilotVisualSizePath!=PilotPlanarThrowPath :
                    _gifPlayer?.ResourcePath != _effectiveBaseIdlePath || _pilotVisualSizePath!=_effectiveBaseIdlePath))
            {
                if(useThrow){PilotPlanarThrowEnd("throw single-monitor track unavailable; Idle fallback");useThrow=false;return;}
                PilotPhysicsEnd("idle single-monitor track unavailable", true); return;
            }
            var lease = PilotPhysicsTryAcquire();
            if (lease == null) { PilotPhysicsEnd("fresh release lease refused", true); return; }
            _pilotPhysicsPending = null;
            _pilotPhysicsSession = new(_pilotRect, release.Velocity, release.Monitor.WorkArea);
            _pilotPhysicsRunToken = lease;
            _pilotPhysicsActiveRelease=release;_pilotPhysicsFirstAccepted=false;
            var last = Stopwatch.GetTimestamp();
            PilotPhysicsStage(release,"lease-acquired");
            PilotPhysicsFact("physics-begin", "fresh lease after release/attachment/visual admission", new { _pilotRect, release.Velocity, release.Monitor, visual=useThrow?"planar-throw":"idle" });
            PilotPhysicsArm(() => {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now-last)/(double)Stopwatch.Frequency; last = now;
                PilotPhysicsAdvance(elapsed);
            },TimeSpan.FromSeconds(1d/120)); // first substep starts AFTER the new lease, never at MouseUp
            if(useThrow)PilotPlanarThrowVoiceAdmitted();
        };
        PilotPhysicsArm(handoff);
        currentCallback=_pilotPhysicsTimerBody; // revision + timer identity guards deferred notifications too
        if(decided)Wake();
    }

    private bool PilotPhysicsReleaseIdentity(PilotPhysicsRelease release, long gesture) =>
        PilotPhysicsEffectiveMode == PhysicsMode.PlanarInertia && PilotClickLive && !_pilotTopologyHold &&
        release.Identity.Generation == _pilotGeneration && gesture == _pilotGesture &&
        release.Identity.SourceToken == _pilotSourceToken && release.Identity.Hwnd == (long)PilotHwnd &&
        release.Identity.TopologyVersion == _pilotTopology!.State.LastKnownGood!.Version &&
        _pilotTopology.State.LastKnownGood.Monitors.Contains(release.Monitor) &&
        PilotMapping(out var scale) && scale == release.Scale && scale == _pilotScale &&
        !_pilotDrag.Active && !_crossActive && !PetSurface.IsMouseCaptured &&
        (!_crossScreen || _crossMapped && _crossEffectiveScale == 1 && _crossRecovery?.Active != true &&
            !_crossBoundaryPreparing && _crossBoundary == null && _crossMove == null && _seamPlan == null);

    private void PilotPhysicsArm(Action body,TimeSpan? firstDelay=null)
    {
        PilotPhysicsStopTimer();
        var revision = ++_pilotPhysicsRevision;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = firstDelay??TimeSpan.FromMilliseconds(30) };
        _pilotPhysicsTimer = timer;
        _pilotPhysicsTimerBody = () => {
            if (revision == _pilotPhysicsRevision && ReferenceEquals(timer, _pilotPhysicsTimer)) body();
        };
        var callback = _pilotPhysicsTimerBody;
        _pilotPhysicsTimerHandler = (_, _) => {
            callback();
            if(firstDelay!=null&&ReferenceEquals(timer,_pilotPhysicsTimer)){
                firstDelay=null;timer.Interval=TimeSpan.FromMilliseconds(30);
            }
        };
        timer.Tick += _pilotPhysicsTimerHandler;
        timer.Start();
    }

    private void PilotPhysicsStopTimer()
    {
        if (_pilotPhysicsTimer != null) {
            _pilotPhysicsTimer.Stop();
            if (_pilotPhysicsTimerHandler != null) _pilotPhysicsTimer.Tick -= _pilotPhysicsTimerHandler;
        }
        _pilotPhysicsTimer = null; _pilotPhysicsTimerHandler = null; _pilotPhysicsTimerBody = null;
        _pilotPhysicsRevision++;
    }

    private void PilotPhysicsEnd(string reason, bool fallback)
        => PilotPhysicsEndCore(reason, fallback, false);

    private void PilotPhysicsEndCore(string reason, bool fallback, bool preserveGravityVisual)
    {
        PilotActionVoiceEnd(ref _pilotThrowVoiceId, completed: false);
        var had = _pilotPhysicsTimer != null || _pilotPhysicsSession != null || _pilotGravitySession != null;
        if (_pilotGravitySession != null || _pilotGravityPending) PilotGravityFact("physics-gravity-yield",reason,_pilotGravitySession);
        var pending = _pilotPhysicsPending != null;
        if (had) PilotPhysicsStopTimer();
        _pilotPhysicsSession = null; _pilotPhysicsRunToken = null; _pilotPhysicsPending = null;
        _pilotGravitySession = null; _pilotGravityPending = false;
        _pilotPhysicsActiveRelease=null;_pilotPhysicsFirstAccepted=false;
        _pilotPhysicsGate?.Yield();
        if (had) PilotPhysicsFact("physics-end", reason);
        PilotPlanarThrowEnd(reason); // timer and lease are gone before Idle ClipChanged
        if (!preserveGravityVisual) {
            PilotGravityLandingEnd(reason);
            PilotGravityThrowEnd(reason); // lease is gone before the Idle clip event
        }
        if (fallback && pending && PilotClickLive) PilotReleaseFloatBegin();
    }

    private void PilotPhysicsAdvance(double elapsed)
    {
        var session = _pilotPhysicsSession; var token = _pilotPhysicsRunToken;
        if (session == null) return;
        if (PilotPhysicsEffectiveMode != PhysicsMode.PlanarInertia || !PilotPhysicsIsCurrent(token) ||
            _pilotPlanarThrowVisual!=null && !PilotPlanarThrowOwnsClip)
        { PilotPhysicsEnd("lease/owner no longer current", false); return; }
        var result = session.Advance(elapsed, _pilotRect, out var target);
        if (session.LastResult is { Failure: not PhysicsFailure.None } timing)
            PilotPhysicsFact("physics-time", timing.Failure.ToString(), new { timing.DroppedSeconds, timing.Substeps });
        if (result is PlanarAdvance.Stop or PlanarAdvance.Invalid)
        {
            if(result == PlanarAdvance.Stop && session.State.Phase == PhysicsPhase.Settled)
                PilotActionVoiceEnd(ref _pilotPlanarThrowVoiceId, completed: true);
            PilotPhysicsEnd(session.LastResult is {} stopped
            ? stopped.Failure==PhysicsFailure.LateFrameDiscarded ? "model late frame"
            : stopped.Impacts.Length!=0 ? "model impact" : stopped.State.Phase==PhysicsPhase.Settled ? "model settled"
            : "model " + result : "model " + result, false); return; }
        if (result == PlanarAdvance.Hold) { PilotPlanarThrowFace(session.State.Velocity); return; }
        // Model proves the single WorkArea track; also retain the product's whole
        // topology corridor proof before entering its existing unique writer.
        var work = _crossScreen ? CrossWork : new[] { _pilotMonitor.WorkArea };
        if (!CrossScreenGeometry.Covered(target, work) || !CrossScreenGeometry.TranslationSafe(_pilotRect, target, work) ||
            !PilotPhysicsIsCurrent(token) || !PilotPlace(target, "physics-planar-step"))
        { PilotPhysicsEnd("safe writer/corridor refused", false); return; }
        if (!ReferenceEquals(session, _pilotPhysicsSession) || !PilotPhysicsIsCurrent(token))
        { PilotPhysicsEnd("owner changed during placement", false); return; }
        if (!PilotNative.GetWindowRect(PilotHwnd, out var actual) || actual.Physical != _pilotRect ||
            !session.Accept(actual.Physical))
        { PilotPhysicsEnd("actual RECT differs from requested integer target", false); return; }
        PilotPlanarThrowFace(session.State.Velocity);
        if(!_pilotPhysicsFirstAccepted&&_pilotPhysicsActiveRelease is {} release){
            _pilotPhysicsFirstAccepted=true;PilotPhysicsStage(release,"first-accepted-displacement");
        }
        if (session.Stopped) {
            PilotActionVoiceEnd(ref _pilotPlanarThrowVoiceId, completed: true);
            PilotPhysicsEnd("settled", false);
        }
    }
}
