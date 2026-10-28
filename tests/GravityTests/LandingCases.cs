using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

static partial class Program
{
    static void LandingCases()
    {
        var first=new GravitySession(new(100,899,200,999),new(0,0,10000,1000),new(600,600));
        first.Advance(.03,first.Actual,out _);
        Check(first.LastResult!.Substeps==1&&Math.Abs(first.State.Velocity.X-600*Math.Exp(-1d/120))<1e-8,
            "multi-substep tick returns at first Ground before unpresented sliding consumes vx");
        var work = new PhysicalRect(0, 0, 10000, 3000);
        foreach(var speed in new[]{20d,450d,1800d})
        foreach(var gap in new[]{1d,200d,800d}) {
            var s = new GravitySession(new(100,3000-gap-100,200,3000-gap),work,new(speed,300));
            bool hit=false; double atContact=0;
            for(int i=0;i<1200&&!s.Stopped;i++) {
                var prior=s.State.Velocity;
                var action=s.Advance(1d/120,s.Actual,out var target);
                Check(action!=GravityAdvance.Invalid,"landing remains valid");
                if(s.LastResult!.Impacts.Any(x=>x.Kind==CollisionKind.Ground)) {
                    Check(!hit,"only one first Ground impact");hit=true;atContact=s.State.Velocity.X;
                    Check(s.State.BounceCount==0&&s.State.Velocity.Y==0,"Gravity first Ground absorbs vertical velocity with zero bounces");
                    Check(Math.Abs(atContact-prior.X*Math.Exp(-1d/120))<1e-8,"contact retains legal air-integrated X");
                }
                else if(hit) {
                    Check(s.State.Velocity.Y==0&&s.State.Rect.Bottom==work.Bottom,"slide stays on real floor");
                    Check(s.State.Velocity.X>=0&&s.State.Velocity.X<=prior.X,"no reverse or acceleration");
                    Check(Math.Abs(s.State.Velocity.X-Math.Max(0,prior.X-7.5))<1e-8||s.State.Velocity.X==0&&prior.X<=11.5,"900 px/s2 friction with existing stop threshold");
                }
                if(action==GravityAdvance.Move)Check(s.Accept(target),"exact native acceptance");
            }
            Check(hit&&s.Grounded&&s.State.BounceCount==0,"fall then slide settles without rebound");
        }
        foreach(var direction in new[]{-1,1})
        foreach(var simultaneous in new[]{false,true}) {
            var rect=direction>0?new PhysicalRect(900,simultaneous?899:900,1000,simultaneous?999:1000):new(0,simultaneous?899:900,100,simultaneous?999:1000);
            var s=new GravitySession(rect,new(0,0,1000,1000),new(direction*600,simultaneous?600:0));
            var action=s.Advance(.03,s.Actual,out var target);
            Check(s.State.Velocity==default&&s.State.BounceCount==0,"ground/side simultaneous or supported wall absorbs both axes");
            Check(direction>0?s.State.Rect.Left==rect.Left:s.State.Rect.Right==rect.Right,"no reverse displacement within multi-substep wall contact");
            if(action==GravityAdvance.Move)Check(s.Accept(target),"corner exact acceptance");
        }
        var defaults=new PhysicsTuning();
        Check(defaults.GroundRestitution==.22,"shared default restitution unchanged");
        var bounce=PhysicsStep.Advance(new(new PhysicalRect(100,899,200,999),new(100,1000)),PhysicsMode.Gravity,1d/120,PhysicsBounds.FromWorkAreas([new(0,0,1000,1000)]));
        Check(bounce.State.BounceCount==1&&bounce.State.Velocity.Y<0,"default model retains bounce");
        var air=new GravitySession(new(900,100,1000,200),new(0,0,1000,3000),new(600,100));
        air.Advance(1d/120,air.Actual,out _);
        Check(Math.Abs(air.State.Velocity.X+450)<1e-8,"air side wall retains 75 percent bounce");
    }
}
