using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal enum PlanarAdvance { Hold, Move, Stop, Invalid }

// A single, immutable WorkArea track. Native placement belongs to the adapter.
// Actual is integer reality; State.Rect retains its independent fractional residue.
internal sealed class PlanarInertiaSession
{
    private readonly PhysicsBounds bounds;
    private readonly PhysicsTuning tuning = new() {
        WallRestitution = 1, PlanarVerticalRule = VerticalBoundaryRule.Reflect,
        PlanarBounceRetention = .85, PlanarBounceMinimumSpeed = 4
    };
    private PhysicalRect? requested;
    internal PhysicsState State { get; private set; }
    internal PhysicalRect Actual { get; private set; }
    internal PhysicsResult? LastResult { get; private set; }
    internal bool Stopped { get; private set; }

    internal PlanarInertiaSession(PhysicalRect rect, PhysicalVelocity velocity, PhysicalRect work)
    {
        Actual = rect;
        State = new(rect, velocity);
        bounds = PhysicsBounds.FromWorkAreas([work]);
    }

    internal PlanarAdvance Advance(double elapsed, PhysicalRect actual, out PhysicalRect target)
    {
        target = default;
        if (Stopped) return PlanarAdvance.Stop;
        if (actual != Actual || requested != null) return End(PlanarAdvance.Invalid);
        var result = LastResult = PhysicsStep.Advance(State, PhysicsMode.PlanarInertia, elapsed, bounds, tuning);
        if (!result.Success || result.State.Phase == PhysicsPhase.Invalid) return End(PlanarAdvance.Invalid);
        // A late frame still ends safely without placement. Reflected contacts
        // follow the same requested-target/exact-readback protocol as free motion.
        if (result.Failure == PhysicsFailure.LateFrameDiscarded)
            return End(PlanarAdvance.Stop);
        State = result.State;
        if (!bounds.TryRoundedTarget(State.Rect, out target) || !Corridor(Actual, target))
            return End(PlanarAdvance.Invalid);
        if (target != Actual) { requested = target; return PlanarAdvance.Move; }
        return State.Phase == PhysicsPhase.Settled ? End(PlanarAdvance.Stop) : PlanarAdvance.Hold;
    }

    private bool Corridor(PhysicalRect a, PhysicalRect b) => bounds.Covered(new(
        System.Math.Min(a.Left,b.Left), System.Math.Min(a.Top,b.Top),
        System.Math.Max(a.Right,b.Right), System.Math.Max(a.Bottom,b.Bottom)));

    internal bool Accept(PhysicalRect readback)
    {
        if (Stopped || requested is not {} expected || readback != expected) { End(PlanarAdvance.Invalid); return false; }
        Actual = readback; requested = null;
        // An exact readback reconciles reality without quantizing State.Rect.
        if (State.Phase == PhysicsPhase.Settled) End(PlanarAdvance.Stop);
        return true;
    }

    private PlanarAdvance End(PlanarAdvance reason) { Stopped = true; requested = null; return reason; }
}
