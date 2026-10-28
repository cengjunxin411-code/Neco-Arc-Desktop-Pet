using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

static partial class Program
{
    const double H = 1d / 120;
    static void Near(double actual, double expected, string name) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) < 1e-7,
            $"{name}: expected {expected:R}, got {actual:R}");
    static double Speed(PhysicalVelocity v) => Math.Sqrt(v.X*v.X + v.Y*v.Y);

    static PhysicsTuning Reflect(double drag = 3, double retention = .85, double minimum = 4) =>
        new() { WallRestitution = 1, PlanarDrag = drag,
            PlanarBounceRetention = retention, PlanarBounceMinimumSpeed = minimum };

    static void ReflectionCases()
    {
        var failures = new List<string>();
        void Case(string name, Action body)
        {
            try { body(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL " + failures[^1]); }
        }
        var work = new PhysicalRect(0, 0, 100, 100);
        var bounds = PhysicsBounds.FromWorkAreas([work]);
        PhysicsResult Step(PhysicsState state, PhysicsTuning? tuning = null, double elapsed = H) =>
            PhysicsStep.Advance(state, PhysicsMode.PlanarInertia, elapsed, bounds, tuning ?? Reflect());

        // Catches normal-only loss, double damping and contact-time velocity
        // leaking into the new full-substep damping contract.
        foreach (var (v, kind) in new[] {
            (new PhysicalVelocity(600,120), CollisionKind.RightWall),
            (new PhysicalVelocity(-600,120), CollisionKind.LeftWall),
            (new PhysicalVelocity(120,600), CollisionKind.Ground),
            (new PhysicalVelocity(120,-600), CollisionKind.Ceiling) })
        Case("T1 model " + kind, () => {
            var x = v.X > 500 ? 88 : v.X < -500 ? 2 : 40;
            var y = v.Y > 500 ? 88 : v.Y < -500 ? 2 : 40;
            var r = Step(new(new(x,y,x+10,y+10),v));
            Check(r.Success && r.Impacts is [var impact] && impact.Kind == kind, "single correct impact");
            var factor = Math.Exp(-.025)*.85;
            Near(r.State.Velocity.X, v.X*factor*(Math.Abs(v.X)>500?-1:1), "normal/tangent x");
            Near(r.State.Velocity.Y, v.Y*factor*(Math.Abs(v.Y)>500?-1:1), "normal/tangent y");
            Check(r.State.Phase==PhysicsPhase.Airborne && r.State.SupportId==null && r.State.BounceCount==0, "Planar has no floor support/bounce budget");
            var after=Step(r.State);
            Check(after.Success && after.Impacts.Length==0 && bounds.Covered(after.State.Rect), "depart contact without repeat");
        });

        Case("T2 corner once per fixed substep", () => {
            var r=Step(new(new(88,88,98,98),new(600,600)));
            Check(r.Impacts.Select(i=>i.Kind).SequenceEqual(new[]{CollisionKind.RightWall,CollisionKind.Ground}), "both axes reflected");
            Near(r.State.Velocity.X,-600*Math.Exp(-.025)*.85,"corner x loses once");
            Near(r.State.Velocity.Y,-600*Math.Exp(-.025)*.85,"corner y loses once");
            var after=Step(r.State);
            Check(after.Impacts.Length==0,"corner not charged on departure");
            Near(after.State.Velocity.X,-600*Math.Exp(-.05)*.85,"departure damping only");
        });
        Case("T2 contacts in separate substeps each lose 15 percent", () => {
            // First X reaches right; Y only reaches bottom on the next substep.
            var start=new PhysicsState(new(88,83,98,93),new(600,600));
            var a=Step(start); var b=Step(a.State);
            Check(a.Impacts is [var x] && x.Kind==CollisionKind.RightWall && b.Impacts is [var y] && y.Kind==CollisionKind.Ground,"distinct substep contacts");
            Near(b.State.Velocity.X,-600*Math.Exp(-.05)*.85*.85,"two losses x");
            Near(b.State.Velocity.Y,-600*Math.Exp(-.05)*.85*.85,"two losses y");
            var batch=Step(start,elapsed:2*H);
            Check(batch.Substeps==2 && batch.Impacts.Length==2,"batched Advance still has two collisions");
            Check(batch.State==b.State,"retention applies per substep, not per Advance");
        });

        foreach(var n in new[]{0d,.001,3.999,4,4.001})
        foreach(var kind in Enum.GetValues<CollisionKind>())
        Case($"T3 grazing {kind} normal {n}",()=>{
            bool horizontal=kind is CollisionKind.LeftWall or CollisionKind.RightWall;
            var x=kind==CollisionKind.LeftWall?0:kind==CollisionKind.RightWall?90:40;
            var y=kind==CollisionKind.Ceiling?0:kind==CollisionKind.Ground?90:40;
            var normal=kind is CollisionKind.LeftWall or CollisionKind.Ceiling?-n:n;
            var v=horizontal?new PhysicalVelocity(normal,100):new(100,normal);
            var r=Step(new(new(x,y,x+10,y+10),v),Reflect(drag:0));
            var factor=n>4?.85:1;
            Near(horizontal?r.State.Velocity.Y:r.State.Velocity.X,100*factor,"tangent charged only above threshold");
            Near(horizontal?r.State.Velocity.X:r.State.Velocity.Y,-normal*factor,"small normal reflects without tangent loss");
            for(int i=0;i<10;i++){
                r=Step(r.State,Reflect(drag:0));
                Check(r.Success && r.Impacts.Length==0 && bounds.Covered(r.State.Rect),"no repeated grazing contact/oscillation");
                Near(horizontal?r.State.Velocity.Y:r.State.Velocity.X,100*factor,"no repeated tangent charge");
            }
        });

        foreach(var v in new[]{new PhysicalVelocity(6000,3000),new(-12000,-12000),new(20000,0),new(0,-20000)})
        Case("T4 bounded repeated reflection " + v,()=>{
            var s=new PlanarInertiaSession(new(40,40,50,50),v,work);
            int hits=0,moves=0; var edges=new HashSet<CollisionKind>();
            for(int i=0;i<400&&!s.Stopped;i++){
                var before=s.State.Velocity; var action=s.Advance(H,s.Actual,out var target);
                Check(action!=PlanarAdvance.Invalid,"valid throughout reflection run");
                var r=s.LastResult!; hits+=r.Impacts.Length;
                foreach(var impact in r.Impacts)edges.Add(impact.Kind);
                Check(Speed(s.State.Velocity)<=Speed(before)+1e-8 && bounds.Covered(s.State.Rect),"finite covered non-increasing speed");
                if(action==PlanarAdvance.Move){Check(s.Accept(target),"repeated reflection accepts integer target");moves++;}
                if(r.Impacts.Length>0 && s.State.Phase!=PhysicsPhase.Settled)Check(!s.Stopped,"collision keeps same session live");
                foreach(bool horizontal in new[]{true,false}){
                    var axisHits=r.Impacts.Count(i=>horizontal?i.Kind is CollisionKind.LeftWall or CollisionKind.RightWall:i.Kind is CollisionKind.Ceiling or CollisionKind.Ground);
                    var old=horizontal?before.X:before.Y;var next=horizontal?s.State.Velocity.X:s.State.Velocity.Y;
                    if(next!=0)Check(Math.Sign(next)==Math.Sign(old)*(axisHits%2==0?1:-1),"direction follows collision parity");
                }
            }
            Check(s.Stopped && s.State.Phase==PhysicsPhase.Settled && s.State.Velocity==default,"finite natural settle");
            Check(hits>=4 && moves>10 && edges.Count>=2,"multiple outward/return collisions");
            Check(s.State.SupportId==null && s.State.BounceCount==0,"no gravity/ground support");
            Console.WriteLine($"TRACE {v}: impacts={hits}, moves={moves}, settled={s.State.Rect}");
        });
        foreach(var n in new[]{.001,4})
        foreach(var kind in Enum.GetValues<CollisionKind>())
        Case($"T3 session quantized grazing {kind} normal {n}",()=>{
            bool horizontal=kind is CollisionKind.LeftWall or CollisionKind.RightWall;
            var x=kind==CollisionKind.LeftWall?0:kind==CollisionKind.RightWall?90:40;
            var y=kind==CollisionKind.Ceiling?0:kind==CollisionKind.Ground?90:40;
            var normal=kind is CollisionKind.LeftWall or CollisionKind.Ceiling?-n:n;
            var s=new PlanarInertiaSession(new(x,y,x+10,y+10),horizontal?new(normal,100):new(100,normal),work);
            for(int i=0;i<20;i++){
                var a=s.Advance(H,s.Actual,out var target);
                Check(a is PlanarAdvance.Move or PlanarAdvance.Hold,"grazing stays live");
                Check(s.LastResult!.Impacts.Length==(i==0?1:0),"integer readback does not repeat contact");
                Near(horizontal?s.State.Velocity.Y:s.State.Velocity.X,100*Math.Exp(-3*(i+1)*H),"grazing session tangent only continuous damping");
                if(a==PlanarAdvance.Move)Check(s.Accept(target),"rounded grazing readback accepted");
            }
        });
        Case("exact end contact and contact-induced settle",()=>{
            var exact=Step(new(new(88,40,98,50),new(240,100)),Reflect(drag:0));
            Check(exact.Impacts is [var hit] && hit.Kind==CollisionKind.RightWall,"end-of-step exact touch is one contact");
            Near(exact.State.Velocity.X,-204,"exact contact normal");Near(exact.State.Velocity.Y,85,"exact contact tangent");
            var low=Step(new(new(90,40,100,50),new(4.1,0)));
            Check(low.Impacts.Length==1 && low.State.Phase==PhysicsPhase.Settled && low.State.Velocity==default,"loss below StopSpeed settles immediately");
        });
        Case("bounded collision budget and late safety stop",()=>{
            var s=new PlanarInertiaSession(new(88,40,98,50),new(600,120),work);
            var a=s.Advance(.25,s.Actual,out var target);
            Check(a==PlanarAdvance.Move && !s.Stopped && s.LastResult is {Substeps:8,Failure:PhysicsFailure.BudgetTimeDiscarded},"eight-step collision budget continues");
            Check(s.Accept(target),"budget reflected target accepted");
            var state=s.State;var actual=s.Actual;
            Check(s.Advance(0,actual,out _)==PlanarAdvance.Hold && s.State.Rect==state.Rect,"no discarded budget catchup");
            Check(s.Advance(.251,actual,out _)==PlanarAdvance.Stop && s.Actual==actual && s.State.Rect==state.Rect,"late frame after reflection stops without placement");
        });
        Case("T4 collision-free glide and settle in place",()=>{
            var s=new PlanarInertiaSession(new(400,400,410,410),new(300,-120),new(0,0,1000,1000));
            for(int i=0;i<400&&!s.Stopped;i++){
                var a=s.Advance(H,s.Actual,out var target);
                Check(a!=PlanarAdvance.Invalid && s.LastResult!.Impacts.Length==0,"free glide no impacts");
                if(a==PlanarAdvance.Move)Check(s.Accept(target),"free glide accepted");
            }
            Check(s.Stopped && s.State.Phase==PhysicsPhase.Settled && s.Actual.Bottom<410,"settled without floor snap");
        });
        Case("safety pending request blocks second advance after reflection",()=>{
            var s=new PlanarInertiaSession(new(88,40,98,50),new(600,120),work);
            Check(s.Advance(H,s.Actual,out _)==PlanarAdvance.Move,"collision pending request exists");
            Check(s.Advance(H,s.Actual,out _)==PlanarAdvance.Invalid && s.Stopped,"unaccepted collision cannot advance");
        });
        Case("safety reflection readback mismatch cannot revive",()=>{
            var s=new PlanarInertiaSession(new(88,40,98,50),new(600,120),work);
            Check(s.Advance(H,s.Actual,out var p)==PlanarAdvance.Move,"collision request exists");
            Check(!s.Accept(p with{Left=p.Left-1,Right=p.Right-1}) && s.Stopped,"one pixel mismatch refused");
            Check(s.Advance(H,s.Actual,out _)==PlanarAdvance.Stop,"refusal permanent");
        });
        Case("tuning validates retention and minimum",()=>{
            foreach(var value in new[]{double.NaN,double.NegativeInfinity,double.PositiveInfinity,-.01,1.01})
                Check(Step(new(new(40,40,50,50),new(100,100)),Reflect(retention:value)).Failure==PhysicsFailure.InvalidTuning,"invalid retention rejected");
            foreach(var value in new[]{double.NaN,double.NegativeInfinity,double.PositiveInfinity,-.01,10000.01})
                Check(Step(new(new(40,40,50,50),new(100,100)),Reflect(minimum:value)).Failure==PhysicsFailure.InvalidTuning,"invalid minimum rejected");
            Check(Reflect(retention:0,minimum:10000).IsValid && Reflect(retention:1,minimum:0).IsValid,"safe endpoint tunings accepted");
        });
        foreach(var kind in Enum.GetValues<CollisionKind>())
        Case("T5 disabled options retain original contact-time response " + kind,()=>{
            bool horizontal=kind is CollisionKind.LeftWall or CollisionKind.RightWall;
            var x=kind==CollisionKind.LeftWall?0:kind==CollisionKind.RightWall?90:40;
            var y=kind==CollisionKind.Ceiling?0:kind==CollisionKind.Ground?90:40;
            var normal=kind is CollisionKind.LeftWall or CollisionKind.Ceiling?-600:600;
            var s=new PhysicsState(new(x,y,x+10,y+10),horizontal?new(normal,120):new(120,normal));
            var legacy=Step(s,new PhysicsTuning());
            Near(horizontal?legacy.State.Velocity.X:legacy.State.Velocity.Y,-normal*.45,"default normal still uses incoming contact-time speed");
            Near(horizontal?legacy.State.Velocity.Y:legacy.State.Velocity.X,120*Math.Exp(-.025),"default tangent has damping only");
            var disabled=Step(s,new PhysicsTuning{PlanarBounceMinimumSpeed=10000});
            Check(legacy.State==disabled.State && legacy.Impacts.SequenceEqual(disabled.Impacts),"retention one disables new response regardless of threshold");
            var gravity=PhysicsStep.Advance(s,PhysicsMode.Gravity,H,bounds);
            var gravityWithPlanarOptions=PhysicsStep.Advance(s,PhysicsMode.Gravity,H,bounds,new PhysicsTuning{PlanarBounceRetention=.85,PlanarBounceMinimumSpeed=4});
            Check(gravity.State==gravityWithPlanarOptions.State && gravity.Impacts.SequenceEqual(gravityWithPlanarOptions.Impacts),"Planar options never affect Gravity response");
        });
        if(failures.Count>0)throw new Exception(string.Join(Environment.NewLine,failures));
    }
}
