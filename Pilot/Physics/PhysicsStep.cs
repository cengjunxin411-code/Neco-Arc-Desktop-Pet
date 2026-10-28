using System;
using System.Collections.Generic;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot.Physics;

internal static class PhysicsStep
{
    internal static PhysicsResult Advance(PhysicsState state, PhysicsMode mode, double elapsed, PhysicsBounds? bounds, PhysicsTuning? tuning = null, bool gravityGroundLanding = false)
    {
        tuning ??= new();
        PhysicsResult Invalid(PhysicsFailure failure) => new(new(
            PhysicsBounds.SafeRect(state.Rect) ? state.Rect : default, default, PhysicsPhase.Invalid),failure,[],0,0);
        if(!tuning.IsValid) return Invalid(PhysicsFailure.InvalidTuning);
        if(bounds is null || !bounds.IsValid) return Invalid(PhysicsFailure.InvalidTopology);
        if(!Enum.IsDefined(mode) || !Enum.IsDefined(state.Phase) || state.Phase==PhysicsPhase.Invalid ||
            !PhysicsBounds.SafeRect(state.Rect) || !bounds.Covered(state.Rect) || !state.Velocity.IsFinite ||
            Math.Abs(state.Velocity.X)>tuning.MaximumSpeed || Math.Abs(state.Velocity.Y)>tuning.MaximumSpeed ||
            !PhysicsTuning.In(state.Accumulator,0,tuning.FixedSeconds) || state.Accumulator>=tuning.FixedSeconds ||
            state.BounceCount<0 || state.BounceCount>tuning.MaximumBounces ||
            !PhysicsTuning.In(state.PreviousBounceSpeed,0,tuning.MaximumSpeed) || !double.IsFinite(elapsed) || elapsed<0)
            return Invalid(PhysicsFailure.InvalidInput);
        var speed=Speed(state.Velocity.X,state.Velocity.Y);
        if(speed>tuning.MaximumSpeed) return Invalid(PhysicsFailure.InvalidInput);
        if(mode==PhysicsMode.Off) return new(state with{Velocity=default,Phase=PhysicsPhase.Inactive,Accumulator=0,SupportId=null},PhysicsFailure.None,[],0,0);
        if(elapsed>tuning.MaximumElapsed) return new(state with{Accumulator=0},PhysicsFailure.LateFrameDiscarded,[],0,elapsed+state.Accumulator);
        if(elapsed==0) return new(state,PhysicsFailure.None,[],0,0);
        var total=state.Accumulator+elapsed;
        // Tiny arithmetic tolerance prevents 240 Hz/irregular split sequences from
        // losing an entire substep. Only <1e-12 seconds is forgiven, never real debt.
        int available=(int)Math.Floor(total/tuning.FixedSeconds+1e-10);
        int steps=Math.Min(available,tuning.MaximumSubsteps);
        double remainder=Math.Max(0,total-available*tuning.FixedSeconds);
        if(remainder>=tuning.FixedSeconds) remainder=0;
        double dropped=(available-steps)*tuning.FixedSeconds;
        var impacts=new List<PhysicsImpact>(steps*2);
        var next=state;
        int performed=0;
        for(int i=0;i<steps;i++) {
            next=Integrate(next,mode,bounds,tuning,impacts,gravityGroundLanding);
            performed++;
            if(!bounds.Covered(next.Rect) || !next.Velocity.IsFinite) return Invalid(PhysicsFailure.InvalidInput);
            // A Gravity lease ends at its first accepted Ground boundary. Do not
            // consume unseen slide motion before the new viewport can be admitted.
            // Unused fixed time is discarded, never accrued as catch-up debt.
            if(gravityGroundLanding && mode==PhysicsMode.Gravity && impacts.Count>0 && impacts[^1].Kind==CollisionKind.Ground) break;
        }
        dropped+=(steps-performed)*tuning.FixedSeconds;
        next=next with{Accumulator=remainder};
        return new(next,dropped>0?PhysicsFailure.BudgetTimeDiscarded:PhysicsFailure.None,impacts.ToArray(),performed,dropped);
    }

