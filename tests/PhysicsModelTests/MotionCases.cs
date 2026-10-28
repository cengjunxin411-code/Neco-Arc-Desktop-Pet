using System.Reflection;
using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

internal static partial class Program
{
    static readonly PhysicsBounds Open = PhysicsBounds.FromWorkAreas([new(-100000, -100000, 100000, 100000)]);
    static readonly PhysicsTuning Ballistic = new() { AirDrag = 0, GroundFriction = 0, StopSpeed = 0 };
    static readonly PhysicsTuning Planar = Ballistic with { PlanarDrag = 0 };
    static PhysicsState Body(double x = 100, double y = 100, double vx = 0, double vy = 0)
        => new(new(x, y, x + 20, y + 20), new(vx, vy));
    static PhysicsResult Step(PhysicsState s, PhysicsMode mode = PhysicsMode.Gravity, double elapsed = 1d / 120,
        PhysicsBounds? bounds = null, PhysicsTuning? tuning = null)
        => PhysicsStep.Advance(s, mode, elapsed, bounds ?? Open, tuning ?? Ballistic);
    static PhysicsState Run(PhysicsState s, PhysicsMode mode, int frames, double dt, PhysicsBounds? bounds = null, PhysicsTuning? tuning = null)
    {
        for (int i = 0; i < frames; i++) { var r = Step(s, mode, dt, bounds, tuning); Check(r.Success, $"run rejected {r.Failure}"); s = r.State; }
        return s;
    }
    static partial void RunCases() { MotionCases(); GeometryCases(); ReleaseCases(); }
    static void MotionCases()
    {
        foreach (var v in new[] { (0d, 0d), (200d, 0d), (0d, -600d), (10d, 500d), (-230d, -300d) })
            Case("Gravity", $"analytic launch {v}", () => {
                var s = Run(Body(100, 100, v.Item1, v.Item2), PhysicsMode.Gravity, 120, 1d / 120);
                Near(s.Rect.Left, 100 + v.Item1); Near(s.Rect.Top, 100 + v.Item2 + 900); Near(s.Velocity.Y, v.Item2 + 1800);
                maximumBallisticPositionError=Math.Max(maximumBallisticPositionError,Math.Max(Math.Abs(s.Rect.Left-(100+v.Item1)),Math.Abs(s.Rect.Top-(100+v.Item2+900))));
                maximumBallisticVelocityError=Math.Max(maximumBallisticVelocityError,Math.Abs(s.Velocity.Y-(v.Item2+1800)));
            });
        Case("Gravity", "apex rises then falls", () => {
            var s = Body(vy: -600); var up = Run(s, PhysicsMode.Gravity, 30, 1d / 120);
            var down = Run(up, PhysicsMode.Gravity, 30, 1d / 120);
            Check(up.Rect.Top < 100 && up.Velocity.Y < 0 && down.Velocity.Y > 0 && down.Rect.Top > up.Rect.Top, "no real apex");
        });
        Case("Gravity", "high refresh subpixel remainder", () => {
            var s = Run(Body(vx: .1), PhysicsMode.PlanarInertia, 2400, 1d / 240, tuning: Planar);
            Near(s.Rect.Left, 101, 1e-8); Near(s.Rect.Top, 100);
        });
        Case("Gravity", "irregular elapsed equals analytic", () => {
            var s = Body(vx: 50, vy: -200);
            for (int i = 0; i < 40; i++) foreach (var dt in new[] { .003, .007, .015 }) s = Step(s, elapsed: dt).State;
            Near(s.Rect.Left, 150); Near(s.Rect.Top, 800); Near(s.Velocity.Y, 1600);
        });
        Case("Gravity", "air drag closed form", () => {
            var s = Run(Body(vx: 300, vy: -100), PhysicsMode.Gravity, 120, 1d / 120, tuning: Ballistic with { AirDrag = 2 });
            var e = Math.Exp(-2); Near(s.Velocity.X, 300 * e); Near(s.Velocity.Y, -100 * e + 900 * (1 - e));
            Near(s.Rect.Top, 100 - 50 * (1 - e) + 900 * (1 - (1 - e) / 2));
        });
        Case("Gravity", "tiny damping retains acceleration precision", () => {
            var s=Run(Body(vx:300,vy:-100),PhysicsMode.Gravity,120,1d/120,tuning:Ballistic with{AirDrag=1e-12});
            Near(s.Rect.Left,400,1e-6);Near(s.Rect.Top,900,1e-6);Near(s.Velocity.Y,1700,1e-6);
        });
        Case("Wall", "apex inside substep cannot pass ceiling and return", () => {
            var bounds=PhysicsBounds.FromWorkAreas([new(0,0,1000,1000)]);
            var r=Step(Body(100,.005,0,-5),bounds:bounds,tuning:Ballistic with{Gravity=1200});
            Check(r.Impacts.Any(x=>x.Kind==CollisionKind.Ceiling),"intermediate apex escaped ceiling");
            Check(r.State.Rect.Top>=0,"ceiling penetration");Near(r.Impacts[0].NormalSpeed,Math.Sqrt(13),1e-6);
        });
        Case("Ground", "impact speed evaluated at time of contact", () => {
            var bounds=PhysicsBounds.FromWorkAreas([new(0,0,1000,1000)]);
            var r=Step(Body(100,979,0,600),bounds:bounds,tuning:Ballistic);
            Near(r.Impacts.Single().NormalSpeed,Math.Sqrt(363600),1e-6);
            Near(r.State.Velocity.Y,-Math.Sqrt(363600)*.22,1e-6);
        });
        foreach (var origin in new[] { -500d, 100d }) foreach (var v in new[] { (600d, -300d), (-20d, 40d), (.1, -.1) })
            Case("Planar", $"damping and stop {origin}/{v}", () => {
                var s = Body(origin, origin, v.Item1, v.Item2); var speed = Math.Sqrt(v.Item1*v.Item1 + v.Item2*v.Item2);
                for (int i=0; i<1200; i++) { s = Step(s, PhysicsMode.PlanarInertia, tuning: new()).State;
                    var next = Math.Sqrt(s.Velocity.X*s.Velocity.X + s.Velocity.Y*s.Velocity.Y);
                    Check(next <= speed + 1e-9 && s.Phase != PhysicsPhase.Invalid, "energy grew or invalid"); speed = next; }
                Check(s.Velocity == default && s.Phase == PhysicsPhase.Settled, "never stopped");
            });
        Case("Planar", "no vertical force", () => { var s = Run(Body(vx: 300), PhysicsMode.PlanarInertia, 100, 1d/120, tuning: new()); Near(s.Rect.Top, 100); Near(s.Velocity.Y, 0); });
        Case("Planar", "zero drag retains both axes", () => { var s = Run(Body(vx: 20, vy: -40), PhysicsMode.PlanarInertia, 120, 1d/120, tuning: Planar); Near(s.Rect.Left, 120); Near(s.Rect.Top, 60); });
        Case("Time", "off is default and inactive", () => { var s = Body(vx: 100, vy: 200); var r = Step(s, default, .05); Check(r.State.Rect == s.Rect && r.Impacts.Length == 0 && r.State.Phase == PhysicsPhase.Inactive, "off moved"); });
        Case("Time", "split and combined frames", () => {
            var s = Body(vx: 100, vy: -100); var a = Step(s, elapsed: 1d/30).State;
            var b = Run(s, PhysicsMode.Gravity, 4, 1d/120); Near(a.Rect.Top, b.Rect.Top); Near(a.Velocity.Y, b.Velocity.Y); Near(a.Accumulator, b.Accumulator);
        });
        Case("Time", "zero elapsed no movement", () => { var s = Body(vx: 100); Check(Step(s, elapsed: 0).State == s, "zero changed state"); });
        foreach (var dt in new[] { -.01, double.NaN, double.PositiveInfinity })
            Case("Time", $"invalid elapsed {dt}", () => { var s = Body(vx: 100); var r = Step(s, elapsed: dt); Check(!r.Success && r.State.Rect == s.Rect && r.State.Phase == PhysicsPhase.Invalid, "bad elapsed accepted"); });
        foreach (var dt in new[] { .250001, .9, 1e200 })
            Case("Time", $"late frame discarded {dt}", () => { var s = Body(vx: 100); var r = Step(s, elapsed: dt); Check(r.State.Rect == s.Rect && r.Substeps == 0 && r.Failure == PhysicsFailure.LateFrameDiscarded && r.State.Accumulator == 0, "late frame chased"); });
        Case("Time", "budget bounded and no time debt", () => {
            var r = Step(Body(vx: 120), PhysicsMode.PlanarInertia, .25, tuning: Planar);
            Check(r.Substeps == 8 && r.DroppedSeconds > .18 && r.State.Accumulator < 1d/120, "unbounded debt"); Near(r.State.Rect.Left, 108);
            var next = Step(r.State, PhysicsMode.PlanarInertia, 0, tuning: Planar); Check(next.State.Rect == r.State.Rect && next.Substeps == 0, "debt recovered");
        });
        Case("Time", "tiny elapsed accumulates", () => { var s = Run(Body(vx: 120), PhysicsMode.PlanarInertia, 100, 1d/12000, tuning: Planar); Near(s.Rect.Left, 101); });
        var room = PhysicsBounds.FromMonitors([new("A", new(0,0,1000,1100), new(0,0,1000,1000))]);
        Case("Ground", "workarea floor ignores bounds bottom", () => {
            var s = Run(Body(100, 500), PhysicsMode.Gravity, 1200, 1d/120, room, new());
            Near(s.Rect.Bottom, 1000); Check(s.Phase == PhysicsPhase.Settled, "not stable");
        });
        foreach (var entry in new[] { (10d, 0), (300d, 1), (1800d, 2) })
            Case("Ground", $"bounded bounce {entry}", () => {
                var s = Body(100, 980, 0, entry.Item1); var impact = new List<PhysicsImpact>();
                for(int i=0;i<1600;i++){ var r=Step(s,bounds:room,tuning:new()); Check(r.Success,"ground rejected"); impact.AddRange(r.Impacts); s=r.State; Check(s.Rect.Bottom<=1000,"penetrated floor"); }
                Check(s.BounceCount == entry.Item2 && s.Phase == PhysicsPhase.Settled, $"bounce count {s.BounceCount}, expected {entry.Item2}");
                Check(impact.Count <= entry.Item2+1 && impact.All(x=>x.Kind == CollisionKind.Ground && x.NormalSpeed > 0), "contact emitted every frame");
                if(entry.Item2==2) Check(impact[1].NormalSpeed < impact[0].NormalSpeed, "bounce energy grew");
            });
        Case("Ground", "sliding uses friction then exact stable contact", () => {
            var s = Body(100, 980, 300, 0); var r=Step(s,bounds:room,tuning:new());
            Check(r.State.Phase==PhysicsPhase.Sliding && r.State.Velocity.X<300 && r.Impacts.Length==0,"sliding/contact semantics");
            s=Run(r.State,PhysicsMode.Gravity,600,1d/120,room,new()); var rect=s.Rect;
            Check(s.Phase==PhysicsPhase.Settled && s.Velocity==default,"slide did not stop");
            for(int i=0;i<500;i++){r=Step(s,bounds:room,tuning:new());Check(r.State.Rect==rect&&r.Impacts.Length==0,"ground jitter/contact spam");s=r.State;}
        });
        Case("Ground", "friction displacement stops at finite distance", () => {
            var s=Run(Body(100,980,90,0),PhysicsMode.Gravity,120,1d/120,room,Ballistic with{GroundFriction=90});
            Near(s.Rect.Left,145,1e-6); Near(s.Velocity.X,0); Near(s.Rect.Bottom,1000);
        });
        foreach (var bad in new[] {
            Ballistic with{Gravity=double.NaN}, Ballistic with{AirDrag=-1}, Ballistic with{GroundFriction=-1},
            Ballistic with{WallRestitution=1.1}, Ballistic with{GroundRestitution=.9}, Ballistic with{FixedSeconds=0},
            Ballistic with{MaximumSubsteps=100000}, Ballistic with{MaximumBounces=3}, Ballistic with{MaximumSpeed=double.MaxValue},
            Ballistic with{SupportHysteresis=double.NaN}, Ballistic with{PlanarVerticalRule=(VerticalBoundaryRule)99} })
            Case("Safety", $"invalid tuning {bad}", () => { var r=Step(Body(vx:100),tuning:bad); Check(r.Failure==PhysicsFailure.InvalidTuning && r.State.Rect==Body().Rect && r.State.Velocity.IsFinite,"invalid tuning leaked"); });
        foreach(var bad in new[]{Body() with{Velocity=new(double.NaN,0)},Body() with{Velocity=new(double.MaxValue,0)},Body() with{Rect=new(0,0,double.PositiveInfinity,20)},Body() with{Rect=new(0,0,0,20)},Body() with{Accumulator=-1},Body() with{Accumulator=1},Body() with{BounceCount=3},Body() with{PreviousBounceSpeed=double.NaN},Body() with{Rect=new(1e200,1e200,1e201,1e201)}})
            Case("Safety","invalid state",()=> {var r=Step(bad); Check(!r.Success&&r.State.Phase==PhysicsPhase.Invalid&&r.State.Velocity.IsFinite&&double.IsFinite(r.State.Rect.Left)&&double.IsFinite(r.State.Rect.Right),"unsafe result");});
        Case("Safety","unknown mode",()=>Check(!Step(Body(),(PhysicsMode)99).Success,"unknown mode accepted"));
        Case("Safety","invalid state cannot resume",()=>Check(!Step(Body() with{Phase=PhysicsPhase.Invalid}).Success,"resumed invalid"));
        Case("Safety","valid low speed cap keeps rebound state reusable",()=>{
            var bounds=PhysicsBounds.FromWorkAreas([new(0,0,1000,1000)]);
            var t=Ballistic with{Gravity=100000,FixedSeconds=1d/30,MaximumSpeed=10,MinimumBounceSpeed=1,StopSpeed=0};
            var first=Step(Body(100,979),bounds:bounds,elapsed:1d/30,tuning:t);
            Check(first.Success&&first.State.Velocity.Y<0,"no first rebound");
            Near(first.State.PreviousBounceSpeed,Math.Abs(first.State.Velocity.Y));
            var s=first.State;for(int i=0;i<120;i++){var r=Step(s,bounds:bounds,elapsed:1d/30,tuning:t);Check(r.Success,"valid output rejected on next frame");s=r.State;}
            Check(s.Phase==PhysicsPhase.Settled&&s.BounceCount<=2,"never settled after speed cap");
        });
        Case("Safety","vector speed cap output is accepted on every next step",()=>{
            foreach(var vx in new[]{-8d,-3d,0d,3d,8d}) {
                var t=Ballistic with{Gravity=100000,MaximumSpeed=10};var s=Body(vx:vx);
                for(int i=0;i<100;i++){var r=Step(s,tuning:t);Check(r.Success,$"speed cap state invalid at vx={vx}, frame={i}");s=r.State;}
            }
        });
        Case("Safety","deterministic full result sequence",()=>{
            var a=Body(100,500,300,-500);var b=a;
            for(int i=0;i<1200;i++){var x=Step(a,bounds:room,tuning:new());var y=Step(b,bounds:room,tuning:new());Check(x.State==y.State&&x.Impacts.SequenceEqual(y.Impacts)&&x.Failure==y.Failure,"nondeterministic");a=x.State;b=y.State;}
        });
        Case("Dependency","no platform assembly references",()=>{
            var names=typeof(PhysicsStep).Assembly.GetReferencedAssemblies().Select(x=>x.Name??"").ToArray();
            Check(!names.Any(x=>x.Contains("Windows")||x.Contains("Presentation")||x.Contains("NecoArcDesktopPet")),"platform reference");
            Check(!typeof(PhysicsStep).Assembly.GetTypes().SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)).Any(m=>(m.Attributes&MethodAttributes.PinvokeImpl)!=0),"native import");
        });
    }
}
