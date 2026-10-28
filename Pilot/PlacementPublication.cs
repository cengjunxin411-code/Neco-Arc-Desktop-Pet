using NecoArcDesktopPet.Coordinates;
using DpiHwndProbe;

namespace NecoArcDesktopPet.Pilot;

internal static class PlacementPublication
{
    // UpdateLayout may synchronously replace the owner after native Place returned.
    // Admission and publication are separate checks; no old commit can restore a
    // new generation's suspended mapping merely because that new checkpoint is ready.
    internal static bool CanPublish(RecoveryOwner requested,RecoveryOwner current,bool live,
        DpiScale captured,DpiScale currentScale,bool currentMapping)=>
        requested==current&&live&&currentMapping&&captured.IsValid&&currentScale.IsValid&&captured==currentScale;
}