    private static double Speed(double x,double y)=>Math.Sqrt(x*x+y*y);
    // Analytic integration of dv/dt=a-k*v for one fixed step. A short series
    // avoids catastrophic cancellation for arbitrarily small damping coefficients.
    private static (double Delta,double Velocity) IntegrateAxis(double v,double a,double drag,double h)
    {
        if(drag==0) return (v*h+.5*a*h*h,v+a*h);
        var z=drag*h;var e=Math.Exp(-z);
        double q,r;
        if(z<1e-4) {
            q=h*(1-z/2+z*z/6-z*z*z/24);
            r=h*h*(.5-z/6+z*z/24-z*z*z/120);
        } else { q=(1-e)/drag; r=(h-q)/drag; }
        return (v*q+a*r,v*e+a*q);
    }
    private static (double Delta,double Velocity) Friction(double v,double deceleration,double h)
    {
        if(deceleration==0) return (v*h,v);
        var time=Math.Min(h,Math.Abs(v)/deceleration);
        var sign=Math.Sign(v);
        return (v*time-sign*.5*deceleration*time*time,sign*Math.Max(0,Math.Abs(v)-deceleration*h));
    }
    private readonly record struct AxisMotion(PhysicalRect Rect,double Velocity,bool Hit,double Direction,double NormalSpeed);
    private static AxisMotion MoveAxis(PhysicalRect rect,PhysicsBounds bounds,bool horizontal,double h,
        Func<double,(double Delta,double Velocity)> motion)
    {
        var end=motion(h);
        var sweep=bounds.Sweep(rect,end.Delta,horizontal);
        bool hit=sweep.Hit;
        // An exact end-of-step contact is still a new impact. The tiny outward
        // probe is a coverage query only; it never changes the accepted position.
        if(!hit && end.Delta!=0) {
            var probe=bounds.Sweep(sweep.Rect,Math.Sign(end.Delta)*.0001,horizontal);
            hit=probe.Hit && probe.Rect==sweep.Rect;
        }
        if(!hit || end.Delta==0) return new(sweep.Rect,end.Velocity,false,end.Delta,0);
        double distance=horizontal ? sweep.Rect.Left-rect.Left : sweep.Rect.Top-rect.Top;
        double low=0,high=h;
        // This segment is monotonic (vertical turning points are split below).
        // Bounded time-of-impact solve, not an unbounded iterative repair.
        if(distance==0) high=0;
        else for(int i=0;i<48;i++) {
            var mid=(low+high)/2;
            if(Math.Abs(motion(mid).Delta)<Math.Abs(distance)) low=mid; else high=mid;
        }
        var velocity=motion(high).Velocity;
        return new(sweep.Rect,velocity,true,end.Delta,Math.Abs(velocity));
    }
    private static AxisMotion MoveVertical(PhysicalRect rect,PhysicsBounds bounds,double v,double a,double drag,double h)
    {
        var end=IntegrateAxis(v,a,drag,h);
        if(v<0 && end.Velocity>0 && a>0) {
            // The parabola can cross a ceiling then return to a legal endpoint.
            // Split at its exact apex so both monotonic paths are swept.
            var z=-drag*v/a;
            var apex=z<1e-4 ? -v/a*(1-z/2+z*z/3-z*z*z/4) : Math.Log(1+z)/drag;
            if(apex>0 && apex<h) {
                var first=MoveAxis(rect,bounds,false,apex,t=>IntegrateAxis(v,a,drag,t));
                if(first.Hit) return first;
                return MoveAxis(first.Rect,bounds,false,h-apex,t=>IntegrateAxis(0,a,drag,t));
            }
        }
        return MoveAxis(rect,bounds,false,h,t=>IntegrateAxis(v,a,drag,t));
    }
    private static PhysicsState Integrate(PhysicsState s,PhysicsMode mode,PhysicsBounds bounds,PhysicsTuning t,List<PhysicsImpact> events,bool absorbGroundWalls)
    {
        var h=t.FixedSeconds; var rect=s.Rect;
        var ground=bounds.Ground(rect,s.SupportId,t.SupportHysteresis);
        bool supported=mode==PhysicsMode.Gravity && s.Velocity.Y==0 && ground is {} floor && floor.Floor==rect.Bottom;
        var drag=mode==PhysicsMode.Gravity?t.AirDrag:t.PlanarDrag;
        var acceleration=mode==PhysicsMode.Gravity?t.Gravity:0;
        bool planarBounce=mode==PhysicsMode.PlanarInertia && t.PlanarBounceRetention<1;
        // The vertical component at the start of the substep. Planar keeps the
        // original per-axis reflection; Gravity scales this pre-step tangent
        // before the Y sweep. Contact-time splitting remains the existing
        // X-then-Y operator-split approximation (documented in the R1 report).
        var vy0=s.Velocity.Y;
        var sx=supported
            ? MoveAxis(rect,bounds,true,h,dt=>Friction(s.Velocity.X,t.GroundFriction,dt))
            : MoveAxis(rect,bounds,true,h,dt=>IntegrateAxis(s.Velocity.X,0,drag,dt));

        // Deterministic X-then-Y operator split. Each complete corridor is proven
        // covered. It is conservative at nonrectangular/diagonal corner routes;
        // it never uses a legal endpoint as proof of a legal path.
        rect=sx.Rect;
        // Opt-in Planar contacts reflect the fully damped substep velocity.
        // MoveAxis still supplies the unchanged sweep/contact-time diagnostics;
        // defaults and Gravity retain their original contact-time response.
        var vx=planarBounce && sx.Hit ? IntegrateAxis(s.Velocity.X,0,drag,h).Velocity : sx.Velocity;
        if(sx.Hit) {
            if(sx.NormalSpeed>0) events.Add(new(sx.Direction<0?CollisionKind.LeftWall:CollisionKind.RightWall,sx.NormalSpeed,rect,s.BounceCount));
            if(mode==PhysicsMode.Gravity) {
                if(absorbGroundWalls && supported) vx=0;
                else if(sx.NormalSpeed>=t.GravityWallBounceMinimumSpeed) {
                    vx=-vx*t.GravityWallBounce;
                    vy0*=t.GravityWallBounce;
                } else vx=0;
            } else vx=-vx*t.WallRestitution;
        }
        // Walking/sliding off a higher floor loses support without teleporting
        // to the lower one. Gravity begins immediately in this same substep.
        ground=bounds.Ground(rect,s.SupportId,t.SupportHysteresis);
        if(supported && (ground is null || ground.Value.Floor!=rect.Bottom)) {
            supported=false;
        }
        var sy=supported ? new AxisMotion(rect,0,false,0,0) : MoveVertical(rect,bounds,vy0,acceleration,drag,h);
        rect=sy.Rect;
        var vy=planarBounce && sy.Hit ? IntegrateAxis(vy0,0,drag,h).Velocity : sy.Velocity;
        int bounces=s.BounceCount; double previous=s.PreviousBounceSpeed;
        if(sy.Hit) {
            var kind=sy.Direction>0?CollisionKind.Ground:CollisionKind.Ceiling;
            double normal=sy.NormalSpeed;
            if(mode==PhysicsMode.Gravity && kind==CollisionKind.Ground) {
                // X was swept first. If this same substep also reaches Ground,
                // absorb the provisional side reflection before another substep
                // can move backwards. Default/model/Planar callers are unchanged.
                if(absorbGroundWalls && sx.Hit) vx=0;
                var rebound=normal*t.GroundRestitution;
                if(bounces>0) rebound=Math.Min(rebound,previous*.5);
                if(normal>=t.MinimumBounceSpeed && bounces<t.MaximumBounces && rebound>t.StopSpeed) {
                    bounces++; previous=rebound; vy=-rebound;
                } else { vy=0; supported=true; }
            } else if(mode==PhysicsMode.Gravity) {
                if(normal>=t.GravityWallBounceMinimumSpeed) {
                    vy=-vy*t.GravityWallBounce;
                    vx*=t.GravityWallBounce;
                } else vy=0;
            } else {
                vy=mode==PhysicsMode.PlanarInertia && t.PlanarVerticalRule==VerticalBoundaryRule.Stop ? 0 : -vy*t.WallRestitution;
            }
            if(normal>0) events.Add(new(kind,normal,rect,bounces));
        }
        // A corner may report X and Y impacts in this same fixed substep.
        // Reflect both above, then charge the whole vector at most once.
        if(planarBounce && (sx.Hit && sx.NormalSpeed>t.PlanarBounceMinimumSpeed ||
                            sy.Hit && sy.NormalSpeed>t.PlanarBounceMinimumSpeed)) {
            vx*=t.PlanarBounceRetention; vy*=t.PlanarBounceRetention;
        }
        var magnitude=Speed(vx,vy);
        if(magnitude>t.MaximumSpeed) { var scale=t.MaximumSpeed/magnitude;vx*=scale;vy*=scale; }
        // Bounce history describes the actual outgoing normal speed, including
        // the vector cap. Every successful output must be valid as the next input,
        // and a later rebound must be weaker than this actual rebound.
        if(bounces>s.BounceCount) previous=Math.Abs(vy);
        PhysicsPhase phase;
        string? supportId=null;
        if(mode==PhysicsMode.PlanarInertia) {
            if(Speed(vx,vy)<=t.StopSpeed) {vx=0;vy=0;}
            phase=vx==0&&vy==0?PhysicsPhase.Settled:PhysicsPhase.Airborne;
        } else {
            ground=bounds.Ground(rect,s.SupportId,t.SupportHysteresis);
            supported=vy==0 && ground is {} current && current.Floor==rect.Bottom;
            if(supported) {
                if(Math.Abs(vx)<=t.StopSpeed) vx=0;
                phase=vx==0?PhysicsPhase.Settled:PhysicsPhase.Sliding; supportId=ground!.Value.Id;
            } else phase=PhysicsPhase.Airborne;
        }
        return new(rect,new(vx,vy),phase,bounces,previous,s.Accumulator,supportId);
    }
}
