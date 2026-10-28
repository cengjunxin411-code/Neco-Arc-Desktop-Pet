using System;
using System.Collections.Generic;
using System.Linq;
using NecoArcDesktopPet.Coordinates;

namespace NecoArcDesktopPet.Pilot;

internal static class CrossScreenGeometry
{
    internal sealed record SeamPlan(bool Ready,string Reason,PhysicalRect TargetWork,
        PhysicalRect Prepared,PhysicalVector Grab,double Effective,int Steps,DpiScale? PredictedDpi=null);
    internal const int SeamMaximumSteps=12;
    internal const double SeamEdgeStep=16,SeamMinimumScale=.75;

    // Only an actual shared edge with a non-empty transverse interval is a seam.
    // No bounding-box topology, corner joining, or moving an illegal trusted RECT.
    internal static PhysicalRect? SeamTarget(PhysicalRect trusted,PhysicalPoint? cursor,
        PhysicalVector grab,IReadOnlyList<PhysicalRect> work)
    {
        if(!trusted.IsValid||cursor is not {} c||!c.IsFinite||!grab.IsFinite||work.Count==0||work.Count>=16||
            work.Any(w=>!w.IsValid)||!Covered(trusted,work)||grab.X<0||grab.Y<0||grab.X>trusted.Width||grab.Y>trusted.Height)return null;
        var anchor=new PhysicalPoint(trusted.Left+grab.X,trusted.Top+grab.Y);
        foreach(var source in work.Where(w=>Contains(w,trusted)))foreach(var target in work.Where(w=>w!=source)) {
            var yJoined=Math.Min(source.Bottom,target.Bottom)>Math.Max(source.Top,target.Top);
            var xJoined=Math.Min(source.Right,target.Right)>Math.Max(source.Left,target.Left);
            if(yJoined&&source.Right==target.Left&&c.X>anchor.X&&trusted.Right+c.X-anchor.X>=source.Right-32)return target;
            if(yJoined&&source.Left==target.Right&&c.X<anchor.X&&trusted.Left+c.X-anchor.X<=source.Left+32)return target;
            if(xJoined&&source.Bottom==target.Top&&c.Y>anchor.Y&&trusted.Bottom+c.Y-anchor.Y>=source.Bottom-32)return target;
            if(xJoined&&source.Top==target.Bottom&&c.Y<anchor.Y&&trusted.Top+c.Y-anchor.Y<=source.Top+32)return target;
        }
        return null;
    }
    private static bool Contains(PhysicalRect area,PhysicalRect rect)=>area.IsValid&&rect.IsValid&&
        rect.Left>=area.Left&&rect.Right<=area.Right&&rect.Top>=area.Top&&rect.Bottom<=area.Bottom;

    // The fraction is captured once on accepted Down. A physical grab may already
    // be rebased while the previous trusted RECT is still awaiting native commit.
    internal static PhysicalVector? SeamGrab(PhysicalVector fraction,LocalDipVector size,DpiScale dpi)
    {
        if(!fraction.IsFinite||fraction.X<0||fraction.X>1||fraction.Y<0||fraction.Y>1||
            !size.IsFinite||size.X<=0||size.Y<=0||!dpi.IsValid)return null;
        var grab=new PhysicalVector(fraction.X*Math.Round(size.X*dpi.X),fraction.Y*Math.Round(size.Y*dpi.Y));
        return grab.IsFinite?grab:null;
    }

    internal static PhysicalRect Anchored(PhysicalPoint anchor,PhysicalVector fraction,
        LocalDipVector desired,DpiScale dpi,double effective)=>
        Target(anchor,new(Math.Round(desired.X*dpi.X*effective)*fraction.X,
            Math.Round(desired.Y*dpi.Y*effective)*fraction.Y),new(desired.X*effective,desired.Y*effective),dpi);

