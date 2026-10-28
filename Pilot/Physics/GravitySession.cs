using System;
using System.Linq;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal enum GravityAdvance { Hold, Move, Stop, Invalid }

// One complete WorkArea and one release snapshot. Only the adapter owns native
// placement. State keeps fractional pixels; Actual is the last exact readback.
internal sealed class GravitySession
{
    private readonly PhysicsBounds bounds;
    // 19C.2-R1 feel tuning: stronger Gravity-only air drag plus a mirror bounce
    // on ceiling and side walls that keeps 75% of the whole velocity vector.
    // P3-B: ground absorbs vertical speed; friction stays 900 px/s^2.
    private readonly PhysicsTuning tuning = new() {
        AirDrag = 1.0,
        GravityWallBounce = .75,
        GravityWallBounceMinimumSpeed = 40,
        GroundRestitution = 0
    };
    private PhysicalRect? requested;
    private double duration;
    internal PhysicsState State { get; private set; }
    internal PhysicalRect Actual { get; private set; }
    internal PhysicsResult? LastResult { get; private set; }
    internal bool Stopped { get; private set; }
    internal string? StopReason { get; private set; }
    internal bool Grounded => Stopped && State.Phase == PhysicsPhase.Settled && requested == null;
    internal bool HasGroundContact { get; private set; }

    internal GravitySession(PhysicalRect rect, PhysicalRect work, PhysicalVelocity release = default)
    {
        Actual = rect;
        State = new(rect, release);
        bounds = PhysicsBounds.FromWorkAreas([work]);
    }

    internal GravityAdvance Advance(double elapsed, PhysicalRect actual, out PhysicalRect target)
    {
        target = default;
        if (Stopped) return GravityAdvance.Stop;
        if (actual != Actual) return End(GravityAdvance.Invalid, "accepted RECT changed");
        if (requested != null) return End(GravityAdvance.Invalid, "pending native acceptance");
        if (!double.IsFinite(elapsed) || elapsed < 0) return End(GravityAdvance.Invalid, "invalid elapsed");
        if ((duration += elapsed) > 12) return End(GravityAdvance.Invalid, "12s session limit");
        var result = LastResult = PhysicsStep.Advance(State, PhysicsMode.Gravity, elapsed, bounds, tuning, gravityGroundLanding: true);
        if (!result.Success || result.State.Phase == PhysicsPhase.Invalid)
            return End(GravityAdvance.Invalid, "model " + result.Failure);
        // Both time discards are successful bounded results. Commit only the
        // computed substeps (<=8); discarded time is never accumulated or repaid.
        // 19C.2-R1: Gravity now mirrors and damps ceiling/side-wall contacts inside
        // PhysicsStep, then keeps integrating. A legal contact must never end the
        // session in mid-air; Ground is absorbed by the private tuning above.
        State = result.State;
        HasGroundContact |= result.Impacts.Any(i => i.Kind == CollisionKind.Ground);
        if (!bounds.TryRoundedTarget(State.Rect, out target) || !bounds.Covered(new(
            Math.Min(Actual.Left,target.Left), Math.Min(Actual.Top,target.Top),
            Math.Max(Actual.Right,target.Right), Math.Max(Actual.Bottom,target.Bottom))))
            return End(GravityAdvance.Invalid, "rounded target/corridor invalid");
        if (target != Actual) { requested = target; return GravityAdvance.Move; }
        return State.Phase == PhysicsPhase.Settled ? End(GravityAdvance.Stop, "settled") : GravityAdvance.Hold;
    }

    internal bool Accept(PhysicalRect readback)
    {
        if (Stopped || requested is not {} expected || readback != expected) {
            End(GravityAdvance.Invalid, "native acceptance mismatch"); return false;
        }
        Actual = readback; requested = null;
        if (State.Phase == PhysicsPhase.Settled) End(GravityAdvance.Stop, "settled");
        return true;
    }

    private GravityAdvance End(GravityAdvance reason, string detail)
    {
        StopReason = detail;
        Stopped = true; requested = null;
        if (reason == GravityAdvance.Invalid) State = State with { Phase = PhysicsPhase.Invalid };
        return reason;
    }
}
