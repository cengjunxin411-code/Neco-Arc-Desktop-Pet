#if PMV2_PILOT
using System;
using System.Windows.Input;
using System.Windows.Media;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

namespace NecoArcDesktopPet;

public partial class MainWindow
{
    private const string PilotPlanarThrowPath = "NecoPetMaterials/drag/throw_fly_left.gif";
    // Synchronous MouseUp only: preserve the admitted scruff through revocation.
    private bool _pilotPlanarThrowReleaseSelected;
    private sealed record PilotPlanarThrowVisual(PhysicsOwnerIdentity Identity,
        DpiScale Scale, object? Source, long Playback);
    private PilotPlanarThrowVisual? _pilotPlanarThrowVisual;
    private bool _pilotPlanarThrowFacingRight;

    private bool PilotPlanarThrowCanSelect(PilotPhysicsRelease? release) =>
        release != null && PilotPhysicsEffectiveMode == PhysicsMode.PlanarInertia &&
        _pilotDragVisualActive && PilotDragClipAdmitted && _pilotDragVisualClipPath == ScruffGifPath;

    private bool PilotPlanarThrowOwnsClip => _pilotPlanarThrowVisual is {} owner &&
        PilotPhysicsEffectiveMode == PhysicsMode.PlanarInertia &&
        owner.Identity.Generation == _pilotGeneration && owner.Identity.Gesture == _pilotGesture &&
        owner.Identity.SourceToken == _pilotSourceToken && owner.Identity.Hwnd == (long)PilotHwnd &&
        owner.Identity.TopologyVersion == _pilotTopology?.State.LastKnownGood?.Version &&
        owner.Scale == _pilotScale && ReferenceEquals(owner.Source, _pilotSource) &&
        _gifPlayer?.ResourcePath == PilotPlanarThrowPath && _gifPlayer.PlaybackGeneration == owner.Playback;

    private bool PilotPlanarThrowBegin(PhysicalVelocity velocity)
    {
        if (_gifPlayer == null) return false;
        _pilotPlanarThrowFacingRight = Math.Abs(velocity.X) > 4 ? velocity.X > 0 : _pilotDragFacingRight;
        _pilotPlanarThrowVisual = new(PilotPhysicsSnapshot().Identity, _pilotScale,
            _pilotSource, _gifPlayer.PlaybackGeneration + 1);
        PetSurface.LostMouseCapture += PilotPlanarThrowLostCapture;
        // This asset faces LEFT. Set its horizontal transform before measuring.
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX =
            _pilotPlanarThrowFacingRight ? -1 : 1;
        if (_gifPlayer.TrySwitchToHoldingTail(PilotPlanarThrowPath, 1)) {
            PilotPhysicsFact("physics-planar-throw", "clip selected before lease; viewport admission pending");
            return true;
        }
        // No clip switch happened; scruff is still the admitted held frame.
        _pilotPlanarThrowVisual = null;
        PetSurface.LostMouseCapture -= PilotPlanarThrowLostCapture;
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
        return false;
    }

    private void PilotPlanarThrowFace(PhysicalVelocity velocity)
    {
        if (!PilotPlanarThrowOwnsClip) return;
        if (double.IsFinite(velocity.X) && Math.Abs(velocity.X) > 4)
            _pilotPlanarThrowFacingRight = velocity.X > 0;
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX =
            _pilotPlanarThrowFacingRight ? -1 : 1;
    }

    private void PilotPlanarThrowLostCapture(object sender, MouseEventArgs e)
    {
        if (!_pilotClickReleasing && _pilotPlanarThrowVisual != null)
            PilotCancelGesture("Planar throw lost mouse capture");
    }

    private void PilotPlanarThrowEnd(string reason)
    {
        PilotActionVoiceEnd(ref _pilotPlanarThrowVoiceId, completed: false);
        var owner = _pilotPlanarThrowVisual;
        _pilotPlanarThrowVisual = null; // revoke before synchronous ClipChanged/Yield
        PetSurface.LostMouseCapture -= PilotPlanarThrowLostCapture;
        if (owner == null) return;
        if (_gifPlayer?.ResourcePath != PilotPlanarThrowPath || _gifPlayer.PlaybackGeneration != owner.Playback) {
            // A successor owns its mirror. Only unowned Idle can need cleanup
            // after the existing visual queue has rejected the throw footprint.
            if (_gifPlayer?.ResourcePath == _effectiveBaseIdlePath && !_pilotDragVisualActive &&
                !_pilotDragVisualPendingAdmit && _pilotAttachmentTarget == null && !_pilotMoveActive)
                ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
            return;
        }
        ((ScaleTransform)((TransformGroup)PetImage.RenderTransform).Children[0]).ScaleX = 1;
        if (_pilotStopped || _pilotClosing) { _gifPlayer.Pause(); return; }
        PilotPhysicsFact("physics-planar-throw-end", reason);
        if (_effectiveBaseIdlePath == null || !_gifPlayer.TrySwitchTo(_effectiveBaseIdlePath))
            PilotStop("Planar throw could not restore idle.");
    }
}
#endif
