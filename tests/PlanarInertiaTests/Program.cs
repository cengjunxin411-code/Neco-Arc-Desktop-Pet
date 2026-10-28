using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

static partial class Program
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static int Main(string[] args){
        try {
            if(args.Contains("--reflection-only")){ReflectionCases();Console.WriteLine($"PASS {checks} reflection assertions");return 0;}
            foreach(var v in new[]{new PhysicalVelocity(100,0),new(-100,0),new(0,-100),new(0,100),new(80,-60)}){
                var s=new PlanarInertiaSession(new(400,400,450,450),v,new(0,0,1000,1000));
                for(int i=0;i<300&&!s.Stopped;i++){
                    var previous=s.State.Velocity;
                    var result=s.Advance(.03,s.Actual,out var target);
                    if(result==PlanarAdvance.Move)Check(s.Accept(target),"accept exact readback");
                    Check(Math.Abs(s.State.Velocity.X)<=Math.Abs(previous.X)&&Math.Abs(s.State.Velocity.Y)<=Math.Abs(previous.Y),"stepwise monotonic damping/no gravity");
                    Check(v.X==0?s.State.Rect.Left==400:Math.Sign(s.State.Rect.Left-400)==Math.Sign(v.X),"x direction");
                    Check(v.Y==0?s.State.Rect.Top==400:Math.Sign(s.State.Rect.Top-400)==Math.Sign(v.Y),"y direction");
                }
                Check(s.Stopped,"natural stop bounded");
            }
            var micro=new PlanarInertiaSession(new(50,50,60,60),new(5,0),new(0,0,100,100));
            for(int i=0;i<20&&!micro.Stopped;i++){if(micro.Advance(.03,micro.Actual,out var target)==PlanarAdvance.Move)Check(micro.Accept(target),"micro accept");}
            Check(micro.State.Rect.Left>50.3,"fractional integration retained despite repeated rounded target");
            foreach(var v in new[]{new PhysicalVelocity(10,0),new(-10,0),new(0,10),new(0,-10)}){
                var s=new PlanarInertiaSession(new(50,50,60,60),v,new(0,0,100,100));var holds=0;var moves=0;
                for(int i=0;i<120&&!s.Stopped;i++){
                    var result=s.Advance(1d/120,s.Actual,out var target);
                    if(result==PlanarAdvance.Hold)holds++;
                    if(result==PlanarAdvance.Move){Check(s.Accept(target),"low speed exact readback");moves++;}
                }
                Check(holds>0&&moves>0,"fractional Holds eventually create accepted integer Move");
                Check(v.X==0?s.Actual.Left==50:Math.Sign(s.Actual.Left-50)==Math.Sign(v.X),"low-speed actual x direction");
                Check(v.Y==0?s.Actual.Top==50:Math.Sign(s.Actual.Top-50)==Math.Sign(v.Y),"low-speed actual y direction");
            }
            foreach(var v in new[]{new PhysicalVelocity(6000,600),new(-6000,600),new(600,6000),new(600,-6000)}){
                var s=new PlanarInertiaSession(new(-70,-70,-20,-20),v,new(-100,-100,0,0));
                var old=s.Actual;
                var action=s.Advance(1d/120,old,out var target);
                Check(action==PlanarAdvance.Move&&!s.Stopped,"four edges reflect and submit contact instead of stopping");
                var kind=Math.Abs(v.X)>Math.Abs(v.Y)
                    ? v.X>0?CollisionKind.RightWall:CollisionKind.LeftWall
                    : v.Y>0?CollisionKind.Ground:CollisionKind.Ceiling;
                Check(s.LastResult!.Impacts is [var impact]&&impact.Kind==kind,"four edges collision kind");
                var factor=Math.Exp(-3d/120)*.85;
                Near(s.State.Velocity.X,v.X*factor*(Math.Abs(v.X)>Math.Abs(v.Y)?-1:1),"reflected/damped x");
                Near(s.State.Velocity.Y,v.Y*factor*(Math.Abs(v.Y)>Math.Abs(v.X)?-1:1),"reflected/damped y");
                Check(s.Accept(target),"collision target exact readback");
                Check(s.Advance(1d/120,s.Actual,out var after)==PlanarAdvance.Move&&!s.Stopped,"same session advances after reflection");
                Check(s.LastResult!.Impacts.Length==0&&s.Accept(after),"leaves contact without a repeated impact");
            }
            ReflectionCases();
            var late=new PlanarInertiaSession(new(50,50,60,60),new(100,0),new(0,0,200,200));
            Check(late.Advance(.9,late.Actual,out _)==PlanarAdvance.Stop&&late.LastResult?.Failure==PhysicsFailure.LateFrameDiscarded,"late time discarded stops no jump");
            var budget=new PlanarInertiaSession(new(50,50,60,60),new(100,0),new(0,0,200,200));
            budget.Advance(.25,budget.Actual,out _);
            Check(budget.LastResult?.Failure==PhysicsFailure.BudgetTimeDiscarded&&budget.State.Rect.Left<57,"budget limited advance");
            foreach(var elapsed in new[]{-1,double.NaN,double.PositiveInfinity}){
                var s=new PlanarInertiaSession(new(50,50,60,60),new(100,0),new(0,0,200,200));
                Check(s.Advance(elapsed,s.Actual,out _)==PlanarAdvance.Invalid,"invalid elapsed safe end");
            }
            var drift=new PlanarInertiaSession(new(50,50,60,60),new(100,0),new(0,0,200,200));
            Check(drift.Advance(.03,new(51,50,61,60),out _)==PlanarAdvance.Invalid,"foreign actual mismatch refused");
            var reject=new PlanarInertiaSession(new(50,50,60,60),new(100,0),new(0,0,200,200));
            reject.Advance(.03,reject.Actual,out _);
            Check(!reject.Accept(new(99,50,109,60))&&reject.Stopped,"readback mismatch no residual rebase");
            var invalid=new PlanarInertiaSession(new(0,0,50,50),new(100,0),new(0,0,20,20));
            Check(invalid.Advance(.03,invalid.Actual,out _)==PlanarAdvance.Invalid,"full body must fit");
            foreach(var rect in new[]{new PhysicalRect(0,80,10,100),new(0,0,10.2,10),new(double.NaN,0,10,10)}){
                var s=new PlanarInertiaSession(rect,new(10,0),new(0,0,100,90));
                Check(s.Advance(.03,s.Actual,out _)==PlanarAdvance.Invalid,"taskbar/noninteger/invalid footprint refused");
            }
            var nan=new PlanarInertiaSession(new(50,50,60,60),new(double.NaN,0),new(0,0,200,200));
            Check(nan.Advance(.03,nan.Actual,out _)==PlanarAdvance.Invalid,"invalid velocity model output never submitted");
            Console.WriteLine($"PASS {checks} planar session assertions");return 0;
        }catch(Exception e){Console.WriteLine(e);return 1;}
    }
}
