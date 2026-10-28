using System;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal enum PhysicsMode { Off, Gravity, PlanarInertia }
internal enum PhysicsPhase { Inactive, Airborne, Sliding, Settled, Invalid }
internal enum CollisionKind { LeftWall, RightWall, Ceiling, Ground }
internal enum VerticalBoundaryRule { Reflect, Stop }
internal enum PhysicsFailure { None, InvalidInput, InvalidTuning, InvalidTopology, LateFrameDiscarded, BudgetTimeDiscarded }

// Rect is the logical body footprint in physical px, not a GIF canvas. Its double
// edges are the integration state. Rounded targets must never feed back into it.
internal readonly record struct PhysicsState(
    PhysicalRect Rect, PhysicalVelocity Velocity, PhysicsPhase Phase = PhysicsPhase.Airborne,
    int BounceCount = 0, double PreviousBounceSpeed = 0, double Accumulator = 0, string? SupportId = null);

internal readonly record struct PhysicsImpact(CollisionKind Kind, double NormalSpeed, PhysicalRect Contact, int BounceCount);
internal sealed record PhysicsResult(PhysicsState State, PhysicsFailure Failure, PhysicsImpact[] Impacts, int Substeps, double DroppedSeconds)
{
    internal bool Success => Failure is PhysicsFailure.None or PhysicsFailure.BudgetTimeDiscarded or PhysicsFailure.LateFrameDiscarded;
}

internal readonly record struct GroundSupport(string Id, double Floor, double Overlap);
internal readonly record struct AxisSweep(PhysicalRect Rect, bool Hit);
