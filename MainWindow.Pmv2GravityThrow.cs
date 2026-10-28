#if PMV2_PILOT
using System;
using System.Windows.Media;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private const string PilotGravityThrowPath = "NecoPetMaterials/drag/throw_away.gif";
    // Only true on the synchronous MouseUp revocation stack. No deferred owner
    // can retain it; the two drag/held-frame exits preserve scruff until planning.
    private bool _pilotGravityThrowReleaseSelected;
    private sealed record PilotGravityThrowVisual(PhysicsOwnerIdentity Identity,
        DpiScale Scale, object? Source, long Playback);
    private PilotGravityThrowVisual? _pilotGravityThrowVisual;
    private bool _pilotGravityThrowFacingRight = true;

    private bool PilotGravityThrowCanSelect(PilotPhysicsRelease? release) =>
        release != null && PilotPhysicsEffectiveMode == PhysicsMode.Gravity &&
        _pilotDragVisualActive && PilotDragClipAdmitted &&
        _pilotDragVisualClipPath == ScruffGifPath;

    // Path alone never admits Gravity. Bind this clip to the original release
    // identity, source object, native HWND, topology, DPI and player generation.
    private bool PilotGravityThrowOwnsClip => _pilotGravityThrowVisual is {} owner &&
        owner.Identity.Generation == _pilotGeneration && owner.Identity.Gesture == _pilotGesture &&
        owner.Identity.SourceToken == _pilotSourceToken && owner.Identity.Hwnd == (long)PilotHwnd &&
        owner.Identity.TopologyVersion == _pilotTopology?.State.LastKnownGood?.Version &&
        owner.Scale == _pilotScale && ReferenceEquals(owner.Source, _pilotSource) &&
        _gifPlayer?.ResourcePath == PilotGravityThrowPath && _gifPlayer.PlaybackGeneration == owner.Playback;

    private bool PilotGravityThrowBegin(PhysicalVelocity velocity)
    {
        if (_gifPlayer == null) return false;
        _pilotGravityThrowFacingRight = Math.Abs(velocity.X) > 4 ? velocity.X > 0 : _pilotDragFacingRight;
        _pilotGravityThrowVisual = new(PilotPhysicsSnapshot().Identity, _pilotScale,
            _pilotSource, _gifPlayer.PlaybackGeneration + 1);
        // Set the direction before the sole visual transaction measures its union.
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX =
            _pilotGravityThrowFacingRight ? 1 : -1;
        if (_gifPlayer.TrySwitchToHoldingTail(PilotGravityThrowPath, 2))
        {
            PilotPhysicsFact("physics-gravity-throw", "clip selected before lease; viewport admission pending");
            return true;
        }
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
        PilotGravityThrowEnd("throw resource unavailable");
        return false;
    }

    private void PilotGravityThrowFace(PhysicalVelocity velocity)
    {
        if (!PilotGravityThrowOwnsClip) return;
        // Same physical dead zone as release qualification. A near-zero wall
        // absorption retains its last useful direction, without any clip event.
        if (double.IsFinite(velocity.X) && Math.Abs(velocity.X) > 4)
            _pilotGravityThrowFacingRight = velocity.X > 0;
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX =
            _pilotGravityThrowFacingRight ? 1 : -1;
    }

    private void PilotGravityThrowEnd(string reason)
    {
        PilotActionVoiceEnd(ref _pilotThrowVoiceId, completed: false);
        var owner = _pilotGravityThrowVisual;
        _pilotGravityThrowVisual = null; // before synchronous ClipChanged/Yield
        if (owner == null) return;
        // A later Drag/Attachment/Movement clip owns its transform already.
        // Never reset that owner's mirror or resurrect an old return callback.
        if (_gifPlayer?.ResourcePath != PilotGravityThrowPath || _gifPlayer.PlaybackGeneration != owner.Playback)
        {
            // The visual queue's legal rejection already switched to Idle. Idle
            // has no directional owner, so return its mirror too; never touch an
            // admitted Drag, Attachment or Movement successor.
            if (_gifPlayer?.ResourcePath == _effectiveBaseIdlePath && !_pilotDragVisualActive &&
                !_pilotDragVisualPendingAdmit && _pilotAttachmentTarget == null && !_pilotMoveActive)
                ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
            return;
        }
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
        if (_pilotStopped || _pilotClosing) { _gifPlayer.Pause(); return; }
        PilotPhysicsFact("physics-gravity-throw-end", reason);
        if (_effectiveBaseIdlePath == null || !_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath))
            PilotStop("Gravity throw could not restore idle.");
    }
}
#endif
