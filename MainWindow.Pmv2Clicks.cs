using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private PilotClickOwner? _pilotClicks;
    private double _pilotGestureDragThresholdDip;
    private double PilotGestureDragThresholdDip => _pilotClicks?.Held == true
        ? _pilotGestureDragThresholdDip : _settings.Interaction.DragThresholdDip;
    private DispatcherTimer? _pilotClickTimer;
    private Action? _pilotClickTimerBody, _pilotClickCompletionBody;
    private bool _pilotClickAction, _pilotClickReleasing, _pilotClickSuppress;
    private long _pilotClickSequence, _pilotClickActionId;
    private RotationSession? _pilotClickRotation;
    private AngrySession? _pilotAngry;
    private bool PilotClickLive => _pilotReady && !_pilotStopped && !_pilotClosing &&
        _pilotSource is {IsDisposed:false} && PilotFresh;

    private void PilotClickFact(string kind,string reason,object? detail=null) => _pilotLog?.Write(kind,
        new {sequence=_pilotClickSequence,gesture=_pilotGesture,generation=_pilotGeneration,source=_pilotSourceToken,
            hwnd=(long)PilotHwnd,owner="PilotClickOwner",gif=_gifPlayer?.ResourcePath,reason,detail});

    // Called only after the existing gesture owner admits real cursor/capture/focus.
    private void PilotClickBegin()
    {
        if(!PilotClickLive) return;
        _pilotClicks ??= new PilotClickOwner(_clickResolver);
        PilotClickStopTimer();
        if(!_clickResolver.IsPending)_pilotClickSequence++;
        _pilotClickSuppress=_pilotClickAction||PilotMotionBusy;
        PilotMotionCancelCore("admitted Down; drag priority",true);
        _pilotClicks.Begin(_pilotGeneration,_pilotGesture,_pilotSourceToken,(long)PilotHwnd);
        if (!_clickResolver.IsPending)
        {
            _clickResolver.CaptureGesture();
            _pilotGestureDragThresholdDip = _settings.Interaction.DragThresholdDip;
        }
        PilotClickFact("click-down","accepted gesture");
    }
    private void PilotClickDragWins()
    {
        if(_pilotClicks?.Held!=true || _pilotClicks.Dragging)return;
        PilotRestCancel("drag priority");
        PilotRandomReset();
        PilotAttachmentCancel("drag wins");
        _pilotClicks.DragWins();PilotClickStopTimer();PilotClickEndAction("drag priority",false);
        PilotClickFact("drag-wins","strictly more than configured 4 DIP Manhattan");
    }
    private void PilotClickMove(PhysicalPoint down,PhysicalPoint cursor)
    {
        if(_pilotClicks?.Held!=true || _pilotClicks.Dragging)return;
        if(!PilotMapping(out var scale) || !PhysicalVector.TryBetween(down,cursor,out var delta) || !scale.TryToLocalDip(delta,out var local))
        {PilotClickCancel("move mapping rejected");return;}
        _pilotClicks.Move(local.X,local.Y,_pilotGestureDragThresholdDip);
        if(_pilotClicks.Dragging) {
            PilotRestCancel("drag priority");
            PilotAttachmentCancel("drag wins");
            PilotClickStopTimer();PilotClickEndAction("drag priority",false);PilotClickFact("drag-wins","threshold crossed");
        }
    }
    private void PilotMouseUp(MouseButtonEventArgs e)
    {
        var releaseTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();
        ++_pilotInputEventSequence;
        PilotInputStopReleaseWait();
        e.Handled=true;
        if(_pilotPhysicsHold!=null||_pilotPhysicsIgnoredUp){PilotPhysicsHoldClear();_pilotPhysicsIgnoredUp=false;return;}
        if(PilotPhysicsBusy)return; // unmatched late Up is not a new physics owner
        var input=_pilotClicks;
        if(input?.Held!=true){PilotCancelGesture("MouseUp without click admission");return;}
        // An OS Up can arrive without a final MouseMove. Sample solely for classification;
        // placement remains in the frozen drag owner, never in this adapter.
        var actualGesture=_crossScreen ? _crossActive : _pilotDrag.Active;
        if(actualGesture) {
            if(!PetSurface.IsMouseCaptured || !PilotCursor(out var cursor)) {PilotCancelGesture("MouseUp capture/cursor rejected");return;}
            PilotClickMove(_crossScreen ? _crossDown : _pilotClickDown,cursor);
        }
        if(!PilotClickLive || !PilotMapping(out _) || (_crossScreen && !_crossMapped))
        {PilotCancelGesture("MouseUp mapping/source rejected");return;}
        var dragging=input.Dragging || (_crossScreen ? _crossDragging : _pilotDrag.Dragging);
        if(dragging)input.DragWins();
        ClickTier? tier=null;
        var restClick=!dragging && _pilotRestEnabled && (PilotRestHasState || _pilotRestExit != null);
        if(restClick) input.Cancel();
        else if(_pilotClickSuppress && !dragging) {
            input.Cancel();
            if(_pilotAngry is {} angry) {
                angry.RegisterClick();
                PilotClickFact("pilot-angry-click","complete gesture consumed by Angry");
            } else if(_pilotClickRotation is {} rotation) {
                rotation.Extend(_clickClock());
                if(rotation.ShouldBecomeAngry)PilotClickFact("pilot-rotation-cap","Caper waits for loop boundary");
            }
        } else tier=input.Complete(_pilotGeneration,_pilotSourceToken,(long)PilotHwnd,_clickClock());
        PilotClickFact("click-classified",dragging ? "drag" : _pilotClickSuppress ? "active action click" : "click");
        var releaseRect=_pilotRect;
        // One-shot manual-placement classification from the real drag cursor input
        // (independent of the physics estimator). It never places or claims a lease.
        var seatIntent=dragging?PilotSeatClassify(releaseRect,releaseTimestamp):null;
        var physicsRelease=PilotPhysicsCaptureRelease(dragging,releaseTimestamp);
        var gravityThrow=PilotGravityThrowCanSelect(physicsRelease);
        var planarThrow=PilotPlanarThrowCanSelect(physicsRelease);
        var throwSelected=gravityThrow||planarThrow;
        // Normal release must not look like unexpected LostCapture or cancel aggregation.
        // Clear pending observers before revoking the gesture; an admitted voice keeps its tail.
        PilotActionVoiceEnd(ref _pilotDragVoiceId, completed: true);
        _pilotClickReleasing=true;
        _pilotGravityThrowReleaseSelected=gravityThrow;
        _pilotPlanarThrowReleaseSelected=planarThrow;
        try {PilotCancelGestureCore("MouseUp",!dragging);}
        finally {_pilotClickReleasing=false;_pilotGravityThrowReleaseSelected=false;_pilotPlanarThrowReleaseSelected=false;}
        // Armed only after the revocation: Cancel/LostCapture/Esc can never leave a
        // correction behind, and the deferred Idle transaction is the sole consumer.
        // A throw owns this release; a manual-placement intent is bound to the
        // old scruff RECT and cannot survive a throw/attachment/Idle round-trip.
        PilotSeatArm(throwSelected?null:seatIntent);
        if(dragging){PilotRestCancel("drag release");PilotPhysicsReleaseOrFloat(physicsRelease,releaseRect,throwSelected);}
        else if(restClick){PilotRestAcceptClick();return;}
        else PilotRestAcceptClick();
        PilotRandomReset();
        if(tier is {} resolved)PilotClickResolve(resolved);
        else if(_clickResolver.IsPending)PilotClickArm();
    }
    private PhysicalPoint _pilotClickDown;
    private void PilotClickStopTimer(){_pilotClickTimer?.Stop();_pilotClickTimer=null;}
    private void PilotClickArm()
    {
        PilotClickStopTimer();
        if(_clickResolver.Deadline is not {} deadline || _pilotClicks is not {} input)return;
        var revision=input.Revision;var sequence=_pilotClickSequence;
        var generation=_pilotGeneration;var source=_pilotSource;var sourceToken=_pilotSourceToken;var hwnd=PilotHwnd;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromTicks(Math.Max(1,(deadline-_clickClock()).Ticks))};
        _pilotClickTimer=timer;
        var body=new Action(()=>{
            if(!ReferenceEquals(timer,_pilotClickTimer) || input.Revision!=revision || sequence!=_pilotClickSequence)return;
            if(!PilotClickLive || generation!=_pilotGeneration || sourceToken!=_pilotSourceToken || !ReferenceEquals(source,_pilotSource) || hwnd!=PilotHwnd ||
                !PilotMapping(out _) || (_crossScreen&&!_crossMapped)) {PilotClickCancel("timer owner/source/mapping rejected");return;}
            timer.Stop();_pilotClickTimer=null;
            if(input.Poll(_clickClock()) is {} tier)PilotClickResolve(tier);
            else if(_clickResolver.IsPending)PilotClickArm();
        });
        _pilotClickTimerBody=body;timer.Tick+=(_,_)=>body();timer.Start();
        PilotClickFact("click-aggregate","existing resolver pending",new{count=_clickResolver.Count,deadlineMs=deadline.TotalMilliseconds});
    }
    private void PilotClickResolve(ClickTier tier)
    {
        PilotClickStopTimer();_clickResolver.Cancel();
        PilotClickFact("click-aggregate",tier.ToString());
        if(!PilotClickLive || _gifPlayer==null || _pilotClickAction)return;
        if(tier==ClickTier.Double) {
            if(_pilotRocketEnabled){PilotRocketBegin();return;}
            PilotAttachmentCancel("Double Rocket disabled");
            PilotClickFact("pilot-click-action-deferred","Rocket requires world-position movement; migration frozen");return;
        }
        var selectedDefinition = tier == ClickTier.Triple ? null : ClickDefinitions.Choose(_availableSingleClicks,_clickRandom);
        var selectedPath = tier == ClickTier.Triple ? ClickDefinitions.RotationPath : selectedDefinition?.ResourcePath;
        if (selectedPath == null || !PilotAttachmentCanOverlay(selectedPath) || !_gifPlayer.TryCacheClip(selectedPath)) {
            PilotClickFact("click-refused","Character Profile action/contact unavailable; retained owner"); return;
        }
        var id=++_pilotClickActionId;_pilotClickAction=true;
        bool accepted;
        RotationSession? rotationLocal=null;
        if(tier==ClickTier.Triple) {
            var session=new RotationSession(_clickClock(),_settings.Interaction);_pilotClickRotation=session;
            accepted=_rotationAvailable&&_gifPlayer.TrySwitchToUntil(ClickDefinitions.RotationPath,
                ()=>_pilotClickAction && id==_pilotClickActionId && ReferenceEquals(session,_pilotClickRotation) && session.ShouldFinish(_clickClock()));
            rotationLocal=session;
        } else {
            accepted=selectedDefinition!=null && _gifPlayer.TrySwitchToLoops(selectedDefinition.ResourcePath,selectedDefinition.RepeatCount);
        }
        if(!accepted){PilotClickEndAction("GIF unavailable",false);PilotClickFact("click-refused","GIF unavailable");return;}
        var generation=_pilotGeneration;var source=_pilotSource;var sourceToken=_pilotSourceToken;var hwnd=PilotHwnd;
        var playback=_gifPlayer.PlaybackGeneration;var path=_gifPlayer.ResourcePath;
        _pilotClickCompletionBody=()=>{
            if(!_pilotClickAction || id!=_pilotClickActionId || _gifPlayer.PlaybackGeneration!=playback || _gifPlayer.ResourcePath!=path)return;
            if(!PilotClickLive || generation!=_pilotGeneration || sourceToken!=_pilotSourceToken || !ReferenceEquals(source,_pilotSource) || hwnd!=PilotHwnd)return;
            if(rotationLocal is {} rotation && ReferenceEquals(rotation,_pilotClickRotation) && rotation.ShouldBecomeAngry) {
                PilotClickBeginCaper(id,rotation);return;
            }
            PilotClickEndAction("complete",true);
        };
        PilotClickFact("click-action-start",tier.ToString(),new{playback});_gifPlayer.Resume();
        if (_pilotClickAction && id == _pilotClickActionId && _gifPlayer.PlaybackGeneration == playback)
            PilotClickVoiceStart(id,path,playback);
    }
    private void PilotClickBeginCaper(long id,RotationSession rotation)
    {
        if(!_pilotClickAction || id!=_pilotClickActionId || !PilotClickLive || _gifPlayer==null ||
            !ReferenceEquals(rotation,_pilotClickRotation))return;
        if(!_caperAvailable || !PilotAttachmentCanOverlay(AngrySession.CaperPath) ||
            !_gifPlayer.TrySwitchToLoops(AngrySession.CaperPath,_settings.Interaction.CaperLoops)) {
            PilotClickFact("pilot-caper-refused","Caper GIF unavailable");
            PilotClickEndAction("Caper unavailable",false);return;
        }
        _pilotClickRotation=null;
        var caperId=++_pilotClickActionId;
        var angry=new AngrySession(_gifPlayer.PlaybackGeneration);_pilotAngry=angry;
        var playback=_gifPlayer.PlaybackGeneration;var path=_gifPlayer.ResourcePath;
        var generation=_pilotGeneration;var source=_pilotSource;var sourceToken=_pilotSourceToken;var hwnd=PilotHwnd;
        _pilotClickCompletionBody=()=>{
            if(!_pilotClickAction || caperId!=_pilotClickActionId || !ReferenceEquals(angry,_pilotAngry) ||
                angry.Stage!=AngryStage.Caper || angry.PlaybackGeneration!=playback ||
                _gifPlayer.PlaybackGeneration!=playback || _gifPlayer.ResourcePath!=path)return;
            if(!PilotClickLive || generation!=_pilotGeneration || sourceToken!=_pilotSourceToken ||
                !ReferenceEquals(source,_pilotSource) || hwnd!=PilotHwnd)return;
            if(angry.EscalateToAngry1 && _angry1Available && PilotAttachmentCanOverlay(AngrySession.Angry1Path) && _gifPlayer.TrySwitchToLoops(AngrySession.Angry1Path,1)) {
                angry.BeginAngry1(_gifPlayer.PlaybackGeneration);
                PilotAngryArmAngry1(caperId,angry);
                PilotClickFact("pilot-angry1-start","Caper completed with escalation click");
                _gifPlayer.Resume();
                PilotClickVoiceStart(caperId,_gifPlayer.ResourcePath,_gifPlayer.PlaybackGeneration);
                return;
            }
            PilotClickEndAction("Caper complete",true);
        };
        PilotClickFact("pilot-caper-start","Rotation cap reached",new{playback,loops=_settings.Interaction.CaperLoops});
        _gifPlayer.Resume();
        PilotClickVoiceStart(caperId,path,playback);
    }
    private void PilotAngryArmAngry1(long id,AngrySession angry)
    {
        var playback=_gifPlayer!.PlaybackGeneration;var path=_gifPlayer.ResourcePath;
        var generation=_pilotGeneration;var source=_pilotSource;var sourceToken=_pilotSourceToken;var hwnd=PilotHwnd;
        _pilotClickCompletionBody=()=>{
            if(!_pilotClickAction || id!=_pilotClickActionId || !ReferenceEquals(angry,_pilotAngry) ||
                angry.Stage!=AngryStage.Angry1 || angry.PlaybackGeneration!=playback ||
                _gifPlayer.PlaybackGeneration!=playback || _gifPlayer.ResourcePath!=path)return;
            if(!PilotClickLive || generation!=_pilotGeneration || sourceToken!=_pilotSourceToken ||
                !ReferenceEquals(source,_pilotSource) || hwnd!=PilotHwnd)return;
            PilotClickEndAction("Angry1 complete",true);
        };
    }
    private void PilotClickEndAction(string reason,bool completed)
    {
        if(!_pilotClickAction)return;
        PilotActionVoiceEnd(ref _pilotClickVoiceId,completed);
        PilotClickFact(completed ? "click-action-end" : "click-action-cancel",reason);
        _pilotClickAction=false;_pilotClickRotation=null;_pilotAngry=null;_pilotClickCompletionBody=null;_pilotClickActionId++;
        PilotVisualHoldGrabFrame();
        if(_pilotVisualHeldFrame==null){
            if(_pilotAttachmentTarget!=null)PilotAttachmentPoseRestore();
            else {_gifPlayer?.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer?.Resume();}
        }
    }
    private void PilotClickCancel(string reason)=>PilotClickCancelCore(reason,false);
    private void PilotClickCancelCore(string reason,bool keepAttachment)
    {
        if(_pilotClickReleasing || reason=="new gesture")return;
        PilotMotionCancelCore(reason,keepAttachment);
        var active=_pilotClicks?.Held==true || _clickResolver.IsPending;
        _pilotClicks?.Cancel();PilotClickStopTimer();PilotClickEndAction(reason,false);
        if(active)PilotClickFact("click-refused",reason);
    }
    private void PilotClickCompleted(object? sender,EventArgs e)
    {
        if(e is GifPlaybackCompletedEventArgs completed && ReferenceEquals(sender,_gifPlayer) &&
            completed.Generation==_gifPlayer?.PlaybackGeneration && completed.ResourcePath==_gifPlayer.ResourcePath && _gifPlayer.IsComplete)
        { PilotGravityLandingCompleted(sender,e);PilotMotionCompleted(sender,e);PilotRestCompleted(sender,e);PilotRandomCompleted(sender,e);_pilotClickCompletionBody?.Invoke(); }
    }
    // The clip event only queues an independently owned footprint checkpoint.
    // It does not resize or place on the clip/layout/native event stack.
    private void PilotRefreshClickImage()
    {
        PilotVisualQueue();
    }
}
