using System;

namespace NecoArcDesktopPet.Coordinates;

// Injected data only: no HWND, OS enumeration, persistence or topology lifecycle.
internal readonly record struct MonitorSnapshot(string Id, PhysicalRect Bounds, PhysicalRect WorkArea)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && Bounds.IsValid && WorkArea.IsValid &&
        WorkArea.Left >= Bounds.Left && WorkArea.Right <= Bounds.Right &&
        WorkArea.Top >= Bounds.Top && WorkArea.Bottom <= Bounds.Bottom;
}

// Conservative logical body occupancy relative to Ground Anchor, in physical px.
// No GIF canvas, alpha boundary, transparent padding or below-ground visual effects.
internal readonly record struct PhysicalFootprint(double LeftExtent, double RightExtent, double HeightAboveGround)
{
    public double Width => LeftExtent + RightExtent;
    public bool IsValid => CoordinateNumbers.Finite(LeftExtent, RightExtent) && double.IsFinite(HeightAboveGround) &&
        double.IsFinite(Width) && LeftExtent >= 0 && RightExtent >= 0 && HeightAboveGround >= 0;
}

// Closed physical px intervals. Exact fit may have equal minimum and maximum.
internal readonly record struct AnchorRange(double MinimumX, double MaximumX, double MinimumY, double MaximumY)
{
    public bool IsValid => CoordinateNumbers.Finite(MinimumX, MaximumX) && CoordinateNumbers.Finite(MinimumY, MaximumY) &&
        MinimumX <= MaximumX && MinimumY <= MaximumY &&
        CoordinateNumbers.Finite(MaximumX - MinimumX, MaximumY - MinimumY);
}

internal enum PlacementFailure
{
    NotEvaluated,
    None,
    InvalidPoint,
    InvalidFootprint,
    NoValidMonitors,
    DuplicateMonitorId,
    FootprintDoesNotFit,
    NumericalOverflow
}

internal readonly record struct PlacementResult(PlacementFailure Failure, string? MonitorId, PhysicalPoint GroundAnchor)
{
    // On failure the default finite point is a placeholder, never a placement instruction.
    public bool Success => Failure == PlacementFailure.None && !string.IsNullOrWhiteSpace(MonitorId) && GroundAnchor.IsFinite;
}
