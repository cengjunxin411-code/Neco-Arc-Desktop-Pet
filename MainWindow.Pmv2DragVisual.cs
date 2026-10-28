using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

// Pilot-only drag visual owner: the selected drag pose + 6 DIP direction mirror +
// 460 ms release float. The clip switch changes the window footprint, so admission
// is a bounded, deferred, grab-preserving transition that reuses PilotVisualArrange
// and the unique PilotPlace/CrossPlace writer with the same Sizes/Geometry checks.
// The mirror is a local Children[0] scale, never a second position writer.
public partial class MainWindow
{
    // The ONE drag-pose selector; the effective mode also enforces the flag gate.
    private string PilotDragClipPath => PilotPhysicsEffectiveMode is PhysicsMode.Gravity or PhysicsMode.PlanarInertia
        ? ScruffGifPath : DraggedGifPath;
    // The pose this gesture actually admitted. A PhysicsMode change mid-gesture
    // must not make the owner lose track of the clip it switched to.
    private string? _pilotDragVisualClipPath;
    private bool PilotDragClipAdmitted =>
        _pilotDragVisualClipPath is { } admitted && _gifPlayer?.ResourcePath == admitted;
    // dragged.gif's entry pose is lengthened to 120 ms. scruff.gif is a 2 x 100 ms
    // source and keeps its decoded cadence; the compensation never applies to it.
    private TimeSpan? PilotDragEntryFirstFrameDuration =>
        _pilotDragVisualClipPath == DraggedGifPath ? DragEntryFirstFrameDuration : null;

    private bool _pilotDragVisualActive;         // drag pose footprint admitted and showing
    private bool _pilotDragVisualPendingAdmit;   // switched to the drag pose, admission queued
    private bool _pilotDragFacingRight = true;
    private double _pilotDragFacingExtremeX;     // physical px
    private long _pilotDragVisualRevision;
    private DispatcherOperation? _pilotDragVisualPending;
    private PilotVisualSnapshot? _pilotDragVisualPrevious;
    private PhysicalRect _pilotDragVisualAdmissionRect;
    private bool _pilotDragVisualNativeAttempted;
    private bool _pilotDragVisualFallback;
    private PhysicalRect? _pilotDragVisualRejectedRect,_pilotDragVisualRejectedTarget;
    private int _pilotDragVisualRejectionLogs;

