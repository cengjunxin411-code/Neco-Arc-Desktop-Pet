using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using DpiHwndProbe;

namespace NecoArcDesktopPet;

// Pilot-only adapter. G's exact pure state protocol is linked by the Pilot build.
// Local layout is the real Sprite; world origin and grab offset are physical px.
public partial class MainWindow
{
    private bool _crossScreen,_crossInitialized,_crossActive,_crossDragging,_crossMapped;
    private PhysicalPoint _crossDown,_crossOrigin;
    private PhysicalVector _crossGrab;
    private PhysicalPoint? _crossLatestCursor;
    private EarlyRecovery? _crossRecovery;
    private DispatcherOperation? _crossMove,_crossLegacy;
    private Action? _crossLegacyBody;
    private DispatcherTimer? _crossDeadline;
    private long _crossPlacementId,_crossMoves,_crossMappingUnavailable,_crossAcceptedMoves;
    private long? _crossRequestedId,_crossAcceptedId,_crossInFlightId;
    private RecoveryOwner? _crossAcceptedOwner;
    private PhysicalRect? _crossExpected;
    private long _crossTransitionTicks,_crossCursorTicks;
    private long _crossLegacyRevision;
    private int _crossLayoutDepth;
    private RecoveryCheckpoint? _crossLastCheckpoint;
    private double _crossEffectiveScale=1;
    private bool _crossBoundaryPreparing,_crossTranslated;
    private DispatcherOperation? _crossBoundary;
    private Action? _crossBoundaryBody;
    private long _crossBoundaryRevision;
    private void CrossAbortBoundary(){_crossBoundaryRevision++;Abort(ref _crossBoundary);}
    private RecoveryOwner CrossOwner => new(_pilotGeneration,_pilotGesture,_pilotSourceToken,(long)PilotHwnd);
    private RecoveryOwner CrossOwnerSnapshot()=>CrossOwner;
    private bool CrossLive=>_crossScreen&&_pilotReady&&!_pilotStopped&&!_pilotClosing&&_pilotSource is {IsDisposed:false};
    private LocalDipVector CrossSize=>new(PetSurface.Width,PetSurface.Height);
    private PhysicalRect[] CrossWork=>PilotFresh ? _pilotTopology!.State.LastKnownGood!.Monitors.Select(m=>m.WorkArea).ToArray() : Array.Empty<PhysicalRect>();
    private static void Abort(ref DispatcherOperation? operation) {if(operation?.Status==DispatcherOperationStatus.Pending)operation.Abort();operation=null;}
    private void CrossAbortLegacy(){_crossLegacyRevision++;Abort(ref _crossLegacy);}

