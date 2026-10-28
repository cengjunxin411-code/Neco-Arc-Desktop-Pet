using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private PilotMotionState _pilotMotionState;
    private DispatcherTimer? _pilotMotionTimer;
    private Action? _pilotMotionTimerBody,_pilotMotionCompletionBody;
    private long _pilotMotionRevision;
    private bool _pilotMotionEnabled=true,_pilotMotionScaleGround,_pilotMotionMaintainingGround,_pilotMotionGroundVisual;
    private bool _pilotVisualGroundAfterScale;
    private bool _pilotTopologyHold;
    private DispatcherOperation? _pilotTopologyRefresh;
    private DispatcherOperation? _pilotFallPending;
    private long _pilotFallRequestId;
    private int _pilotTopologyReflowAttempts,_pilotTopologyRectReadFailures,_pilotTopologyMappingFailures;
    private bool _pilotTopologyBudgetReported;
    private PilotTopologyChange? _pilotTopologyChange;
    // Immutable pre-change facts: a legal WorkArea change is answered with a bounded,
    // same-monitor correction instead of an exit or an arbitrary origin substitution.
    private readonly record struct PilotTopologyChange(bool Grounded,PhysicalRect Trusted,PhysicalRect OldWorkArea,
        string MonitorId,object? Source,long Token,nint Hwnd,Coordinates.DpiScale Scale);
    private bool PilotMotionBusy=>_pilotMotionState is PilotMotionState.Falling or PilotMotionState.Landing;
    private void PilotMotionFact(string kind,string reason,object? detail=null)=>_pilotLog?.Write(kind,
        new{owner="PilotMotion",revision=_pilotMotionRevision,generation=_pilotGeneration,gesture=_pilotGesture,
            source=_pilotSourceToken,hwnd=(long)PilotHwnd,state=_pilotMotionState.ToString(),gif=_gifPlayer?.ResourcePath,reason,detail});

    // The calibrated source shoe/silhouette reference is transformed through the
    // actual existing profile/mirror. Only local Canvas placement is changed.
    // This uses the otherwise transparent bottom padding while preserving HWND size.
    private double PilotMotionSourceContact()
    {
        if (CharacterProfiles.CharacterProfileRuntime.Current?.TryAnimation(_gifPlayer?.ResourcePath,out var animation) != true ||
            animation.Contacts.Top is not {} support) return double.NaN;
        return support.Y * animation.Canvas.Height;
    }
    private double PilotMotionContactDip()
    {
        if(_gifPlayer==null)return double.NaN;
        var sourceY=PilotMotionSourceContact()*PetImage.Height/_gifPlayer.PixelHeight;
        return AttachmentGeometry.TransformPoint(new(PetImage.Width/2,sourceY),new(PetImage.Width,PetImage.Height),
            new(Canvas.GetLeft(PetImage),Canvas.GetTop(PetImage)),PetImage.RenderTransform.Value,PetImage.RenderTransformOrigin).Y;
    }
    private void PilotMotionAlignImage()
    {
        if(_pilotVisualCalibrated)return; // tight all-frame union already registers its shoe edge
        if(_gifPlayer==null||!_pilotReady||!_pilotMotionEnabled||!_pilotMotionGroundVisual)return;
        var contact=PilotMotionContactDip();
        var dpi=PilotNative.GetDpiForWindow(PilotHwnd)/96d;
        if(double.IsFinite(contact)&&dpi>0)Canvas.SetTop(PetImage,Canvas.GetTop(PetImage)+Math.Round(PetSurface.Height*dpi)/dpi-contact);
    }
    private void PilotMotionStopTimer(){_pilotMotionTimer?.Stop();_pilotMotionTimer=null;}
    private void PilotMotionCancel(string reason)=>PilotMotionCancelCore(reason,false);
    private void PilotMotionCancelCore(string reason,bool keepAttachment)
    {
        PilotMotionAbortFall();
        PilotActionVoiceEnd(ref _pilotFallVoiceId,false);
        PilotActionVoiceEnd(ref _pilotLandVoiceId,false);
        if(!keepAttachment)PilotAttachmentCancel(reason);
        PilotRocketCancel(reason);
        if(!_pilotMotionScaleGround&&reason is not ("wheel/scale" or "replace planning" or "fall priority" or "mapping/recovery checkpoint unavailable")&&
            !reason.StartsWith("G recovery:",StringComparison.Ordinal))_pilotVisualGroundAfterScale=false;
        if(!_pilotMotionScaleGround&&reason is not ("wheel/scale" or "replace planning" or "fall priority"))_pilotMotionMaintainingGround=false;
        var active=PilotMotionBusy||_pilotMotionTimer!=null;
        PilotMotionStopTimer();_pilotMotionRevision++;_pilotMotionCompletionBody=null;
        _pilotMotionState=PilotMotionState.Idle;
        if(active) {
            PilotMotionFact("fall-cancel",reason);
            _gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer?.Resume();
        }
        // CrossStartRecovery calls this after a DPI generation transition. Bind
        // current idle layout to that new owner, never revive old return/Down work.
        if(PilotVisualBusy&&reason.StartsWith("G recovery:",StringComparison.Ordinal)) {
            PilotVisualCancelPending();PilotVisualQueue();
        }
    }
    private bool PilotMotionStable()=>!PilotVisualBusy&&PilotClickLive&&PilotMapping(out var scale)&&scale==_pilotScale&&
        _pilotPlacementDepth==0&&_pilotHookDepth==0&&!_crossActive&&!_pilotDrag.Active&&!PetSurface.IsMouseCaptured&&
        (!_crossScreen||_crossMapped&&_crossRecovery?.Active!=true&&_crossLayoutDepth==0&&CrossCheckpoint().Ready);

    private void PilotMotionAbortFall(){_pilotFallRequestId++;Abort(ref _pilotFallPending);}
    private void PilotMotionRequestFall(string reason)
    {
        PilotMotionAbortFall();
        var id=_pilotFallRequestId;var began=Stopwatch.GetTimestamp();
        var generation=_pilotGeneration;var gesture=_pilotGesture;var source=_pilotSource;
        var token=_pilotSourceToken;var hwnd=PilotHwnd;var attachmentRevision=_pilotAttachmentRevision;
        var topology=_pilotTopology?.State.LastKnownGood;
        _pilotFallPending=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
            if(id!=_pilotFallRequestId)return;
            _pilotFallPending=null;_pilotFallRequestId++;
            if(Stopwatch.GetElapsedTime(began).TotalMilliseconds>500||
                generation!=_pilotGeneration||gesture!=_pilotGesture||!ReferenceEquals(source,_pilotSource)||
                token!=_pilotSourceToken||hwnd!=PilotHwnd||attachmentRevision!=_pilotAttachmentRevision||
                !PilotClickLive||!_pilotMotionEnabled||_pilotTopologyHold||_crossRecovery?.Active==true||
                !ReferenceEquals(topology,_pilotTopology?.State.LastKnownGood)||
                !PilotMapping(out var scale)||scale!=_pilotScale||
                (_crossScreen&&(!_crossMapped||_crossLayoutDepth!=0||!CrossCheckpoint().Ready))||
                _pilotAttachmentTarget!=null||_pilotAttachmentTimer!=null||_pilotClickAction||
                _pilotDrag.Active||_crossActive||PetSurface.IsMouseCaptured||
                _pilotRocketTimer!=null||_pilotRocketSession!=null||_pilotRocketAttachment!=null||
                _pilotMotionTimer!=null||_pilotMotionState!=PilotMotionState.Idle)return;
            PilotMotionBegin("attachment target invalid: "+reason);
        }));
    }

    private void PilotMotionBegin(string reason)
    {
        PilotPhysicsReset();
        PilotMotionAbortFall();
        if(!_pilotMotionEnabled||PilotMotionBusy)return;
        PilotMoveCancel("Falling priority");
        PilotRandomCancel("Falling priority");
        PilotRestCancel("Falling priority");
        var groundedRequest=_pilotMotionState==PilotMotionState.Grounded;
        if(_pilotTopologyHold){_pilotTopologyReflowAttempts=0;_pilotTopologyBudgetReported=false;if(!PilotMotionTryLeaveTopologyHold())return;}
        if(!PilotClickLive)return;
        PilotAttachmentCancel("fall priority");
        PilotMotionCancel("replace planning");PilotClickCancel("fall priority");
        // Refresh on every explicit request, even if no WM_DISPLAYCHANGE was sent
        // for a taskbar work-area change. No old topology snapshot is blessed.
        if(_pilotTopology?.Refresh().Success!=true||!PilotFresh){PilotMotionFact("motion-safety-refused","fresh topology unavailable");return;}
        if(groundedRequest&&!PilotVisualBusy&&PilotMapping(out var groundScale)&&groundScale==_pilotScale&&
            PilotNative.GetWindowRect(PilotHwnd,out var groundRect)&&CrossScreenGeometry.Near(groundRect.Physical,_pilotRect)&&
            _pilotTopology.State.LastKnownGood!.Monitors.Any(m=>Math.Abs(_pilotRect.Bottom-m.WorkArea.Bottom)<1e-6&&SingleMonitorDrag.Fits(_pilotRect,m.WorkArea))) {
            _pilotMotionState=PilotMotionState.Grounded;return; // fresh floor, no placement or Landing replay
        }
        var revision=_pilotMotionRevision;var generation=_pilotGeneration;var gesture=_pilotGesture;
        var source=_pilotSource;var token=_pilotSourceToken;var hwnd=PilotHwnd;
        var topology=_pilotTopology.State.LastKnownGood;var began=Stopwatch.GetTimestamp();
        bool Own()=>revision==_pilotMotionRevision&&generation==_pilotGeneration&&gesture==_pilotGesture&&
            token==_pilotSourceToken&&ReferenceEquals(source,_pilotSource)&&hwnd==PilotHwnd&&PilotClickLive&&
            ReferenceEquals(topology,_pilotTopology.State.LastKnownGood);
        var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(30)};
        _pilotMotionTimer=timer;
        PhysicalRect start=default,target=default;MonitorSnapshot monitor=default;
        bool planned=false,selected=false;double duration=0;long started=0;int acceptedSteps=0;
        var body=new Action(()=> {
            if(!ReferenceEquals(timer,_pilotMotionTimer)||revision!=_pilotMotionRevision)return;
            if(!Own()){PilotMotionCancel("motion owner/source/topology changed");return;}
            if(!PilotMotionStable()) {
                // Wait only for the existing G owner to settle; never start/renew G.
                if(planned||Stopwatch.GetElapsedTime(began).TotalMilliseconds>=500) {
                    PilotMotionCancel("mapping/recovery checkpoint unavailable");PilotMotionFact("motion-safety-refused","bounded settled checkpoint wait expired");
                }
                return;
            }
            if(!planned) {
                if(_pilotMotionState==PilotMotionState.Grounded) {
                    PilotMotionStopTimer();PilotMotionFact("grounded","accepted scale checkpoint already at floor; no GIF/Landing replay");return;
                }
                if(!selected) {
                    var first=_fallRandom.Next(2)==0?SidewaysFallingGifPath:UprightFallingGifPath;
                    var second=first==SidewaysFallingGifPath?UprightFallingGifPath:SidewaysFallingGifPath;
                    if(_gifPlayer?.TrySwitchTo(first)!=true&&_gifPlayer?.TrySwitchTo(second)!=true) {
                        PilotMotionCancel("Falling GIF unavailable");PilotMotionFact("motion-safety-refused","real Falling resource unavailable");return;
                    }
                    selected=true;_gifPlayer.Resume();return; // next tick uses the accepted new footprint
                }
                if(!PilotNative.GetWindowRect(hwnd,out var actual)||!CrossScreenGeometry.Near(actual.Physical,_pilotRect)) {
                    PilotMotionCancel("untrusted native geometry");return;
                }
                start=_pilotRect;
                var center=new PhysicalPoint((start.Left+start.Right)/2,(start.Top+start.Bottom)/2);
                monitor=topology!.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(center));
                // A proved legal full footprint supplies the local ground slot.
                // Register the measured shoe there only after corridor admission.
                var contact=start.Height;
                var end=PilotMotionGeometry.GroundTarget(start,monitor.WorkArea,contact);
                if(end is not {} legal||!CrossScreenGeometry.TranslationSafe(start,legal,new[]{monitor.WorkArea})) {
                    PilotMotionCancel("no single-monitor safe ground corridor");PilotMotionFact("motion-safety-refused","deferred: partial monitor, gap, oversized or below-ground footprint");return;
                }
                target=legal;_pilotMonitor=monitor;
                _pilotMotionGroundVisual=true;
                if(target==start) {PilotMotionStopTimer();_pilotMotionState=PilotMotionState.Grounded;_gifPlayer!.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer.Resume();PilotMotionFact("grounded","already at contact; no placement or Landing replay");return;}
                _pilotMotionState=PilotMotionState.Falling;_gifPlayer!.Resume();
                duration=PilotMotionGeometry.Duration(target.Top-start.Top,_pilotScale.Y);started=Stopwatch.GetTimestamp();planned=true;
                PilotMotionFact("fall-start",reason,new{start,target,monitor,durationSeconds=duration,contactDip=PilotMotionContactDip(),desiredHeightDip=_petHeight,effective=_crossEffectiveScale});
                // planned is this Motion session's once-only latch. Resource selection alone
                // is insufficient: require the admitted footprint and a nonzero safe descent.
                var fallPlayback=_gifPlayer.PlaybackGeneration;
                PilotActionVoiceStart(ref _pilotFallVoiceId,_gifPlayer.ResourcePath,fallPlayback,
                    ()=>Own()&&_pilotMotionState==PilotMotionState.Falling);
                return;
            }
            var elapsed=Stopwatch.GetElapsedTime(started).TotalSeconds;var progress=Math.Clamp(elapsed/duration,0,1);
            if(elapsed>duration+2) {PilotMotionCancel("motion time bound exceeded");PilotMotionFact("motion-safety-refused","late Dispatcher tick; no catch-up jump");return;}
            var candidate=PilotMotionGeometry.Step(start,target,progress);
            if(!CrossScreenGeometry.TranslationSafe(_pilotRect,candidate,new[]{monitor.WorkArea})) {
                PilotMotionCancel("unsafe descent candidate");PilotMotionFact("motion-safety-refused","candidate corridor rejected");return;
            }
            // No second native writer, WPF Left/Top, layout flushing or anchor writes.
            if(candidate!=_pilotRect) {
                if(!PilotPlace(candidate,"fall-step")) {
                    if(Own()) {PilotMotionCancel("placement rejected");PilotMotionFact("motion-safety-refused","existing writer refused candidate");}
                    return;
                }
                if(!Own())return;
                acceptedSteps++;
            }
            if(progress<1)return;
            PilotMotionStopTimer();PilotMotionFact("fall-step-accepted","aggregated completion",new{acceptedSteps,rect=_pilotRect});
            PilotMotionFact("ground-contact","WorkArea.Bottom of locked active monitor",new{contactPhysicalY=_pilotRect.Top+PilotMotionContactDip()*_pilotScale.Y,monitor.WorkArea});
            _pilotMotionState=PilotMotionState.Landing;
            // D10-A: stop the owned flight even if Landing's visual admission fails
            // or is deferred. The next voice still waits for the shared first-frame gate.
            PilotActionVoiceEnd(ref _pilotFallVoiceId,false);
            if(_gifPlayer!.TrySwitchTo(LandingGifPath,loop:false)) {
                var playback=_gifPlayer.PlaybackGeneration;
                _pilotMotionCompletionBody=()=> {
                    if(!Own()||_pilotMotionState!=PilotMotionState.Landing||_gifPlayer.PlaybackGeneration!=playback||
                        _gifPlayer.ResourcePath!=LandingGifPath||!_gifPlayer.IsComplete)return;
                    PilotMotionFact("landing-completed","real GifFramePlayer.Completed");
                    PilotActionVoiceEnd(ref _pilotLandVoiceId,true);
                    _pilotMotionState=PilotMotionState.Grounded;_pilotMotionCompletionBody=null;
                    _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer.Resume();PilotMotionFact("grounded","idle at fixed contact");
                };
                PilotMotionFact("landing-start","original one-shot GIF",new{playback});_gifPlayer.Resume();
                PilotActionVoiceStart(ref _pilotLandVoiceId,LandingGifPath,playback,
                    ()=>Own()&&_pilotMotionState==PilotMotionState.Landing);
            } else {
                _pilotMotionState=PilotMotionState.Grounded;_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer.Resume();
                PilotMotionFact("grounded","Landing unavailable; effective idle at ground");
            }
        });
        _pilotMotionTimerBody=body;timer.Tick+=(_,_)=>body();timer.Start();body();
    }
    private void PilotMotionCompleted(object? sender,EventArgs e)
    {
        if(e is Animation.GifPlaybackCompletedEventArgs completed&&ReferenceEquals(sender,_gifPlayer)&&
            completed.Generation==_gifPlayer?.PlaybackGeneration&&completed.ResourcePath==_gifPlayer.ResourcePath)
            _pilotMotionCompletionBody?.Invoke();
    }
    private void PilotMotionScaleBegin()
    {
        _pilotMotionScaleGround=_pilotMotionMaintainingGround||_pilotMotionState is PilotMotionState.Grounded or PilotMotionState.Landing ||
            _pilotMotionGroundVisual&&PilotFresh&&_pilotTopology!.State.LastKnownGood!.Monitors.Any(m=>
                Math.Abs(_pilotRect.Bottom-m.WorkArea.Bottom)<1e-6&&SingleMonitorDrag.Fits(_pilotRect,m.WorkArea));
        _pilotMotionMaintainingGround=_pilotMotionScaleGround;
        _pilotVisualGroundAfterScale=_pilotMotionScaleGround;
        PilotScaleBegin(); // arranged foot contact of this scale transaction, before any layout
        PilotMotionCancel("wheel/scale");
    }
    private void PilotMotionScaleEnd()
    {
        PilotRefreshClickImage();
        if(_pilotMotionScaleGround){
            // The scale layout itself already kept the arranged foot contact; the
            // legacy ground plan is only a fallback when no anchor was available.
            if(!_pilotScaleActive) PilotMotionPlanScaleGround();
            _pilotMotionScaleGround=false;PilotMotionBegin("scale ground continuation");
        }
        PilotScaleEnd();
    }
    private void PilotMotionPlanScaleGround()
    {
        // ApplyPetLayout has left its WPF property stack. The existing G owner
        // already owns the new natural size; give it a proved ground origin before
        // its deferred first commit. Never renew G or publish placement here.
        if(!PilotClickLive||!PilotMapping(out var scale)||scale!=_pilotScale||
            _pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0)return;
        var center=new PhysicalPoint((_pilotRect.Left+_pilotRect.Right)/2,(_pilotRect.Top+_pilotRect.Bottom)/2);
        var monitor=_pilotTopology!.State.LastKnownGood!.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(center));
        if(!monitor.IsValid)return;
        var width=Math.Round(PetSurface.Width*scale.X);var height=Math.Round(PetSurface.Height*scale.Y);
        var target=new PhysicalRect(_pilotRect.Left,monitor.WorkArea.Bottom-height,_pilotRect.Left+width,monitor.WorkArea.Bottom);
        if(!CrossScreenGeometry.TranslationSafe(_pilotRect,target,new[]{monitor.WorkArea})) {
            PilotMotionFact("motion-safety-refused","resize cannot hold ground within the locked single-monitor corridor");return;
        }
        if(_crossScreen) {
            if(_crossRecovery?.Active!=true||_crossRecovery.Accepted||_crossRecovery.Owner!=CrossOwner)return;
            _crossOrigin=new(target.Left,target.Top); // target only; CrossPlace remains sole committer
            PilotMotionFact("ground-resize-planned","existing G owner retains WorkArea ground",new{target,owner=CrossOwner});
        } else if(target!=_pilotRect&&!PilotPlace(target,"scale ground contact"))
            PilotMotionFact("motion-safety-refused","existing single-monitor writer refused resize ground contact");
    }
    private void PilotMotionEnable(bool enabled)
    {
        _pilotMotionEnabled=enabled;
        if(!enabled){PilotMotionCancel("user disabled 15B motion");_pilotMotionGroundVisual=false;PilotRefreshClickImage();}
    }
    private void PilotMotionTopologyChanged()
    {
        PilotPhysicsReset();
        if(_pilotStopped||_pilotClosing)return;
        PilotMoveCancel("topology/workarea invalidation");
        // Capture the pre-change grounded fact and last trusted geometry before any
        // cancellation, so the fresh snapshot can be answered with a proved correction.
        var work=_pilotMonitor.IsValid?_pilotMonitor.WorkArea:_pilotRect;
        var grounded=_pilotMotionState is PilotMotionState.Grounded or PilotMotionState.Landing ||
            _pilotMotionGroundVisual&&_pilotMonitor.IsValid&&Math.Abs(_pilotRect.Bottom-work.Bottom)<1e-6;
        _pilotTopologyChange=new(grounded,_pilotRect,work,_pilotMonitor.Id,_pilotSource,_pilotSourceToken,PilotHwnd,_pilotScale);
        // A hidden native topology HWND broadcasts on this same STA. Revoke first;
        // query only in a deferred callback, never place on its native message stack.
        _pilotTopologyHold=true;PilotCancelGesture("topology/workarea invalidation");_pilotGeneration++;
        PilotVisualCancelPending(); // layout is rebound only after the fresh hold resolves
        Abort(ref _pilotTopologyRefresh);
        _pilotTopologyReflowAttempts=0;_pilotTopologyRectReadFailures=0;_pilotTopologyMappingFailures=0;_pilotTopologyBudgetReported=false;
        var generation=_pilotGeneration;var gesture=_pilotGesture;var source=_pilotSource;var token=_pilotSourceToken;var hwnd=PilotHwnd;
        _pilotTopologyRefresh=Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=> {
            if(_pilotStopped||_pilotClosing||generation!=_pilotGeneration||gesture!=_pilotGesture||
                !ReferenceEquals(source,_pilotSource)||token!=_pilotSourceToken||hwnd!=PilotHwnd)return;
            _pilotTopologyRefresh=null;
            PilotMotionTryLeaveTopologyHold();
        }));
        PilotMotionFact("topology-invalidated","work-area/topology changed; frozen until a fresh legal snapshot",
            new{grounded,trusted=_pilotRect,oldWorkArea=work,monitorId=_pilotMonitor.Id,dpi=_pilotScale});
    }
    private bool PilotMotionTryLeaveTopologyHold()
    {
        if(!_pilotTopologyHold)return true;
        if(_pilotStopped||_pilotClosing||_pilotSource is null or {IsDisposed:true}||!PilotNative.IsWindow(PilotHwnd)||
            _pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0)return false;
        if(_pilotTopology?.Refresh().Success!=true||!PilotFresh) {
            PilotMotionFact("motion-safety-refused","topology hold: fresh snapshot unavailable; last trusted position retained");return false;
        }
        if(!PilotMapping(out var scale)) {
            PilotMotionFact("motion-safety-refused","topology hold: source/DPI mapping unavailable; last trusted position retained");return false;
        }
        if(!PilotNative.GetWindowRect(PilotHwnd,out var actual)) {
            PilotMotionFact("motion-safety-refused","topology hold: native RECT unreadable; bounded retry retained");return false;
        }
        var snapshot=_pilotTopology.State.LastKnownGood!;
        var workAreas=snapshot.Monitors.Select(m=>m.WorkArea).ToArray();
        var centre=new PhysicalPoint((actual.Physical.Left+actual.Physical.Right)/2,(actual.Physical.Top+actual.Physical.Bottom)/2);
        var freshMonitor=snapshot.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(centre));
        // A grounded pet follows the fresh floor even when its current rect is still legal.
        var follows=_pilotTopologyChange is {Grounded:true}&&freshMonitor.IsValid&&
            Math.Abs(actual.Physical.Bottom-freshMonitor.WorkArea.Bottom)>1e-6;
        if(!follows&&CrossScreenGeometry.Near(actual.Physical,_pilotRect)&&CrossScreenGeometry.Covered(actual.Physical,workAreas)) {
            if(!_crossScreen) {
                var locked=snapshot.Monitors.FirstOrDefault(m=>m.Id==_pilotMonitor.Id);
                if(!locked.IsValid||scale!=_pilotScale||!SingleMonitorDrag.Fits(actual.Physical,locked.WorkArea)) {
                    PilotMotionFact("motion-safety-refused","topology hold: locked single-monitor identity unavailable; bounded reflow retained");return false;
                }
                _pilotMonitor=locked; // the locked identity survives; its WorkArea must be fresh
            }
            _pilotTopologyHold=false;_pilotTopologyChange=null;
            if(_crossScreen&&(!_crossMapped||scale!=_pilotScale))CrossStartRecovery("fresh legal topology snapshot");
            PilotVisualQueue();
            PilotMotionFact("topology-refreshed","last trusted footprint still legal; old motion remains cancelled");return true;
        }
        return PilotMotionPlanTopologyReflow(actual.Physical,snapshot,scale);
    }
    // Only a same-monitor, same-footprint, same-DPI correction whose whole sweep is covered
    // by the last trusted work area union the fresh one may be planned. The existing single
    // writer commits it; no WPF Left/Top, GIF, Landing, cursor or second native writer.
    private bool PilotMotionPlanTopologyReflow(PhysicalRect actual,MonitorTopologySnapshot snapshot,Coordinates.DpiScale scale)
    {
        if(_pilotTopologyChange is not {} change||_pilotStopped||_pilotClosing)return false;
        if(!ReferenceEquals(change.Source,_pilotSource)||change.Token!=_pilotSourceToken||change.Hwnd!=PilotHwnd||change.Scale!=scale) {
            PilotMotionFact("topology-reflow-refused","owner/source/scale identity changed; last trusted position retained");return false;
        }
        if(_pilotDrag.Active||_crossActive||PetSurface.IsMouseCaptured) {
            PilotMotionFact("topology-reflow-deferred","active gesture/capture; reflow waits for the next Down or ground menu");return false;
        }
        // Bounded per invalidation: every full evaluation costs one of two automatic
        // attempts, so a persistent refusal cannot become an unbounded log or retry loop.
        _pilotTopologyReflowAttempts++;
        var centre=new PhysicalPoint((actual.Left+actual.Right)/2,(actual.Top+actual.Bottom)/2);
        var monitor=snapshot.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(centre));
        if(!monitor.IsValid||!string.Equals(monitor.Id,change.MonitorId,StringComparison.Ordinal)) {
            PilotMotionFact("topology-reflow-refused","observed RECT is not on the same fresh monitor; no cross-screen migration",
                new{observed=actual,monitorId=change.MonitorId});return false;
        }
        if(!CrossScreenGeometry.NaturalFootprint(actual,CrossSize,scale)) {
            PilotMotionFact("topology-reflow-refused","observed RECT is not the natural live footprint; no origin substitution",new{observed=actual});return false;
        }
        var grounded=change.Grounded;
        var target=grounded
            ? new PhysicalRect(actual.Left,monitor.WorkArea.Bottom-actual.Height,actual.Right,monitor.WorkArea.Bottom)
            : CrossScreenGeometry.Fit(actual,new[]{monitor.WorkArea}) ?? default;
        if(!target.IsValid||!CrossScreenGeometry.Covered(target,new[]{monitor.WorkArea})||
            !CrossScreenGeometry.TranslationSafe(actual,target,new[]{change.OldWorkArea,monitor.WorkArea})) {
            PilotMotionFact("topology-reflow-refused","no proved same-monitor correction corridor; last trusted position retained",
                new{observed=actual,target,oldWorkArea=change.OldWorkArea,workArea=monitor.WorkArea});return false;
        }
        // Never place on a busy native/input/layout stack: the hold is retained and the
        // existing deferred invalidation callback, the 100 ms watch, or the next user
        // Down/ground menu retries this same proved plan.
        if(_pilotHookDepth!=0||_pilotPlacementDepth!=0||_crossLayoutDepth!=0) {
            PilotMotionFact("topology-reflow-deferred","busy native/layout stack; proved plan retried outside the stack",
                new{observed=actual,target});return false;
        }
        // An already legal but divergent observed rect is re-asserted through the existing
        // writer, so every trusted field keeps one consistent accepted owner.
        var reason=grounded ? "topology reflow: ground" : "topology reflow: legality";
        if(!_crossScreen)_pilotMonitor=monitor; // fresh live WorkArea for the locked identity
        var ok=_crossScreen ? CrossPlace(target,reason) : PilotPlace(target,reason);
        PilotNative.Rect post=default;var read=ok&&PilotNative.GetWindowRect(PilotHwnd,out post);
        var verified=read&&CrossScreenGeometry.Near(post.Physical,target)&&CrossScreenGeometry.Covered(post.Physical,new[]{monitor.WorkArea});
        if(!verified) {
            PilotMotionFact("topology-reflow-refused","existing writer refused or postcheck failed; last trusted position retained",
                new{observed=actual,target,attempts=_pilotTopologyReflowAttempts});return false;
        }
        if(grounded)_pilotMotionState=PilotMotionState.Grounded;
        _pilotTopologyHold=false;_pilotTopologyChange=null;
        PilotVisualQueue();
        PilotMotionFact("topology-reflow-committed",grounded
                ? "grounded re-seat onto the fresh WorkArea floor; no Landing replay"
                : "floating legality correction; no Landing replay",
            new{observed=actual,target,oldWorkArea=change.OldWorkArea,workArea=monitor.WorkArea,attempts=_pilotTopologyReflowAttempts});
        return true;
    }
    private void PilotMotionWatchTopologyHold()
    {
        // A recoverable geometry hold suspends planning. Only independently proved
        // source/DPI/native failures stop here; geometry divergence never does.
        if(_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0)return;
        if(_pilotSource is null or {IsDisposed:true}||!PilotNative.IsWindow(PilotHwnd))
        { PilotStop("Topology hold: source/HWND no longer live; no origin substitution.");return; }
        if(!PilotMapping(out _)) {
            _pilotTopologyMappingFailures++;
            if(_pilotTopologyMappingFailures>=3)
            { PilotStop("Topology hold: source/DPI mapping no longer coherent; no origin substitution.");return; }
            PilotMotionFact("motion-safety-refused","topology hold: source/DPI mapping temporarily unavailable; bounded retry retained");return;
        }
        _pilotTopologyMappingFailures=0;
        if(!PilotNative.GetWindowRect(PilotHwnd,out _)) {
            _pilotTopologyRectReadFailures++;
            if(_pilotTopologyRectReadFailures>=3)
            { PilotStop("Topology hold: native RECT unreadable at a stable stack; no origin substitution.");return; }
            PilotMotionFact("motion-safety-refused","topology hold: native RECT read failed; bounded retry retained");return;
        }
        _pilotTopologyRectReadFailures=0;
        if(_pilotTopologyReflowAttempts>=2) {
            if(!_pilotTopologyBudgetReported) {
                _pilotTopologyBudgetReported=true;
                PilotMotionFact("motion-safety-refused","topology hold: bounded reflow budget exhausted; last trusted position retained; retry on Down or ground menu");
            }
            return;
        }
        PilotMotionTryLeaveTopologyHold();
    }
}
