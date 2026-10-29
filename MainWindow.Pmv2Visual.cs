using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NecoArcDesktopPet.Animation;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private bool _pilotVisualCalibrated;
    private Point _pilotVisualAnchorLocal;
    // The cropped all-frame visible union in surface DIP. Its bottom edge is the
    // arranged foot contact the viewport is fitted to; its centre is the horizontal
    // point a user scale keeps.
    private Rect _pilotVisualVisibleBounds;
    private DispatcherOperation? _pilotVisualPending;
    private DispatcherTimer? _pilotVisualWait;
    private BitmapSource? _pilotVisualHeldFrame;
    // A held image is an intentional, previously admitted presentation. It may
    // survive a clip switch, but a prospective/rejected drag is never its owner.
    private sealed record PilotHeldFrame(BitmapSource Frame,string Path,long Playback,int Index,long Gesture);
    private PilotHeldFrame? _pilotVisualHeldIdentity;
    private void PilotVisualClearHeld(){_pilotVisualHeldFrame=null;_pilotVisualHeldIdentity=null;}
    private void PilotVisualKeepCurrentFrame()
    {
        if(_gifPlayer==null||_pilotVisualSizePath!=_gifPlayer.ResourcePath)return;
        _pilotVisualHeldIdentity=new(_gifPlayer.PilotCurrentFrame,_gifPlayer.ResourcePath,
            _gifPlayer.PlaybackGeneration,_gifPlayer.PilotFrameIndex,_pilotGesture);
        _pilotVisualHeldFrame=_pilotVisualHeldIdentity.Frame;
    }
    private bool PilotVisualHeldIsAdmitted=>_pilotVisualHeldIdentity is {} held&&
        ReferenceEquals(held.Frame,_pilotVisualHeldFrame)&&held.Path==_pilotVisualSizePath&&held.Gesture==_pilotGesture;

    // Full local transaction state, including the clip-size cache and profile:
    // restoring only Width/Height leaves a candidate anchor/cache behind.
    private sealed record PilotVisualSnapshot(double SurfaceWidth,double SurfaceHeight,double ImageWidth,double ImageHeight,
        double Left,double Top,double WindowWidth,double WindowHeight,ImageSource? Source,Transform Transform,Point Origin,
        Point Anchor,Rect Visible,Size BaseSize,BitmapSource? Arranged,string? Path,double SizeHeight,uint Dpi,Rect Union,bool Calibrated,
        PilotHeldFrame? Held,string? PlaybackPath,long PlaybackGeneration,int FrameIndex,
        bool Deferred,bool Resume,bool PresentationHeld,bool Playing,PhysicalRect? RetainedViewport);
    private PilotVisualSnapshot PilotVisualCapture()=>new(PetSurface.Width,PetSurface.Height,PetImage.Width,PetImage.Height,
        Canvas.GetLeft(PetImage),Canvas.GetTop(PetImage),Width,Height,PetImage.Source,PetImage.RenderTransform.Clone(),
        PetImage.RenderTransformOrigin,_pilotVisualAnchorLocal,_pilotVisualVisibleBounds,_pilotVisualBaseSize,_pilotVisualArrangedFrame,
        _pilotVisualSizePath,_pilotVisualSizeHeight,_pilotVisualSizeDpi,_pilotVisualSizeUnion,_pilotVisualCalibrated,
        _pilotVisualHeldIdentity,_gifPlayer?.ResourcePath,_gifPlayer?.PlaybackGeneration??0,_gifPlayer?.PilotFrameIndex??0,
        _pilotVisualPresentationDeferred,_pilotVisualResume,_gifPlayer?.PilotPresentationHeld??false,_gifPlayer?.PilotFrameTimerEnabled??false,
        _pilotVisualRetainedViewport);
    private void PilotVisualRestore(PilotVisualSnapshot s,bool presentation=false)
    {
        PetSurface.Width=s.SurfaceWidth;PetSurface.Height=s.SurfaceHeight;
        PetImage.Width=s.ImageWidth;PetImage.Height=s.ImageHeight;PetImage.Source=s.Source;
        Canvas.SetLeft(PetImage,s.Left);Canvas.SetTop(PetImage,s.Top);
        PetImage.RenderTransform=s.Transform.Clone();PetImage.RenderTransformOrigin=s.Origin;
        _pilotVisualAnchorLocal=s.Anchor;_pilotVisualVisibleBounds=s.Visible;_pilotVisualBaseSize=s.BaseSize;_pilotVisualArrangedFrame=s.Arranged;
        _pilotVisualSizePath=s.Path;_pilotVisualSizeHeight=s.SizeHeight;_pilotVisualSizeDpi=s.Dpi;
        _pilotVisualSizeUnion=s.Union;_pilotVisualCalibrated=s.Calibrated;
        if(presentation&&_gifPlayer!=null) {
            // Only the unchanged playback owner may use this restoration. Do not
            // switch clips, rewind completion callbacks, or resurrect an action.
            _pilotVisualHeldIdentity=s.Held;_pilotVisualHeldFrame=s.Held?.Frame;
            _pilotVisualPresentationDeferred=s.Deferred;_pilotVisualResume=s.Resume;
            _pilotVisualRetainedViewport=s.RetainedViewport;
            _gifPlayer.PilotPresentationHeld=s.PresentationHeld;
            if(s.Playing)_gifPlayer.Resume();else _gifPlayer.Pause();
        }
        using(PilotLayoutScope()){Width=s.WindowWidth;Height=s.WindowHeight;UpdateLayout();}
    }
    private BitmapSource? _pilotVisualArrangedFrame;
    private bool _pilotVisualPresentationDeferred;
    private bool _pilotVisualDirty,_pilotVisualResume;
    private string? _pilotVisualSizePath;
    private double _pilotVisualSizeHeight;
    private uint _pilotVisualSizeDpi;
    private Size _pilotVisualBaseSize;
    private Rect _pilotVisualSizeUnion;
    private bool PilotVisualBusy=>_pilotVisualDirty||_pilotVisualPending!=null||_pilotVisualWait!=null||_pilotVisualHeldFrame!=null;
    private long _pilotVisualRevision;
    private PhysicalRect? _pilotVisualReturn;
    private Action? _pilotVisualCompleted;
    private PhysicalRect? _pilotVisualRetainedViewport;

    // Attachment may refuse after the larger throw viewport is admitted. The
    // old rectangle then has the wrong size for Idle. Let the existing natural
    // anchor/Fit/sweep/writer/postcheck transaction resize it, rather than asking
    // its strict return-size gate to accept an unrelated clip's dimensions.
    private PhysicalRect? PilotVisualAttachmentIdleReturn(PhysicalRect old, string? previousSizePath) =>
        (_pilotGravityPending && _pilotGravityThrowVisual != null ||
            _pilotPhysicsPending != null && _pilotPlanarThrowVisual != null ||
            previousSizePath == PilotPlanarThrowPath) ? null : old;

    // A physics candidate owns input capture only. Its capture must not freeze
    // the current physics owner's throw/landing footprint transition.
    private bool PilotVisualGrabActive=>_crossActive||_pilotDrag.Active||(PetSurface.IsMouseCaptured&&_pilotPhysicsHold==null);
    private void PilotVisualHoldGrabFrame()
    {
        if(PilotVisualGrabActive) {
            if(!PilotVisualHeldIsAdmitted)PilotVisualKeepCurrentFrame();
            if(!PilotVisualHeldIsAdmitted)return;
            _pilotVisualDirty=true;
            if(_gifPlayer!=null){_gifPlayer.PilotPresentationHeld=true;_gifPlayer.Pause();}
        }
    }

    private void PilotVisualDeferPresentation()
    {
        if(_gifPlayer==null||_pilotVisualArrangedFrame==null||_pilotVisualHeldFrame!=null||
            _pilotVisualSizePath==_gifPlayer.ResourcePath&&!_pilotVisualPresentationDeferred)return;
        // New bitmap metadata would make WPF Uniform remeasure the old Image size
        // with the new aspect ratio. Keep the old matched frame until admission;
        // otherwise G correctly refuses Sizes and cannot accept its own checkpoint.
        _pilotVisualPresentationDeferred=true;_pilotVisualResume=true;
        _gifPlayer.PilotPresentationHeld=true;_gifPlayer.Pause();PetImage.Source=_pilotVisualArrangedFrame;
    }

    // Source unit/profile is preserved. Cropping transparent union margins changes
    // the viewport, not the image scale or asset. The logical anchor survives crop.
    private void PilotVisualArrange()
    {
        if(_gifPlayer==null)return;
        if(_pilotVisualPresentationDeferred){_gifPlayer.PilotPresentationHeld=false;_gifPlayer.PilotRestoreCurrentFrame();_pilotVisualPresentationDeferred=false;}
        var effective=_crossScreen?_crossEffectiveScale:1;
        var height=_petHeight*effective;
        var width=height*_imageAspect;var unit=height/AnimationVisualProfiles.CanonicalCanvasHeight;
        PetImage.Width=_gifPlayer.PixelWidth*unit;PetImage.Height=_gifPlayer.PixelHeight*unit;
        Canvas.SetLeft(PetImage,(width-PetImage.Width)/2);Canvas.SetTop(PetImage,height-PetImage.Height);
        ApplyAnimationVisualProfile();
        var source=_gifPlayer.PilotVisibleUnion;
        if(source.IsEmpty)throw new InvalidOperationException("Pilot refuses an empty visible GIF union.");
        Point Map(double x,double y)=>AttachmentGeometry.TransformPoint(new(x*unit,y*unit),
            new(PetImage.Width,PetImage.Height),new(Canvas.GetLeft(PetImage),Canvas.GetTop(PetImage)),
            PetImage.RenderTransform.Value,PetImage.RenderTransformOrigin);
        var bounds=Rect.Empty;
        foreach(var p in new[]{Map(source.Left,source.Top),Map(source.Right,source.Top),Map(source.Left,source.Bottom),Map(source.Right,source.Bottom)})bounds.Union(p);
        // Quantize the desired size once per clip/height/DPI, then scale it linearly.
        // Frozen seam preparation requires an invariant desiredSize = size/effective.
        // One base physical pixel protects the union from native rounding during
        // the existing >=.75 seam contraction; it adds at most two pixels of margin.
        var nativeDpi=PilotNative.GetDpiForWindow(PilotHwnd);var dpi=nativeDpi/96d;
        if(dpi<=0)throw new InvalidOperationException("Pilot visual footprint requires a live native DPI.");
        if(_pilotVisualSizePath!=_gifPlayer.ResourcePath||_pilotVisualSizeHeight!=_petHeight||_pilotVisualSizeDpi!=nativeDpi||_pilotVisualSizeUnion!=source) {
            _pilotVisualBaseSize=new((Math.Ceiling(bounds.Width/effective*dpi)+1)/dpi,(Math.Ceiling(bounds.Height/effective*dpi)+1)/dpi);
            _pilotVisualSizePath=_gifPlayer.ResourcePath;_pilotVisualSizeHeight=_petHeight;_pilotVisualSizeDpi=nativeDpi;_pilotVisualSizeUnion=source;
        }
        PetSurface.Width=_pilotVisualBaseSize.Width*effective;
        PetSurface.Height=_pilotVisualBaseSize.Height*effective;
        var roundX=PetSurface.Width-bounds.Width;var roundY=PetSurface.Height-bounds.Height;
        Canvas.SetLeft(PetImage,Canvas.GetLeft(PetImage)-bounds.Left+roundX);
        Canvas.SetTop(PetImage,Canvas.GetTop(PetImage)-bounds.Top+roundY);
        _pilotVisualAnchorLocal=new(width/2-bounds.Left+roundX,height-bounds.Top+roundY);
        _pilotVisualVisibleBounds=new(roundX,roundY,roundX+bounds.Width,roundY+bounds.Height);
        _pilotVisualCalibrated=true;
        _pilotVisualArrangedFrame=PetImage.Source as BitmapSource;
    }

    private void PilotVisualCancelPending()
    {
        _pilotVisualRevision++;Abort(ref _pilotVisualPending);
        _pilotVisualWait?.Stop();_pilotVisualWait=null;
        _pilotVisualReturn=null;_pilotVisualCompleted=null;
    }

    private bool PilotVisualContainRetainedIdle(double width,double height)
    {
        // A target can invalidate during the successful native return write. A
        // side pose may then be shorter than idle. Contain idle's ALL-frame union
        // locally in that last legal viewport; never grow/move HWND to recover.
        var factor=Math.Min(1,Math.Min(width/PetSurface.Width,height/PetSurface.Height));
        if(!double.IsFinite(factor)||factor<=0||_gifPlayer==null)return false;
        var profile=(System.Windows.Media.ScaleTransform)((System.Windows.Media.TransformGroup)PetImage.RenderTransform).Children[3];
        profile.ScaleX*=factor;profile.ScaleY*=factor;
        var source=_gifPlayer.PilotVisibleUnion;var bounds=Rect.Empty;
        foreach(var p in new[]{source.TopLeft,source.TopRight,source.BottomLeft,source.BottomRight})
            bounds.Union(AttachmentGeometry.TransformPoint(new(p.X*PetImage.Width/_gifPlayer.PixelWidth,p.Y*PetImage.Height/_gifPlayer.PixelHeight),
                new(PetImage.Width,PetImage.Height),new(Canvas.GetLeft(PetImage),Canvas.GetTop(PetImage)),
                PetImage.RenderTransform.Value,PetImage.RenderTransformOrigin));
        if(bounds.IsEmpty||bounds.Width>width||bounds.Height>height)return false;
        Canvas.SetLeft(PetImage,Canvas.GetLeft(PetImage)-bounds.Left+(width-bounds.Width)/2);
        Canvas.SetTop(PetImage,Canvas.GetTop(PetImage)-bounds.Top+height-bounds.Height);
        _pilotVisualAnchorLocal=new(width/2,height);
        _pilotVisualVisibleBounds=new((width-bounds.Width)/2,height-bounds.Height,(width+bounds.Width)/2,height);
        return true; // normal Arrange reapplies the original profile on next clip
    }

    // A clip switch can resize its viewport. It is a finite, same-DPI transition on
    // an independent Dispatcher callback. G and its native/DPI recovery are untouched.
    // It uses the existing unique writer, proves the full resize sweep, and permits
    // one post-layout reassertion. It never places from ClipChanged/layout/native hook.
    private void PilotVisualQueue()
    {
        // A refused pose has restored the admitted viewport and clip. Capture
        // alone must not hold/pause that fallback or enqueue a second resize.
        if(_pilotDragVisualFallback&&_gifPlayer!=null&&_gifPlayer.ResourcePath==_pilotVisualSizePath) {
            PilotVisualCancelPending();PilotVisualClearHeld();
            _pilotVisualDirty=false;_pilotVisualResume=false;_pilotVisualPresentationDeferred=false;
            _gifPlayer.PilotPresentationHeld=false;_gifPlayer.PilotRestoreCurrentFrame();_gifPlayer.Resume();return;
        }
        PilotPhysicsYield();
        if(!_pilotReady||_gifPlayer==null||_pilotClosing||_pilotStopped)return;
        _pilotVisualDirty=true;
        Abort(ref _pilotVisualPending);_pilotVisualWait?.Stop();_pilotVisualWait=null;var revision=++_pilotVisualRevision;
        // Capture retains the admitted box, so retain its matching bitmap too.
        // The helper leaves an existing held frame under its original owner.
        PilotVisualDeferPresentation();
        if(PilotVisualGrabActive) {
            _pilotVisualRetainedViewport=null;
            // The existing physical grab and 4D fraction retain their admitted size.
            // Only the interrupted frame is held; idle is laid out after release.
            if(PilotVisualHeldIsAdmitted){PetImage.Source=_pilotVisualHeldFrame;_gifPlayer.Pause();}
            _pilotVisualReturn=null;_pilotVisualCompleted=null;return;
        }
        if(_pilotVisualHeldFrame!=null) {
            // MouseUp has selected a qualified throw but is still revoking its
            // gesture. Keep scruff until the post-revocation planning boundary.
            if(_pilotGravityThrowReleaseSelected||_pilotPlanarThrowReleaseSelected)return;
            // The old clip/profile remained current during capture, so frozen 4D
            // scale preparation could still arrange it normally. Release owns idle.
            PilotVisualClearHeld();_pilotVisualResume=true;
            _gifPlayer.PilotPresentationHeld=false;
            if(!PilotGravityThrowOwnsClip && !PilotPlanarThrowOwnsClip) {
                if(!_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!))PilotStop("Held visual could not restore idle.");
                return; // ClipChanged queued a current-playback callback.
            }
            // Selected throw uses this very transaction, without a second switch.
        }
        // A selected throw may just have cleared a held frame above. Recheck
        // deferral now that it can admit its own clip; otherwise this is idempotent.
        PilotVisualDeferPresentation();
        var generation=_pilotGeneration;var gesture=_pilotGesture;var source=_pilotSource;var token=_pilotSourceToken;var hwnd=PilotHwnd;
        var playback=_gifPlayer.PlaybackGeneration;var path=_gifPlayer.ResourcePath;var began=Stopwatch.GetTimestamp();var attempts=0;
        var rejectionRetries=0;var finished=false;
        if(_pilotAttachmentTarget!=null&&!_pilotAttachmentPosePending&&
            path==(_pilotAttachmentPoseSide==AttachmentSide.Top?PilotStandPath:PilotSidePath))
        {
            _pilotAttachmentPosePending=true;
            _pilotAttachmentPoseVisualRevision=revision;
        }
        var attachedOverlay=_pilotAttachmentTarget!=null&&_pilotClickAction&&
            path!=(_pilotAttachmentPoseSide==AttachmentSide.Top?PilotStandPath:PilotSidePath);
        var rocketReturning=_pilotRocketAttachment!=null&&_pilotRocketReturnGuard!=null;
        if(attachedOverlay)_pilotAttachmentOverlayVisualRevision=revision;
        bool Own()=>!finished&&revision==_pilotVisualRevision&&generation==_pilotGeneration&&gesture==_pilotGesture&&
            ReferenceEquals(source,_pilotSource)&&token==_pilotSourceToken&&hwnd==PilotHwnd&&PilotClickLive&&
            _gifPlayer.PlaybackGeneration==playback&&_gifPlayer.ResourcePath==path;
        void Run()
        {
            if(!Own()){if(revision==_pilotVisualRevision){_pilotVisualPending=null;_pilotVisualReturn=null;_pilotVisualCompleted=null;}return;}
            if(rocketReturning&&_pilotRocketReturnGuard?.Invoke()!=true){PilotRocketCancel("attached return changed before visual admission");return;}
            // A legitimate capture can start while this independent callback is
            // waiting for G. Retain dirty work until the ordinary release path;
            // capture is not an exhausted safety checkpoint or a polling loop.
            if(PilotVisualGrabActive){_pilotVisualPending=null;_pilotVisualWait=null;return;}
            if(_crossRecovery?.Active==true) {
                // G retains an accepted owner until its original 500ms guard ends.
                // Wait once for that deadline; never renew, cancel, place or mutate G.
                _pilotVisualPending=null;
                if(++attempts>2){PilotStop("Visual footprint: G guard did not end within its existing deadline.");return;}
                var wait=new DispatcherTimer(DispatcherPriority.ContextIdle){Interval=TimeSpan.FromMilliseconds(Math.Max(1,_crossRecovery.RemainingMilliseconds+1))};
                _pilotVisualWait=wait;
                wait.Tick+=(_,_)=>{wait.Stop();if(!ReferenceEquals(wait,_pilotVisualWait))return;_pilotVisualWait=null;_pilotVisualPending=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Run));};
                wait.Start();return;
            }
            if(_pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0||
                !PilotMapping(out var scale)||scale!=_pilotScale)
            {
                if(++attempts<=2&&Stopwatch.GetElapsedTime(began).TotalMilliseconds<500){_pilotVisualPending=Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(Run));return;}
                _pilotVisualPending=null;PilotStop("Visual footprint: bounded same-DPI checkpoint unavailable; no safety bypass.");return;
            }
            _pilotVisualPending=null;
            if(!PilotNative.GetWindowRect(hwnd,out var native)||!CrossScreenGeometry.Near(native.Physical,_pilotRect))
            {PilotStop("Visual footprint: untrusted native RECT.");return;}
            var old=_pilotRect;var anchor=new PhysicalPoint(old.Left+_pilotVisualAnchorLocal.X*scale.X,old.Top+_pilotVisualAnchorLocal.Y*scale.Y);
            var grounded=_pilotMotionState is PilotMotionState.Grounded or PilotMotionState.Landing||
                (_pilotMotionGroundVisual||_pilotGravityGrounded&&(path==_effectiveBaseIdlePath||PilotGravityLandingOwnsClip))&&PilotFresh&&_pilotTopology!.State.LastKnownGood!.Monitors.Any(m=>
                    Math.Abs(old.Bottom-m.WorkArea.Bottom)<1e-6&&SingleMonitorDrag.Fits(old,m.WorkArea));
            // Planar's bind-time Attachment priority may already have revoked its
            // throw owner. Keep the actual previous size-path across Arrange so
            // a rejected successor never returns throw dimensions as Idle.
            var previousSizePath=_pilotVisualSizePath;
            var snapshot=PilotVisualCapture();
            var rollbackReady=!_crossScreen||CrossCheckpoint().Ready;
            var placementBefore=_crossPlacementId;var requestedBefore=_crossRequestedId;
            var acceptedBefore=_crossAcceptedId;var acceptedOwnerBefore=_crossAcceptedOwner;
            var expectedBefore=_crossExpected;var originBefore=_crossOrigin;
            PilotVisualArrange();
            var w=Math.Round(PetSurface.Width*scale.X);var h=Math.Round(PetSurface.Height*scale.Y);
            // This is viewport bookkeeping, not a return continuation: a higher
            // cancel may revoke/requeue idle, but it may never move back to this
            // RECT if drag or another writer has already changed it.
            var retainViewport=_pilotVisualRetainedViewport==old&&path==_effectiveBaseIdlePath;
            _pilotVisualRetainedViewport=null;
            if(retainViewport){
                // An aborted attached flight keeps its last admitted native RECT.
                // Admit the idle union inside that existing viewport, with
                // the same Fit/sweep/writer/postcheck protocol as every resize.
                if((w>old.Width||h>old.Height)&&!PilotVisualContainRetainedIdle(old.Width/scale.X,old.Height/scale.Y)){
                    PilotStop("Rocket idle union cannot fit retained viewport.");return;
                }
                PetSurface.Width=old.Width/scale.X;PetSurface.Height=old.Height/scale.Y;
                w=old.Width;h=old.Height;
            }
            var x=Math.Round(anchor.X-_pilotVisualAnchorLocal.X*scale.X);
            var y=grounded?old.Bottom-h:Math.Round(anchor.Y-_pilotVisualAnchorLocal.Y*scale.Y);
            var wanted=retainViewport?old:_pilotVisualReturn??new PhysicalRect(x,y,x+w,y+h);
            var attaching=_pilotAttachmentTarget!=null&&
                (_pilotAttachmentPosePending&&_pilotAttachmentPoseVisualRevision==revision||
                 attachedOverlay&&_pilotAttachmentOverlayVisualRevision==revision);
            PhysicalRect[]? attachmentAreas=null;
            if(attaching)
            {
                if(!_pilotAttachmentTarget!.TryRead(out var attachmentTarget,out _)||
                    attachmentTarget.Monitor!=PilotAttachmentTarget.Monitor(hwnd)||
                    rocketReturning&&(attachmentTarget.Rect.Width<80*scale.X||attachmentTarget.Rect.Height<80*scale.Y))
                {
                    if(rocketReturning){PilotRocketCancel("attached return target unavailable during visual admission");return;}
                    PilotAttachmentCancel("pose target unavailable during visual admission; REFUSED");
                    _pilotVisualReturn=PilotVisualAttachmentIdleReturn(old,previousSizePath);
                    _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer.Resume();
                    return;
                }
                var center=new PhysicalPoint((attachmentTarget.Rect.Left+attachmentTarget.Rect.Right)/2,
                    (attachmentTarget.Rect.Top+attachmentTarget.Rect.Bottom)/2);
                var targetMonitor=_pilotTopology!.State.LastKnownGood!.Monitors.FirstOrDefault(m=>m.Bounds.ContainsClosed(center));
                if(!targetMonitor.IsValid){PilotAttachmentCancel("attachment target monitor unavailable during visual admission");return;}
                attachmentAreas=new[]{targetMonitor.WorkArea};
                var local=PilotAttachmentGeometry.Local(attachmentTarget.Rect,old,scale);
                if (!TryPilotAttachmentContact(_pilotAttachmentPoseSide, out var contact)) {
                    if (rocketReturning) { PilotRocketCancel("Character Profile return contact unavailable"); return; }
                    if (attachedOverlay) PilotClickEndAction("Character Profile overlay contact unavailable",false);
                    else PilotAttachmentReject("Character Profile pose contact unavailable");
                    return;
                }
                var height=_petHeight*(_crossScreen?_crossEffectiveScale:1);
                var align=AttachmentGeometry.Align(_pilotAttachmentPoseSide,local,new(contact,contact,contact),
                    _pilotAttachmentPoseOffsetX,_pilotAttachmentPoseOffsetY,height,height*_imageAspect);
                wanted=new PhysicalRect(Math.Round(old.Left+align.X*scale.X),Math.Round(old.Top+align.Y*scale.Y),
                    Math.Round(old.Left+align.X*scale.X)+w,Math.Round(old.Top+align.Y*scale.Y)+h);
            }
            var areas=attachmentAreas??(_crossScreen?CrossScreenGeometry.Reachable(old,CrossWork):new[]{_pilotMonitor.WorkArea});
            var fitted=CrossScreenGeometry.Fit(wanted,areas);
            if(fitted is not {} target||attaching&&target!=wanted||!CrossScreenGeometry.TranslationSafe(old,target,areas))
            {
                _pilotVisualReturn=null;_pilotVisualCompleted=null;
                if(rocketReturning){PilotRocketCancel("attached return footprint/contact corridor denied");return;}
                if(attaching){
                    if(attachedOverlay){
                        PilotAttachmentFact("attachment-overlay-refused","footprint/contact corridor denied",new{old,wanted,status="REFUSED"});
                        _pilotVisualReturn=old;
                        PilotClickEndAction("attached overlay footprint refused",false);
                    }else{
                        PilotAttachmentFact("attachment-pose-refused","footprint/contact corridor denied",new{old,wanted,status="REFUSED"});
                        PilotAttachmentReject("pose footprint/contact corridor denied; REFUSED");
                        _pilotVisualReturn=PilotVisualAttachmentIdleReturn(old,previousSizePath);
                        _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer.Resume();
                    }
                    return;
                }
                if(path==_effectiveBaseIdlePath){PilotStop("Idle footprint has no covered corridor; no safety bypass.");return;}
                PilotRestCancel("visual footprint refused",false);
                PilotRandomCancel("visual footprint refused",false);
                PilotMotionCancel("visual footprint refused");PilotClickEndAction("visual footprint refused",false);
                if(!_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!)){
                    PilotStop("Visual footprint: refused corridor and idle fallback unavailable.");return;
                }
                _gifPlayer.Resume();
                _pilotLog?.Write("visual-footprint-refused",new{path,old,wanted,reason="no covered resize corridor; idle fallback; safety unchanged"},"REFUSED");return;
            }
            if(Math.Abs(target.Width-w)>1e-6||Math.Abs(target.Height-h)>1e-6)
            {PilotStop("Visual footprint: return size no longer matches live GIF/scale.");return;}
            // One-shot release-time Idle edge seating. Purely a horizontal choice of
            // the same natural Idle target, proven Covered/TranslationSafe before it
            // replaces the target, so the single writer and postcheck are unchanged.
            if(_pilotSeatIntent!=null)
            {
                // The fitted legal Idle RECT is the base: a vertical Fit (corner/top
                // contact) must not lose the horizontal wall correction.
                var seatEligible=!attaching&&!retainViewport&&_pilotVisualReturn==null;
                if(PilotSeatPlacement(old,target,path,seatEligible,areas) is {} seated)target=seated;
            }
            if(rocketReturning&&_pilotRocketReturnGuard?.Invoke()!=true){PilotRocketCancel("attached return changed before placement");return;}
            if(!PilotPlace(target,"visual footprint"))
            {
                // Native hooks may synchronously cancel this owner or queue a
                // successor. An old transaction must never restore over it.
                if(!Own())return;
                if(rocketReturning){PilotRocketCancel("attached return existing writer refused pose");return;}
                if(attaching){
                    if(attachedOverlay){
                        PilotAttachmentFact("attachment-overlay-refused","existing placement writer denied overlay",new{old,target,status="REFUSED"});
                        _pilotVisualReturn=old;
                        PilotClickEndAction("attached overlay placement refused",false);
                    }else{
                        PilotAttachmentFact("attachment-pose-refused","existing placement writer denied pose",new{old,target,status="REFUSED"});
                        PilotAttachmentCancel("pose placement refused; FALLBACK");
                        _pilotVisualReturn=PilotVisualAttachmentIdleReturn(old,previousSizePath);
                        _gifPlayer.TrySwitchTo(_effectiveBaseIdlePath!);_gifPlayer.Resume();
                    }
                    return;
                }
                void Refuse(string why) {
                    finished=true;
                    _pilotLog?.Write("visual-footprint-rollback-refused",new{revision,generation,gesture,path,old,target,reason=why},"FAIL-CLOSED");
                    PilotStop("Visual footprint: placement refused; "+why);
                }
                // Require exact unchanged native/trusted geometry, not merely the
                // writer's 2px admission tolerance. A real overwrite is not ours
                // to roll back. Check the entire placement lineage before touching IDs.
                if(!rollbackReady||!snapshot.Calibrated||snapshot.Source==null||snapshot.Arranged==null||
                    snapshot.PlaybackPath!=path||snapshot.PlaybackGeneration!=playback||
                    snapshot.FrameIndex!=_gifPlayer.PilotFrameIndex||_pilotRect!=old||
                    !PilotNative.GetWindowRect(hwnd,out var refused)||refused.Physical!=old||
                    !PilotMapping(out var refusedScale)||refusedScale!=scale||!PilotFresh||
                    _pilotPlacementDepth!=0||_pilotHookDepth!=0||_crossLayoutDepth!=0||
                    _crossScreen&&(_crossRecovery?.Active==true||_crossInFlightId!=null||
                        _crossAcceptedId!=acceptedBefore||_crossAcceptedOwner!=acceptedOwnerBefore||_crossExpected!=expectedBefore||
                        !(_crossPlacementId==placementBefore&&_crossRequestedId==requestedBefore||
                          _crossPlacementId==placementBefore+1&&_crossRequestedId==_crossPlacementId))) {
                    Refuse("old presentation/native/placement lineage cannot be proven unchanged");return;
                }
                var withdraw=_crossScreen&&_crossPlacementId==placementBefore+1;
                var withdrawn=withdraw?_crossRequestedId:null;
                var rejectedPlacement=_crossPlacementId;
                if(_crossScreen) {
                    // Withdraw just this rejected request, never manufacture an
                    // acceptance or change AcceptedId. No successor/in-flight exists.
                    if(withdraw)_crossRequestedId=null;
                    _crossOrigin=originBefore;
                }
                PilotVisualRestore(snapshot,true);
                if(!Own())return; // UpdateLayout may synchronously transfer ownership.
                if(!PilotNative.GetWindowRect(hwnd,out refused)||refused.Physical!=old||
                    !PilotMapping(out refusedScale)||refusedScale!=scale||_pilotRect!=old||
                    _crossScreen&&(!CrossCheckpoint().Ready||_crossPlacementId!=rejectedPlacement||
                        _crossAcceptedId!=acceptedBefore||_crossAcceptedOwner!=acceptedOwnerBefore||
                        _crossExpected!=expectedBefore||_crossRequestedId!=(withdraw?null:requestedBefore)||_crossInFlightId!=null)) {
                    Refuse("restored presentation failed independent checkpoint");return;
                }
                _pilotLog?.Write("visual-footprint-rollback",new{revision,generation,gesture,path,old,
                    withdrawnPlacementId=withdrawn,acceptedPlacementId=_crossAcceptedId,
                    retry=rejectionRetries,sourcePath=snapshot.Path,scale},"RESTORED; NOT ACCEPTED");
                if(rejectionRetries++>=1){Refuse("one retry exhausted after trusted rollback");return;}
                // Same closure and revision: a successor invalidates this callback
                // and cannot renew its one-retry budget through recursive Queue().
                _pilotVisualPending=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Run));
                return;
            }
            if(!Own())return;
            if(rocketReturning&&_pilotRocketReturnGuard?.Invoke()!=true){PilotRocketCancel("attached return changed during placement");return;}
            using(PilotLayoutScope())
            {
                Width=PetSurface.Width;Height=PetSurface.Height;
                // Independent callback, outside native and layout event stacks.
                UpdateLayout();
            }
            if(!Own())return;
            if(!PilotNative.GetWindowRect(hwnd,out var post)) {PilotStop("Visual footprint: post-layout RECT unreadable.");return;}
            if(rocketReturning&&_pilotRocketReturnGuard?.Invoke()!=true){PilotRocketCancel("attached return changed before reassertion");return;}
            if(!CrossScreenGeometry.Near(post.Physical,target)&&!PilotPlace(target,"visual footprint reassert"))
            {PilotStop("Visual footprint: bounded post-layout reassertion refused.");return;}
            if(!Own()||!PilotNative.GetWindowRect(hwnd,out post)||!CrossScreenGeometry.Near(post.Physical,target)||
                !PilotMapping(out var mapped)||mapped!=scale||!CrossScreenGeometry.Covered(post.Physical,areas))
            {if(Own())PilotStop("Visual footprint: postcheck failed.");return;}
            if(rocketReturning&&_pilotRocketReturnGuard?.Invoke()!=true){PilotRocketCancel("attached return changed after visual postcheck");return;}
            var completed=_pilotVisualCompleted;_pilotVisualCompleted=null;_pilotVisualReturn=null;
            _pilotVisualDirty=false;
            if(attaching){_pilotAttachmentPosePending=false;PilotAttachmentFact("attachment-pose-admitted","full clip union and contact accepted",new{side=_pilotAttachmentPoseSide,sourceUnion=_gifPlayer.PilotVisibleUnion,target});PilotAttachmentVoiceAdmitted();}
            // A wheel's already accepted floor survives a planner wait/cancellation.
            // Derive the state only from the trusted, postchecked footprint here;
            // this is a ground fact, without Falling or Landing replay.
            if(path==_effectiveBaseIdlePath) {
                if(grounded&&_pilotVisualGroundAfterScale&&_pilotMotionState==PilotMotionState.Idle)_pilotMotionState=PilotMotionState.Grounded;
                _pilotVisualGroundAfterScale=false;
            }
            _pilotLog?.Write("visual-footprint",new{revision,generation,gesture,path,sourceUnion=_gifPlayer.PilotVisibleUnion,
                old,target,anchorLocal=_pilotVisualAnchorLocal,grounded,scale,repairs=post.Physical==target?0:1},"CONTROLLED WPF/OS GEOMETRY; USER PIXELS PENDING");
            if(_pilotVisualResume){_pilotVisualResume=false;_gifPlayer.Resume();}
            finished=true;
            completed?.Invoke();
        }
        _pilotVisualPending=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Run));
    }
}
