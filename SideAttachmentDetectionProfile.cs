using System;

namespace NecoArcDesktopPet;

// Entry sensitivity only. Keep accepted contact/alignment profiles untouched.
internal static class SideAttachmentDetectionProfile
{
    public const double MinimumDistance = 48;
    public const double HeightFactor = 0.53;
    public const double MaximumDistance = 140;
    private const double BoundaryTolerance = 0.0000001;

    // The optional maximum only tightens the historical clamp; null keeps it intact.
    public static double SnapDistance(double petHeight, double? maximumDistanceDip = null)
    {
        var snap = Math.Clamp(petHeight * HeightFactor, MinimumDistance, MaximumDistance);
        return maximumDistanceDip is double maximum ? Math.Min(snap, maximum) : snap;
    }

    // Subtracting screen coordinates can round the same left/right boundary differently.
    // The cap is applied before the existing tolerance, so a tightened boundary keeps the
    // exact same rounding allowance as the historical one.
    public static bool IsWithinSnapDistance(double distance, double petHeight, double? maximumDistanceDip = null) =>
        distance <= SnapDistance(petHeight, maximumDistanceDip) + BoundaryTolerance;
}
