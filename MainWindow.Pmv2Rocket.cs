using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

// Pilot-only Rocket owner. The ordinary RocketSession/Left-Top/_anchorBottomY path is
// never linked here. All native world movement goes through the existing unique
// PilotPlace/CrossPlace writer; the "fly off the top edge" is a local visual-layer
// translation through a clipped viewport after the native RECT reaches WorkArea.Top.
public partial class MainWindow
{
    private bool _pilotRocketEnabled;
    private DispatcherTimer? _pilotRocketTimer;
    private Action? _pilotRocketTimerBody;
    private long _pilotRocketRevision;
    private PilotRocketSession? _pilotRocketSession;
    private bool _pilotRocketGrounded;
    private PhysicalRect _pilotRocketReturnRect;
    private TranslateTransform? _pilotRocketVisual;
    private Transform? _pilotRocketSavedTransform;
    private Geometry? _pilotRocketSavedClip;
    private PilotAttachmentTarget? _pilotRocketAttachment;
    // Set only after bounded stable sampling, and revoked before any cancellation
    // can switch clips or reenter the native writer.
    private Func<bool>? _pilotRocketReturnGuard;

    private void PilotRocketResetVisual()
    {
        if(_pilotRocketSavedTransform!=null)PetImage.RenderTransform=_pilotRocketSavedTransform;
        if(_pilotRocketVisual!=null)PetSurface.Clip=_pilotRocketSavedClip;
        _pilotRocketVisual=null;_pilotRocketSavedTransform=null;_pilotRocketSavedClip=null;
    }

    private void PilotRocketFact(string kind,string reason,object? detail=null)=>_pilotLog?.Write(kind,
        new{owner="PilotRocket",revision=_pilotRocketRevision,generation=_pilotGeneration,gesture=_pilotGesture,
            source=_pilotSourceToken,hwnd=(long)PilotHwnd,state=_pilotMotionState.ToString(),gif=_gifPlayer?.ResourcePath,reason,detail});

    private void PilotRocketStopTimer(){_pilotRocketTimer?.Stop();_pilotRocketTimer=null;}

    private bool PilotRocketStable()=>!PilotVisualBusy&&PilotClickLive&&PilotMapping(out var scale)&&scale==_pilotScale&&
        _pilotPlacementDepth==0&&_pilotHookDepth==0&&!_crossActive&&!_pilotDrag.Active&&!PetSurface.IsMouseCaptured&&
        (!_crossScreen||_crossMapped&&_crossRecovery?.Active!=true&&_crossLayoutDepth==0&&CrossCheckpoint().Ready);

    private void PilotRocketCancel(string reason)
    {
        PilotActionVoiceEnd(ref _pilotRocketVoiceId, false);
        PilotMotionAbortFall();
        var active=_pilotRocketTimer!=null||_pilotRocketSession!=null;
        var attached=_pilotRocketAttachment!=null;
        var retained=attached&&_pilotRocketSession!=null?(PhysicalRect?)_pilotRect:null;
        _pilotRocketAttachment=null;_pilotRocketReturnGuard=null;
        if(active)PilotVisualCancelPending();
        PilotRocketStopTimer();_pilotRocketRevision++;_pilotRocketSession=null;_pilotRocketTimerBody=null;
        PilotRocketResetVisual();
        if(attached)PilotAttachmentCancel(reason);
        if(active){
            PetSurface.Visibility=Visibility.Visible;
            _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer?.Resume();
            if(retained is {} last)_pilotVisualRetainedViewport=last;
            PilotRocketFact("rocket-cancel",reason);
        }
    }

    private void PilotRocketRefuseStart(string reason)
    {
        var lease=_pilotRocketAttachment;
        var restore=_pilotRocketSession==null&&lease!=null&&ReferenceEquals(lease,_pilotAttachmentTarget)&&
            PilotClickLive&&!_pilotTopologyHold&&lease.TryRead(out _);
        if(restore)_pilotRocketAttachment=null;
        PilotRocketCancel(reason);
        if(restore)PilotAttachmentPoseRestore();
    }

