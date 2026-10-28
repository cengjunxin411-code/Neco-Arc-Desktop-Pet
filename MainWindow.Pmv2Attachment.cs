using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.CharacterProfiles;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private const string PilotStandPath = "NecoPetMaterials/idle/stand.gif";
    private const string PilotSidePath = "NecoPetMaterials/idle/loitering.gif";
    private bool _pilotAttachmentEnabled,_pilotAttachmentPaused;
    private bool _pilotAttachmentPosePending;
    private AttachmentSide _pilotAttachmentPoseSide;
    private double _pilotAttachmentPoseOffsetX,_pilotAttachmentPoseOffsetY;
    private long _pilotAttachmentPoseVisualRevision;
    private long _pilotAttachmentOverlayVisualRevision;
    private PilotAttachmentTarget? _pilotAttachmentTarget;
    private DispatcherTimer? _pilotAttachmentTimer;
    private Action? _pilotAttachmentTimerBody;
    private PilotAttachmentCadence? _pilotAttachmentCadence;
    private long _pilotAttachmentRevision;
    private int _pilotAttachmentLossSamples;
    private Action<bool>? _pilotAttachmentHandoff;
    private Action? _pilotAttachmentRefuse;
    // 19C.2 bounded outcome of the current release: miss/refused/attached. It is
    // written only by the existing terminal settle points and never owns control.
    private string? _pilotAttachmentReleaseOutcome;
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint GetPropW(nint hwnd,string name);
    private void PilotAttachmentFact(string kind,string reason,object? detail=null)=>_pilotLog?.Write(kind,new{
        owner="PilotAttachment",revision=_pilotAttachmentRevision,generation=_pilotGeneration,gesture=_pilotGesture,
        source=_pilotSourceToken,hwnd=(long)PilotHwnd,targetHwnd=(long?)_pilotAttachmentTarget?.Identity.Hwnd,reason,detail});

    // Runtime observations for the Workbench. Field-only: no cancellation, reset,
    // placement or ownership change is performed by any of these.
    internal bool PilotAttachmentExperimentalEnabled => PilotApplication.Attachment;
    internal bool PilotAttachmentEffectiveEnabled => _pilotAttachmentEnabled;
    internal bool PilotAttachmentPreference => _settings.Attachment.Enabled;
    internal double PilotAttachmentSnapDistanceDip => _settings.Attachment.SnapDistanceDip;
    internal double PilotAttachmentHeightDip => _petHeight*(_crossScreen?_crossEffectiveScale:1);
    // The applied thresholds in DIP for the current pet height. Display only; the
    // planner takes its own single snapshot so a staged edit can never leak into it.
    internal (double Top,double Side) PilotAttachmentThresholds(double maximumSnapDistanceDip)
    {
        var height=PilotAttachmentHeightDip;
        return (Math.Min(AttachmentContactProfile.TopSnapDistance(height),maximumSnapDistanceDip),
            SideAttachmentDetectionProfile.SnapDistance(height,maximumSnapDistanceDip));
    }

    // Called by the Workbench before anything is published. Attached steady state is
    // safely detachable; an unresolved release planner or Gravity/Planar handoff is
    // not, because the existing cancel entry deliberately leaves that handoff to its
    // own bounded deadline. A live timer alone is never the sole refusal criterion:
    // the attached branch keeps its 50 ms timer running, so "timer != null" would
    // otherwise forbid exactly the steady state this phase must close.
    internal string? PilotAttachmentChangeUnavailableReason(bool requestedEnabled)
    {
        // Without the process gate the runtime owner is permanently off: only the
        // saved preference could change, and unavailable fields are never written.
        if(!PilotApplication.Attachment)return null;
        if(requestedEnabled==_pilotAttachmentEnabled)return null;
        if(requestedEnabled)return null; // enabling only admits the next legal release
        if(_pilotClosing||Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)
            return "Pilot 正在关闭，无法关闭外部依附。";
        if(_pilotStopped)return "Pilot 已安全停止，无法关闭外部依附。";
        if(!_pilotReady||_pilotSource is null or {IsDisposed:true})return "Pilot 尚未就绪，无法关闭外部依附。";
        // The existing release path must be quiescent: detaching during a gesture
        // would change the semantics of a release that is still owned by input.
        if(_pilotDrag.Active||_crossActive||PetSurface.IsMouseCaptured||_pilotClicks?.Held==true||
            _clickResolver.IsPending||_pilotClickAction)
            return "正在拖拽、捕获鼠标或保持点击，请结束当前手势后再关闭外部依附。";
        if(_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0)
            return "正在处理窗口位置或布局，请稍后再关闭外部依附。";
        // Rocket reuses the attachment lease and restores its own visual; cancel is
        // not ours to run while that owner is live.
        if(_pilotRocketTimer!=null||_pilotRocketSession!=null||_pilotRocketAttachment!=null)
            return "火箭动作尚未结束，请稍后再关闭外部依附。";
        if(_pilotAttachmentHandoff!=null||_pilotAttachmentRefuse!=null)
            return "依附规划交接尚未结束，请稍后再关闭外部依附。";
        // A paid planning session without a lease is a handoff candidate, not an
        // attached relation; it may still bind on a later tick.
        if(_pilotAttachmentTimer!=null&&_pilotAttachmentTarget==null)
            return "依附规划尚未结束，请稍后再关闭外部依附。";
        if(_pilotAttachmentPosePending)return "依附姿态仍在等待视觉准入，请稍后再关闭外部依附。";
        if(PilotVisualBusy)return "视觉事务尚未结束，请稍后再关闭外部依附。";
        if(_pilotTopologyHold||_crossRecovery?.Active==true)return "屏幕拓扑或跨屏恢复尚未结束，请稍后再关闭外部依附。";
        return null;
    }

    // loitering/stand are the attachment relation's own pose clips and no other owner
    // ever plays them, so "a pose clip is current" is exactly "the relation still
    // owns the visible pose". The mirror is part of the same ownership.
    private bool PilotAttachmentPoseVisible => _gifPlayer is {} pose &&
        (pose.ResourcePath==PilotSidePath||pose.ResourcePath==PilotStandPath);

    // The one owner of the pose -> base-Idle hand-back. A revoked relation may not
    // leave loitering/stand on screen; the clip is switched first and the existing
    // visual transaction then re-fits the viewport (same Fit/TranslationSafe/unique
    // writer/postcheck as every other clip change). It returns false without
    // touching the clip when another owner already took the visual or when no real
    // Idle can be proven, so this is never a blind re-switch loop.
    private bool PilotAttachmentRestoreIdle(string reason)
    {
        if(_gifPlayer==null||!PilotAttachmentPoseVisible)return false;
        if(_pilotClosing||_pilotStopped||!_pilotReady)return false;
        if(_effectiveBaseIdlePath==null)
        {
            PilotAttachmentFact("attachment-idle-restore-refused","no real idle clip; retained legal visual",new{retained=_pilotRect});
            return false;
        }
        // A pose never leaves a mirror behind: base Idle is always unmirrored.
        if(PetImage.RenderTransform is System.Windows.Media.TransformGroup mirror)
            ((System.Windows.Media.ScaleTransform)mirror.Children[0]).ScaleX=1;
        bool switched;
        try { switched=_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath); }
        catch(ArgumentOutOfRangeException) { switched=false; }
        if(!switched)
        {
            // Fail closed: without a proven Idle clip there is no admission for a
            // different footprint, so the last legal RECT and clip stay untouched.
            PilotAttachmentFact("attachment-idle-restore-refused","idle clip unavailable; retained legal visual",new{retained=_pilotRect});
            return false;
        }
        _gifPlayer.Resume();
        PilotVisualQueue();
        PilotAttachmentFact("attachment-idle-restore","attachment pose released; base Idle admitted",new{reason,retained=_pilotRect});
        return true;
    }

    private void PilotAttachmentCancel(string reason)
    {
        PilotActionVoiceEnd(ref _pilotAttachVoiceId, completed: false);
        // A forced detach also revokes hidden return/visual callbacks. Rocket
        // clears its owner before calling back here, so cancellation is idempotent.
        if(_pilotRocketAttachment!=null){PilotRocketCancel(reason);return;}
        // A generic cancellation (owner loss, unresolved watchdog, target loss)
        // is not a planner result. Clear any pending fallback fact; Gravity's
        // bounded 1750 ms deadline still owns that unresolved handoff.
        _pilotAttachmentRefuse=null;
        _pilotAttachmentHandoff=null;
        _pilotAttachmentLossSamples=0;
        // Revoking the relation is not the owner of the visible clip: an attached
        // steady state releases only through PilotAttachmentDisableBySettings, and
        // a generic preemption (gesture/menu/layout/loss) keeps the last admitted
        // pose and RECT exactly as every other higher owner already expects.
        if(_pilotAttachmentTimer==null&&_pilotAttachmentTarget==null)return;
        _pilotAttachmentRevision++;
        _pilotAttachmentTimer?.Stop();_pilotAttachmentTimer=null;
        _pilotAttachmentCadence?.Flush(reason);_pilotAttachmentCadence=null;
        var attached=_pilotAttachmentTarget!=null;
        _pilotAttachmentTarget?.Dispose();_pilotAttachmentTarget=null;
        _pilotAttachmentPosePending=false;
        if(attached && PetImage.RenderTransform is System.Windows.Media.TransformGroup group)
            ((System.Windows.Media.ScaleTransform)group.Children[0]).ScaleX=1;
        if(_pilotAttachmentPaused){_pilotAttachmentPaused=false;_gifPlayer?.Resume();}
        if(attached)PilotAttachmentFact("attachment-detached",reason,new{retained=_pilotRect});
    }

    // Called only for a final pose admission refusal (resource/switch/footprint),
    // never for generic owner cancellation. This is the one fact that lets the
    // same release hand control back to Gravity.
    private void PilotAttachmentReject(string reason)
    {
        var reject=_pilotAttachmentRefuse;_pilotAttachmentRefuse=null;
        PilotAttachmentCancel(reason);
        _pilotAttachmentReleaseOutcome="refused";
        reject?.Invoke();
    }

    private bool PilotAttachmentCanOverlay(string path) => _pilotAttachmentTarget == null ||
        CharacterProfileRuntime.Current?.CanOverlay(path, _pilotAttachmentPoseSide) == true;

    private bool TryPilotAttachmentContact(AttachmentSide side, out Point contact)
    {
        contact = default;
        if (_gifPlayer == null) return false;
        var height = _petHeight * (_crossScreen ? _crossEffectiveScale : 1);
        var path = _gifPlayer.ResourcePath;
        var posePath = side == AttachmentSide.Top ? PilotStandPath : PilotSidePath;
        if (_pilotAttachmentTarget != null && side != AttachmentSide.Top && _pilotClickAction && path != posePath)
        {
            if (!_pilotVisualCalibrated || _pilotVisualSizePath != path || _gifPlayer.PilotVisibleUnion.IsEmpty) return false;
            var unit = height / AttachmentContactProfile.CanonicalHeight;
            var source = _gifPlayer.PilotVisibleUnion;
            Point Map(double x, double y) => AttachmentGeometry.TransformPoint(new(x * unit, y * unit),
                new(PetImage.Width, PetImage.Height), new(Canvas.GetLeft(PetImage), Canvas.GetTop(PetImage)),
                PetImage.RenderTransform.Value, PetImage.RenderTransformOrigin);
            var bounds = Rect.Empty;
            foreach (var point in new[] { Map(source.Left,source.Top), Map(source.Right,source.Top),
                Map(source.Left,source.Bottom), Map(source.Right,source.Bottom) }) bounds.Union(point);
            return AttachmentGeometry.TrySideOverlayContact(CharacterProfileRuntime.Current, path, side, height, bounds, out contact);
        }
        // Contacts are declared in the unmirrored source canvas and transformed
        // exactly once. The profile supplies the authored basis; no character
        // specific pre-reflection or canonical registration is inferred here.
        return AttachmentGeometry.TryContact(CharacterProfileRuntime.Current, side, height,
            new(PetImage.Width,PetImage.Height), new(Canvas.GetLeft(PetImage),Canvas.GetTop(PetImage)),
            PetImage.RenderTransform.Value, PetImage.RenderTransformOrigin, path, out contact);
    }

    private bool PilotAttachmentBeginPose(AttachmentSide side,double offsetX,double offsetY)
    {
        if(_gifPlayer==null)return false;
        var path=side==AttachmentSide.Top?PilotStandPath:PilotSidePath;
        if(!AttachmentContactProfile.HasContact(path, side) || !_gifPlayer.TryCacheClip(path)){
            PilotAttachmentFact("attachment-pose-refused","pose resource unavailable",new{path,side,status="REFUSED"});
            return false;
        }
        _pilotAttachmentPoseSide=side;
        _pilotAttachmentPoseOffsetX=offsetX;
        _pilotAttachmentPoseOffsetY=offsetY;
        _pilotAttachmentPosePending=true;
        var mirror=(System.Windows.Media.ScaleTransform)((System.Windows.Media.TransformGroup)PetImage.RenderTransform).Children[0];
        // Phase 22-B.1: the source pose leans on the right wall, so the left
        // attachment keeps the authored image and the right attachment mirrors it.
        mirror.ScaleX=side==AttachmentSide.Right?-1:1;
        bool switched;
        try { switched=_gifPlayer.TrySwitchTo(path); }
        catch(ArgumentOutOfRangeException) { switched=false; }
        if(!switched){
            _pilotAttachmentPosePending=false;mirror.ScaleX=1;
            PilotAttachmentFact("attachment-pose-refused","pose switch unavailable",new{path,side,status="REFUSED"});
            return false;
        }
        _pilotAttachmentPoseVisualRevision=_pilotVisualRevision;
        PilotAttachmentFact("attachment-pose-pending","clip and mirror await visual admission",new{path,side,range=side==AttachmentSide.Top?"all":"f0-f2"});
        return true;
    }

    private void PilotAttachmentPoseRestore()
    {
        if(_pilotAttachmentTarget!=null && !PilotAttachmentBeginPose(_pilotAttachmentPoseSide,
            _pilotAttachmentPoseOffsetX,_pilotAttachmentPoseOffsetY))
            PilotAttachmentCancel("attached pose restore unavailable");
        _gifPlayer?.Resume();
    }

    // Menu close only returns side ownership. The existing timer catches up;
    // no clip switch, playback reset, target rebind or visual admission is needed.
    private void PilotAttachmentMenuResume()
    {
        if(_pilotAttachmentTarget==null)return;
        var mirror=(System.Windows.Media.ScaleTransform)((System.Windows.Media.TransformGroup)PetImage.RenderTransform).Children[0];
        mirror.ScaleX=_pilotAttachmentPoseSide==AttachmentSide.Right?-1:1;
    }

    // Runs from PilotApplySettings after _settings.ApplyFrom. Preference and the
    // process gate are the only inputs; the distance is read by each planner.
    private void PilotAttachmentApplySettings()
    {
        var effective=PilotApplication.Attachment&&_settings.Attachment.Enabled;
        if(effective==_pilotAttachmentEnabled)return;
        var previous=_pilotAttachmentEnabled;
        _pilotAttachmentEnabled=effective; // gate first: a reentrant planner must never start half-applied
        if(!effective&&previous)PilotAttachmentDisableBySettings();
        PilotAttachmentFact("attachment-setting-applied",
            effective?"enabled; the next legal release may plan":"disabled; existing relation released",
            new{effective,preference=_settings.Attachment.Enabled,experimental=PilotApplication.Attachment,
                snapDistanceDip=_settings.Attachment.SnapDistanceDip,retained=_pilotRect});
        PilotRefreshAttachmentStatus();
    }

    // Settings-disabled detach. The existing idempotent cancel revokes the
    // revision/timer/cadence/lease (RemovePropW), clears the pose and resets the
    // mirror; it also hands the pose clip back to base Idle, which the ordinary
    // visual queue re-fits (Fit/TranslationSafe/writer/postcheck) — no new writer,
    // no fall. The second call is the settings contract itself: it must hold even
    // when the lease was already revoked by an earlier generic cancellation,
    // where only the pose clip is left behind.
    private void PilotAttachmentDisableBySettings()
    {
        PilotAttachmentCancel("external attachment disabled by settings");
        PilotAttachmentRestoreIdle("external attachment disabled by settings");
    }

    // Called after the accepted drag release revokes the input owner. No native
    // placement/target enumeration on the MouseUp, layout or native hook stack.
    private void PilotAttachmentRelease(bool dragged,PhysicalRect release)
        =>PilotAttachmentPlanRelease(dragged,release,null);
    private void PilotAttachmentPlanRelease(bool dragged,PhysicalRect release,Action<bool>? completed,bool deferUntilAttached=false)
    {
        PilotPhysicsReset();
        if(!_pilotAttachmentEnabled||!dragged||!release.IsValid||!PilotClickLive)return;
        // Exactly one DIP snapshot per planning session. A later Apply may change the
        // saved preference, but this closure (including its 1500 ms deadline) keeps a
        // single value so candidate scoring can never mix two thresholds. The value is
        // a cap only: the geometry layer applies Min() after the historical clamps and
        // never multiplies it by height or DPI scale.
        var maximumSnapDistanceDip=_settings.Attachment.SnapDistanceDip;
        // PilotPhysicsReset above already ended the old physics request; drop
        // its handoff closure so a late completion can never touch this release.
        _pilotAttachmentHandoff=null;_pilotAttachmentRefuse=null;_pilotAttachmentReleaseOutcome=null;
        PilotAttachmentCancel("replace release planning");
        var revision=++_pilotAttachmentRevision;var generation=_pilotGeneration;var gesture=_pilotGesture;
        var source=_pilotSource;var token=_pilotSourceToken;var hwnd=PilotHwnd;
        var began=Stopwatch.GetTimestamp();var planning=true;var visualRevision=_pilotVisualRevision;
        if(completed!=null)PilotPhysicsFact("physics-handoff-stage","planner-start",new{ticks=began});
        long lossFirstAt=0;var unresolvedReported=false;
        long topologyVersion=0;MonitorSnapshot monitor=default;PhysicalRect lastTarget=default;bool pendingRefresh=false;
        AttachmentSide side=default;double offsetX=0,offsetY=0;Action<bool>? conclude=null;
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(50)};
        _pilotAttachmentTimer=timer;
        var cadence=new PilotAttachmentCadence(_pilotLog);_pilotAttachmentCadence=cadence;
        Action? body=null;var queued=0;
        void Wake()
        {
            cadence.Notification();
            if(Interlocked.Exchange(ref queued,1)!=0)return;
            if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished){Interlocked.Exchange(ref queued,0);return;}
            // A foreign WinEvent stack can only enqueue one later UI attempt.
            Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{
                Interlocked.Exchange(ref queued,0);body?.Invoke();
            }));
        }
        bool Own()=>revision==_pilotAttachmentRevision&&ReferenceEquals(timer,_pilotAttachmentTimer)&&
            generation==_pilotGeneration&&(planning?gesture==_pilotGesture:_pilotAttachmentTarget!=null)&&
            token==_pilotSourceToken&&ReferenceEquals(source,_pilotSource)&&hwnd==PilotHwnd;
        bool PetStable()=>PilotMotionStable()&&!_pilotTopologyHold&&!_pilotClickAction&&!_clickResolver.IsPending&&
            _pilotClicks?.Held!=true&&_pilotMotionTimer==null&&_pilotMotionState==PilotMotionState.Idle&&_pilotRocketTimer==null;
        bool BoundSafe()=>!_pilotTopologyHold&&_pilotMotionTimer==null&&_pilotMotionState==PilotMotionState.Idle&&
            _pilotRocketTimer==null&&_pilotRocketSession==null&&PilotClickLive&&
            (!_crossScreen||_crossMapped&&_crossRecovery?.Active!=true&&_crossLayoutDepth==0&&CrossCheckpoint().Ready);
        bool TargetAllowed(PilotAttachmentTarget.Snapshot target,bool candidate)=>target.Monitor==PilotAttachmentTarget.Monitor(hwnd)&&
            target.Rect.Width>=(candidate?140:80)*_pilotScale.X&&target.Rect.Height>=(candidate?110:80)*_pilotScale.Y;
        body=new Action(()=>{
            var callbackBegan=Stopwatch.GetTimestamp();
            try{
            if(!Own()){
                // A forced old delegate must never detach a newer owner.
                if(revision==_pilotAttachmentRevision&&ReferenceEquals(timer,_pilotAttachmentTimer))PilotAttachmentCancel("pet owner changed");return;
            }
            if(_pilotStopped||_pilotClosing||_pilotSource is null or {IsDisposed:true}||!PilotNative.IsWindow(hwnd)) {
                PilotAttachmentCancel("pet source/HWND unavailable");return; // existing watchdog retains fatal pet policy
            }
            // Reentrancy is only a deferral; it cannot enumerate/place on this stack.
            if(_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0){cadence.Held();return;}
            if(_pilotRocketAttachment!=null&&ReferenceEquals(_pilotRocketAttachment,_pilotAttachmentTarget)){
                cadence.Held();return; // the original lease/hook stays live; no flight-time following
            }
            if(_pilotAttachmentPosePending&&PilotVisualBusy){
                cadence.Held();
                if(Stopwatch.GetElapsedTime(began).TotalMilliseconds>=1500)
                    PilotAttachmentCancel("pose visual admission deadline; REFUSED");
                return;
            }
            if(planning?!PetStable():!BoundSafe()){
                cadence.Held();
                if(!planning||Stopwatch.GetElapsedTime(began).TotalMilliseconds>=1500)PilotAttachmentCancel("higher owner or stable checkpoint unavailable");
                return;
            }
            if(!planning&&PilotVisualBusy){cadence.Held();return;}
            if(_pilotTopology?.Refresh().Success!=true||!PilotFresh||!PilotMapping(out var scale)||scale!=_pilotScale||
                !PilotNative.GetWindowRect(hwnd,out var native)||!CrossScreenGeometry.Near(native.Physical,_pilotRect)) {
                PilotAttachmentCancel("fresh pet mapping/topology/RECT unavailable");return;
            }
            var snapshot=_pilotTopology.State.LastKnownGood!;
            var current=_pilotRect;var center=new PhysicalPoint((current.Left+current.Right)/2,(current.Top+current.Bottom)/2);
            var freshMonitor=snapshot.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(center));
            if(!freshMonitor.IsValid||!SingleMonitorDrag.Fits(current,freshMonitor.WorkArea)||
                !_crossScreen&&freshMonitor.Id!=_pilotMonitor.Id) {PilotAttachmentCancel("full same-monitor footprint unavailable");return;}
            if(planning){
                monitor=freshMonitor;topologyVersion=snapshot.Version;visualRevision=_pilotVisualRevision;
                PilotAttachmentTarget.Snapshot selected=default;AttachmentCandidate? best=null;double bestScore=double.PositiveInfinity;var z=0;
                var bubble=_bubbleWindow==null?0:new WindowInteropHelper(_bubbleWindow).Handle;
                var height=_petHeight*(_crossScreen?_crossEffectiveScale:1);
                var uncertain=false;var observed=new List<PilotAttachmentTarget.Snapshot>();var unstableHandles=new List<long>();
                var enumerated=EnumWindows((candidate,_)=>{
                    if(candidate==hwnd||candidate==bubble||candidate==GetDesktopWindow()||candidate==GetShellWindow())return true;
                    PilotAttachmentTarget.Snapshot target;
                    if(completed!=null){
                        if(!PilotAttachmentTarget.ObservePlanning(candidate,out target,out var unstable)){
                            uncertain|=unstable;if(unstable&&unstableHandles.Count<8)unstableHandles.Add((long)candidate);return true;
                        }
                        observed.Add(target);
                    }else if(!PilotAttachmentTarget.Observe(candidate,out target))return true;
                    if(!TargetAllowed(target,true))return true;
                    z++;
                    var detected=PilotAttachmentGeometry.Detect(release,current,target.Rect,monitor.WorkArea,scale,height,
                        height*AnimationVisualProfiles.For(PilotStandPath).Scale,maximumSnapDistanceDip);
                    if(detected is {} match){
                        var posePath = match.Side == AttachmentSide.Top ? PilotStandPath : PilotSidePath;
                        if (!AttachmentContactProfile.HasContact(posePath, match.Side) || _gifPlayer?.TryCacheClip(posePath) != true) return true;
                        var score=AttachmentGeometry.CandidateScore(match.Side,match.Score,z);
                        if(score<bestScore){selected=target;best=match;bestScore=score;}}
                    return true;
                },0);
                if(completed!=null){
                    // A failed/partial enumeration or changing external snapshot is
                    // unresolved planning, never permission for an inertia lease.
                    foreach(var observedTarget in observed)
                        if(!PilotAttachmentTarget.Observe(observedTarget.Hwnd,out var again)||again!=observedTarget)uncertain=true;
                    if(!enumerated||uncertain||!Own()||!PetStable()||visualRevision!=_pilotVisualRevision||
                        snapshot.Version!=_pilotTopology.State.LastKnownGood?.Version||!PilotFresh){
                        if(!unresolvedReported){unresolvedReported=true;PilotAttachmentFact("attachment-plan-unresolved",
                            "partial or unstable target snapshot",new{enumerated,uncertain,unstableHandles});}
                        cadence.Held();
                        if(Stopwatch.GetElapsedTime(began).TotalMilliseconds>=1500)PilotAttachmentCancel("planning snapshot unresolved deadline");
                        return;
                    }
                }
                if(best is not {} chosen){
                    PilotAttachmentCancel("no qualified external target");
                    _pilotAttachmentReleaseOutcome="miss";
                    conclude?.Invoke(false);
                    return;
                }
                var lease=PilotAttachmentTarget.Bind(selected);
                if(lease==null){PilotAttachmentCancel("target instance lease unavailable (including UIPI)");return;}
                _pilotAttachmentTarget=lease;side=chosen.Side;offsetX=chosen.OffsetX;offsetY=chosen.OffsetY;planning=false;
                // Legacy Planar consumers keep their bind-time contract. Only
                // Gravity defers until the first accepted follow commit.
                if(!deferUntilAttached)conclude?.Invoke(true);
                _pilotAttachmentLossSamples=0;
                PilotReleaseFloatCancel("attachment attached");
                PilotAttachmentFact("attachment-location-hook",lease.WatchLocation(Wake)?"registered":"unavailable; watchdog only");
                if(!PilotAttachmentBeginPose(side,offsetX,offsetY))
                    PilotAttachmentReject("pose unavailable; FALLBACK to retained legal visual");
                else visualRevision=_pilotAttachmentPoseVisualRevision;
                return; // ClipChanged admits the new footprint and contact on an independent callback.
            }else if(freshMonitor!=monitor||snapshot.Version!=topologyVersion){
                PilotAttachmentCancel("monitor/topology changed");return;
            }else if(visualRevision!=_pilotVisualRevision){
                var posePath=side==AttachmentSide.Top?PilotStandPath:PilotSidePath;
                if(_pilotAttachmentPoseVisualRevision!=_pilotVisualRevision&&
                    _pilotAttachmentOverlayVisualRevision!=_pilotVisualRevision&&
                    (_pilotAttachmentPosePending||_gifPlayer?.ResourcePath!=posePath)){
                    PilotAttachmentCancel("unowned visual footprint changed");return;
                }
                visualRevision=_pilotVisualRevision;
            }
            var targetOwner=_pilotAttachmentTarget;
            if(targetOwner==null){PilotAttachmentCancel("external target lease unavailable");return;}
            if(!targetOwner.TryRead(out var fresh,out var sampleMoving)){
                if(sampleMoving){ResetLoss();RefreshPending("target RECT changed during sample",lastTarget,default);return;}
                ConfirmLoss(targetOwner,"external target invalid/hidden/minimized/destroyed/reused or migrated");return;
            }
            ResetLoss();
            if(!TargetAllowed(fresh,false)){PilotAttachmentCancel("external target monitor or footprint invalid");return;}
            cadence.Rect(fresh.Rect);
            // Keep identity/safety observation live while the menu holds position.
            // Do not mark the new target RECT as followed: close must catch up.
            if(_contextMenuOpen){cadence.Held();return;}
            if(lastTarget==fresh.Rect){pendingRefresh=false;cadence.Unchanged();return;} // no native submission or repeated log for stationary target
            if (!TryPilotAttachmentContact(side, out var localContact)) {
                PilotAttachmentReject("Character Profile contact unavailable; retained legal visual"); return;
            }
            var effectiveHeight=_petHeight*(_crossScreen?_crossEffectiveScale:1);
            var aligned=PilotAttachmentGeometry.Align(current,fresh.Rect,side,offsetX,offsetY,localContact,effectiveHeight,effectiveHeight*_imageAspect,scale,monitor.WorkArea);
            if(aligned is not {} target||!CrossScreenGeometry.TranslationSafe(current,target,_crossScreen?CrossWork:new[]{monitor.WorkArea})) {
                PilotAttachmentCancel("contact target/full RECT/sweep corridor cannot be proved");return;
            }
            // Identity and geometry are reread immediately before the unique writer.
            if(!Own()||!ReferenceEquals(targetOwner,_pilotAttachmentTarget))return;
            if(!targetOwner.TryRead(out var before,out var beforeMoving)){
                if(beforeMoving){ResetLoss();RefreshPending("target RECT changed before commit",fresh.Rect,default);return;}
                ConfirmLoss(targetOwner,"external identity invalid before commit");return;
            }
            ResetLoss();
            if(before!=fresh){RefreshPending("target RECT changed before commit",fresh.Rect,before.Rect);return;}
            if(target!=current&&!PilotPlace(target,"attachment-follow")) {
                if(Own())PilotAttachmentCancel("existing placement writer refused follow");return;
            }
            if(target!=current)cadence.Commit();
            if(!Own())return; // native DPI/source reentry has revoked the old owner
            if(!targetOwner.TryRead(out var after,out var afterMoving)){
                if(afterMoving){ResetLoss();RefreshPending("target RECT changed during commit",fresh.Rect,default);return;}
                ConfirmLoss(targetOwner,"external identity invalid during commit");return;
            }
            ResetLoss();
            if(after!=fresh){RefreshPending("target RECT changed during commit",fresh.Rect,after.Rect);return;}
            var first=!lastTarget.IsValid;lastTarget=fresh.Rect;
            PilotAttachmentFact(first?"attachment-attached":"attachment-followed","same-monitor full RECT and sweep accepted",new{
                external=new{hwnd=(long)fresh.Hwnd,fresh.Process,fresh.Thread,fresh.Class,fresh.Dpi,context=(long)fresh.Context,monitor=(long)fresh.Monitor,fresh.Rect},
                side=side.ToString(),offsetX,offsetY,contactDip=localContact,rect=_pilotRect,monitorId=monitor.Id,topologyVersion,scale,
                identityValidated=true,pendingRefresh});
            pendingRefresh=false;
            // The planner is finally attached only after the first accepted
            // follow commit. Until then Gravity must keep waiting unresolved.
            if(first){_pilotAttachmentReleaseOutcome="attached";conclude?.Invoke(true);}
            }finally{cadence.Callback(callbackBegan);cadence.MaybeFlush();}
        });
        void RefreshPending(string reason,PhysicalRect sampleRect,PhysicalRect freshRect){
            cadence.Pending();
            if(!pendingRefresh)PilotAttachmentFact("attachment-refresh-pending",reason,new{
                sampleRect,freshRect,identityValidated=true,pendingRefresh=true,retained=_pilotRect});
            pendingRefresh=true;
        }
        void ResetLoss(){_pilotAttachmentLossSamples=0;lossFirstAt=0;}
        void ConfirmLoss(PilotAttachmentTarget lease,string reason){
            var targetHwnd=lease.Identity.Hwnd;
            // TryRead also rejects monitor/DPI/context migration. Only independently
            // observable death, visibility, minimization or nonce loss is a fall source.
            var definite=!PilotNative.IsWindow(targetHwnd)||!IsWindowVisible(targetHwnd)||IsIconic(targetHwnd)||
                GetPropW(targetHwnd,lease.PropertyName)==0;
            if(!definite||_pilotTopologyHold||_crossRecovery?.Active==true||
                !PilotMapping(out var currentScale)||currentScale!=_pilotScale||
                (_crossScreen&&(!_crossMapped||_crossLayoutDepth!=0||!CrossCheckpoint().Ready))||
                !ReferenceEquals(lease,_pilotAttachmentTarget)||!Own()){
                PilotAttachmentCancel(reason+"; loss source/topology unproved");return;
            }
            if(_pilotAttachmentLossSamples==0){
                _pilotAttachmentLossSamples=1;lossFirstAt=Stopwatch.GetTimestamp();cadence.Pending();
                PilotAttachmentFact("attachment-loss-pending",reason,new{samples=1,retained=_pilotRect});return;
            }
            if(Stopwatch.GetElapsedTime(lossFirstAt).TotalMilliseconds<45){cadence.Pending();return;}
            PilotAttachmentFact("attachment-loss-confirmed",reason,new{samples=2,retained=_pilotRect});
            PilotAttachmentCancel(reason);
            PilotClickCancel("attachment target invalid");
            PilotMotionRequestFall(reason);
        }
        if(completed!=null){
            // Resolve exactly once: miss/pose-refusal -> false, first accepted
            // attachment -> true. A bounded undecided handoff never gets a miss.
            var resolved=false;
            conclude=won=>{
                if(resolved)return;
                resolved=true;
                if(ReferenceEquals(_pilotAttachmentHandoff,conclude))_pilotAttachmentHandoff=null;
                _pilotAttachmentRefuse=null;
                completed(won);
            };
            _pilotAttachmentHandoff=conclude;
            _pilotAttachmentRefuse=()=>conclude!(false);
        }
        _pilotAttachmentTimerBody=body;timer.Tick+=(_,_)=>body();timer.Start();
        // Only an eligible planar release opts in. This is a later Dispatcher
        // stack, running the entire same planner; Off/Gravity keep the 50ms timer.
        if(completed!=null)Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>body()));
    }
}