    private void CrossLoaded()
    {
        _crossInitialized=true;_crossMapped=true;_crossOrigin=new(_pilotRect.Left,_pilotRect.Top);
        _crossRecovery=new EarlyRecovery(CrossCheckpoint,CrossCommit,
            (priority,action)=> {
                var operation=Dispatcher.BeginInvoke(priority=="Loaded"?DispatcherPriority.Loaded:DispatcherPriority.ContextIdle,action);
                return ()=>{if(operation.Status==DispatcherOperationStatus.Pending)operation.Abort();};
            },CrossTrace,()=>_crossMapped=false,reason=>PilotStop("Cross-screen recovery: "+reason));
        LayoutUpdated+=CrossLayoutUpdated;SizeChanged+=CrossSizeChanged;DpiChanged+=CrossWpfDpiChanged;
        _pilotLog!.Write("cross-screen-ready",new{mode="EXPERIMENTAL explicit opt-in",owner=CrossOwner,
            sizeDip=CrossSize,rect=_pilotRect,workAreas=CrossWork,semantics="natural live WPF DIP size; physical grab offset",
            budget=EarlyRecovery.CheckBudget,repairs=2,deadlineMilliseconds=EarlyRecovery.WindowMilliseconds,tolerancePhysicalPx=2},"REAL WPF OS; USER DRAG PENDING");
    }
    private void CrossTrace(string stage,string reason)
    {
        _pilotLog!.Write("candidate-"+stage,new{owner=CrossOwner,scheduledOwner=_crossRecovery?.Owner,reason,
            requestedPlacementId=_crossRequestedId,acceptedPlacementId=_crossAcceptedId,inFlightPlacementId=_crossInFlightId,
            attempts=_crossRecovery?.Attempts,repairs=_crossRecovery?.Repairs,signals=_crossRecovery?.Signals,
            decidableChecks=_crossRecovery?.DecidableChecks,deferred=_crossRecovery?.Deferred,
            elapsedMilliseconds=_crossRecovery?.ElapsedMilliseconds,checkpoint=_crossLastCheckpoint,
            sizeDip=CrossSize,actualDip=new{ActualWidth,ActualHeight,surfaceWidth=PetSurface.ActualWidth,surfaceHeight=PetSurface.ActualHeight,imageWidth=PetImage.ActualWidth,imageHeight=PetImage.ActualHeight},
            dpi=PilotNative.GetDpiForWindow(PilotHwnd),trusted=_pilotRect});
    }
    private void CrossLayoutUpdated(object? sender,EventArgs e)=>CrossSignal();
    private void CrossSizeChanged(object sender,SizeChangedEventArgs e)=>CrossSignal();
    private void CrossWpfDpiChanged(object sender,DpiChangedEventArgs e)=>CrossSignal();
    private void CrossSignal()
    {
        if(!CrossLive || !_crossInitialized || _crossRecovery?.Active!=true)return;
        // Size/position accommodation precedes Candidate/Legacy; G still reads and
        // verifies the actual legal target, with its original deadline and budgets.
        var dpi=PilotNative.GetDpiForWindow(PilotHwnd)/96d;
        if(!_crossBoundaryPreparing&&!CrossScreenGeometry.Covered(CrossFrozenTarget(new(dpi,dpi)),CrossWork)) {
            var s=CrossCheckpoint();
            if(!s.StackBusy&&s.Matrix&&s.Topology){CrossQueueBoundary();return;}
        }
        _crossRecovery.Observe(); // schedules only; the pure protocol classifies all synchronous depths first
    }
    private RecoveryCheckpoint CrossCheckpoint()
    {
        var owner=CrossOwner;
        bool alive=CrossLive&&PilotNative.IsWindow(PilotHwnd);
        bool matrix=alive&&PilotMapping(out _);
        bool layout=alive&&IsMeasureValid&&IsArrangeValid&&PetSurface.IsMeasureValid&&PetSurface.IsArrangeValid&&PetImage.IsMeasureValid&&PetImage.IsArrangeValid;
        var read=PilotNative.GetWindowRect(PilotHwnd,out var rect);var clientRead=PilotNative.GetClientRect(PilotHwnd,out var client);
        var dpi=PilotNative.GetDpiForWindow(PilotHwnd)/96d;
        bool Near(double a,double b)=>double.IsFinite(a)&&double.IsFinite(b)&&Math.Abs(a-b)<=2;
        // Sizes proves internal live WPF/native coherence, not equality to the
        // previously accepted footprint. Geometry independently detects an overwrite
        // and protected repair restores the natural Sprite target without loosening 2 px.
        bool sizes=matrix&&layout&&read&&clientRead&&Near(ActualWidth*dpi,rect.Right-rect.Left)&&Near(ActualHeight*dpi,rect.Bottom-rect.Top)&&
            Near(ActualWidth*dpi,client.Right-client.Left)&&Near(ActualHeight*dpi,client.Bottom-client.Top)&&
            Near(PetSurface.ActualWidth*dpi,PetSurface.Width*dpi)&&Near(PetSurface.ActualHeight*dpi,PetSurface.Height*dpi)&&
            Near(PetImage.ActualWidth*dpi,PetImage.Width*dpi)&&Near(PetImage.ActualHeight*dpi,PetImage.Height*dpi);
        bool geometry=_crossAcceptedOwner!=owner || _crossExpected==null || read&&CrossScreenGeometry.Near(rect.Physical,_crossExpected.Value);
        bool safe=matrix&&PilotFresh&&CrossScreenGeometry.Covered(CrossFrozenTarget(new(dpi,dpi)),CrossWork);
        return _crossLastCheckpoint=new(owner,alive,matrix,layout,sizes,PilotFresh,geometry,_crossActive,PetSurface.IsMouseCaptured,
            _pilotPlacementDepth,_crossLayoutDepth,_pilotHookDepth,safe,_crossRequestedId,_crossAcceptedId,_crossInFlightId,_crossAcceptedOwner);
    }
    private PhysicalRect CrossFrozenTarget(Coordinates.DpiScale scale)=>
        CrossScreenGeometry.Target(_crossOrigin,new(0,0),CrossSize,scale);

