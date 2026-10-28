using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using DpiHwndProbe;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private bool _seamHandoff;
    private CrossScreenGeometry.SeamPlan? _seamPlan;
    private DispatcherOperation? _seamOperation;
    private DispatcherTimer? _seamTimer;
    private Action? _seamBody;
    private long _seamRevision,_seamStarted,_seamTopologyVersion;
    private long _seamArmed,_seamCommitted,_seamRejected,_seamPlacements;
    private double _seamMaximumMilliseconds,_seamStartingScale;
    private int _seamStep,_seamDeferrals;
    private LocalDipVector _seamDesired;
    private RecoveryOwner _seamOwner;
    private PhysicalRect _seamOriginal;
    private bool _seamDpiReanchor;
    private PhysicalVector _seamDpiFraction,_seamGrabFraction;

    // Read-only target DPI prediction. This can never replace GetDpiForWindow or
    // the WPF matrix gate, and never publishes an Accepted placement/mapping.
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref PilotNative.Rect rect,uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor,int kind,out uint x,out uint y);
    private bool CrossTargetDpi(PhysicalRect area,out DpiScale scale)
    {
        scale=default;
        if(area.Left<int.MinValue||area.Top<int.MinValue||area.Right>int.MaxValue||area.Bottom>int.MaxValue)return false;
        var rect=new PilotNative.Rect{Left=(int)area.Left,Top=(int)area.Top,Right=(int)area.Right,Bottom=(int)area.Bottom};
        var monitor=MonitorFromRect(ref rect,0);
        if(monitor==0||GetDpiForMonitor(monitor,0,out var x,out var y)!=0||x==0||x!=y)return false;
        scale=new(x/96d,y/96d);return scale.IsValid;
    }
    private void CrossSeamTrace(string stage,string reason,PhysicalPoint? cursor=null,PhysicalRect? request=null)
    {
        var dpi=PilotNative.GetDpiForWindow(PilotHwnd)/96d;
        var desired=new LocalDipVector(CrossSize.X/_crossEffectiveScale,CrossSize.Y/_crossEffectiveScale);
        _pilotLog?.Write("seam-"+stage,new{owner=CrossOwner,scheduledOwner=_seamOwner,revision=_seamRevision,
            topologyVersion=_pilotTopology?.State.LastKnownGood?.Version,realDpi=PilotNative.GetDpiForWindow(PilotHwnd),
            cursor,original=_seamOriginal.IsValid?(PhysicalRect?)_seamOriginal:null,trusted=_pilotRect,requested=request,final=_pilotRect,plan=_seamPlan,step=_seamStep,
            desiredHeightDip=_petHeight,desiredSizeDip=desired,effectiveScale=_crossEffectiveScale,effectiveSizeDip=CrossSize,
            desiredPhysicalFootprint=new PhysicalVector(desired.X*dpi,desired.Y*dpi),effectivePhysicalFootprint=new PhysicalVector(CrossSize.X*dpi,CrossSize.Y*dpi),
            workAreas=CrossWork,unionCheck="full slab coverage",pathCheck="trusted to intermediate resize to anchored target sweep",
            elapsedMilliseconds=_seamStarted==0?0:Stopwatch.GetElapsedTime(_seamStarted).TotalMilliseconds,reason});
    }
    private void CrossAbortSeam(string reason)
    {
        _seamRevision++;Abort(ref _seamOperation);_seamTimer?.Stop();_seamTimer=null;
        if(_seamPlan!=null) {
            _seamMaximumMilliseconds=Math.Max(_seamMaximumMilliseconds,Stopwatch.GetElapsedTime(_seamStarted).TotalMilliseconds);
            CrossSeamTrace("cancelled",reason);
        }
        _seamPlan=null;
    }
    private void CrossRejectSeam(string reason,PhysicalPoint? cursor=null,PhysicalRect? requested=null)
    {
        _seamRejected++;CrossSeamTrace("rejected",reason,cursor,requested);CrossAbortSeam(reason);
        CrossBoundaryRejected(reason); // recoverable size boundary; retains legal position/re-grab
    }
    private bool CrossTryArmSeam(PhysicalPoint cursor,DpiScale scale)
    {
        if(!_seamHandoff||_seamPlan!=null)return _seamPlan!=null;
        var target=CrossScreenGeometry.SeamTarget(_pilotRect,cursor,_crossGrab,CrossWork);
        if(target is not {} area)return false;
        _seamOriginal=_pilotRect;_seamOwner=CrossOwner;
        if(!CrossTargetDpi(area,out var predicted)){CrossRejectSeam("TARGET_DPI_UNAVAILABLE",cursor);return true;}
        var desired=new LocalDipVector(CrossSize.X/_crossEffectiveScale,CrossSize.Y/_crossEffectiveScale);
        var plan=CrossScreenGeometry.PlanSeam(_pilotRect,cursor,_crossGrab,desired,scale,_crossEffectiveScale,predicted,CrossWork);
        if(!plan.Ready) {
            if(plan.Reason is "NOT_NEEDED" or "NO_SEAM_INTENT")return false;
            CrossRejectSeam(plan.Reason,cursor);return true;
        }
        // Do not suspend, cancel, renew, or outlive G's independent 500ms/128 gate.
        // A still-active recovery generation remains its sole owner until settled.
        if(_crossRecovery?.Active==true||!CrossCheckpoint().Ready){CrossRejectSeam("RECOVERY_IN_PROGRESS",cursor);return true;}
        _seamPlan=plan;_seamOwner=CrossOwner;_seamDesired=desired;_seamStartingScale=_crossEffectiveScale;
        _seamStarted=Stopwatch.GetTimestamp();_seamTopologyVersion=_pilotTopology!.State.LastKnownGood!.Version;_seamStep=_seamDeferrals=0;_seamArmed++;
        CrossSeamTrace("armed","target DPI is prediction only; actual mapping required for every step",cursor);
        CrossQueueSeamStep();return true;
    }
    private void CrossQueueSeamStep()
    {
        Abort(ref _seamOperation);var owner=CrossOwner;var source=_pilotSource;var hwnd=PilotHwnd;var revision=++_seamRevision;
        var body=new Action(()=>{
            // Reject old delegates before touching the new scheduler, cursor or scale.
            if(revision!=_seamRevision||owner!=CrossOwner||!CrossLive||!ReferenceEquals(source,_pilotSource)||hwnd!=PilotHwnd)return;
            if(_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0) {
                // A real nested Dispatcher dispatch would otherwise consume the only
                // queued operation and leave a captured gesture waiting indefinitely.
                // Forced delivery of a still-pending body must leave that body intact.
                if(_seamPlan!=null&&_seamOperation?.Status==DispatcherOperationStatus.Executing) {
                    _seamRevision++;_seamOperation=null;
                    if(++_seamDeferrals<=2&&Stopwatch.GetElapsedTime(_seamStarted).TotalMilliseconds<=300)CrossQueueSeamStep();
                    else CrossAbortSeam("BUSY_STACK_DEFER_EXHAUSTED");
                }
                return; // never synchronously resize/place on any busy stack
            }
            _seamRevision++;_seamOperation=null;
            if(_seamPlan==null||_seamOwner!=owner||!_crossActive||!PetSurface.IsMouseCaptured)return;
            if(!PilotFresh||_pilotTopology!.State.LastKnownGood!.Version!=_seamTopologyVersion){CrossRejectSeam("TOPOLOGY_CHANGED");return;}
            if(Stopwatch.GetElapsedTime(_seamStarted).TotalMilliseconds>300||_seamStep>=CrossScreenGeometry.SeamMaximumSteps){CrossRejectSeam("PREPARATION_BUDGET");return;}
            if(!CrossCheckpoint().Ready||!PilotMapping(out var scale)){CrossRejectSeam("STALE_OWNER");return;}
            if(!PilotCursor(out var cursor)){CrossRejectSeam("CURSOR_UNAVAILABLE");return;}
            var old=_pilotRect;var desired=new LocalDipVector(CrossSize.X/_crossEffectiveScale,CrossSize.Y/_crossEffectiveScale);
            if(Math.Abs(desired.X-_seamDesired.X)>1e-7||Math.Abs(desired.Y-_seamDesired.Y)>1e-7){CrossRejectSeam("LAYOUT_CHANGED",cursor);return;}
            var anchor=new PhysicalPoint(old.Left+_crossGrab.X,old.Top+_crossGrab.Y);
            var fraction=_seamGrabFraction;
            if(CrossScreenGeometry.SeamGrab(fraction,CrossSize,scale)==null){CrossRejectSeam("INVALID_GRAB_FRACTION",cursor);return;}
            var area=_seamPlan.TargetWork;
            // A reversal cancels preparation and keeps its absolute cap. Next native
            // input resumes ordinary drag; no old operation runs under a new owner.
            if((area.Left>=_seamOriginal.Right&&cursor.X<anchor.X)||(area.Right<=_seamOriginal.Left&&cursor.X>anchor.X)||
                (area.Top>=_seamOriginal.Bottom&&cursor.Y<anchor.Y)||(area.Bottom<=_seamOriginal.Top&&cursor.Y>anchor.Y)) {
                CrossAbortSeam("REVERSED_INTENT");return;
            }
            var next=_seamStartingScale+(_seamPlan.Effective-_seamStartingScale)*(_seamStep+1)/_seamPlan.Steps;
            next=Math.Min(next,_crossEffectiveScale);
            var target=CrossScreenGeometry.Anchored(cursor,fraction,desired,scale,next);
            // Width/Height setters initially contract at the old native origin. This
            // intermediate rectangle and the following move both need a real sweep.
            var intermediate=new PhysicalRect(old.Left,old.Top,old.Left+target.Width,old.Top+target.Height);
            var edge=Math.Max(Math.Max(Math.Abs(target.Left-old.Left),Math.Abs(target.Right-old.Right)),
                Math.Max(Math.Abs(target.Top-old.Top),Math.Abs(target.Bottom-old.Bottom)));
            if(edge>CrossScreenGeometry.SeamEdgeStep+1||!CrossScreenGeometry.TranslationSafe(old,intermediate,CrossWork)||
                !CrossScreenGeometry.TranslationSafe(old,target,CrossWork)){CrossRejectSeam("NEEDS_UNSAFE_GRAB_SHIFT",cursor,target);return;}
            CrossSeamTrace("prepared","verified current-DPI contraction and continuous path before layout",cursor,target);
            var previous=_crossEffectiveScale;_crossBoundaryPreparing=true;
            try {
                _crossEffectiveScale=next;ApplyPetLayout();
                _crossLayoutDepth++;try{UpdateLayout();}finally{_crossLayoutDepth--;}
            } finally {_crossBoundaryPreparing=false;}
            if(!CrossLive||owner!=CrossOwner||!ReferenceEquals(source,_pilotSource)||hwnd!=PilotHwnd)return;
            if(!PilotMapping(out var current)||current!=scale||!PilotFresh||
                _pilotTopology!.State.LastKnownGood!.Version!=_seamTopologyVersion||!PetSurface.IsMouseCaptured||!_crossActive){CrossRejectSeam("STALE_OWNER");return;}
            if(!PilotNative.GetWindowRect(hwnd,out var actual)){PilotStop("Seam intermediate native RECT read failed.");return;}
            var state=CrossCheckpoint();
            if(!state.Alive||!state.Matrix||!state.Layout||!state.Sizes||!state.Decidable||
                !CrossScreenGeometry.NaturalFootprint(actual.Physical,CrossSize,scale)||
                !CrossScreenGeometry.TranslationSafe(old,actual.Physical,CrossWork)||
                !CrossScreenGeometry.TranslationSafe(actual.Physical,target,CrossWork)) {
                PilotStop("Seam WPF/native intermediate resize violated verified footprint.");return;
            }
            // Rebase the same normalized local point to the new live Sprite footprint.
            _crossGrab=new(fraction.X*target.Width,fraction.Y*target.Height);
            if(!CrossPlace(target,"seam preparation")&&CrossLive){PilotStop("Seam native placement postcheck failed.");return;}
            if(owner!=CrossOwner||!CrossLive||_seamPlan==null)return; // genuine native DPI reentry belongs to G
            _seamStep++;_seamPlacements++;
            CrossSeamTrace("cap-changed","absolute desired cap; previous="+previous,cursor,target);
            if(_seamStep==_seamPlan.Steps) {
                _seamCommitted++;_seamMaximumMilliseconds=Math.Max(_seamMaximumMilliseconds,Stopwatch.GetElapsedTime(_seamStarted).TotalMilliseconds);
                CrossSeamTrace("committed","next physical input uses ordinary CrossPlace and real DPI generation",cursor,target);
                CrossAbortSeam("completed");return;
            }
            var timer=new DispatcherTimer(DispatcherPriority.Loaded){Interval=TimeSpan.FromMilliseconds(16)};
            _seamTimer=timer;var ticket=_seamRevision;
            timer.Tick+=(_,_)=>{
                timer.Stop();
                if(ticket!=_seamRevision||owner!=CrossOwner||!CrossLive||!ReferenceEquals(timer,_seamTimer))return;
                _seamTimer=null;CrossQueueSeamStep();
            };timer.Start();
        });
        _seamBody=body;_seamOperation=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,body);
    }

    private void CrossQueueBoundary()
    {
        if(_crossBoundary?.Status==DispatcherOperationStatus.Pending||_crossBoundaryPreparing)return;
        var owner=CrossOwner;var revision=++_crossBoundaryRevision;
        var body=new Action(()=>{
            if(revision!=_crossBoundaryRevision||!CrossLive||owner!=CrossOwner||_crossRecovery?.Active!=true)return;
            _crossBoundaryRevision++;
            _crossBoundary=null;
            CrossPrepareBoundary(owner);
            if(CrossLive&&owner==CrossOwner)_crossRecovery.Observe();
        });
        _crossBoundaryBody=body;
        _crossBoundary=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,body);
    }

    private bool CrossPrepareBoundary(RecoveryOwner owner)
    {
        if(!CrossLive||owner!=CrossOwner||_crossBoundaryPreparing)return false;
        var s=CrossCheckpoint();
        if(!s.Alive||!s.Matrix||!s.Layout||!s.Sizes||!s.Topology||!s.Decidable||s.Dragging&&!s.Captured||
            !PilotMapping(out var scale))return false;
        if(_seamHandoff&&_seamDpiReanchor&&_crossActive) {
            if(!PilotCursor(out var cursor)){CrossBoundaryRejected("CURSOR_UNAVAILABLE after real DPI generation");return false;}
            if(CrossScreenGeometry.SeamGrab(_seamDpiFraction,CrossSize,scale) is not {} grab){CrossBoundaryRejected("INVALID_GRAB_FRACTION after real DPI generation");return false;}
            var anchored=CrossScreenGeometry.Target(cursor,grab,CrossSize,scale);
            if(!CrossScreenGeometry.TranslationSafe(_pilotRect,anchored,CrossWork)) {
                // No ordinary size boundary becomes a fatal repair checkpoint. The
                // distinct release owner retains the last trusted physical origin.
                CrossBoundaryRejected("NO_FEASIBLE_CORRIDOR after actual DPI reanchor");return false;
            }
            _crossGrab=grab;_crossOrigin=new(anchored.Left,anchored.Top);_seamDpiReanchor=false;
            CrossSeamTrace("dpi-reanchored","new real DPI/matrix only; normalized local grab recomputed",cursor,anchored);
        }
        var work=CrossScreenGeometry.Reachable(_pilotRect,CrossWork);
        var requested=CrossFrozenTarget(scale);
        var desired=new LocalDipVector(CrossSize.X/_crossEffectiveScale,CrossSize.Y/_crossEffectiveScale);
        var natural=CrossScreenGeometry.Target(_crossOrigin,new(0,0),desired,scale);
        var naturalFit=CrossScreenGeometry.Fit(natural,work);
        double effective=_crossEffectiveScale;
        if(naturalFit==null)effective=CrossScreenGeometry.Limit(desired,scale,work);
        else if(effective<1&&!_crossActive&&CrossScreenGeometry.Fit(natural with{Right=natural.Right+16,Bottom=natural.Bottom+16},work)!=null)
            effective=1; // restore only outside a gesture, with 16 physical px spare room
        if(!double.IsFinite(effective)||effective<=0||effective>1) {
            PilotStop("Boundary accommodation has no trusted reachable legal footprint.");return false;
        }
        // A cap is absolute with respect to desired dimensions. Active reversals never
        // enlarge; release/new Down or wheel/layout recovery can restore with hysteresis.
        if(_crossActive)effective=Math.Min(effective,_crossEffectiveScale);
        var source=_pilotSource;var hwnd=PilotHwnd;
        if(Math.Abs(effective-_crossEffectiveScale)>1e-10) {
            if(_seamHandoff&&_crossActive) {
                CrossBoundaryRejected("NEEDS_UNSAFE_GRAB_SHIFT: unprepared active boundary cap");return false;
            }
            var previous=_crossEffectiveScale;_crossEffectiveScale=effective;
            _crossBoundaryPreparing=true;
            try {
                ApplyPetLayout();
                _crossLayoutDepth++;try{UpdateLayout();}finally{_crossLayoutDepth--;}
            } finally {_crossBoundaryPreparing=false;}
            if(!CrossLive||owner!=CrossOwner||!ReferenceEquals(source,_pilotSource)||hwnd!=PilotHwnd||
                !PilotMapping(out var after)||after!=scale)return false;
            var post=CrossCheckpoint();
            if(!post.Alive||!post.Matrix||!post.Layout||!post.Sizes||!post.Topology||!post.Decidable)return false;
            _pilotLog!.Write(effective<previous?"effective-scale-limited":"effective-scale-restored",new{
                owner,desiredHeightDip=_petHeight,desiredSizeDip=desired,effectiveSizeDip=CrossSize,
                previous,effective,workAreas=work,reason="absolute desired footprint; release-only restoration with 16px headroom",
                requestedPlacementId=_crossRequestedId,acceptedPlacementId=_crossAcceptedId,inFlightPlacementId=_crossInFlightId});
        }
        var frozen=CrossFrozenTarget(scale);
        var fitted=CrossScreenGeometry.Fit(frozen,work);
        if(fitted is not {} legal) {
            PilotStop("Boundary accommodation could not verify a legal frozen target.");return false;
        }
        if(legal!=frozen) {
            _pilotLog!.Write("oversized-target-observed",new{owner,requested,frozen,desiredSizeDip=desired,
                effectiveSizeDip=CrossSize,desiredHeightDip=_petHeight,effectiveScale=_crossEffectiveScale,workAreas=work,
                validTarget=legal,trusted=_pilotRect,requestedPlacementId=_crossRequestedId,acceptedPlacementId=_crossAcceptedId,
                inFlightPlacementId=_crossInFlightId,reason="position outside full union; not necessarily oversized dimensions"});
            CrossRecordTranslation(frozen,legal,"frozen recovery target");
            _crossOrigin=new(legal.Left,legal.Top); // proposed legal origin, NOT Accepted RECT
            if(_crossActive&&Math.Max(Math.Abs(legal.Left-frozen.Left),Math.Abs(legal.Top-frozen.Top))>64) {
                CrossBoundaryRejected("large boundary translation; release capture at legal frozen target");return false;
            }
        }
        return true;
    }

    private void CrossRecordTranslation(PhysicalRect requested,PhysicalRect legal,string reason)
    {
        if(legal==requested){_crossTranslated=false;return;}
        if(!_crossTranslated) _pilotLog!.Write("workarea-translation-applied",new{owner=CrossOwner,reason,
            requested,validTarget=legal,offset=new PhysicalVector(legal.Left-requested.Left,legal.Top-requested.Top),
            desiredHeightDip=_petHeight,effectiveScale=_crossEffectiveScale,effectiveSizeDip=CrossSize,workAreas=CrossWork,
            requestedPlacementId=_crossRequestedId,acceptedPlacementId=_crossAcceptedId,inFlightPlacementId=_crossInFlightId});
        _crossTranslated=true;
    }

    private void CrossBoundaryRejected(string reason)
    {
        _pilotLog!.Write("drag-boundary-rejected",new{owner=CrossOwner,reason,trusted=_pilotRect,
            desiredHeightDip=_petHeight,effectiveScale=_crossEffectiveScale,effectiveSizeDip=CrossSize,workAreas=CrossWork,
            frozenOrigin=_crossOrigin,requestedPlacementId=_crossRequestedId,acceptedPlacementId=_crossAcceptedId,inFlightPlacementId=_crossInFlightId});
        CrossCancelGesture(reason);
        if(_crossRecovery?.Active!=true)CrossStartRecovery("bounded boundary rejection; frozen trusted origin");
    }
}
