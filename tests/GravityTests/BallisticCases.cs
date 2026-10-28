using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

// Phase 19C.2 ballistic coverage. Every case drives the production GravitySession,
// which in turn drives the shared PhysicsStep.Advance(PhysicsMode.Gravity). There
// is deliberately no second parabola, no bespoke collision test and no DPI input.
static partial class Program
{
    const double BallisticDt = 1d / 120;
    static PhysicalRect Tall => new(0, 0, 4000, 4000);

    readonly record struct Snapshot(double Left, double Top, double Vx, double Vy, PhysicsPhase Phase, int Bounces);

    static (List<Snapshot> Track, List<PhysicsImpact> Impacts) Fly(GravitySession s, double dt, int limit)
    {
        var track = new List<Snapshot>();
        var impacts = new List<PhysicsImpact>();
        for (int i = 0; i < limit && !s.Stopped; i++)
        {
            var action = s.Advance(dt, s.Actual, out var target);
            if (action == GravityAdvance.Invalid)
                throw new Exception("ballistic session became Invalid at step " + i + ": " + s.StopReason);
            if (action == GravityAdvance.Move && !s.Accept(target))
                throw new Exception("own rounded target rejected by Accept at step " + i);
            if (s.LastResult is { } step) impacts.AddRange(step.Impacts);
            track.Add(new(s.State.Rect.Left, s.State.Rect.Top, s.State.Velocity.X, s.State.Velocity.Y,
                s.State.Phase, s.State.BounceCount));
        }
        return (track, impacts);
    }

    static (double X, double Y, double Vx, double Vy) Analytic(double x0, double y0, double vx0, double vy0, double t)
    {
        // Phase 19C.2-R1: the production Gravity air drag is 1.0 s^-1.
        const double k = 1.0, a = 1800;
        var decay = Math.Exp(-k * t);
        return (x0 + vx0 * (1 - decay) / k, y0 + a / k * t + (vy0 - a / k) / k * (1 - decay),
            vx0 * decay, a / k + (vy0 - a / k) * decay);
    }

    static void BallisticCases()
    {
        UpwardThrow();
        DownwardThrow();
        HorizontalThrows();
        DiagonalThrows();
        ZeroReleaseMatchesC1();
        AnalyticTrend();
        CeilingContact();
        SideWallContact();
        HighSpeedNoTunneling();
        BounceAndSlide();
        LateFrameResumes();
        LongTicksRetainSession();
        InvalidReleaseRejected();
        TwelveSecondLimit();
        ExternalActualRejected();
        VelocitySnapshotIsOneTime();
    }

    static void UpwardThrow()
    {
        var start = new PhysicalRect(1000, 3000, 1020, 3020);
        var s = new GravitySession(start, Tall, new(0, -1200));
        var (track, _) = Fly(s, BallisticDt, 1400);
        Check(track.Count > 8, "upward release produces a real trajectory");
        Check(track[0].Top < start.Top, "upward release rises on the first fixed step");
        var apex = 0;
        for (int i = 1; i < track.Count; i++) if (track[i].Top < track[apex].Top) apex = i;
        Check(apex > 4, "upward release keeps rising for multiple steps");
        Check(track.Skip(apex + 1).Any(p => p.Top > track[apex].Top + 1), "after the apex the body descends");
        Check(track.Take(apex).All(p => p.Vy <= 0) && track.Skip(apex).Any(p => p.Vy > 100),
            "vertical velocity reverses through the apex");
        const double k = 1.0, a = 1800;
        var tApex = Math.Log(a / (a - k * -1200)) / -k;
        var yApex = 3000 + a / k * tApex + ((-1200 - a / k) / k) * (1 - Math.Exp(-k * tApex));
        Check(Math.Abs(track[apex].Top - yApex) < .5, "apex height matches the closed-form drag/gravity solution");
        Check(s.Grounded && s.Actual.Bottom == Tall.Bottom, "upward release lands and settles");
    }