    private void CrossStartRecovery(string reason)
    {
        PilotPhysicsReset();
        if(!CrossLive||!_crossInitialized)return;
        PilotMoveCancel("G recovery: "+reason);
        PilotRestCancel("G recovery: "+reason);
        PilotRandomCancel("G recovery: "+reason);
        PilotMotionCancel("G recovery: "+reason);
        if(_pilotTopologyHold)return; // outer topology hold never creates/renews a G owner
        CrossAbortSeam("generation recovery: "+reason);
        Abort(ref _crossMove);CrossAbortLegacy();_crossMapped=false;
        CrossAbortBoundary();
        _crossRequestedId=_crossAcceptedId=null;_crossAcceptedOwner=null;_crossExpected=null;
        _crossRecovery!.Begin(CrossOwner);
        _crossDeadline?.Stop();var owner=CrossOwner;
        var deadline=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(_crossRecovery.RemainingMilliseconds)};
        _crossDeadline=deadline;
        deadline.Tick+=(_,_)=>{
            // A stopped old delegate can be forcibly delivered. It cannot touch the
            // new owner's timer or protocol before validating both identity and owner.
            if(!CrossLive||owner!=CrossOwner||!ReferenceEquals(deadline,_crossDeadline))return;
            deadline.Stop();_crossRecovery.Expire();CrossSignal();
        };
        deadline.Start();CrossQueueLegacy(owner,0);
        CrossQueueBoundary();
        _pilotLog!.Write("recovery-start",new{reason,owner,frozenOrigin=_crossOrigin,dragging=_crossActive,captured=PetSurface.IsMouseCaptured});
    }
    private void CrossQueueLegacy(RecoveryOwner owner,int retry)
    {
        CrossAbortLegacy();var revision=_crossLegacyRevision;
        var body=new Action(()=> {
            if(revision!=_crossLegacyRevision||!CrossLive||owner!=CrossOwner||_crossRecovery?.Active!=true)return;
            _crossLegacy=null;
            if(_crossRecovery.Accepted) { _crossRecovery.LegacyCompleted();return; }
            if(!CrossPrepareBoundary(owner))return;
            var checkpoint=CrossCheckpoint();
            // Legacy is bounded and independent of Loaded Candidate; never places on a busy stack.
            if(!(checkpoint with{Geometry=true}).Ready) {
                if(retry<2)CrossQueueLegacy(owner,retry+1);
                else CrossTrace("fallback-wait","three ContextIdle checkpoints unavailable; finite Background watchdog retained");
                return;
            }
            if(CrossCommit(owner,"legacy"))_crossRecovery.LegacyCompleted();
            else if(CrossLive&&owner==CrossOwner)PilotStop("Legacy recovery commit rejected at stable checkpoint.");
        });
        _crossLegacyBody=body;
        _crossLegacy=Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,body);
    }
    private bool CrossCommit(RecoveryOwner owner,string kind)
    {
        if(!CrossLive||owner!=CrossOwner)return false;
        var checkpoint=CrossCheckpoint();
        if(!(kind is "repair" or "legacy" ? checkpoint with{Geometry=true}:checkpoint).Ready)return false;
        if(!PilotMapping(out var scale))return false;
        var boundSource=_pilotSource;var hwnd=PilotHwnd;
        var target=CrossFrozenTarget(scale);
        if(_crossActive) {
            if(!PetSurface.IsMouseCaptured || !PilotCursor(out var cursor))return false;
            _crossLatestCursor=cursor;_crossCursorTicks=Stopwatch.GetTimestamp();
            if(_crossDragging)target=CrossScreenGeometry.Target(cursor,_crossGrab,CrossSize,scale);
        }
        var fitted=CrossScreenGeometry.Fit(target,CrossScreenGeometry.Reachable(_pilotRect,CrossWork));
        if(fitted is not {} legal)return false;
        if(_crossDragging&&!CrossScreenGeometry.TranslationSafe(CrossFrozenTarget(scale),legal,CrossWork)) {
            CrossBoundaryRejected("recovery cursor target crosses a WorkArea hole");return false;
        }
        CrossRecordTranslation(target,legal,"recovery target");target=legal;
        if(!CrossScreenGeometry.Covered(target,CrossWork))return false;
        var ok=CrossPlace(target,"early "+kind);
        if(!ok||!CrossLive||owner!=CrossOwner)return false;
        // Runs on an independent Dispatcher callback, after the full native Place stack exits.
        _crossLayoutDepth++;try{UpdateLayout();}finally{_crossLayoutDepth--;}
        var post=CrossCheckpoint();var currentMapping=PilotMapping(out var currentScale);
        if(!post.Ready||!PlacementPublication.CanPublish(owner,post.Owner,
            CrossLive&&ReferenceEquals(boundSource,_pilotSource)&&hwnd==PilotHwnd,scale,currentScale,currentMapping))return false;
        _pilotScale=scale;_crossMapped=true;
        _pilotLog!.Write("mapping-recovered",new{owner,kind,placementId=_crossAcceptedId,rect=_pilotRect,sizeDip=CrossSize,
            dpi=PilotNative.GetDpiForWindow(PilotHwnd),matrix=_pilotSource!.CompositionTarget!.TransformToDevice,
            wmToMappingRecoveredMilliseconds=_crossTransitionTicks==0?(double?)null:Stopwatch.GetElapsedTime(_crossTransitionTicks).TotalMilliseconds,
            mappingUnavailable=_crossMappingUnavailable,cursorLagMilliseconds=_crossCursorTicks==0?(double?)null:Stopwatch.GetElapsedTime(_crossCursorTicks).TotalMilliseconds,
            capture=PetSurface.IsMouseCaptured},"REAL WPF OS ACCEPTED; NO DWM FENCE");
        return true;
    }
    private bool CrossPlace(PhysicalRect target,string reason)
    {
        if(!CrossLive||_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0||!PilotFresh||
            !PilotMapping(out var scale)||!CrossScreenGeometry.Covered(target,CrossWork))return false;
        var owner=CrossOwner;var boundSource=_pilotSource;var hwnd=PilotHwnd;
        var trustedBefore=_pilotRect;
        var id=++_crossPlacementId;_crossRequestedId=_crossInFlightId=id;
        _crossOrigin=new(target.Left,target.Top);_pilotPlacementDepth++;
        bool sampled=_crossRecovery?.Active==true||(reason!="physical drag"&&reason!="fall-step")||id%32==0;
        if(sampled)_pilotLog!.Write("placement-requested",new{owner,placementId=id,target,reason});
        try {
            if(sampled)_pilotLog!.Write("placement-native-enter",new{owner,placementId=id,hwnd=$"0x{hwnd:X}"});
            var ok=PilotNative.Place(hwnd,target);var error=ok?0:Marshal.GetLastPInvokeError();
            var read=PilotNative.GetWindowRect(hwnd,out var actual);
            var current=CrossLive&&owner==CrossOwner&&ReferenceEquals(boundSource,_pilotSource)&&PilotHwnd==hwnd;
            var accepted=ok&&read&&current&&PilotFresh&&PilotMapping(out var post)&&post==scale&&
                CrossScreenGeometry.Near(actual.Physical,target)&&CrossScreenGeometry.Covered(actual.Physical,CrossWork);
            if(accepted&&_seamHandoff&&_crossActive)
                accepted=CrossScreenGeometry.TranslationSafe(trustedBefore,actual.Physical,CrossWork);
            if(accepted) {
                _crossAcceptedId=id;_crossAcceptedOwner=owner;_crossExpected=target;_pilotRect=actual.Physical;
                _crossOrigin=new(target.Left,target.Top);_pilotScale=scale;
            }
            if(sampled||!accepted)_pilotLog!.Write("placement-completed",new{owner,currentOwner=CrossOwner,placementId=id,target,
                actual=read?(PhysicalRect?)actual.Physical:null,error,accepted,reason,cursorLagMilliseconds=_crossCursorTicks==0?(double?)null:Stopwatch.GetElapsedTime(_crossCursorTicks).TotalMilliseconds},accepted?"ACCEPTED":current?"REJECTED":"SUPERSEDED");
            if(!ok||!read) {PilotStop("Native placement/RECT failed: "+error);return false;}
            if(!current)return CrossLive; // DPI reentry owns a new generation; the old completion cannot publish.
            return accepted;
        }
        finally {_pilotPlacementDepth--;if(_crossInFlightId==id)_crossInFlightId=null;CrossSignal();}
    }
    private void CrossDpiChanged(nint hwnd,nint wParam,nint lParam)
    {
        PilotPhysicsReset();
        if(!CrossLive)return;
        PilotClickCancel("DPI generation change");
        if(_seamHandoff&&_crossActive&&_pilotRect.IsValid) {
            _seamDpiFraction=_seamGrabFraction;
            _seamDpiReanchor=true;
        }
        // A controlled OS relocation outside our Place stack establishes a new
        // observed physical origin. Own in-flight requests already froze their target.
        if(!_crossActive&&_pilotPlacementDepth==0&&PilotNative.GetWindowRect(hwnd,out var native))
            _crossOrigin=new(native.Left,native.Top);
        _pilotGeneration++;_crossTransitionTicks=Stopwatch.GetTimestamp();
        var packed=(long)wParam;var x=(uint)(packed&0xffff);var y=(uint)((packed>>16)&0xffff);
        PilotNative.Rect? suggested=lParam==0?null:Marshal.PtrToStructure<PilotNative.Rect>(lParam);
        _pilotLog!.Write("dpi-transition",new{owner=CrossOwner,hwnd=$"0x{hwnd:X}",previousDpi=_pilotScale.X*96,newDpiX=x,newDpiY=y,
            suggested=suggested?.Physical,dragging=_crossActive,captured=PetSurface.IsMouseCaptured,
            semantics="WPF consumes suggested sizing; deferred live layout determines natural physical footprint"},"REAL WM; NOT HANDLED BY PILOT");
        if(x==0||y==0||x!=y||suggested==null||!suggested.Value.Physical.IsValid) {PilotStop("Invalid DPI message; no trusted suggested geometry.");return;}
        CrossStartRecovery("WM_DPICHANGED");
    }
    private void CrossApplyLayout(PilotScaleAnchor? scaleAnchor=null)
    {
        if(!CrossLive)return;
        if(_crossBoundaryPreparing)return; // same recovery owner, no deadline/repair renewal
        if(!_crossInitialized) {
            // LoadPmv2Pilot places after the entire ApplyPetLayout property stack exits.
            return;
        }
        // A user scale keeps the arranged foot contact. Only the frozen target
        // origin changes; the existing G owner still performs the single commit,
        // its own Fit/Covered/TranslationSafe proof and its native readback.
        if(scaleAnchor is {} anchor && PilotMapping(out var scale) && scale==_pilotScale) {
            var width=Math.Round(CrossSize.X*scale.X);var height=Math.Round(CrossSize.Y*scale.Y);
            var x=Math.Round(anchor.ContactX-PilotVisualFootCenterX*scale.X);
            var y=Math.Round(anchor.Bottom-height);
            _crossOrigin=new(x,y);
            _pilotLog?.Write("scale-origin",new{owner=CrossOwner,contactX=anchor.ContactX,bottom=anchor.Bottom,
                anchorLocal=_pilotVisualAnchorLocal,scale,width,height,origin=_crossOrigin,grounded=anchor.Grounded,
                semantics="arranged foot contact; G remains the only committer"},"PENDING EXISTING WRITER");
        }
        // Property writes may have synchronously entered WPF native sizing. Never place here.
        CrossCancelGesture("dynamic Sprite layout");CrossStartRecovery("dynamic Sprite layout");
    }
    private void CrossMouseDown(MouseButtonEventArgs e)
    {
        if(_pilotPhysicsHold!=null||PilotPhysicsBusy){PilotMouseDown(e);return;}
        e.Handled=true;CrossCancelGesture("new gesture");
        CrossMouseAdmit();
    }
    private void CrossMouseAdmit(PhysicalPoint? heldCursor=null,bool confirmed=false,double? threshold=null)
    {
        if(!CrossLive)return;
        // Never bless a late native/layout overwrite when the old gesture is revoked,
        // even if the following cursor/capture API fails. A distinct restoration owner
        // keeps the last trusted origin and the original natural Sprite dimensions.
        if(!_crossMapped||!PilotFresh||!PilotMapping(out var scale)||!PilotNative.GetWindowRect(PilotHwnd,out var rect)||
            !CrossScreenGeometry.NaturalFootprint(rect.Physical,CrossSize,scale)||!CrossScreenGeometry.Near(rect.Physical,_pilotRect)||
            !CrossCheckpoint().Ready) {CrossStartRecovery("new Down refused pending natural geometry restoration");return;}
        // Preserve ordinary Down's geometry-before-cursor guard order.
        PhysicalPoint cursor;
        if(heldCursor is {} physical)cursor=physical;
        else if(!PilotCursor(out cursor))return;
        if(!cursor.IsFinite||!rect.Physical.ContainsClosed(cursor)||!CrossScreenGeometry.Covered(rect.Physical,CrossWork))return;
        var captured=PetSurface.CaptureMouse();_pilotGesture++;
        if(!captured||!PetSurface.IsMouseCaptured){_pilotLog!.Write("gesture-down",new{owner=CrossOwner,captured,accepted=false,rect=rect.Physical},"BLOCKED");return;}
        _crossActive=captured&&PetSurface.IsMouseCaptured;_crossDragging=false;_crossDown=cursor;
        _crossGrab=new(cursor.X-rect.Left,cursor.Y-rect.Top);_crossOrigin=new(rect.Left,rect.Top);_pilotRect=rect.Physical;
        if(_seamHandoff)_seamGrabFraction=new(_crossGrab.X/rect.Physical.Width,_crossGrab.Y/rect.Physical.Height);
        _pilotScale=scale;_crossMapped=true;_crossLatestCursor=cursor;
        _pilotLog!.Write("gesture-down",new{owner=CrossOwner,captured,accepted=_crossActive,cursor,grab=_crossGrab,rect=rect.Physical,thresholdDip=_settings.Interaction.DragThresholdDip});
        if(!_crossActive)return;
        if(!(Focus()&&ReferenceEquals(Keyboard.Focus(this),this)))CrossCancelGesture("Esc keyboard focus unavailable");
        else {
            PilotClickBegin();
            if(confirmed){_crossDragging=true;_pilotGestureDragThresholdDip=threshold!.Value;PilotClickDragWins();PilotDragVisualEnsure(cursor);}
        }
    }
    private void CrossMouseMove(MouseEventArgs e)
    {
        if(_pilotPhysicsHold!=null){PilotMouseMove(e);return;}
        e.Handled=true;if(!_crossActive)return;
        if(!PilotInputMoveAllowed(e,true))return;
        if(!PilotCursor(out var cursor)){CrossCancelGesture("physical cursor API failure");return;}
        CrossMovePhysical(cursor);
    }
    private void CrossMovePhysical(PhysicalPoint cursor)
    {
        if(_pilotPhysicsHold!=null){PilotPhysicsHoldMove(cursor);return;}
        if(!CrossLive||!_crossActive||!PetSurface.IsMouseCaptured||!cursor.IsFinite)return;
        _crossMoves++;_crossLatestCursor=cursor;_crossCursorTicks=Stopwatch.GetTimestamp();
        // Every real cross drag input feeds the seat classifier before the deferred
        // placement callback or a wall clamp can drop/absorb it.
        PilotSeatInputSample(cursor,_crossCursorTicks);
        if(!PilotMapping(out var scale)||!_crossMapped) {_crossMappingUnavailable++;CrossSignal();return;}
        if(!_crossDragging) {
            if(!PhysicalVector.TryBetween(_crossDown,cursor,out var delta)||!CrossScreenGeometry.Threshold(delta,scale,PilotGestureDragThresholdDip))return;
            _crossDragging=true;
            PilotClickDragWins();
            PilotDragVisualEnsure(cursor);
        }
        if(_crossMove?.Status==DispatcherOperationStatus.Pending)return;
        var owner=CrossOwner;
        _crossMove=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
            if(!CrossLive||owner!=CrossOwner||!_crossActive||!PetSurface.IsMouseCaptured)return;
            _crossMove=null;
            if(!_crossMapped||!PilotMapping(out var current)) {_crossMappingUnavailable++;CrossSignal();return;}
            if(_crossLatestCursor is not {} latest)return;
            PilotDragUpdateFacing(latest);
            if(_seamHandoff) {
                if(_seamPlan!=null)return;
                if(!PilotCursor(out latest)){CrossCancelGesture("seam fresh cursor unavailable");return;}
                if(CrossTryArmSeam(latest,current))return;
            }
            var target=CrossScreenGeometry.Target(latest,_crossGrab,CrossSize,current);
            var fitted=CrossScreenGeometry.Fit(target,CrossScreenGeometry.Reachable(_pilotRect,CrossWork));
            if(fitted is not {} legal){CrossBoundaryRejected("no reachable legal drag target");return;}
            if(!CrossScreenGeometry.TranslationSafe(_pilotRect,legal,CrossWork)) {
                CrossBoundaryRejected("drag sweep crosses a WorkArea hole or narrow corridor");return;
            }
            CrossRecordTranslation(target,legal,"physical drag");target=legal;
            var accepted=CrossPlace(target,"physical drag");
            if(!accepted&&CrossLive)PilotStop("Stable physical drag native postcheck rejected.");
            else if(accepted&&owner==CrossOwner) {
                // A legal idle fallback may retry within the same capture once
                // movement has made the prospective drag corridor admissible.
                if(_pilotDragVisualFallback)PilotDragVisualEnsure(latest);
                _crossAcceptedMoves++;
                var acceptedAt=Stopwatch.GetTimestamp();
                if(target==CrossScreenGeometry.Target(latest,_crossGrab,CrossSize,current))
                    PilotPhysicsAcceptedSample(latest,acceptedAt);
                else PilotPhysicsResetSamples("Fit clamped accepted drag target"); // not trusted release velocity
            }
        }));
    }
    private void CrossCancelGesture(string reason)=>CrossCancelGestureCore(reason,false);
    private void CrossCancelGestureCore(string reason,bool keepAttachment)
    {
        PilotPhysicsHoldClear();
        PilotInputStopReleaseWait();
        PilotSeatReset();
        PilotPhysicsReset();
        PilotDragVisualEnd(reason);
        PilotReleaseFloatCancel(reason);
        PilotClickCancelCore(reason,keepAttachment);
        CrossAbortSeam(reason);
        _seamDpiReanchor=false;
        var wasActive=_crossActive;var needsRecovery=CrossLive&&_crossInitialized&&(!_crossMapped||_crossRecovery?.Active==true||_crossEffectiveScale<1);
        _crossActive=_crossDragging=false;_crossLatestCursor=null;_pilotGesture++;
        _crossGrab=default;_seamGrabFraction=default;
        Abort(ref _crossMove);CrossAbortLegacy();_crossRecovery?.Cancel(reason);_crossDeadline?.Stop();
        CrossAbortBoundary();
        _crossRequestedId=_crossAcceptedId=null;_crossAcceptedOwner=null;_crossExpected=null;
        if(PetSurface.IsMouseCaptured)PetSurface.ReleaseMouseCapture(); // owner revoked before synchronous loss event
        if(wasActive)_pilotLog!.Write("gesture-cancel",new{reason,owner=CrossOwner,frozenOrigin=_crossOrigin,releaseOwner="new revision; no future cursor reads"});
        if(needsRecovery&&reason is not ("new gesture" or "dynamic Sprite layout"))CrossStartRecovery("release owner: "+reason);
        if(PilotVisualBusy)PilotVisualQueue();
    }
    private void CrossWatch()
    {
        if(!_crossScreen||_pilotClosing||_pilotStopped||!_pilotReady||!_crossInitialized)return;
        if(_pilotSource is null or {IsDisposed:true}||!PilotNative.IsWindow(PilotHwnd)) {
            PilotStop("Cross-screen source/HWND no longer live.");return;
        }
        if(_pilotTopologyHold){PilotMotionWatchTopologyHold();return;}
        if(_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0)return;
        if(_crossRecovery?.Active==true) {CrossSignal();return;}
        // Daily-owner cancellation can switch the clip in this same watch tick.
        // Settle its deferred layout before testing coherence; end the scope first
        // so the unchanged checkpoint still requires zero synchronous layout depth.
        using(PilotLayoutScope()) {UpdateLayout();}
        var s=CrossCheckpoint();
        var nativeRead=PilotNative.GetWindowRect(PilotHwnd,out var rect);
        var nativeNear=nativeRead&&CrossScreenGeometry.Near(rect.Physical,_pilotRect);
        if(!s.Ready||!nativeRead||!nativeNear) {
            // Failure-only snapshot: identify the divergent gate without adding
            // disk writes to the accepted physical drag path or changing G.
            try { _pilotLog!.Write("stable-late-mismatch",new{owner=CrossOwner,checkpoint=s,
                nativeRead,native=nativeRead?(PhysicalRect?)rect.Physical:null,nativeNear,
                trusted=_pilotRect,expected=_crossExpected,source=_pilotSourceToken,hwnd=(long)PilotHwnd,
                sizeDip=CrossSize,actualDip=new{ActualWidth,ActualHeight,
                    surfaceWidth=PetSurface.ActualWidth,surfaceHeight=PetSurface.ActualHeight,
                    imageWidth=PetImage.ActualWidth,imageHeight=PetImage.ActualHeight},
                dpi=PilotNative.GetDpiForWindow(PilotHwnd)},"FAIL-CLOSED DIAGNOSTIC"); }
            catch(Exception) { /* Diagnostics must never prevent the existing safety stop. */ }
            PilotStop("Stable late source/layout/native overwrite outside bounded recovery window.");
        }
    }
    private void CrossClosing(object? sender,CancelEventArgs e)
    {
        PilotPhysicsReset();
        _pilotClosing=true;_pilotGeneration++;CrossCancelGesture("Closing");_crossMapped=false;
    }
    private void CrossClosed()
    {
        CrossAbortSeam("Closed");
        _crossDeadline?.Stop();_crossRecovery?.Cancel("Closed");Abort(ref _crossMove);CrossAbortLegacy();CrossAbortBoundary();
        LayoutUpdated-=CrossLayoutUpdated;SizeChanged-=CrossSizeChanged;DpiChanged-=CrossWpfDpiChanged;Closing-=CrossClosing;
        _pilotLog?.Write("cross-screen-metrics",new{moves=_crossMoves,mappingUnavailable=_crossMappingUnavailable,
            ordinaryDragAccepted=_crossAcceptedMoves,placementId=_crossPlacementId,settings="memory only",hardwareDrag="PENDING"});
        if(_seamHandoff)_pilotLog?.Write("seam-metrics",new{armed=_seamArmed,committed=_seamCommitted,rejected=_seamRejected,
            placements=_seamPlacements,maximumMilliseconds=_seamMaximumMilliseconds,maximumSteps=CrossScreenGeometry.SeamMaximumSteps,
            cursorTrackingLagPx=(double?)null,lagEvidence="N/A: no DWM/cursor fence",perMouseMoveDiskWrites=0});
    }
    private IDisposable? PilotLayoutScope()=>_crossScreen&&_pilotLog!=null ? new CrossLayoutScope(this) : null;
    private sealed class CrossLayoutScope : IDisposable
    {
        private readonly MainWindow window;
        internal CrossLayoutScope(MainWindow window){this.window=window;window._crossLayoutDepth++;}
        public void Dispose(){window._crossLayoutDepth--;window.CrossSignal();}
    }
}