    // This is a local rollback only when a fresh read proves no native change.
    // A failed writer may already have moved HWND; never infer its outcome from
    // its bool return, and never restore WPF to a RECT that is no longer real.
    private void PilotDragVisualReject(string reason,PhysicalRect old,PhysicalRect? target=null)
    {
        PilotActionVoiceEnd(ref _pilotDragVoiceId, completed: false);
        var snapshot=_pilotDragVisualPrevious;
        var before=new { width=PetSurface.Width,height=PetSurface.Height,anchor=_pilotVisualAnchorLocal };
        var held=_pilotVisualHeldIdentity;
        var rejectedPlayback=_gifPlayer?.PlaybackGeneration;
        _pilotDragVisualPrevious=null;
        _pilotDragVisualNativeAttempted=false;
        _pilotDragVisualRevision++;Abort(ref _pilotDragVisualPending);
        _pilotDragVisualPendingAdmit=false;_pilotDragVisualActive=false;
        PilotVisualCancelPending();PilotVisualClearHeld();
        _pilotVisualPresentationDeferred=false;_pilotVisualDirty=false;_pilotVisualResume=false;
        _pilotDragVisualFallback=false;
        var read=PilotNative.GetWindowRect(PilotHwnd,out var native);
        var restored=false;
        // Any divergence (including a post-write failure) uses the existing
        // safety termination. No compensating writer or relaxed check is added.
        if(snapshot!=null&&read&&native.Physical==old&&_pilotRect==old&&
            !_pilotStopped&&!_pilotClosing&&PilotMapping(out var scale)&&scale==_pilotScale&&
            snapshot.Path!=null&&(_crossScreen?CrossScreenGeometry.Covered(old,CrossWork):SingleMonitorDrag.Fits(old,_pilotMonitor.WorkArea))) {
            PilotVisualRestore(snapshot);
            _pilotDragVisualFallback=true; // before synchronous ClipChanged
            _pilotDragVisualRejectedRect=old;_pilotDragVisualRejectedTarget=target;
            if(_gifPlayer!=null) {
                _gifPlayer.PilotPresentationHeld=false;
                restored=_gifPlayer.TrySwitchTo(snapshot.Path);
                if(restored){_gifPlayer.PilotRestoreCurrentFrame();_gifPlayer.Resume();}
            }
            restored=restored&&PilotNative.GetWindowRect(PilotHwnd,out var post)&&post.Physical==old;
        }
        if(_pilotDragVisualRejectionLogs++<8)
            PilotDragVisualFact("drag-visual-rejected",reason,new { old,target,native=read?(PhysicalRect?)native.Physical:null,
                before,after=new {width=PetSurface.Width,height=PetSurface.Height,anchor=_pilotVisualAnchorLocal},
                heldSource=held==null?null:new {held.Path,held.Playback,held.Index,held.Gesture},
                rejectedPlayback,currentPlayback=_gifPlayer?.PlaybackGeneration,currentClip=_gifPlayer?.ResourcePath,
                grab=PilotVisualGrabActive,capture=PetSurface.IsMouseCaptured,
                presentationHeld=_gifPlayer?.PilotPresentationHeld,timer=_gifPlayer?.PilotFrameTimerEnabled,
                rollback=restored?"restored verified native viewport":"FAILED; native/owner/layout not provably restorable; safety stop" });
        if(!restored) {
            _pilotDragVisualFallback=false;
            if(_gifPlayer!=null){_gifPlayer.PilotPresentationHeld=false;_gifPlayer.Pause();}
            PilotStop("Drag visual rejection rollback unsafe: "+reason+"; native RECT/owner must remain trusted.");
        }
    }
    private bool _pilotDragFrameDiagnosticsEnabled;
    private int _pilotDragFrameAdvances, _pilotDragFrameSamples;
    private DateTimeOffset _pilotDragFrameStarted;

    private void PilotDragFrameAdmitted()
    {
        if (!_pilotDragFrameDiagnosticsEnabled || _gifPlayer == null) return;
        _pilotDragFrameAdvances = 0;
        _pilotDragFrameSamples = 0;
        _pilotDragFrameStarted = DateTimeOffset.UtcNow;
        _pilotLog?.Write("drag-frame-baseline", new { revision = _pilotDragVisualRevision,
            playbackGeneration = _gifPlayer.PlaybackGeneration, frameIndex = _gifPlayer.PilotFrameIndex,
            timerEnabled = _gifPlayer.PilotFrameTimerEnabled,
            presentationHeld = _gifPlayer.PilotPresentationHeld }, "CONTROLLED PLAYER STATE; SCREEN PIXELS PENDING");
    }

    private void PilotDragFrameAdvanced(object? sender, EventArgs e)
    {
        if (!_pilotDragFrameDiagnosticsEnabled || !_pilotDragVisualActive || _gifPlayer == null ||
            !ReferenceEquals(sender, _gifPlayer) || !PilotDragClipAdmitted) return;
        _pilotDragFrameAdvances++;
        if (_pilotDragFrameSamples >= 32 || _pilotDragFrameAdvances != 1 && _pilotDragFrameAdvances % 4 != 0) return;
        _pilotDragFrameSamples++;
        _pilotLog?.Write("drag-frame-progress", new { revision = _pilotDragVisualRevision,
            playbackGeneration = _gifPlayer.PlaybackGeneration, frameIndex = _gifPlayer.PilotFrameIndex,
            advances = _pilotDragFrameAdvances, elapsedMs = (DateTimeOffset.UtcNow - _pilotDragFrameStarted).TotalMilliseconds,
            presentationHeld = _gifPlayer.PilotPresentationHeld }, "PLAYER FRAME ADVANCED; SCREEN PIXELS PENDING");
    }

