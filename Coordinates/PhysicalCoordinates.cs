using System;

namespace NecoArcDesktopPet.Coordinates;

// Absolute virtual-desktop position in physical px. Never DPI-scale this point.
internal readonly record struct PhysicalPoint(double X, double Y)
{
    public bool IsFinite => CoordinateNumbers.Finite(X, Y);

    public bool TryTranslate(PhysicalVector offset, out PhysicalPoint point)
    {
        point = default;
        if (!IsFinite || !offset.IsFinite) return false;
        var candidate = new PhysicalPoint(X + offset.X, Y + offset.Y);
        if (!candidate.IsFinite) return false;
        point = candidate;
        return true;
    }
}

// Displacement in physical px, distinct from a world point and from px/s.
internal readonly record struct PhysicalVector(double X, double Y)
{
    public bool IsFinite => CoordinateNumbers.Finite(X, Y);

    public static bool TryBetween(PhysicalPoint from, PhysicalPoint to, out PhysicalVector vector)
    {
        vector = default;
        if (!from.IsFinite || !to.IsFinite) return false;
        var candidate = new PhysicalVector(to.X - from.X, to.Y - from.Y);
        if (!candidate.IsFinite) return false;
        vector = candidate;
        return true;
    }
}

// Physical px/s. This does not reinterpret the existing DIP/s settings.
internal readonly record struct PhysicalVelocity(double X, double Y)
{
    public bool IsFinite => CoordinateNumbers.Finite(X, Y);

    public bool TryGetDisplacement(double elapsedSeconds, out PhysicalVector vector)
    {
        vector = default;
        if (!IsFinite || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return false;
        var candidate = new PhysicalVector(X * elapsedSeconds, Y * elapsedSeconds);
        if (!candidate.IsFinite || (elapsedSeconds != 0 &&
            (!CoordinateNumbers.PreservesNonzero(X, candidate.X) || !CoordinateNumbers.PreservesNonzero(Y, candidate.Y)))) return false;
        vector = candidate;
        return true;
    }
}

// Local layout length/displacement in DIP; no absolute screen origin.
internal readonly record struct LocalDipVector(double X, double Y)
{
    public bool IsFinite => CoordinateNumbers.Finite(X, Y);
}

// Physical px per local DIP. Supply the relevant rendering boundary's scales.
internal readonly record struct DpiScale(double X, double Y)
{
    public bool IsValid => CoordinateNumbers.Finite(X, Y) && X > 0 && Y > 0;

    public bool TryToPhysical(LocalDipVector local, out PhysicalVector physical)
    {
        physical = default;
        if (!IsValid || !local.IsFinite) return false;
        var candidate = new PhysicalVector(local.X * X, local.Y * Y);
        if (!candidate.IsFinite || !CoordinateNumbers.PreservesNonzero(local.X, candidate.X) ||
            !CoordinateNumbers.PreservesNonzero(local.Y, candidate.Y)) return false;
        physical = candidate;
        return true;
    }

    public bool TryToLocalDip(PhysicalVector physical, out LocalDipVector local)
    {
        local = default;
        if (!IsValid || !physical.IsFinite) return false;
        var candidate = new LocalDipVector(physical.X / X, physical.Y / Y);
        if (!candidate.IsFinite || !CoordinateNumbers.PreservesNonzero(physical.X, candidate.X) ||
            !CoordinateNumbers.PreservesNonzero(physical.Y, candidate.Y)) return false;
        local = candidate;
        return true;
    }
}

// Physical px edges, not (X, Y, Width, Height). Positive finite area required.
internal readonly record struct PhysicalRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool IsValid => CoordinateNumbers.Finite(Left, Top) && CoordinateNumbers.Finite(Right, Bottom) &&
        CoordinateNumbers.Finite(Width, Height) && Width > 0 && Height > 0;

    // Inclusive edges intentionally admit Ground Anchor == WorkArea.Bottom.
    public bool ContainsClosed(PhysicalPoint point) => IsValid && point.IsFinite &&
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public static bool TryFromOriginAndSize(PhysicalPoint origin, double width, double height, out PhysicalRect rect)
    {
        rect = default;
        if (!origin.IsFinite || !CoordinateNumbers.Finite(width, height) || width <= 0 || height <= 0) return false;
        var candidate = new PhysicalRect(origin.X, origin.Y, origin.X + width, origin.Y + height);
        if (!candidate.IsValid) return false;
        rect = candidate;
        return true;
    }
}

internal static class CoordinateNumbers
{
    public static bool Finite(double x, double y) => double.IsFinite(x) && double.IsFinite(y);
    // Reject complete underflow instead of silently turning a nonzero length into zero.
    public static bool PreservesNonzero(double input, double output) => input == 0 || output != 0;
}