    static void DownwardThrow()
    {
        var start = new PhysicalRect(1000, 3000, 1020, 3020);
        var thrown = new GravitySession(start, Tall, new(0, 600));
        var (down, _) = Fly(thrown, BallisticDt, 1400);
        Check(down[0].Vy > 600, "downward release accelerates with gravity in the same direction");
        var drift = new GravitySession(start, Tall);
        var (zero, _) = Fly(drift, BallisticDt, 1400);
        Check(thrown.Grounded && thrown.Actual.Bottom == Tall.Bottom, "downward release settles on the floor");
        Check(down.Count < zero.Count, "downward release reaches the floor sooner than zero velocity");
        Check(down[0].Top > start.Top, "downward release starts moving down immediately");
    }

    static void HorizontalThrows()
    {
        foreach (var direction in new[] { 1, -1 })
        {
            var start = new PhysicalRect(10000, 1000, 10020, 1020);
            var wide = new PhysicalRect(0, 0, 20000, 4000);
            var s = new GravitySession(start, wide, new(900 * direction, 0));
            var (track, _) = Fly(s, BallisticDt, 1400);
            Check(Math.Sign(track[0].Left - start.Left) == direction, "horizontal release keeps its direction " + direction);
            Check(track[0].Top > start.Top, "horizontal release immediately loses height " + direction);
            var t = 36 * BallisticDt;
            var expected = Analytic(10000, 1000, 900 * direction, 0, t);
            Check(Math.Abs(track[35].Left - expected.X) < 1e-6 && Math.Abs(track[35].Top - expected.Y) < 1e-6,
                "horizontal trend matches the analytic solution " + direction);
            Check(s.Grounded && s.Actual.Bottom == wide.Bottom, "horizontal release still lands " + direction);
            Check(s.State.Velocity.X == 0, "ground friction removes the horizontal speed " + direction);
        }
    }

    static void DiagonalThrows()
    {
        foreach (var (dx, dy) in new[] { (1, -1), (1, 1), (-1, -1), (-1, 1) })
        {
            var start = new PhysicalRect(10000, 2000, 10020, 2020);
            var wide = new PhysicalRect(0, 0, 20000, 4000);
            var s = new GravitySession(start, wide, new(600 * dx, 600 * dy));
            var (track, _) = Fly(s, BallisticDt, 1400);
            Check(Math.Sign(track[0].Left - start.Left) == dx, "diagonal keeps the X quadrant " + dx + "," + dy);
            Check(dy < 0 ? track[0].Top < start.Top : track[0].Top > start.Top,
                "diagonal keeps the Y quadrant " + dx + "," + dy);
            if (dy < 0)
            {
                var apex = 0;
                for (int i = 1; i < track.Count; i++) if (track[i].Top < track[apex].Top) apex = i;
                Check(track.Skip(apex + 1).Any(p => p.Top > track[apex].Top + 1), "upward diagonal has rise and descent");
            }
            Check(s.Grounded && s.Actual.Bottom == wide.Bottom, "diagonal settles " + dx + "," + dy);
        }
    }

    static void ZeroReleaseMatchesC1()
    {
        var start = new PhysicalRect(1000, 3000, 1020, 3020);
        var implicitZero = new GravitySession(start, Tall);
        var explicitZero = new GravitySession(start, Tall, default);
        var (a, impactsA) = Fly(implicitZero, BallisticDt, 900);
        var (b, impactsB) = Fly(explicitZero, BallisticDt, 900);
        Check(a.Count > 0 && a.SequenceEqual(b), "explicit zero release is identical to the 19C.1 zero-velocity session");
        Check(impactsA.Count == impactsB.Count, "zero release keeps identical impact evidence");
        Check(implicitZero.Grounded && implicitZero.Actual.Bottom == Tall.Bottom, "zero release still settles on the floor");
        Check(a.All(p => p.Vx == 0), "zero release never invents horizontal movement");
    }