    private DispatcherTimer? _pilotReleaseFloatTimer;
    private DateTimeOffset _pilotReleaseFloatStarted;
    private long _pilotReleaseFloatRevision;

    private void PilotDragVisualFact(string kind, string reason, object? detail = null) => _pilotLog?.Write(kind,
        new { owner = "PilotDragVisual", revision = _pilotDragVisualRevision,
            generation = _pilotGeneration, gesture = _pilotGesture, source = _pilotSourceToken,
            hwnd = (long)PilotHwnd, gif = _gifPlayer?.ResourcePath, active = _pilotDragVisualActive,
            pending = _pilotDragVisualPendingAdmit, facingRight = _pilotDragFacingRight, reason, detail });

    // Called on every accepted drag move.
    private void PilotDragVisualEnsure(PhysicalPoint cursor)
    {
        if (_pilotDragVisualActive) { PilotDragUpdateFacing(cursor); return; }
        if (_pilotDragVisualPendingAdmit) return;
        // Retrying an identical or still-illegal prospective footprint would
        // switch clips on every MouseMove. Recheck geometry without presentation.
        if(_pilotDragVisualFallback&&_pilotDragVisualRejectedRect is {} rejected&&_pilotDragVisualRejectedTarget is {} denied) {
            var dx=_pilotRect.Left-rejected.Left;var dy=_pilotRect.Top-rejected.Top;
            var retry=new PhysicalRect(denied.Left+dx,denied.Top+dy,denied.Right+dx,denied.Bottom+dy);
            if(DragVisualPlacement.Resolve(_pilotRect,retry,_crossScreen?CrossWork:new[]{_pilotMonitor.WorkArea})==null)return;
        }
        PilotDragVisualBegin(cursor);
    }

