using System;

namespace NecoArcDesktopPet.Pilot.Physics;

internal sealed record PhysicsTuning
{
    internal double FixedSeconds { get; init; } = 1d / 120;
    internal int MaximumSubsteps { get; init; } = 8;
    internal double MaximumElapsed { get; init; } = .25;
    internal double Gravity { get; init; } = 1800;                 // px/s^2, downward
    internal double AirDrag { get; init; } = .08;                // 1/s
    internal double PlanarDrag { get; init; } = 3;               // 1/s
    internal double GroundFriction { get; init; } = 900;          // px/s^2
    internal double WallRestitution { get; init; } = .45;         // Planar normal reflection; tangential speed preserved
    internal double PlanarBounceRetention { get; init; } = 1;    // 1 disables the opt-in whole-vector contact rule
    internal double PlanarBounceMinimumSpeed { get; init; } = 0; // px/s incoming contact normal; strict greater-than
    // 19C.2-R1 Gravity-only contact rule. Ceiling and side walls mirror the
    // velocity and retain this fraction of the whole vector. A contact below
    // the incoming normal-speed tolerance is absorbed instead of micro-bouncing.
    internal double GravityWallBounce { get; init; } = .75;       // dimensionless, whole vector
    internal double GravityWallBounceMinimumSpeed { get; init; } = 40; // px/s incoming normal
    internal double GroundRestitution { get; init; } = .22;       // dimensionless
    internal double MinimumBounceSpeed { get; init; } = 160;     // incoming normal px/s
    internal double StopSpeed { get; init; } = 4;                // px/s
    internal int MaximumBounces { get; init; } = 2;
    internal double SupportHysteresis { get; init; } = 8;         // overlap px; coplanar surfaces only
    internal double MaximumSpeed { get; init; } = 20000;         // vector magnitude px/s
    internal VerticalBoundaryRule PlanarVerticalRule { get; init; } = VerticalBoundaryRule.Reflect;

    internal bool IsValid => In(FixedSeconds, 1d / 1000, 1d / 30) && MaximumSubsteps is >= 1 and <= 32 &&
        In(MaximumElapsed, FixedSeconds, .25) && In(Gravity, 0, 100000) &&
        In(AirDrag, 0, 100) && In(PlanarDrag, 0, 100) && In(GroundFriction, 0, 100000) &&
        In(WallRestitution, 0, 1) && In(PlanarBounceRetention, 0, 1) &&
        In(PlanarBounceMinimumSpeed, 0, 10000) && In(GravityWallBounce, 0, 1) &&
        In(GravityWallBounceMinimumSpeed, 0, 10000) && In(GroundRestitution, 0, .5) &&
        In(MinimumBounceSpeed, 1, 100000) && In(StopSpeed, 0, 1000) &&
        MaximumBounces is >= 0 and <= 2 && In(SupportHysteresis, 0, 10000) &&
        In(MaximumSpeed, 1, 100000) && Enum.IsDefined(PlanarVerticalRule);

    internal static bool In(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
