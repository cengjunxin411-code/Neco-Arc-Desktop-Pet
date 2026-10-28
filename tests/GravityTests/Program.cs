using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

static partial class Program
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static int Main(string[] args)
    {
        try {
            if(args.Contains("--landing-only")){LandingCases();Console.WriteLine($"PASS {checks} landing assertions");return 0;}
            if (args.Contains("--trajectory-report")) { TrajectoryReport(); return 0; }
            FeelCases();
            BallisticCases();
            TimingCases();
            if(args.Contains("--timing-only")){Console.WriteLine($"PASS {checks} timing assertions");return 0;}
            foreach(var work in new[]{new PhysicalRect(0,0,1920,1040),new(-1920,-300,0,740),new(-800,-1200,500,-100)})
            foreach(var size in new[]{30,170,470})
            foreach(var gap in new[]{1,8,30,150,500}) {
                var start=new PhysicalRect(work.Left+50,work.Bottom-size-gap,work.Left+50+size,work.Bottom-gap);
                var s=new GravitySession(start,work);var moves=0;var collisions=0;double previous=double.PositiveInfinity;
                Check(s.State.Velocity==default,"19C1 starts with zero velocity");
                for(int i=0;i<1000&&!s.Stopped;i++) {
                    var action=s.Advance(1d/120,s.Actual,out var target);
                    Check(action!=GravityAdvance.Invalid,"legal single screen model remains valid");
                    Check(s.State.Rect.Left==start.Left&&s.State.Velocity.X==0,"zero horizontal speed keeps exact X");
                    Check(s.State.Rect.Bottom<=work.Bottom,"never crosses taskbar floor");
                    if(s.LastResult is {} result)foreach(var impact in result.Impacts){
                        collisions++;Check(impact.Kind==CollisionKind.Ground,"only ground impacts");
                        Check(impact.Contact.Bottom==work.Bottom,"physical WorkArea bottom, including negative coordinates");
                        if(s.State.BounceCount>0&&s.State.Velocity.Y<0){Check(s.State.PreviousBounceSpeed<previous,"outgoing rebounds diminish");previous=s.State.PreviousBounceSpeed;}
                    }
                    if(action==GravityAdvance.Move){moves++;Check(s.Accept(target),"exact accepted integer readback");}
                }
                Check(s.Grounded&&s.State.Phase==PhysicsPhase.Settled,"bounded natural stable settling");
                Check(s.Actual.Bottom==work.Bottom,"accepted integer body at real floor");
                Check(s.State.BounceCount==0&&collisions==1,"exactly one Ground contact and zero bounces");
                if(gap==1)Check(s.State.BounceCount==0,"low-speed fall does not rebound");
                if(gap==500)Check(s.State.BounceCount==0,"high fall also absorbs vertical speed");
                var actual=s.Actual;Check(s.Advance(.03,actual,out _)==GravityAdvance.Stop&&s.Actual==actual,"no repeated ground movement");
                Check(moves>0,"fall actually creates integer displacements");
            }
            var floor=new GravitySession(new(10,90,20,100),new(0,0,100,100));
            Check(floor.Advance(.03,floor.Actual,out _)==GravityAdvance.Stop&&floor.Grounded,"already grounded yields without a duplicate native request");
            foreach(var elapsed in new[]{-1d,double.NaN,double.PositiveInfinity,.251,.1}){
                var s=new GravitySession(new(10,10,20,20),new(0,0,100,100));var old=s.Actual;
                var action=s.Advance(elapsed,old,out var requestTime);
                if(elapsed is .251 or .1){
                    Check(!s.Stopped&&action is GravityAdvance.Hold or GravityAdvance.Move,"bounded discard retains session");
                    Check(action!=GravityAdvance.Move||s.Accept(requestTime),"bounded target accepted when movement is required");
                }else{
                    Check(action is GravityAdvance.Invalid or GravityAdvance.Stop&&s.Stopped&&s.Actual==old,"invalid time safely stops without requested movement");
                    Check(!s.Accept(old),"invalid/stopped cannot be accepted later");
                }
            }
            var zero=new GravitySession(new(10,10,20,20),new(0,0,100,100));
            Check(zero.Advance(0,zero.Actual,out _)==GravityAdvance.Hold&&zero.State.Rect==zero.Actual,"zero dt retains state without writing");
            Check(zero.Advance(.001,zero.Actual,out _)==GravityAdvance.Hold&&zero.State.Accumulator>0,"substep residue retained");
            var fractional=new GravitySession(new(10,10,20,20),new(0,0,100,100));
            Check(fractional.Advance(1d/120,fractional.Actual,out _)==GravityAdvance.Hold&&fractional.State.Rect.Top>10,"fractional model moves before integer native target");
            foreach(var pair in new[]{(new PhysicalRect(double.NaN,0,10,10),new PhysicalRect(0,0,100,100)),(new PhysicalRect(10,10,20,20),new PhysicalRect(0,0,0,0)),(new PhysicalRect(90,10,110,20),new PhysicalRect(0,0,100,100)),(new PhysicalRect(10,10,20.2,20),new PhysicalRect(0,0,100,100))}){
                var s=new GravitySession(pair.Item1,pair.Item2);
                Check(s.Advance(.03,s.Actual,out _)==GravityAdvance.Invalid,"invalid/noninteger/partial body or WorkArea refused without Fit");
            }
            var drift=new GravitySession(new(10,10,20,20),new(0,0,100,100));
            Check(drift.Advance(.03,new(11,10,21,20),out _)==GravityAdvance.Invalid,"foreign native actual refuses integration");
            var reject=new GravitySession(new(10,10,20,20),new(0,0,100,100));
            Check(reject.Advance(.03,reject.Actual,out var request)==GravityAdvance.Move,"native test has pending request");
            Check(!reject.Accept(request with{Top=request.Top+1,Bottom=request.Bottom+1})&&reject.Stopped&&!reject.Grounded,"one pixel mismatch stops permanently");
            var doubleStep=new GravitySession(new(10,10,20,20),new(0,0,100,100));doubleStep.Advance(.03,doubleStep.Actual,out _);
            Check(doubleStep.Advance(.03,doubleStep.Actual,out _)==GravityAdvance.Invalid,"cannot integrate twice without native accept");
            var bounded=new GravitySession(new(10,10,20,20),new(0,0,100,1e9));
            for(int i=0;i<1500&&!bounded.Stopped;i++)if(bounded.Advance(.01,bounded.Actual,out var target)==GravityAdvance.Move)Check(bounded.Accept(target),"bounded long flight readback");
            Check(bounded.Stopped&&!bounded.Grounded,"unreachable practical floor ends at finite session duration");
            Console.WriteLine($"PASS {checks} gravity session assertions");return 0;
        } catch(Exception e){Console.WriteLine(e);return 1;}
    }

    static void TimingCases()
    {
        var failures=new List<string>();
        void Case(string name,Action body){try{body();Console.WriteLine("PASS "+name);}catch(Exception e){failures.Add(name+": "+e.Message);Console.WriteLine("FAIL "+failures[^1]);}}
        foreach(var dt in new[]{.03,.06,.09,.12})Case($"tick {dt*1000}ms",()=>{
            var s=new GravitySession(new(10,10,30,30),new(0,0,1000,2000));int impacts=0,moves=0;double previous=double.PositiveInfinity;
            for(int i=0;i<400&&!s.Stopped;i++){
                var action=s.Advance(dt,s.Actual,out var target);
                Check(action!=GravityAdvance.Invalid&&(!s.Stopped||s.Grounded),"scheduling discard cannot terminate airborne session");
                var step=s.LastResult!;Check(step.Substeps<=8,"hard eight-substep cap");
                foreach(var impact in step.Impacts){impacts++;Check(impact.Kind==CollisionKind.Ground,"only real ground");if(impact.BounceCount>s.State.BounceCount)throw new Exception("invalid bounce history");}
                if(s.State.Velocity.Y<0&&s.State.PreviousBounceSpeed!=previous){Check(s.State.PreviousBounceSpeed<previous,"bounce decay");previous=s.State.PreviousBounceSpeed;}
                if(action==GravityAdvance.Move){moves++;Check(s.Accept(target),"one exact accept per computed move");}
            }
            Check(s.Grounded&&s.Actual.Bottom==2000&&moves>0,"same session reaches stable floor");
            Check(s.State.BounceCount==0&&impacts==1,"exactly one Ground contact under every bounded timing pattern");
        });
        Case("300ms discard resets residue and resumes",()=>{
            var s=new GravitySession(new(10,10,30,30),new(0,0,1000,2000));s.Advance(.005,s.Actual,out _);var before=s.State;
            Check(s.Advance(.3,s.Actual,out _)==GravityAdvance.Hold&&!s.Stopped,"late frame holds the same active session");
            Check(s.LastResult is {Failure:PhysicsFailure.LateFrameDiscarded,Substeps:0}&&Math.Abs(s.LastResult.DroppedSeconds-.305)<1e-10,"all late time and residue discarded");
            Check(s.State.Rect==before.Rect&&s.State.Accumulator==0,"late frame has zero motion/debt");
            Check(s.Advance(.03,s.Actual,out var next)==GravityAdvance.Move&&s.LastResult!.Substeps==3&&s.Accept(next),"normal tick resumes without catchup");
        });
        Case("budget commits only eight steps",()=>{
            var start=new PhysicalRect(10,10,30,30);var work=new PhysicalRect(0,0,1000,2000);var s=new GravitySession(start,work);var reference=new GravitySession(start,work);
            s.Advance(.005,s.Actual,out _);
            Check(s.Advance(.12,s.Actual,out var target)==GravityAdvance.Move&&!s.Stopped,"budget returns a live move");
            Check(s.LastResult is {Failure:PhysicsFailure.BudgetTimeDiscarded,Substeps:8}&&Math.Abs(s.LastResult.DroppedSeconds-7d/120)<1e-10,"seven excess steps discarded");
            for(int i=0;i<8;i++)if(reference.Advance(1d/120,reference.Actual,out var p)==GravityAdvance.Move)reference.Accept(p);
            Check(s.State.Rect==reference.State.Rect&&s.State.Velocity==reference.State.Velocity,"only eight physical steps integrated");Check(s.Accept(target),"computed budget target goes through Accept");
            var actual=s.Actual;Check(s.Advance(0,actual,out _)==GravityAdvance.Hold&&s.Actual==actual&&s.LastResult!.Substeps==0,"no repay on zero tick");
            s.Advance(.03,s.Actual,out var after);Check(s.LastResult!.Substeps==3&&s.Accept(after),"no extra debt on next tick");
        });
        Case("near-floor budget contact committed once",()=>{
            var s=new GravitySession(new(10,89,20,99),new(0,0,100,100));
            Check(s.Advance(.1,s.Actual,out var target)==GravityAdvance.Move&&!s.Grounded,"budget contact requires native acceptance");
            Check(s.LastResult!.Impacts.Length==1&&s.LastResult.Failure==PhysicsFailure.BudgetTimeDiscarded,"computed contact survives bounded discard");
            Check(s.Accept(target)&&s.Grounded&&s.Actual.Bottom==100,"settle only after accept");
            Check(s.Advance(.03,s.Actual,out _)==GravityAdvance.Stop&&s.Actual==target,"no second contact move after settle");
        });
        Case("safety stops never revive",()=>{
            foreach(var dt in new[]{-1d,double.NaN,double.PositiveInfinity}){var s=new GravitySession(new(10,10,20,20),new(0,0,100,100));Check(s.Advance(dt,s.Actual,out _)==GravityAdvance.Invalid,"invalid clock stops");Check(s.Advance(.03,s.Actual,out _)==GravityAdvance.Stop&&!s.Grounded,"invalid cannot revive");}
            // Phase 19C.2-R1 contract change: a side wall mirrors the horizontal
            // velocity at 75% and keeps the (damped) vertical velocity, so the
            // body leaves the wall and lands under gravity. C1 stopped the normal
            // velocity and C2.0 zeroed it; neither may terminate the session.
            var wall=new GravitySession(new(89,10,99,20),new(0,0,100,100));typeof(GravitySession).GetProperty("State",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(wall,wall.State with{Velocity=new(400,0)});
            for(int i=0;i<400&&!wall.Stopped;i++){var action=wall.Advance(.03,wall.Actual,out var wallTarget);Check(action!=GravityAdvance.Invalid,"side wall keeps a legal Gravity session");if(action==GravityAdvance.Move)Check(wall.Accept(wallTarget),"side wall target accepted by the same session");Check(wall.State.Rect.Left>=0&&wall.State.Rect.Right<=100,"side wall bounce stays inside the WorkArea");}
            Check(wall.Grounded&&wall.Actual.Bottom==100,"side wall throw settles on the real floor");Check(wall.State.BounceCount is >=0 and <=2,"side wall bounce does not consume extra ground bounces");Check(wall.Advance(.03,wall.Actual,out _)==GravityAdvance.Stop,"settled wall throw cannot revive");
            var late=new GravitySession(new(10,10,20,20),new(0,0,100,1e9));for(int i=0;i<41;i++)late.Advance(.3,late.Actual,out _);
            Check(late.Stopped&&!late.Grounded&&late.State.Phase==PhysicsPhase.Invalid,"discarded time still counts toward 12s lifetime");
        });
        if(failures.Count>0)throw new Exception(string.Join("; ",failures));
    }
}