    private void PilotDragVisualBegin(PhysicalPoint cursor)
    {
        if (_gifPlayer == null || _effectiveBaseIdlePath == null) return;
        var clip = PilotDragClipPath;
        if (_gifPlayer.ResourcePath == clip)
        {
            // Resource identity alone is not proof of admitted layout/playback.
            PilotStop("Unowned dragged clip cannot bypass visual admission.");return;
        }
        _pilotDragVisualPrevious=PilotVisualCapture();
        _pilotDragVisualNativeAttempted=false;
        _pilotDragVisualFallback=false;
        _pilotDragVisualPendingAdmit = true;
        _pilotDragFacingRight = true;
        _pilotDragFacingExtremeX = cursor.X;
        // Hold the current frame and pause presentation (the 15F.2 contract) before the
        // clip switch can make WPF Uniform remeasure a mismatched Image during capture.
        PilotVisualHoldGrabFrame();
        _pilotDragVisualClipPath = clip;
        if (!_gifPlayer.TrySwitchTo(clip))
        {
            // Original semantics: a missing/unsupported drag pose keeps the held old frame.
            PilotDragVisualReject("drag clip unavailable",_pilotRect);
            PilotDragVisualFact("drag-gif-unavailable", "held current frame; no drag visual");
            return;
        }
        // ClipChanged already ran PilotVisualQueue; during the grab that path only holds the frame.
        if (_pilotVisualHeldFrame != null) { PetImage.Source = _pilotVisualHeldFrame; _gifPlayer.Pause(); }
        _gifPlayer.PilotPresentationHeld = true;
        var generation = _pilotGeneration; var gesture = _pilotGesture;
        var token = _pilotSourceToken; var hwnd = PilotHwnd;
        var playback=_gifPlayer.PlaybackGeneration;
        var revision = ++_pilotDragVisualRevision;
        _pilotDragVisualPending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (revision != _pilotDragVisualRevision || generation != _pilotGeneration || gesture != _pilotGesture ||
                token != _pilotSourceToken || hwnd != PilotHwnd || playback!=_gifPlayer.PlaybackGeneration || !PilotDragClipAdmitted) return;
            _pilotDragVisualPending = null;
            if (!_pilotDragVisualPendingAdmit) return;
            if (!PilotCursor(out var fresh)) { PilotDragVisualEnd("admit cursor unavailable"); return; }
            PilotDragVisualAdmit(fresh);
        }));
        PilotDragVisualFact("drag-visual-begin", "clip switched; bounded admission queued");
    }

    private void PilotDragVisualAdmit(PhysicalPoint cursor)
    {
        if (_crossScreen) { PilotDragVisualAdmitCross(cursor); return; }
        PilotDragVisualAdmitSingle(cursor);
    }

    private void PilotDragVisualAdmitSingle(PhysicalPoint cursor)
    {
        if (!_pilotDragVisualPendingAdmit || _pilotDragVisualActive) return;
        if (!_pilotReady || _pilotStopped || _pilotClosing || _gifPlayer == null || !PilotDragClipAdmitted)
        { PilotDragVisualEnd("admit precondition"); return; }
        if (!PilotMapping(out var scale) || scale != _pilotScale || !PilotFresh) { PilotDragVisualEnd("admit mapping"); return; }
        if (!PilotNative.GetWindowRect(PilotHwnd, out var native) || !CrossScreenGeometry.Near(native.Physical, _pilotRect))
        { PilotDragVisualEnd("admit native RECT"); return; }
        var old = _pilotRect;
        var revision=_pilotDragVisualRevision;var generation=_pilotGeneration;var gesture=_pilotGesture;
        var token=_pilotSourceToken;var hwnd=PilotHwnd;var playback=_gifPlayer.PlaybackGeneration;
        bool Own()=>revision==_pilotDragVisualRevision&&generation==_pilotGeneration&&gesture==_pilotGesture&&
            token==_pilotSourceToken&&hwnd==PilotHwnd&&playback==_gifPlayer.PlaybackGeneration&&
            _pilotDragVisualPendingAdmit&&!_pilotStopped&&!_pilotClosing;
        _pilotDragVisualAdmissionRect=old;
        var anchor = new PhysicalPoint(old.Left + _pilotVisualAnchorLocal.X * scale.X, old.Top + _pilotVisualAnchorLocal.Y * scale.Y);
        _pilotDragVisualPrevious=PilotVisualCapture();
        PilotVisualArrange();
        _gifPlayer.PilotRestoreCurrentFrame(); // dragged frame now matches the arranged Width
        var w = Math.Round(PetSurface.Width * scale.X); var h = Math.Round(PetSurface.Height * scale.Y);
        var x = Math.Round(anchor.X - _pilotVisualAnchorLocal.X * scale.X);
        var y = Math.Round(anchor.Y - _pilotVisualAnchorLocal.Y * scale.Y);
        var target = new PhysicalRect(x, y, x + w, y + h);
        var natural=target;
        if(DragVisualPlacement.Resolve(old,natural,new[]{_pilotMonitor.WorkArea}) is not {} legal)
        { PilotDragVisualReject("admit footprint has no safe WorkArea placement",old,natural); return; }
        target=legal;
        _pilotDragVisualNativeAttempted=true;
        if (!PilotPlace(target, "drag visual")) { if(Own())PilotDragVisualReject("admit placement rejected",old,target); return; }
        if(!Own())return;
        using (PilotLayoutScope()) { Width = PetSurface.Width; Height = PetSurface.Height; UpdateLayout(); }
        if(!Own())return;
        if (!PilotNative.GetWindowRect(PilotHwnd, out var post) || post.Physical != target)
        { PilotDragVisualReject("admit postcheck",old,target); return; }
        _pilotDrag.Rebase(cursor, target);
        PilotPhysicsRebaseDragSamples(old,target);
        PilotVisualClearHeld();_pilotDragVisualPrevious=null;_pilotDragVisualFallback=false;_pilotDragVisualNativeAttempted=false;
        _gifPlayer.PilotPresentationHeld = false;
        if (PilotDragEntryFirstFrameDuration is { } entry) _gifPlayer.SetDragEntryFirstFrameDuration(entry);
        _gifPlayer.Resume();
        _pilotVisualArrangedFrame = PetImage.Source as BitmapSource;
        _pilotDragVisualPendingAdmit = false;
        _pilotDragVisualActive = true;
        PilotDragFrameAdmitted();
        PilotDragApplyMirror();
        PilotDragVisualFact("drag-visual-admitted", "controlled footprint admission during grab",
            new { old, natural, target, grab=new PhysicalVector(cursor.X-target.Left,cursor.Y-target.Top),anchorLocal = _pilotVisualAnchorLocal });
        PilotDragVoiceAdmitted();
    }

    private void PilotDragVisualAdmitCross(PhysicalPoint cursor)
    {
        if (!_pilotDragVisualPendingAdmit || _pilotDragVisualActive) return;
        if (!CrossLive || !PilotMapping(out var scale) || scale != _pilotScale || !PilotFresh || _crossRecovery?.Active == true)
        { PilotDragVisualEnd("cross admit precondition"); return; }
        if (!PilotNative.GetWindowRect(PilotHwnd, out var native) || !CrossScreenGeometry.Near(native.Physical, _pilotRect))
        { PilotDragVisualEnd("cross admit native RECT"); return; }
        var old = _pilotRect;
        var revision=_pilotDragVisualRevision;var generation=_pilotGeneration;var gesture=_pilotGesture;
        var token=_pilotSourceToken;var hwnd=PilotHwnd;var playback=_gifPlayer!.PlaybackGeneration;
        bool Own()=>revision==_pilotDragVisualRevision&&generation==_pilotGeneration&&gesture==_pilotGesture&&
            token==_pilotSourceToken&&hwnd==PilotHwnd&&playback==_gifPlayer.PlaybackGeneration&&
            _pilotDragVisualPendingAdmit&&!_pilotStopped&&!_pilotClosing;
        _pilotDragVisualAdmissionRect=old;
        var anchor = new PhysicalPoint(old.Left + _pilotVisualAnchorLocal.X * scale.X, old.Top + _pilotVisualAnchorLocal.Y * scale.Y);
        _pilotDragVisualPrevious=PilotVisualCapture();
        PilotVisualArrange();
        _gifPlayer!.PilotRestoreCurrentFrame();
        var w = Math.Round(PetSurface.Width * scale.X); var h = Math.Round(PetSurface.Height * scale.Y);
        var x = Math.Round(anchor.X - _pilotVisualAnchorLocal.X * scale.X);
        var y = Math.Round(anchor.Y - _pilotVisualAnchorLocal.Y * scale.Y);
        var target = new PhysicalRect(x, y, x + w, y + h);
        var natural=target;
        if(DragVisualPlacement.Resolve(old,natural,CrossWork) is not {} legal)
        { PilotDragVisualReject("cross admit no covered corridor",old,natural); return; }
        target=legal;
        _pilotDragVisualNativeAttempted=true;
        if (!CrossPlace(target, "drag visual")) { if(Own())PilotDragVisualReject("cross admit placement rejected",old,target); return; }
        if(!Own())return;
        using (PilotLayoutScope()) { Width = PetSurface.Width; Height = PetSurface.Height; UpdateLayout(); }
        if(!Own())return;
        var checkpoint = CrossCheckpoint();
        if (!checkpoint.Sizes || !checkpoint.Geometry || !PilotNative.GetWindowRect(PilotHwnd, out var post) || post.Physical != target)
        { PilotDragVisualReject("cross admit postcheck",old,target); return; }
        _crossOrigin = new(target.Left, target.Top);
        _crossGrab = new(cursor.X - target.Left, cursor.Y - target.Top);
        PilotPhysicsRebaseDragSamples(old,target);
        if (_seamHandoff) _seamGrabFraction = new(_crossGrab.X / target.Width, _crossGrab.Y / target.Height);
        PilotVisualClearHeld();_pilotDragVisualPrevious=null;_pilotDragVisualFallback=false;_pilotDragVisualNativeAttempted=false;
        _gifPlayer.PilotPresentationHeld = false;
        if (PilotDragEntryFirstFrameDuration is { } entry) _gifPlayer.SetDragEntryFirstFrameDuration(entry);
        _gifPlayer.Resume();
        _pilotVisualArrangedFrame = PetImage.Source as BitmapSource;
        _pilotDragVisualPendingAdmit = false;
        _pilotDragVisualActive = true;
        PilotDragFrameAdmitted();
        PilotDragApplyMirror();
        PilotDragVisualFact("drag-visual-admitted", "controlled cross-screen footprint admission",
            new { old, natural, target, grab=_crossGrab,sizes = checkpoint.Sizes, geometry = checkpoint.Geometry });
        PilotDragVoiceAdmitted();
    }

    // Reset the drag visual and restore idle. The existing PilotVisualQueue admits the
    // idle footprint after the current gesture is fully revoked.
    private void PilotDragVisualEnd(string reason)
    {
        PilotActionVoiceEnd(ref _pilotDragVoiceId, completed: false);
        var ownedMirror=_pilotDragVisualActive||_pilotDragVisualPendingAdmit;
        if(_pilotDragVisualPrevious!=null) {
            PilotDragVisualReject(reason,_pilotDragVisualNativeAttempted?_pilotDragVisualAdmissionRect:_pilotRect);
            if(_pilotStopped||_pilotClosing)return;
            // End revokes the grab visual, unlike a geometry refusal while the
            // grab continues. A retained pre-grab Sleep/Rest/action clip must
            // still take the ordinary held-frame -> idle admission on release.
        }
        var wasActive=_pilotDragVisualActive;
        var fallback=_pilotDragVisualFallback;
        _pilotDragVisualFallback=false;_pilotDragVisualRejectedRect=null;_pilotDragVisualRejectedTarget=null;
        _pilotDragVisualRejectionLogs=0;
        var hadState = _pilotDragVisualActive || _pilotDragVisualPendingAdmit || _pilotDragVisualPending != null;
        if (_pilotDragFrameDiagnosticsEnabled && _pilotDragVisualActive)
            _pilotLog?.Write("drag-frame-summary", new { revision = _pilotDragVisualRevision,
                playbackGeneration = _gifPlayer?.PlaybackGeneration, advances = _pilotDragFrameAdvances,
                elapsedMs = (DateTimeOffset.UtcNow - _pilotDragFrameStarted).TotalMilliseconds,
                lastFrameIndex = _gifPlayer?.PilotFrameIndex, reason },
                "PLAYER PROGRESS SUMMARY; SCREEN PIXELS PENDING");
        _pilotDragVisualRevision++;
        Abort(ref _pilotDragVisualPending);
        _pilotDragVisualPendingAdmit = false;
        _pilotDragVisualActive = false;
        var transform = (ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0];
        // Only a real drag owner surrenders the drag mirror. Otherwise the live
        // attachment owns its side, or the ordinary default cleanup still applies.
        transform.ScaleX = !ownedMirror&&_pilotAttachmentTarget!=null
            ? _pilotAttachmentPoseSide==AttachmentSide.Right?-1:1
            : 1;
        if (_gifPlayer != null && _effectiveBaseIdlePath != null &&
            (wasActive&&PilotDragClipAdmitted || fallback&&_gifPlayer.ResourcePath!=_effectiveBaseIdlePath))
        {
            PilotVisualKeepCurrentFrame();
            _gifPlayer.PilotPresentationHeld = true; _gifPlayer.Pause();
            if (_pilotVisualHeldFrame != null) PetImage.Source = _pilotVisualHeldFrame;
            if (!_pilotGravityThrowReleaseSelected && !_pilotPlanarThrowReleaseSelected && !_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath))
                PilotStop("Drag visual end could not restore idle.");
        }
        _pilotDragVisualClipPath = null; // the next gesture re-selects its own pose
        if (hadState) PilotDragVisualFact("drag-visual-end", reason);
    }

    private void PilotDragUpdateFacing(PhysicalPoint cursor)
    {
        if (!_pilotDragVisualActive) return;
        var threshold = DragFacingThreshold * _pilotScale.X; // 6 DIP in physical px
        var x = cursor.X;
        if (_pilotDragFacingRight)
        {
            if (x <= _pilotDragFacingExtremeX - threshold) { _pilotDragFacingRight = false; _pilotDragFacingExtremeX = x; PilotDragApplyMirror(); }
            else _pilotDragFacingExtremeX = Math.Max(_pilotDragFacingExtremeX, x);
        }
        else if (x >= _pilotDragFacingExtremeX + threshold)
        { _pilotDragFacingRight = true; _pilotDragFacingExtremeX = x; PilotDragApplyMirror(); }
        else _pilotDragFacingExtremeX = Math.Min(_pilotDragFacingExtremeX, x);
    }

    private void PilotDragApplyMirror()
    {
        var transform = (ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0];
        transform.ScaleX = _pilotDragVisualActive && !_pilotDragFacingRight ? -1 : 1;
    }

    private void PilotReleaseFloatBegin()
    {
        if (_pilotReleaseFloatTimer != null || _pilotAttachmentTarget != null || _pilotStopped || _pilotClosing) return;
        var revision = ++_pilotReleaseFloatRevision;
        _pilotReleaseFloatStarted = DateTimeOffset.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _pilotReleaseFloatTimer = timer;
        timer.Tick += (_, _) => { if (ReferenceEquals(timer, _pilotReleaseFloatTimer) && revision == _pilotReleaseFloatRevision) PilotReleaseFloatTick(timer, revision); };
        timer.Start();
        _pilotLog?.Write("release-float-begin", new { revision, owner = "PilotDragVisual" });
    }

    private void PilotReleaseFloatTick(DispatcherTimer timer, long revision)
    {
        var progress = (DateTimeOffset.UtcNow - _pilotReleaseFloatStarted).TotalMilliseconds / 460.0;
        if (progress >= 1) { PilotReleaseFloatCancel("completed"); return; }
        var p = Math.Clamp(progress, 0, 1);
        var lift = Math.Sin(Math.PI * p);
        var fade = 1 - p;
        var group = (TransformGroup)PetImage.RenderTransform;
        var scale = (ScaleTransform)group.Children[0];
        var rotate = (RotateTransform)group.Children[1];
        var translate = (TranslateTransform)group.Children[2];
        scale.ScaleX = 1 + 0.01 * lift;
        scale.ScaleY = 1 + 0.01 * lift;
        rotate.Angle = Math.Sin(Math.PI * 3 * p) * 1.8 * fade;
        translate.X = Math.Sin(Math.PI * 3 * p) * 5 * fade;
        translate.Y = -12 * lift; // local rise; PMv2 viewport is tightly fit (no legacy 48 DIP padding)
    }

    private void PilotReleaseFloatCancel(string reason)
    {
        if (_pilotReleaseFloatTimer == null) return;
        _pilotReleaseFloatRevision++;
        _pilotReleaseFloatTimer?.Stop();
        _pilotReleaseFloatTimer = null;
        var group = (TransformGroup)PetImage.RenderTransform;
        ((ScaleTransform)group.Children[0]).ScaleX = 1;
        ((ScaleTransform)group.Children[0]).ScaleY = 1;
        ((RotateTransform)group.Children[1]).Angle = 0;
        ((TranslateTransform)group.Children[2]).X = 0;
        ((TranslateTransform)group.Children[2]).Y = 0;
        _pilotLog?.Write("release-float-end", new { reason, owner = "PilotDragVisual" });
    }
}