    static void AnalyticTrend()
    {
        foreach (var (vx, vy) in new[] { (600d, -800d), (-300d, 500d), (1500d, -1500d), (0d, 0d) })
        {
            var s = new GravitySession(new PhysicalRect(1000, 2000, 1020, 2020), Tall, new(vx, vy));
            var (track, _) = Fly(s, BallisticDt, 40);
            foreach (var step in new[] { 11, 23, 35 })
            {
                var t = (step + 1) * BallisticDt;
                var expected = Analytic(1000, 2000, vx, vy, t);
                Check(Math.Abs(track[step].Left - expected.X) < 1e-6 && Math.Abs(track[step].Top - expected.Y) < 1e-6,
                    "position follows the closed-form solver v0=" + vx + "," + vy);
                Check(Math.Abs(track[step].Vx - expected.Vx) < 1e-6 && Math.Abs(track[step].Vy - expected.Vy) < 1e-6,
                    "velocity follows the closed-form solver v0=" + vx + "," + vy);
            }
        }
    }

    static void CeilingContact()
    {
        var work = new PhysicalRect(0, 0, 400, 600);
        var s = new GravitySession(new(100, 300, 120, 320), work, new(0, -5000));
        var (track, impacts) = Fly(s, BallisticDt, 900);
        var ceilings = impacts.Where(i => i.Kind == CollisionKind.Ceiling).ToArray();
        Check(ceilings.Length == 1, "exactly one ceiling contact is reported");
        Check(track.All(p => p.Top >= -1e-9), "ceiling contact never leaves the legal WorkArea");
        var hit = track.FindIndex(p => Math.Abs(p.Top) < 1e-9);
        Check(hit > 0, "ceiling contact clamps the body exactly to the WorkArea top");
        Check(track[hit].Vy > 0, "upward normal velocity is mirrored downward, never zeroed");
        Check(Math.Abs(track[hit].Vy - .75 * ceilings[0].NormalSpeed) < 1e-9,
            "ceiling collision keeps exactly 75% of the incoming normal speed");
        Check(track.Skip(hit + 1).Any(p => p.Top > 1), "the reflected body falls immediately after the ceiling");
        Check(s.Grounded && s.Actual.Bottom == work.Bottom, "ceiling throw eventually lands instead of hanging");
        var settled = s.Actual;
        Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Stop && s.Actual == settled,
            "ceiling contact cannot replay after settling");
    }

    static void SideWallContact()
    {
        foreach (var direction in new[] { 1, -1 })
        {
            var work = new PhysicalRect(0, 0, 400, 600);
            var s = new GravitySession(new(100, 100, 120, 120), work, new(4000 * direction, 0));
            var (track, impacts) = Fly(s, BallisticDt, 1800);
            var kind = direction > 0 ? CollisionKind.RightWall : CollisionKind.LeftWall;
            var walls = impacts.Select((impact, index) => (impact, index))
                .Where(x => x.impact.Kind is CollisionKind.LeftWall or CollisionKind.RightWall).ToArray();
            Check(walls.Length > 0 && walls[0].impact.Kind == kind, "first side wall contact is " + kind);
            Check(track.All(p => p.Left >= -1e-9 && p.Left <= 380 + 1e-9), "side wall never crosses the WorkArea " + direction);
            var hit = direction > 0 ? track.FindIndex(p => Math.Abs(p.Left - 380) < 1e-9) : track.FindIndex(p => Math.Abs(p.Left) < 1e-9);
            Check(hit > 0, "side wall clamps to the exact WorkArea edge " + direction);
            Check(Math.Abs(track[hit].Vx + direction * .75 * walls[0].impact.NormalSpeed) < 1e-9,
                "side wall mirrors the normal velocity at 75% " + direction);
            Check(Math.Sign(track[hit].Vx) == -direction, "side wall reverses the horizontal direction " + direction);
            Check(hit + 1 < track.Count && (direction > 0 ? track[hit + 1].Left < track[hit].Left : track[hit + 1].Left > track[hit].Left),
                "the reflected body leaves the wall on the next fixed step " + direction);
            Check(track.Skip(hit + 1).Any(p => p.Top > track[hit].Top), "vertical gravity keeps acting after a side wall " + direction);
            for (int i = 1; i < walls.Length; i++)
            {
                Check(walls[i].impact.Kind != walls[i - 1].impact.Kind, "consecutive wall contacts must alternate sides " + direction);
                Check(walls[i].impact.NormalSpeed <= walls[i - 1].impact.NormalSpeed + 1e-9,
                    "wall rebound energy must not grow " + direction);
            }
            Check(s.Grounded && s.Actual.Bottom == work.Bottom, "side wall throw still reaches the floor " + direction);
            Check(s.State.BounceCount is >= 0 and <= 2, "wall contacts do not consume extra ground bounces " + direction);
            Check(s.State.Velocity.X == 0, "ground friction removes the residual horizontal velocity " + direction);
            var settled = s.Actual;
            Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Stop && s.Actual == settled,
                "settled side wall throw cannot jitter " + direction);
        }
    }

    static void HighSpeedNoTunneling()
    {
        var work = new PhysicalRect(0, 0, 400, 600);
        var s = new GravitySession(new(100, 0, 120, 20), work, new(0, 15000));
        var (track, impacts) = Fly(s, BallisticDt, 600);
        Check(track.All(p => p.Top <= 580 + 1e-9), "15000 px/s fall never tunnels through the floor");
        Check(impacts.Any(i => i.Kind == CollisionKind.Ground && i.Contact.Bottom == work.Bottom),
            "ground contact is reported at the real WorkArea bottom");
        Check(s.Grounded && s.Actual.Bottom == work.Bottom, "high-speed fall settles exactly on the floor");
        var burst = new GravitySession(new(100, 0, 120, 20), work, new(0, 15000));
        var action = burst.Advance(.12, burst.Actual, out var target);
        Check(action != GravityAdvance.Invalid && burst.LastResult!.Substeps == 5 &&
            burst.LastResult.Impacts.Count(i=>i.Kind==CollisionKind.Ground)==1 &&
            Math.Abs(burst.LastResult.DroppedSeconds-9d/120)<1e-10,
            "high-speed burst stops on fifth substep at first Ground and discards nine unused steps");
        Check(burst.State.Rect.Bottom <= 600 + 1e-9, "eight-substep burst cannot cross the floor");
        if (action == GravityAdvance.Move) Check(burst.Accept(target), "burst target is accepted by its own session");
    }

    static void BounceAndSlide()
    {
        var work = new PhysicalRect(0, 0, 4000, 600);
        var s = new GravitySession(new(100, 200, 120, 220), work, new(1500, 800));
        var (track, impacts) = Fly(s, BallisticDt, 1800);
        Check(s.Grounded && s.Actual.Bottom == work.Bottom, "diagonal landing settles on the ground");
        Check(s.State.BounceCount is >= 0 and <= 2, "at most two decaying ground bounces");
        var ground = impacts.Count(i => i.Kind == CollisionKind.Ground);
        Check(ground == s.State.BounceCount + 1, "one ground fact per bounce plus the final settle");
        var outgoing = impacts.Where(i => i.Kind == CollisionKind.Ground).Select(i => i.NormalSpeed).ToArray();
        for (int i = 1; i < outgoing.Length; i++) Check(outgoing[i] <= outgoing[i - 1] + 1e-9, "ground contacts decay monotonically");
        Check(s.State.Velocity.X == 0, "ground friction stops the horizontal slide");
        Check(s.Actual.Left > 100, "landing preserves a legal horizontal displacement");
        Check(track[^1].Phase == PhysicsPhase.Settled, "final phase is Settled");
        var settled = s.Actual;
        Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Stop && s.Actual == settled, "settled session never jitters again");
        var lateImpacts = s.LastResult?.Impacts.Length ?? 0;
        Check(lateImpacts == 0, "no repeated ground contact after settling");
    }

    static void LateFrameResumes()
    {
        var s = new GravitySession(new(1000, 3000, 1020, 3020), Tall, new(0, -1200));
        s.Advance(.005, s.Actual, out _);
        var before = s.State;
        Check(s.Advance(.3, s.Actual, out _) == GravityAdvance.Hold && !s.Stopped, "300ms late frame holds the ballistic session");
        Check(s.LastResult is { Failure: PhysicsFailure.LateFrameDiscarded, Substeps: 0 }, "late frame is a bounded discard");
        Check(s.State.Rect == before.Rect && s.State.Accumulator == 0, "late frame has zero motion and zero debt");
        Check(s.Advance(.03, s.Actual, out _) != GravityAdvance.Invalid && s.State.Rect.Top < before.Rect.Top,
            "normal tick resumes the same ballistic session with no catch-up");
    }

    static void LongTicksRetainSession()
    {
        foreach (var dt in new[] { .090, .120 })
        {
            var s = new GravitySession(new(1000, 2000, 1020, 2020), Tall, new(500, -900));
            for (int i = 0; i < 200 && !s.Stopped; i++)
            {
                var action = s.Advance(dt, s.Actual, out var target);
                Check(action != GravityAdvance.Invalid, "schedule discard retains the ballistic session " + dt);
                Check(s.LastResult!.Substeps <= 8, "hard eight substep cap " + dt);
                if (action == GravityAdvance.Move) Check(s.Accept(target), "bounded target accepted " + dt);
            }
            Check(s.Grounded && s.Actual.Bottom == Tall.Bottom, "long ticks still land " + dt);
        }
    }

    static void InvalidReleaseRejected()
    {
        var rect = new PhysicalRect(10, 10, 30, 30);
        var work = new PhysicalRect(0, 0, 100, 100);
        foreach (var bad in new[]
        {
            new PhysicalVelocity(double.NaN, 0), new PhysicalVelocity(0, double.PositiveInfinity),
            new PhysicalVelocity(double.NegativeInfinity, 0), new PhysicalVelocity(30000, 0),
            new PhysicalVelocity(0, -30000)
        })
        {
            var s = new GravitySession(rect, work, bad);
            Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Invalid && s.Stopped &&
                s.State.Phase == PhysicsPhase.Invalid, "invalid release velocity is refused " + bad);
            Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Stop, "refused release cannot revive " + bad);
        }
        foreach (var badRect in new[]
        {
            new PhysicalRect(double.NaN, 0, 10, 10), new PhysicalRect(90, 10, 110, 20),
            new PhysicalRect(10, 10, 20, 20.2), new PhysicalRect(10, 10, 20, 120)
        })
        {
            var s = new GravitySession(badRect, work, new(10, 10));
            Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Invalid,
                "invalid ballistic start position is refused " + badRect);
        }
    }

    static void TwelveSecondLimit()
    {
        var work = new PhysicalRect(0, 0, 400, 1e9);
        var s = new GravitySession(new(100, 100, 120, 120), work, new(0, -1500));
        for (int i = 0; i < 100 && !s.Stopped; i++) s.Advance(.3, s.Actual, out _);
        Check(s.Stopped && !s.Grounded && s.State.Phase == PhysicsPhase.Invalid,
            "12s session limit still ends a long ballistic flight");
        Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Stop, "expired session never revives");
    }

    static void ExternalActualRejected()
    {
        var s = new GravitySession(new(10, 10, 30, 30), new(0, 0, 1000, 2000), new(300, -300));
        Check(s.Advance(.03, s.Actual, out var request) == GravityAdvance.Move, "ballistic move exposes one integer request");
        Check(!s.Accept(request with { Left = request.Left + 1, Right = request.Right + 1 }) && s.Stopped && !s.Grounded,
            "one pixel native mismatch stops a ballistic session permanently");
        var foreign = new GravitySession(new(10, 10, 30, 30), new(0, 0, 1000, 2000), new(300, -300));
        Check(foreign.Advance(.03, new(11, 10, 31, 30), out _) == GravityAdvance.Invalid,
            "foreign Actual refuses ballistic integration");
    }

    static void VelocitySnapshotIsOneTime()
    {
        var s = new GravitySession(new(1000, 1000, 1020, 1020), Tall, new(0, -1000));
        var v = s.State.Velocity;
        for (int i = 0; i < 20; i++) s.Advance(.3, s.Actual, out _);
        Check(s.State.Velocity == v && !s.Stopped, "release velocity is a one-time snapshot, never re-sampled by waiting");
    }
}