    private void PilotRocketFinishAttached()
    {
        if(_pilotRocketReturnGuard?.Invoke()!=true){PilotRocketCancel("attached return final identity/RECT changed");return;}
        PilotActionVoiceEnd(ref _pilotRocketVoiceId, true);
        var lease=_pilotRocketAttachment!;
        _pilotRocketAttachment=null;_pilotRocketReturnGuard=null;
        PilotRocketStopTimer();_pilotRocketRevision++;_pilotRocketSession=null;_pilotRocketTimerBody=null;
        PilotRocketResetVisual();_pilotMotionState=PilotMotionState.Idle;
        PetSurface.Visibility=Visibility.Visible;_gifPlayer?.Resume();
        PilotRocketFact("rocket-complete","original attachment resumed after hidden visual postcheck",new{
            rect=_pilotRect,targetHwnd=(long)lease.Identity.Hwnd,side=_pilotAttachmentPoseSide.ToString(),sameLease=true});
    }

    private void PilotRocketFinish()
    {
        PilotActionVoiceEnd(ref _pilotRocketVoiceId, true);
        PilotRocketStopTimer();_pilotRocketRevision++;_pilotRocketSession=null;_pilotRocketTimerBody=null;
        PilotRocketResetVisual();
        PetSurface.Visibility=Visibility.Visible;
        _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer?.Resume();
        if(_pilotRocketGrounded)_pilotMotionState=PilotMotionState.Grounded;
        // A grounded start is re-seated at the same floor (no Falling, no Landing
        // replay); a floating start stays Idle.
        PilotRocketFact("rocket-complete",_pilotRocketGrounded?"grounded return; no Landing replay":"floating return",new{rect=_pilotRect});
    }