    internal static SeamPlan PlanSeam(PhysicalRect trusted,PhysicalPoint? cursor,PhysicalVector grab,
        LocalDipVector desired,DpiScale dpi,double effective,DpiScale targetDpi,IReadOnlyList<PhysicalRect> work)
    {
        SeamPlan Reject(string reason,PhysicalRect area=default)=>new(false,reason,area,default,default,effective,0);
        if(cursor is not {} c||!c.IsFinite||!desired.IsFinite||desired.X<=0||desired.Y<=0||
            !dpi.IsValid||!targetDpi.IsValid||!double.IsFinite(effective)||effective<=0||effective>1||
            !NaturalFootprint(trusted,new(desired.X*effective,desired.Y*effective),dpi))return Reject("INVALID_INPUT");
        var target=SeamTarget(trusted,cursor,grab,work);
        if(target is not {} destination)return Reject("NO_SEAM_INTENT");
        var source=work.First(w=>Contains(w,trusted)&&w!=destination&&
            (w.Right==destination.Left||w.Left==destination.Right||w.Bottom==destination.Top||w.Top==destination.Bottom));
        bool horizontal=source.Right==destination.Left||source.Left==destination.Right;
        var low=horizontal?Math.Max(source.Top,destination.Top):Math.Max(source.Left,destination.Left);
        var high=horizontal?Math.Min(source.Bottom,destination.Bottom):Math.Min(source.Right,destination.Right);
        var anchor=new PhysicalPoint(trusted.Left+grab.X,trusted.Top+grab.Y);
        var fraction=new PhysicalVector(grab.X/trusted.Width,grab.Y/trusted.Height);
        var future=Anchored(anchor,fraction,desired,targetDpi,effective);
        var currentLow=horizontal?trusted.Top:trusted.Left;var currentHigh=horizontal?trusted.Bottom:trusted.Right;
        var futureLow=horizontal?future.Top:future.Left;var futureHigh=horizontal?future.Bottom:future.Right;
        if(currentLow>=low&&currentHigh<=high&&futureLow>=low&&futureHigh<=high&&
            future.Width<=destination.Width&&future.Height<=destination.Height)return Reject("NOT_NEEDED",destination);
        var a=horizontal?anchor.Y:anchor.X;var q=horizontal?fraction.Y:fraction.X;
        if(a<=low+2||a>=high-2)return Reject("NEEDS_UNSAFE_GRAB_SHIFT",destination);
        var extent=horizontal?desired.Y*Math.Max(dpi.Y,targetDpi.Y):desired.X*Math.Max(dpi.X,targetDpi.X);
        double cap=effective;
        if(q>0)cap=Math.Min(cap,(a-low-2)/(extent*q));
        if(q<1)cap=Math.Min(cap,(high-2-a)/(extent*(1-q)));
        cap=Math.Min(cap,Math.Min(destination.Width/(desired.X*Math.Max(dpi.X,targetDpi.X)),
            destination.Height/(desired.Y*Math.Max(dpi.Y,targetDpi.Y))));
        if(!double.IsFinite(cap)||cap<SeamMinimumScale)return Reject("NO_FEASIBLE_CORRIDOR",destination);
        var prep=Anchored(anchor,fraction,desired,dpi,cap);
        var predicted=Anchored(anchor,fraction,desired,targetDpi,cap);
        if(!Contains(source,prep)||!TranslationSafe(trusted,prep,work)||
            (horizontal?Math.Min(prep.Top,predicted.Top)<low||Math.Max(prep.Bottom,predicted.Bottom)>high:
                Math.Min(prep.Left,predicted.Left)<low||Math.Max(prep.Right,predicted.Right)>high))return Reject("NO_FEASIBLE_CORRIDOR",destination);
        // Prove a complete route into the actual destination at this transverse anchor.
        var across=horizontal?new PhysicalRect(destination.Left,prep.Top,destination.Left+prep.Width,prep.Bottom):
            new PhysicalRect(prep.Left,destination.Top,prep.Right,destination.Top+prep.Height);
        if(source.Left>destination.Left&&horizontal)across=across with{Left=destination.Right-prep.Width,Right=destination.Right};
        if(source.Top>destination.Top&&!horizontal)across=across with{Top=destination.Bottom-prep.Height,Bottom=destination.Bottom};
        if(!Contains(destination,across)||!TranslationSafe(prep,across,work))return Reject("NO_FEASIBLE_CORRIDOR",destination);
        var motion=Math.Max(Math.Max(Math.Abs(prep.Left-trusted.Left),Math.Abs(prep.Right-trusted.Right)),
            Math.Max(Math.Abs(prep.Top-trusted.Top),Math.Abs(prep.Bottom-trusted.Bottom)));
        var steps=(int)Math.Ceiling(motion/SeamEdgeStep);
        if(steps<1||steps>SeamMaximumSteps)return Reject("NEEDS_UNSAFE_GRAB_SHIFT",destination);
        return new(true,"SAFE_ANCHORED_CONTRACTION",destination,prep,
            new(fraction.X*prep.Width,fraction.Y*prep.Height),cap,steps,targetDpi);
    }
    // Sweep every vertical slab of the rectangle; merged Y intervals must cover all
    // of each slab. Checking corners, anchor or intersections cannot detect holes.
    internal static bool Covered(PhysicalRect rect,IReadOnlyList<PhysicalRect> work)
    {
        if(!rect.IsValid || work.Count==0 || work.Any(w=>!w.IsValid))return false;
        var xs=work.SelectMany(w=>new[]{w.Left,w.Right}).Where(x=>x>rect.Left&&x<rect.Right)
            .Append(rect.Left).Append(rect.Right).Distinct().OrderBy(x=>x).ToArray();
        for(int i=1;i<xs.Length;i++) {
            var end=rect.Top;
            foreach(var w in work.Where(w=>w.Left<=xs[i-1]&&w.Right>=xs[i]).OrderBy(w=>w.Top)) {
                if(w.Top>end)break;
                end=Math.Max(end,w.Bottom);if(end>=rect.Bottom)break;
            }
            if(end<rect.Bottom)return false;
        }
        return true;
    }
    internal static PhysicalRect Target(PhysicalPoint cursor,PhysicalVector grab,LocalDipVector size,DpiScale scale)
    {
        if(!cursor.IsFinite||!grab.IsFinite||!size.IsFinite||!scale.IsValid)return default;
        var x=Math.Round(cursor.X-grab.X);var y=Math.Round(cursor.Y-grab.Y);
        return PhysicalRect.TryFromOriginAndSize(new(x,y),Math.Round(size.X*scale.X),Math.Round(size.Y*scale.Y),out var rect)?rect:default;
    }
    // Legal-origin space changes only at area edges or edge minus footprint size.
    // Project onto each such cell boundary and keep the closest *fully covered* rectangle.
    // Finite O(n^4) search, limited to the verified desktop's small monitor set.
    internal static PhysicalRect? Fit(PhysicalRect request,IReadOnlyList<PhysicalRect> work)
    {
        if(!request.IsValid||work.Count==0||work.Count>16||work.Any(w=>!w.IsValid))return null;
        if(Covered(request,work))return request;
        var xs=work.SelectMany(w=>new[]{Math.Ceiling(w.Left),Math.Floor(w.Right-request.Width)}).Append(request.Left).Distinct();
        var ys=work.SelectMany(w=>new[]{Math.Ceiling(w.Top),Math.Floor(w.Bottom-request.Height)}).Append(request.Top).Distinct().ToArray();
        PhysicalRect? best=null;double distance=double.PositiveInfinity;
        foreach(var x in xs)foreach(var y in ys) {
            if(!PhysicalRect.TryFromOriginAndSize(new(x,y),request.Width,request.Height,out var candidate)||!Covered(candidate,work))continue;
            var dx=x-request.Left;var dy=y-request.Top;var d=dx*dx+dy*dy;
            if(!double.IsFinite(d))continue;
            if(d<distance){distance=d;best=candidate;}
        }
        return best;
    }
    internal static double Limit(LocalDipVector desired,DpiScale dpi,IReadOnlyList<PhysicalRect> work)
    {
        if(!desired.IsFinite||desired.X<=0||desired.Y<=0||!dpi.IsValid||work.Count==0||work.Count>16||work.Any(w=>!w.IsValid))return 0;
        var width=desired.X*dpi.X;var height=desired.Y*dpi.Y;
        if(!double.IsFinite(width)||!double.IsFinite(height))return 0;
        // Include connected tiled rectangles, not just individual monitors.
        // Coverage verification excludes holes and disconnected bounding boxes.
        double best=0;
        var xs=work.SelectMany(w=>new[]{w.Left,w.Right}).Distinct().ToArray();
        var ys=work.SelectMany(w=>new[]{w.Top,w.Bottom}).Distinct().ToArray();
        foreach(var l in xs)foreach(var r in xs)foreach(var t in ys)foreach(var b in ys) {
            var region=new PhysicalRect(l,t,r,b);
            if(!region.IsValid||!Covered(region,work))continue;
            best=Math.Max(best,Math.Min(1,Math.Min(Math.Floor(region.Width)/width,Math.Floor(region.Height)/height)));
        }
        return best;
    }
    // The adapter may constrain within the last trusted connected component, never
    // teleport a captured gesture over a disconnected gap into another component.
    internal static PhysicalRect[] Reachable(PhysicalRect trusted,IReadOnlyList<PhysicalRect> work)
    {
        if(!trusted.IsValid||work.Count==0||work.Count>16||work.Any(w=>!w.IsValid))return Array.Empty<PhysicalRect>();
        bool Overlap(PhysicalRect a,PhysicalRect b)=>Math.Min(a.Right,b.Right)>Math.Max(a.Left,b.Left)&&Math.Min(a.Bottom,b.Bottom)>Math.Max(a.Top,b.Top);
        bool Joined(PhysicalRect a,PhysicalRect b)=>
            Math.Min(a.Right,b.Right)>=Math.Max(a.Left,b.Left)&&Math.Min(a.Bottom,b.Bottom)>=Math.Max(a.Top,b.Top)&&
            (Math.Min(a.Right,b.Right)>Math.Max(a.Left,b.Left)||Math.Min(a.Bottom,b.Bottom)>Math.Max(a.Top,b.Top));
        var selected=new HashSet<int>();
        for(int i=0;i<work.Count;i++)if(Overlap(trusted,work[i]))selected.Add(i);
        // Multiple disconnected components touched by an illegal frozen rectangle are untrusted.
        if(selected.Count==0)return Array.Empty<PhysicalRect>();
        var seed=selected.First();selected.Clear();selected.Add(seed);
        for(int pass=0;pass<work.Count;pass++)for(int i=0;i<work.Count;i++)
            if(!selected.Contains(i)&&selected.Any(j=>Joined(work[i],work[j])))selected.Add(i);
        if(work.Where((w,i)=>!selected.Contains(i)).Any(w=>Overlap(trusted,w)))return Array.Empty<PhysicalRect>();
        return work.Where((_,i)=>selected.Contains(i)).ToArray();
    }
    internal static bool TranslationSafe(PhysicalRect from,PhysicalRect to,IReadOnlyList<PhysicalRect> work)=>
        from.IsValid&&to.IsValid&&Covered(new(Math.Min(from.Left,to.Left),Math.Min(from.Top,to.Top),
            Math.Max(from.Right,to.Right),Math.Max(from.Bottom,to.Bottom)),work);
    internal static bool Threshold(PhysicalVector delta,DpiScale scale,double threshold)=>
        double.IsFinite(threshold)&&threshold>=0&&scale.TryToLocalDip(delta,out var local)&&Math.Abs(local.X)+Math.Abs(local.Y)>threshold;
    internal static bool CursorUsable(bool succeeded,PhysicalPoint cursor)=>succeeded&&cursor.IsFinite;
    internal static bool NaturalFootprint(PhysicalRect rect,LocalDipVector size,DpiScale scale)=>
        rect.IsValid&&size.IsFinite&&size.X>0&&size.Y>0&&scale.IsValid&&
        Math.Abs(rect.Width-Math.Round(size.X*scale.X))<=2&&Math.Abs(rect.Height-Math.Round(size.Y*scale.Y))<=2;
    internal static bool Near(PhysicalRect actual,PhysicalRect expected)=>actual.IsValid&&expected.IsValid&&
        Math.Abs(actual.Left-expected.Left)<=2&&Math.Abs(actual.Top-expected.Top)<=2&&
        Math.Abs(actual.Right-expected.Right)<=2&&Math.Abs(actual.Bottom-expected.Bottom)<=2;
}
