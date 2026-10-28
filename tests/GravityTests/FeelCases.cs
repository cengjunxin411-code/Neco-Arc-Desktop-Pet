using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

// Phase 19C.2-R1 physics-feel coverage. The collision cases drive the production
// PhysicsStep with gravity/air drag removed so the mirror-reflection formula and
// the whole-vector retention can be checked exactly. The trajectory cases drive
// the production GravitySession end to end (release -> integrator -> impacts).
static partial class Program
{
    static readonly PhysicsBounds FeelRoom = PhysicsBounds.FromWorkAreas([new PhysicalRect(0, 0, 1000, 1000)]);

    // Gravity-only laboratory tuning: no gravity, drag or friction, so the only
    // velocity change in the step is the wall/ceiling collision under test.
    static readonly PhysicsTuning FeelLab = new()
    {
        Gravity = 0, AirDrag = 0, GroundFriction = 0, GroundRestitution = 0, StopSpeed = 0
    };

    static PhysicsResult FeelStep(PhysicsState state, PhysicsBounds bounds, double dt = 1d / 120) =>
        PhysicsStep.Advance(state, PhysicsMode.Gravity, dt, bounds, FeelLab);

    static void FeelNear(double actual, double expected, double tolerance = 1e-9)
    {
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            "expected " + expected.ToString("R") + ", got " + actual.ToString("R"));
    }

    static (List<Snapshot> Track, List<(int Step, PhysicsImpact Impact)> Impacts) FlyTuned(
        PhysicalRect start, PhysicalRect work, PhysicalVelocity release, PhysicsTuning tuning, double dt, int limit)
    {
        var bounds = PhysicsBounds.FromWorkAreas([work]);
        var state = new PhysicsState(start, release);
        var track = new List<Snapshot>();
        var impacts = new List<(int, PhysicsImpact)>();
        for (int i = 0; i < limit && state.Phase != PhysicsPhase.Settled; i++)
        {
            var result = PhysicsStep.Advance(state, PhysicsMode.Gravity, dt, bounds, tuning);
            if (!result.Success) throw new Exception("tuned flight rejected at step " + i + ": " + result.Failure);
            state = result.State;
            if (state.Phase == PhysicsPhase.Invalid) throw new Exception("tuned flight became Invalid at step " + i);
            foreach (var impact in result.Impacts) impacts.Add((i, impact));
            track.Add(new(state.Rect.Left, state.Rect.Top, state.Velocity.X, state.Velocity.Y, state.Phase, state.BounceCount));
        }
        return (track, impacts);
    }

    static void FeelCases()
    {
        var failures = new List<string>();
        void Case(string name, Action body)
        {
            try { body(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.GetBaseException().Message); Console.WriteLine("FAIL " + failures[^1]); }
        }

        Case("ceiling mirrors the velocity and keeps 75% of the whole vector", () => {
            var r = FeelStep(new(new PhysicalRect(100, 5, 120, 25), new PhysicalVelocity(300, -600)), FeelRoom);
            Check(r.Success && r.Impacts.Length == 1, "exactly one ceiling contact");
            Check(r.Impacts[0].Kind == CollisionKind.Ceiling, "contact kind is ceiling");
            FeelNear(r.Impacts[0].NormalSpeed, 600);
            FeelNear(r.State.Rect.Top, 0);
            FeelNear(r.State.Velocity.X, 225);   // 300 * .75
            FeelNear(r.State.Velocity.Y, 450);   // -(-600) * .75
            FeelNear(Math.Sqrt(225 * 225 + 450 * 450) / Math.Sqrt(300 * 300 + 600 * 600), .75);
            Check(Math.Abs(Math.Atan2(300, 600) - Math.Atan2(225, 450)) < 1e-12,
                "ceiling incidence and reflection angles are symmetric about the normal");
        });

        Case("left and right walls mirror the velocity and keep 75% of the whole vector", () => {
            foreach (var (start, release, kind, expectedVx, expectedVy) in new[]
            {
                (new PhysicalRect(975, 100, 995, 120), new PhysicalVelocity(600, 120), CollisionKind.RightWall, -450d, 90d),
                (new PhysicalRect(5, 100, 25, 120), new PhysicalVelocity(-600, 120), CollisionKind.LeftWall, 450d, 90d)
            })
            {
                var r = FeelStep(new(start, release), FeelRoom);
                Check(r.Success && r.Impacts.Length == 1, "exactly one side-wall contact " + kind);
                Check(r.Impacts[0].Kind == kind, "contact kind is " + kind);
                FeelNear(r.Impacts[0].NormalSpeed, 600);
                FeelNear(r.State.Rect.Left, kind == CollisionKind.RightWall ? 980 : 0);
                FeelNear(r.State.Velocity.X, expectedVx);
                FeelNear(r.State.Velocity.Y, expectedVy);
                FeelNear(Math.Sqrt(expectedVx * expectedVx + expectedVy * expectedVy) / Math.Sqrt(600 * 600 + 120 * 120), .75);
                Check(Math.Abs(Math.Atan2(release.Y, Math.Abs(release.X)) -
                    Math.Atan2(expectedVy, Math.Abs(expectedVx))) < 1e-12,
                    "side-wall incidence and reflection angles are symmetric about the normal");
            }
        });

        Case("corner contact reflects both axes without tunnelling or sign errors", () => {
            var r = FeelStep(new(new PhysicalRect(975, 1, 995, 21), new PhysicalVelocity(600, -600)), FeelRoom);
            Check(r.Success && r.Impacts.Length == 2, "corner produces two bounded contacts");
            Check(r.Impacts[0].Kind == CollisionKind.RightWall && r.Impacts[1].Kind == CollisionKind.Ceiling,
                "X contact is resolved before the Y contact");
            FeelNear(r.Impacts[0].NormalSpeed, 600);
            FeelNear(r.Impacts[1].NormalSpeed, 450);
            FeelNear(r.State.Rect.Right, 1000);
            FeelNear(r.State.Rect.Top, 0);
            FeelNear(r.State.Velocity.X, -337.5); // 600 * .75 (X) * .75 (Y)
            FeelNear(r.State.Velocity.Y, 337.5);  // 600 * .75 (X) * .75 (Y)
            FeelNear(Math.Sqrt(337.5 * 337.5 * 2) / Math.Sqrt(600 * 600 * 2), .75 * .75);
        });

        Case("near-rest contact is absorbed once and never micro-bounces", () => {
            var low = FeelStep(new(new PhysicalRect(980, 100, 1000, 120), new PhysicalVelocity(30, 0)), FeelRoom);
            Check(low.Impacts.Length == 1 && low.Impacts[0].Kind == CollisionKind.RightWall, "low-speed side contact is still reported");
            FeelNear(low.State.Velocity.X, 0);
            FeelNear(low.State.Rect.Right, 1000);
            var lowAgain = FeelStep(low.State, FeelRoom);
            Check(lowAgain.Impacts.Length == 0, "absorbed side contact does not replay");
            var ceiling = FeelStep(new(new PhysicalRect(100, 0, 120, 20), new PhysicalVelocity(0, -30)), FeelRoom);
            Check(ceiling.Impacts.Length == 1 && ceiling.Impacts[0].Kind == CollisionKind.Ceiling, "low-speed ceiling contact is reported");
            FeelNear(ceiling.State.Velocity.Y, 0);
            var ceilingAgain = FeelStep(ceiling.State, FeelRoom);
            Check(ceilingAgain.Impacts.Length == 0 && ceilingAgain.State.Rect == ceiling.State.Rect,
                "absorbed ceiling contact cannot replay an impact");
            var fast = FeelStep(new(new PhysicalRect(980, 100, 1000, 120), new PhysicalVelocity(100, 0)), FeelRoom);
            FeelNear(fast.State.Velocity.X, -75);
        });

        Case("separating or resting bodies never receive a wall impulse", () => {
            var away = FeelStep(new(new PhysicalRect(980, 100, 1000, 120), new PhysicalVelocity(-600, 120)), FeelRoom);
            Check(away.Impacts.Length == 0, "velocity away from the wall is not an impact");
            FeelNear(away.State.Velocity.X, -600);
            FeelNear(away.State.Velocity.Y, 120);
            Check(away.State.Rect.Left < 980, "separating body moves away");
            var leftAway = FeelStep(new(new PhysicalRect(5, 100, 25, 120), new PhysicalVelocity(600, 120)), FeelRoom);
            Check(leftAway.Impacts.Length == 0, "velocity away from the left wall is not an impact");
            var resting = FeelStep(new(new PhysicalRect(980, 100, 1000, 120), default), FeelRoom);
            Check(resting.Impacts.Length == 0, "zero velocity at the wall is not an impact");
            FeelNear(resting.State.Velocity.X, 0);
            var ceilingAway = FeelStep(new(new PhysicalRect(100, 0, 120, 20), new PhysicalVelocity(0, 300)), FeelRoom);
            Check(ceilingAway.Impacts.Length == 0, "downward motion away from the ceiling is not an impact");
        });

        Case("high-speed flight cannot tunnel through walls or ceiling", () => {
            var room = PhysicsBounds.FromWorkAreas([new PhysicalRect(0, 0, 400, 600)]);
            var state = new PhysicsState(new PhysicalRect(100, 100, 120, 120), new PhysicalVelocity(9000, 15000));
            for (int i = 0; i < 240; i++)
            {
                var r = FeelStep(state, room);
                Check(r.Success, "high-speed step rejected at " + i);
                state = r.State;
                Check(state.Rect.Left >= 0 && state.Rect.Right <= 400 && state.Rect.Top >= 0 && state.Rect.Bottom <= 600,
                    "high-speed body escaped the WorkArea at step " + i);
                Check(state.Velocity.IsFinite, "high-speed velocity became non-finite");
            }
        });

        Case("wall and ceiling contacts do not consume the two ground bounces", () => {
            var s = new GravitySession(new(100, 300, 120, 320), new PhysicalRect(0, 0, 400, 600), new(4000, -5000));
            var (_, impacts) = Fly(s, BallisticDt, 2400);
            var ground = impacts.Count(i => i.Kind == CollisionKind.Ground);
            Check(s.Grounded && s.Actual.Bottom == 600, "wall/ceiling throw still settles on the real floor");
            Check(s.State.BounceCount is >= 0 and <= 2, "ground never exceeds two rebounds");
            Check(ground == s.State.BounceCount + 1, "one ground fact per bounce plus the final settle");
            Check(s.State.Phase == PhysicsPhase.Settled && s.State.Velocity.X == 0, "settled ground contact is stable");
            var settled = s.Actual;
            Check(s.Advance(.03, s.Actual, out _) == GravityAdvance.Stop && s.Actual == settled,
                "settled wall throw cannot revive or jitter");
        });

        Case("large ticks with live collisions remain bounded and eventually settle", () => {
            var s = new GravitySession(new(100, 100, 120, 120), new PhysicalRect(0, 0, 400, 600), new(5000, -5000));
            for (int i = 0; i < 400 && !s.Stopped; i++)
            {
                var action = s.Advance(.12, s.Actual, out var target);
                Check(action != GravityAdvance.Invalid, "large tick became Invalid: " + s.StopReason);
                if (action == GravityAdvance.Move) Check(s.Accept(target), "large tick target rejected");
                Check(s.State.Rect.Left >= 0 && s.State.Rect.Right <= 400 &&
                    s.State.Rect.Top >= 0 && s.State.Rect.Bottom <= 600, "large tick left the WorkArea");
            }
            Check(s.Grounded && s.Actual.Bottom == 600, "large tick collision flight settles");
        });

        Case("Gravity air drag 1.0 follows its closed-form trajectory", () => {
            foreach (var (vx, vy) in new[] { (900d, 0d), (600d, -800d), (-300d, 500d), (0d, -1200d) })
            {
                var s = new GravitySession(new PhysicalRect(1000, 1000, 1020, 1020), Tall, new(vx, vy));
                var (track, _) = Fly(s, BallisticDt, 121);
                foreach (var step in new[] { 29, 59, 119 })
                {
                    var t = (step + 1) * BallisticDt;
                    var expected = Analytic(1000, 1000, vx, vy, t);
                    Check(Math.Abs(track[step].Left - expected.X) < 1e-6 && Math.Abs(track[step].Top - expected.Y) < 1e-6,
                        "position does not follow air drag 1.0 at t=" + t + " v0=" + vx + "," + vy);
                    Check(Math.Abs(track[step].Vx - expected.Vx) < 1e-6 && Math.Abs(track[step].Vy - expected.Vy) < 1e-6,
                        "velocity does not follow air drag 1.0 at t=" + t + " v0=" + vx + "," + vy);
                }
            }
        });

        Case("air drag 1.0 shortens free flight versus 0.08", () => {
            var start = new PhysicalRect(1000, 1000, 1020, 1020);
            var slow = FlyTuned(start, Tall, new(900, 0), new PhysicsTuning { AirDrag = .08 }, BallisticDt, 240);
            var fast = FlyTuned(start, Tall, new(900, 0), new PhysicsTuning { AirDrag = 1.0 }, BallisticDt, 240);
            foreach (var step in new[] { 29, 59, 119 })
            {
                Check(slow.Track[step].Left - fast.Track[step].Left > 10,
                    "1.0 should be measurably shorter than 0.08 at step " + step);
                Check(Math.Abs(fast.Track[step].Vx) < Math.Abs(slow.Track[step].Vx),
                    "1.0 should retain less horizontal speed at step " + step);
                var expected = Analytic(1000, 1000, 900, 0, (step + 1) * BallisticDt);
                Check(Math.Abs(fast.Track[step].Left - expected.X) < 1e-6,
                    "1.0 is the production solver, not a second formula at step " + step);
            }
            Check(slow.Impacts.All(i => i.Impact.Kind is not (CollisionKind.LeftWall or CollisionKind.RightWall)) &&
                fast.Impacts.All(i => i.Impact.Kind is not (CollisionKind.LeftWall or CollisionKind.RightWall)),
                "the 2s free-flight fixture must stay clear of both walls");
        });

        Case("air drag 1.0 delays the first wall contact versus 0.08", () => {
            var work = new PhysicalRect(0, 0, 2000, 4000);
            var start = new PhysicalRect(200, 500, 220, 520);
            var slow = FlyTuned(start, work, new(3000, 0), new PhysicsTuning { AirDrag = .08 }, BallisticDt, 240);
            var fast = FlyTuned(start, work, new(3000, 0), new PhysicsTuning { AirDrag = 1.0 }, BallisticDt, 240);
            var slowWall = slow.Impacts.FirstOrDefault(i => i.Impact.Kind == CollisionKind.RightWall);
            var fastWall = fast.Impacts.FirstOrDefault(i => i.Impact.Kind == CollisionKind.RightWall);
            Check(slowWall.Impact.Kind == CollisionKind.RightWall, "0.08 reaches the right wall");
            Check(fastWall.Impact.Kind == CollisionKind.RightWall, "1.0 still reaches the right wall at this speed");
            Check(fastWall.Step > slowWall.Step + 20, "1.0 must arrive measurably later than 0.08");
            Check(Math.Abs(slow.Track[slowWall.Step].Left - 1980) < 1e-9 && Math.Abs(fast.Track[fastWall.Step].Left - 1980) < 1e-9,
                "both contacts happen exactly at the legal right wall");
        });

        Case("air drag 1.0 keeps a normal throw away from a wall that 0.08 reaches", () => {
            var work = new PhysicalRect(0, 0, 2000, 4000);
            var start = new PhysicalRect(200, 500, 220, 520);
            var slow = FlyTuned(start, work, new(1200, 0), new PhysicsTuning { AirDrag = .08 }, BallisticDt, 240);
            var fast = FlyTuned(start, work, new(1200, 0), new PhysicsTuning { AirDrag = 1.0 }, BallisticDt, 240);
            Check(slow.Impacts.Any(i => i.Impact.Kind == CollisionKind.RightWall), "0.08 hits the right wall in 2s");
            Check(!fast.Impacts.Any(i => i.Impact.Kind == CollisionKind.RightWall), "1.0 must not hit the wall in 2s");
            Check(fast.Track[^1].Left <= 200 + 1200 + 1e-6, "1.0 horizontal asymptote is bounded by vx/k");
        });

        Case("air drag 1.0 lowers the apex versus 0.08", () => {
            var start = new PhysicalRect(900, 800, 920, 820);
            var slow = FlyTuned(start, Tall, new(900, -1500), new PhysicsTuning { AirDrag = .08 }, BallisticDt, 240);
            var fast = FlyTuned(start, Tall, new(900, -1500), new PhysicsTuning { AirDrag = 1.0 }, BallisticDt, 240);
            var slowApex = slow.Track.Min(p => p.Top);
            var fastApex = fast.Track.Min(p => p.Top);
            Check(fastApex > slowApex + 100, "1.0 apex should be at least 100 px lower than 0.08");
        });

        Case("air drag 1.0 landing position matches the closed form at the contact step", () => {
            var start = new PhysicalRect(1000, 1000, 1020, 1020);
            var run = FlyTuned(start, Tall, new(900, 0), new PhysicsTuning { AirDrag = 1.0 }, BallisticDt, 1200);
            var ground = run.Impacts.First(i => i.Impact.Kind == CollisionKind.Ground);
            var t = (ground.Step + 1) * BallisticDt;
            var expected = Analytic(1000, 1000, 900, 0, t);
            Check(Math.Abs(run.Track[ground.Step].Left - expected.X) < 1e-6,
                "landing X does not match the 1.0 closed form: " + run.Track[ground.Step].Left + " vs " + expected.X);
        });

        Case("GravitySession production boundary absorbs 39 and mirrors 41 px/s", () => {
            foreach (var (vx, absorbed) in new[] { (39d, true), (40d, false), (41d, false) })
            {
                var side = new GravitySession(new(90, 10, 100, 20), new(0, 0, 100, 100), new(vx, 0));
                side.Advance(1d / 120, side.Actual, out _);
                var sideImpact = side.LastResult!.Impacts.Single(i => i.Kind == CollisionKind.RightWall);
                Check(sideImpact.NormalSpeed == vx, "production side-wall incoming normal " + vx);
                if (absorbed)
                {
                    Check(side.State.Velocity.X == 0, "39 px/s must be absorbed by the production 40 px/s tolerance");
                    var again = side.Advance(1d / 120, side.Actual, out _);
                    Check(again != GravityAdvance.Invalid && side.LastResult!.Impacts.Length == 0,
                        "absorbed production contact must not replay");
                }
                else
                {
                    Check(side.State.Velocity.X < 0 && Math.Abs(side.State.Velocity.X + .75 * sideImpact.NormalSpeed) < 1e-9,
                        "41 px/s must mirror at 75% in the production session");
                }
            }
            foreach (var (vy, absorbed) in new[] { (-39d, true), (-41d, false) })
            {
                var ceiling = new GravitySession(new(10, 0, 30, 20), new(0, 0, 100, 100), new(0, vy));
                ceiling.Advance(1d / 120, ceiling.Actual, out _);
                var ceilingImpact = ceiling.LastResult!.Impacts.Single(i => i.Kind == CollisionKind.Ceiling);
                Check(ceilingImpact.NormalSpeed == -vy, "production ceiling incoming normal " + (-vy));
                if (absorbed)
                    Check(ceiling.State.Velocity.Y == 0, "39 px/s ceiling must be absorbed by the production tolerance");
                else
                    Check(ceiling.State.Velocity.Y > 0 && Math.Abs(ceiling.State.Velocity.Y - .75 * ceilingImpact.NormalSpeed) < 1e-9,
                        "41 px/s ceiling must mirror at 75% in the production session");
            }
        });

        Case("GravitySession production contacts damp the tangential component too", () => {
            var side = new GravitySession(new(90, 10, 100, 20), new(0, 0, 100, 1000), new(600, 600));
            side.Advance(1d / 120, side.Actual, out _);
            var sideImpact = side.LastResult!.Impacts.Single(i => i.Kind == CollisionKind.RightWall);
            Check(side.State.Velocity.X < 0, "production side wall mirrors X");
            var expectedSideTangent = .75 * 600 * Math.Exp(-1d / 120) + 1800 * (1 - Math.Exp(-1d / 120));
            Check(Math.Abs(side.State.Velocity.Y - expectedSideTangent) < 1e-9,
                "production side wall damped the 600 px/s tangent to the exact 75% + one-substep gravity value");
            Check(Math.Abs(sideImpact.NormalSpeed - 600) < 1e-9, "production side wall normal is the contact-time 600");
            var ceiling = new GravitySession(new(10, 0, 30, 20), new(0, 0, 1000, 1000), new(600, -600));
            ceiling.Advance(1d / 120, ceiling.Actual, out _);
            var expectedTangent = .75 * 600 * Math.Exp(-1d / 120);
            Check(Math.Abs(ceiling.State.Velocity.X - expectedTangent) < 1e-9,
                "production ceiling damped the X tangent at the contact");
            Check(ceiling.State.Velocity.Y > 0, "production ceiling mirrors Y");
        });

        Case("GravitySession production corner contact applies both 75% losses", () => {
            var s = new GravitySession(new(80, 0, 100, 20), new(0, 0, 100, 100), new(600, -600));
            s.Advance(1d / 120, s.Actual, out _);
            var impacts = s.LastResult!.Impacts;
            Check(impacts.Length == 2 && impacts[0].Kind == CollisionKind.RightWall && impacts[1].Kind == CollisionKind.Ceiling,
                "production corner resolves the side wall then the ceiling");
            Check(Math.Abs(s.State.Velocity.X + 337.5) < 1e-9 && Math.Abs(s.State.Velocity.Y - 337.5) < 1e-9,
                "production corner retains 0.75^2 of each component");
        });

        Case("wall+ground corner absorbs vertical speed and provisional wall reflection", () => {
            foreach (var (vy, expectedBounces) in new[] { (200d, 0), (240d, 0) })
            {
                var s = new GravitySession(new(80, 80, 100, 100), new(0, 0, 100, 100), new(600, vy));
                s.Advance(1d / 120, s.Actual, out _);
                var ground = s.LastResult!.Impacts.Single(i => i.Kind == CollisionKind.Ground);
                Check(Math.Abs(ground.NormalSpeed - .75 * vy) < 1e-9,
                    "wall tangent damping is part of the incoming ground state " + vy);
                Check(s.State.BounceCount == expectedBounces,
                    "zero-bounce ground rule sees the post-wall normal " + vy);
                Check(s.State.Velocity == default, "Ground plus wall absorbs X and Y without reversing");
            }
        });

        Case("invalid Gravity wall tuning is rejected", () => {
            foreach (var bad in new[] {
                FeelLab with { GravityWallBounce = -.01 },
                FeelLab with { GravityWallBounce = 1.01 },
                FeelLab with { GravityWallBounceMinimumSpeed = -1 },
                FeelLab with { GravityWallBounceMinimumSpeed = 10001 }
            })
            {
                var result = PhysicsStep.Advance(new(new PhysicalRect(100, 100, 120, 120), default),
                    PhysicsMode.Gravity, 1d / 120, FeelRoom, bad);
                Check(!result.Success && result.Failure == PhysicsFailure.InvalidTuning,
                    "invalid Gravity wall tuning was accepted: " + bad);
            }
        });

        if (failures.Count > 0) throw new Exception(string.Join(" | ", failures));
    }
}