    private void PilotRocketBegin()
    {
        // Resolve material/capability before resetting physics or other owners.
        var selectedDefinition = ClickDefinitions.Choose(_availableRockets,_clickRandom);
        if (selectedDefinition == null || _gifPlayer == null ||
            !PilotAttachmentCanOverlay(selectedDefinition.ResourcePath) || !_gifPlayer.TryCacheClip(selectedDefinition.ResourcePath)) {
            PilotRocketFact("rocket-safety-refused","Character Profile action/contact unavailable; retained owner"); return;
        }
        PilotPhysicsReset();
        PilotMotionAbortFall();
        if(!_pilotRocketEnabled||_pilotRocketTimer!=null||_pilotRocketSession!=null)return;
        PilotMoveCancel("Rocket priority");
        PilotRestCancel("Rocket priority");
        if(_pilotTopologyHold){_pilotTopologyReflowAttempts=0;_pilotTopologyBudgetReported=false;if(!PilotMotionTryLeaveTopologyHold())return;}
        if(!PilotClickLive||PilotMotionBusy)return;
        if(_pilotTopology?.Refresh().Success!=true||!PilotFresh){PilotRocketFact("rocket-safety-refused","fresh topology unavailable");return;}
        var revision=_pilotRocketRevision;var generation=_pilotGeneration;var gesture=_pilotGesture;
        var source=_pilotSource;var token=_pilotSourceToken;var hwnd=PilotHwnd;
        var topology=_pilotTopology.State.LastKnownGood;var began=Stopwatch.GetTimestamp();
        var lease=_pilotAttachmentTarget;var side=_pilotAttachmentPoseSide;
        var offsetX=_pilotAttachmentPoseOffsetX;var offsetY=_pilotAttachmentPoseOffsetY;
        _pilotRocketAttachment=lease;
        bool Own()=>revision==_pilotRocketRevision&&generation==_pilotGeneration&&gesture==_pilotGesture&&
            token==_pilotSourceToken&&ReferenceEquals(source,_pilotSource)&&hwnd==PilotHwnd&&PilotClickLive&&
            ReferenceEquals(topology,_pilotTopology.State.LastKnownGood)&&
            (lease==null||ReferenceEquals(lease,_pilotAttachmentTarget)&&ReferenceEquals(lease,_pilotRocketAttachment));
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(30)};
        _pilotRocketTimer=timer;
        var sessionLocal=(PilotRocketSession?)null;bool selected=false;long playback=0;string? rocketPath=null;
        PilotAttachmentTarget.Snapshot? returnSample=null;var returnAttempts=0;long returnBegan=0;
        var body=new Action(()=>{
            if(!ReferenceEquals(timer,_pilotRocketTimer)||revision!=_pilotRocketRevision)return;
            if(!Own()){PilotRocketCancel("rocket owner/source/topology changed");return;}
            if(selected&&(_gifPlayer?.PlaybackGeneration!=playback||_gifPlayer.ResourcePath!=rocketPath))
            {PilotRocketCancel("Rocket playback changed");return;}
            if(!PilotRocketStable()){
                if(sessionLocal!=null||Stopwatch.GetElapsedTime(began).TotalMilliseconds>=500){
                    if(sessionLocal==null)PilotRocketRefuseStart("mapping/recovery checkpoint unavailable");
                    else PilotRocketCancel("mapping/recovery checkpoint unavailable");
                    PilotRocketFact("rocket-safety-refused","bounded settled checkpoint wait expired");
                }
                return;
            }
            if(sessionLocal==null){
                if(!selected){
                    _pilotRocketReturnRect=_pilotRect;
                    _pilotRocketGrounded=_pilotMotionState==PilotMotionState.Grounded||
                        (_pilotMotionGroundVisual&&_pilotMonitor.IsValid&&Math.Abs(_pilotRect.Bottom-_pilotMonitor.WorkArea.Bottom)<1e-6);
                    var definition=selectedDefinition;
                    if(definition==null||_gifPlayer?.TrySwitchTo(definition.ResourcePath)!=true){
                        PilotRocketRefuseStart("Rocket GIF unavailable");return;
                    }
                    selected=true;playback=_gifPlayer.PlaybackGeneration;rocketPath=_gifPlayer.ResourcePath;_gifPlayer.Resume();return;
                }
                if(!PilotNative.GetWindowRect(hwnd,out var actual)||!CrossScreenGeometry.Near(actual.Physical,_pilotRect)){
                    PilotRocketRefuseStart("untrusted native geometry");return;
                }
                var start=_pilotRect;
                var center=new PhysicalPoint((start.Left+start.Right)/2,(start.Top+start.Bottom)/2);
                var monitor=topology!.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(center));
                if(!monitor.IsValid||!SingleMonitorDrag.Fits(start,monitor.WorkArea)){
                    PilotRocketRefuseStart("no single-monitor safe rocket start");PilotRocketFact("rocket-safety-refused","deferred: partial monitor, gap, oversized or edge footprint");return;
                }
                var topRect=PilotRocketGeometry.AtTop(start,monitor.WorkArea.Top);
                if(!CrossScreenGeometry.TranslationSafe(start,topRect,new[]{monitor.WorkArea})){
                    PilotRocketRefuseStart("no proved ascent corridor");PilotRocketFact("rocket-safety-refused","no proved single-monitor ascent corridor");return;
                }
                var speed=PilotRocketGeometry.SpeedPhysical(_settings.Interaction.RocketVerticalSpeed,_pilotScale.Y);
                _pilotRocketSavedTransform=PetImage.RenderTransform;
                var group=(TransformGroup)PetImage.RenderTransform.Clone();
                _pilotRocketVisual=new TranslateTransform();group.Children.Add(_pilotRocketVisual);PetImage.RenderTransform=group;
                _pilotRocketSavedClip=PetSurface.Clip;PetSurface.Clip=new RectangleGeometry(new Rect(0,0,PetSurface.Width,PetSurface.Height));
                // The full viewport covers the all-frame union, including both flames.
                sessionLocal=new PilotRocketSession(start,monitor.WorkArea.Top,speed,_settings.Interaction.RocketHiddenDuration,_clickClock(),PetSurface.Height*_pilotScale.Y);
                _pilotRocketSession=sessionLocal;
                // The double-click's admitted Down already reset _pilotMotionState, so
                // re-derive the ground fact from the still-live ground visual and floor
                // contact; a grounded start must return to the same effective floor.
                _gifPlayer!.Resume();
                PilotRocketFact("rocket-start","double-click",new{start,monitor=monitor.Id,workTop=monitor.WorkArea.Top,
                    speedDip=_settings.Interaction.RocketVerticalSpeed,speedPhysical=speed,
                    hiddenMs=_settings.Interaction.RocketHiddenDuration.TotalMilliseconds,grounded=_pilotRocketGrounded});
                PilotActionVoiceStart(ref _pilotRocketVoiceId,rocketPath!,playback,
                    () => Own() && ReferenceEquals(sessionLocal,_pilotRocketSession));
                return;
            }
            sessionLocal.Advance(_clickClock());
            if(sessionLocal.IsComplete){
                if(lease!=null){
                    // The existing 30ms owner supplies at most five samples/150ms.
                    // Two consecutive stable snapshots are required; no chasing
                    // during flight and no new timer or HWND enumeration.
                    if(returnBegan==0)returnBegan=Stopwatch.GetTimestamp();
                    var read=lease.TryRead(out var fresh,out var moving);
                    returnAttempts++;
                    if(!read&&!moving){PilotRocketCancel("original attachment invalid at return");return;}
                    if(Stopwatch.GetElapsedTime(returnBegan).TotalMilliseconds>150){PilotRocketCancel("attached return bounded sample deadline");return;}
                    if(!read||returnSample!=fresh){
                        returnSample=read?fresh:null;
                        if(returnAttempts>=5)PilotRocketCancel("attached return still moving after five samples");
                        return;
                    }
                    _pilotRocketReturnGuard=()=>Own()&&lease.TryRead(out var current)&&current==fresh;
                    PilotRocketStopTimer();_pilotRocketTimerBody=null;
                    PilotRocketResetVisual(); // remains Hidden throughout pose admission
                    // Arm completion BEFORE the switch, whose ClipChanged queues
                    // the independent visual admission. Never announce Attached first.
                    _pilotVisualCompleted=()=>{if(Own())PilotRocketFinishAttached();};
                    if(!PilotAttachmentBeginPose(side,offsetX,offsetY))PilotRocketCancel("return pose unavailable");
                    return;
                }
                PilotRocketStopTimer();_pilotRocketTimerBody=null;
                PilotRocketResetVisual(); // still Hidden while resize/return is committed
                _gifPlayer!.TrySwitchTo(_effectiveBaseIdlePath!);
                _pilotVisualReturn=_pilotRocketReturnRect;
                _pilotVisualCompleted=()=>{if(Own())PilotRocketFinish();};
                return;
            }
            var target=PilotRocketGeometry.AtTop(sessionLocal.Original,Math.Round(sessionLocal.Top));
            if(target!=_pilotRect){
                if(!PilotPlace(target,sessionLocal.IsHidden?"rocket-hide":"rocket-step")){
                    if(Own()){PilotRocketCancel("placement rejected");PilotRocketFact("rocket-safety-refused","existing writer refused candidate");}
                    return;
                }
                if(!Own())return;
            }
            _pilotRocketVisual!.Y=sessionLocal.VisualOffsetPhysical/_pilotScale.Y;
            PetSurface.Visibility=sessionLocal.IsHidden?Visibility.Hidden:Visibility.Visible;
        });
        _pilotRocketTimerBody=body;timer.Tick+=(_,_)=>body();timer.Start();body();
    }
}
