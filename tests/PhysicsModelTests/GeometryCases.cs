using NecoArcDesktopPet.Coordinates;
using NecoArcDesktopPet.Pilot.Physics;

internal static partial class Program
{
    static void GeometryCases()
    {
        var room=PhysicsBounds.FromWorkAreas([new(0,0,1000,1000)]);
        foreach(var v in new[]{(-600d,0d,CollisionKind.LeftWall),(600d,0d,CollisionKind.RightWall),(0d,-600d,CollisionKind.Ceiling),(0d,600d,CollisionKind.Ground)})
            Case("Wall",$"normal reflection {v}",()=>{
                var s=Body(v.Item1<0?0:v.Item1>0?980:100,v.Item2<0?0:v.Item2>0?980:100,v.Item1,v.Item2);
                var r=Step(s,PhysicsMode.PlanarInertia,bounds:room,tuning:Planar with{WallRestitution=.5});
                Check(r.Impacts.Length==1 && r.Impacts[0].Kind==v.Item3,"wrong collision kind");Near(r.Impacts[0].NormalSpeed,600);
                Near(r.State.Velocity.X,-v.Item1*.5);Near(r.State.Velocity.Y,-v.Item2*.5);Check(room.Covered(r.State.Rect),"outside");
            });
        Case("Wall","tangent velocity retained",()=>{var r=Step(Body(980,100,600,120),PhysicsMode.PlanarInertia,bounds:room,tuning:Planar);Near(r.State.Velocity.Y,120);Near(r.State.Rect.Top,101);});
        Case("Wall","planar vertical stop rule",()=>{var r=Step(Body(100,0,100,-600),PhysicsMode.PlanarInertia,bounds:room,tuning:Planar with{PlanarVerticalRule=VerticalBoundaryRule.Stop});Near(r.State.Velocity.Y,0);Near(r.State.Velocity.X,100);});
        Case("Wall","fast narrow gap legal endpoint cannot tunnel",()=>{
            var gap=PhysicsBounds.FromWorkAreas([new(0,0,100,1000),new(101,0,1000,1000)]);
            var r=Step(Body(70,100,20000),PhysicsMode.PlanarInertia,bounds:gap,tuning:Planar);
            Near(r.State.Rect.Right,100);Check(r.Impacts.Any(x=>x.Kind==CollisionKind.RightWall)&&r.State.Velocity.X<0,"tunnelled gap");
        });
        Case("Wall","vertical hole cannot tunnel",()=>{
            var gap=PhysicsBounds.FromWorkAreas([new(0,0,1000,100),new(0,101,1000,1000)]);
            var r=Step(Body(100,70,0,20000),PhysicsMode.PlanarInertia,bounds:gap,tuning:Planar);Near(r.State.Rect.Bottom,100);Check(r.State.Velocity.Y<0,"vertical tunnel");
        });
        var adjacent=PhysicsBounds.FromWorkAreas([new(-2560,0,0,1560),new(0,0,2560,1400)]);
        Case("Geometry","200/150 simulated horizontal seam",()=>{
            var r=Step(Body(-25,100,1200),PhysicsMode.PlanarInertia,bounds:adjacent,tuning:Planar);
            Near(r.State.Rect.Left,-15);Check(r.Impacts.Length==0,"seam became wall");
            var s=Run(r.State,PhysicsMode.PlanarInertia,10,1d/120,adjacent,Planar);Check(s.Rect.Left>0,"failed crossing");
        });
        Case("Geometry","partial seam rejects unfit transverse corridor",()=>{
            var r=Step(Body(0,1450,1200),PhysicsMode.PlanarInertia,bounds:adjacent,tuning:Planar);
            Check(!r.Success,"illegal starting footprint accepted");
            var s=Body(-20,1450,1200);r=Step(s,PhysicsMode.PlanarInertia,bounds:adjacent,tuning:Planar);Near(r.State.Rect.Right,0);Check(r.State.Velocity.X<0,"partial seam bypassed");
        });
        Case("Geometry","vertically stacked seam",()=>{
            var stack=PhysicsBounds.FromWorkAreas([new(0,-1000,1000,0),new(0,0,1000,1000)]);
            var r=Step(Body(100,-25,0,1200),PhysicsMode.PlanarInertia,bounds:stack,tuning:Planar);Near(r.State.Rect.Top,-15);Check(r.Impacts.Length==0,"stack seam wall");
        });
        Case("Geometry","corner-only topology blocks diagonal shortcut",()=>{
            var corner=PhysicsBounds.FromWorkAreas([new(0,0,100,100),new(100,100,200,200)]);
            var r=Step(Body(80,80,2400,2400),PhysicsMode.PlanarInertia,bounds:corner,tuning:Planar);
            Check(r.State.Rect.Right<=100 && r.State.Rect.Bottom<=100,"crossed corner void");
        });
        Case("Geometry","interior rectangular hole full slab coverage",()=>{
            var ring=PhysicsBounds.FromWorkAreas([new(0,0,300,100),new(0,100,100,200),new(200,100,300,200),new(0,200,300,300)]);
            Check(!ring.Covered(new(50,50,250,250)),"corners hid hole");
            Check(ring.Covered(new(0,0,300,100)),"legal strip refused");
        });
        Case("Geometry","different floors retain high support then fall",()=>{
            var bounds=PhysicsBounds.FromWorkAreas([new(-1000,0,0,500),new(0,0,1000,800)]);
            var s=Body(-25,480,120,0);s=Run(s,PhysicsMode.Gravity,4,1d/120,bounds,Ballistic);Near(s.Rect.Bottom,500);
            s=Run(s,PhysicsMode.Gravity,30,1d/120,bounds,Ballistic);Check(s.Rect.Left>0&&s.Rect.Bottom>500&&s.Phase==PhysicsPhase.Airborne,"support levitated over lower floor");
        });
        Case("Geometry","higher adjacent floor is wall, never auto lift",()=>{
            var bounds=PhysicsBounds.FromWorkAreas([new(-1000,0,0,800),new(0,0,1000,500)]);
            var r=Step(Body(-20,780,120,0),bounds:bounds,tuning:Ballistic);Near(r.State.Rect.Top,780);Near(r.State.Rect.Right,0);Check(r.State.Velocity.X<0,"auto lifted");
        });
        Case("Geometry","support overlap with coplanar hysteresis",()=>{
            var bounds=PhysicsBounds.FromMonitors([new("left",new(-100,0,0,100),new(-100,0,0,100)),new("right",new(0,0,100,100),new(0,0,100,100))]);
            var r=new PhysicalRect(-9,80,11,100);Check(bounds.Ground(r,"left")?.Id=="left","hysteresis absent");
            Check(bounds.Ground(new(-1,80,19,100),"left")?.Id=="right","hysteresis never releases");
            for(int i=0;i<100;i++)Check(bounds.Ground(new(-10+(i%2==0?.2:-.2),80,10+(i%2==0?.2:-.2),100),"left")?.Id=="left","support oscillates");
        });
        Case("Geometry","nearest blocking floor wins over overlap",()=>{
            var bounds=PhysicsBounds.FromWorkAreas([new(-100,0,0,100),new(0,0,100,200)]);
            Near(bounds.Ground(new(-1,50,19,70))!.Value.Floor,100);
        });
        Case("Geometry","integer target preserves state and coverage",()=>{
            var rect=new PhysicalRect(.6,.6,20.6,20.6);Check(room.TryRoundedTarget(rect,out var target),"target refused");Near(target.Left,1);Near(target.Top,1);Near(rect.Left,.6);
        });
        Case("Geometry","rounding into narrow hole refused",()=>{
            var bounds=PhysicsBounds.FromWorkAreas([new(0,0,100.7,100)]);
            Check(!bounds.TryRoundedTarget(new(80.6,0,100.6,20),out _),"rounded target entered a hole");
        });
        foreach(var areas in new PhysicalRect[][] { [],[new(0,0,0,20)],[new(0,0,double.NaN,20)],[new(0,0,100,100),new(50,0,150,100)],[new(-1e200,0,1e200,20)]})
            Case("Safety","bad WorkAreas rejected",()=>{var bounds=PhysicsBounds.FromWorkAreas(areas);Check(!bounds.IsValid&&!Step(Body(),bounds:bounds).Success,"bad topology accepted");});
        Case("Safety","duplicate monitor ids",()=>Check(!PhysicsBounds.FromMonitors([new("A",new(0,0,100,100),new(0,0,100,100)),new("A",new(100,0,200,100),new(100,0,200,100))]).IsValid,"duplicate id accepted"));
        Case("Safety","invalid monitor WorkArea",()=>Check(!PhysicsBounds.FromMonitors([new("A",new(0,0,100,100),new(0,0,101,100))]).IsValid,"outside bounds accepted"));
        Case("Safety","null topology",()=>Check(!PhysicsStep.Advance(Body(),PhysicsMode.Gravity,.01,null).Success&&!PhysicsBounds.FromWorkAreas(null).IsValid,"null accepted"));
        Case("Safety","topology snapshot immutable",()=>{var list=new[]{new PhysicalRect(0,0,1000,1000)};var b=PhysicsBounds.FromWorkAreas(list);list[0]=default;Check(b.Covered(new(1,1,21,21)),"caller mutated snapshot");});
    }
}
